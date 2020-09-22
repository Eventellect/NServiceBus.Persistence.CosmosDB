﻿namespace AzureStoragePersistenceSagaExporter.AcceptanceTests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Table;
    using NServiceBus;
    using NServiceBus.AcceptanceTesting;
    using NServiceBus.AcceptanceTesting.Customization;
    using NUnit.Framework;
    using Particular.AzureStoragePersistenceSagaExporter;

    class MigrationEndToEnd : NServiceBusAcceptanceTest
    {
        [SetUp]
        public async Task Setup()
        {
            var account = CloudStorageAccount.Parse(AzureStoragePersistenceConnectionString);
            var client = account.CreateCloudTableClient();

            table = client.GetTableReference(nameof(MigratingEndpoint.MigratingSagaData));

            await table.CreateIfNotExistsAsync();

            workingDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Path.GetFileNameWithoutExtension(Path.GetTempFileName()));
            Directory.CreateDirectory(workingDir);
        }

        [TearDown]
        public async Task Teardown()
        {
            await table.DeleteIfExistsAsync();
            Directory.Delete(workingDir, true);
        }

        [Test]
        public async Task Can_migrate_from_ASP_to_CosmosDB()
        {
            // Arrange
            var testContext = await Scenario.Define<Context>(c => c.MyId = Guid.NewGuid())
                .WithEndpoint<MigratingEndpoint>(b => b.CustomConfig(ec =>
                {
                    var routing = ec.ConfigureTransport().Routing();
                    routing.RouteToEndpoint(typeof(CompleteSagaRequest), typeof(SomeOtherEndpoint));

                    var persistence = ec.UsePersistence<AzureStoragePersistence>();
                    persistence.ConnectionString(AzureStoragePersistenceConnectionString);
                }).When((s, c) => s.SendLocal(new StartSaga
                {
                    MyId = c.MyId
                })))
                .Done(ctx => ctx.CompleteSagaRequestSent)
                .Run();

            // Act
            await Exporter.Run(new ConsoleLogger(true), AzureStoragePersistenceConnectionString, nameof(MigratingEndpoint.MigratingSagaData), workingDir, CancellationToken.None);

            var filePath = DetermineAndVerifyExport(testContext);
            await ImportIntoCosmosDB(filePath);

            // Assert
            await Scenario.Define<Context>(c => c.MyId = testContext.MyId)
                .WithEndpoint<MigratingEndpoint>(b => b.CustomConfig(ec =>
                {
                    var routing = ec.ConfigureTransport().Routing();
                    routing.RouteToEndpoint(typeof(CompleteSagaRequest), typeof(SomeOtherEndpoint));

                    var persistence = ec.UsePersistence<CosmosDbPersistence>();
                    persistence.CosmosClient(CosmosClient);
                    persistence.DatabaseName(DatabaseName);
                    persistence.DefaultContainer(ContainerName, PartitionPathKey);
                    persistence.EnableMigrationMode();
                }))
                .WithEndpoint<SomeOtherEndpoint>()
                .Done(ctx => ctx.CompleteSagaResponseReceived)
                .Run();
        }

        string DetermineAndVerifyExport(Context testContext)
        {
            var newId = SagaIdGenerator.Generate(typeof(MigratingEndpoint.MigratingSagaData).FullName, nameof(MigratingEndpoint.MigratingSagaData.MyId), testContext.MyId.ToString());

            var filePath = Path.Combine(workingDir, nameof(MigratingEndpoint.MigratingSagaData), $"{newId}.json");

            Assert.IsTrue(File.Exists(filePath), "File exported");
            return filePath;
        }

        async Task ImportIntoCosmosDB(string filePath)
        {
            var container = CosmosClient.GetContainer(DatabaseName, ContainerName);

            var partitionKey = Path.GetFileNameWithoutExtension(filePath);

            using (var stream = File.OpenRead(filePath))
            {
                var response = await container.CreateItemStreamAsync(stream, new PartitionKey(partitionKey));

                Assert.IsTrue(response.IsSuccessStatusCode, "Successfully imported");
            }
        }

        CloudTable table;
        string workingDir;

        public class Context : ScenarioContext
        {
            public bool CompleteSagaRequestSent { get; set; }
            public bool CompleteSagaResponseReceived { get; set; }
            public Guid MyId { get; internal set; }
        }

        public class MigratingEndpoint : EndpointConfigurationBuilder
        {
            public MigratingEndpoint()
            {
                EndpointSetup<BaseEndpoint>();
            }

            public class MigratingSaga : Saga<MigratingSagaData>,
                IAmStartedByMessages<StartSaga>,
                IHandleMessages<CompleteSagaResponse>
            {
                public MigratingSaga(Context testContext)
                {
                    this.testContext = testContext;
                }

                public Task Handle(StartSaga message, IMessageHandlerContext context)
                {
                    Data.MyId = message.MyId;
                    testContext.CompleteSagaRequestSent = true;
                    return context.Send(new CompleteSagaRequest());
                }

                public Task Handle(CompleteSagaResponse message, IMessageHandlerContext context)
                {
                    testContext.CompleteSagaResponseReceived = true;
                    MarkAsComplete();
                    return Task.CompletedTask;
                }

                protected override void ConfigureHowToFindSaga(SagaPropertyMapper<MigratingSagaData> mapper)
                {
                    mapper.ConfigureMapping<StartSaga>(msg => msg.MyId).ToSaga(saga => saga.MyId);
                }

                readonly Context testContext;
            }

            public class MigratingSagaData : ContainSagaData
            {
                public Guid MyId { get; set; }
                public List<string> ListOfStrings { get; set; } = new List<string>
                {
                    "Hello World"
                };

                public List<int> ListOfINts { get; set; } = new List<int>
                {
                    43, 42
                };

                public Nested Nested { get; set; } = new Nested();

                public int IntValue { get; set; } = 1;
                public long LongValue { get; set; } = 1;
                public double DoubleValue { get; set; } = 1.24;
                public byte[] BinaryValue { get; set; } = Encoding.UTF8.GetBytes("Hello World");
                public DateTime DateTimeValue { get; set; } = new DateTime(2020, 09, 21, 5, 5, 5, 5);
                public bool BooleanValue { get; set; } = true;
                public decimal DecimalValue { get; set; } = 1.2m;
                public float FloatValue { get; set; } = 1.2f;

                public string PretendsToBeAnArray { get; set; } = "[ Garbage ]";
                public string PretendsToBeAnObject { get; set; } = "{ \"Garbage\" }";
            }

            public class Nested
            {
                public string Foo { get; set; } = "Foo";
                public string Bar { get; set; } = "Bar";
            }
        }

        public class SomeOtherEndpoint : EndpointConfigurationBuilder
        {
            public SomeOtherEndpoint()
            {
                EndpointSetup<BaseEndpoint>(c => c.UsePersistence<InMemoryPersistence>());
            }

            public class CompleteSagaRequestHandler : IHandleMessages<CompleteSagaRequest>
            {
                public Task Handle(CompleteSagaRequest message, IMessageHandlerContext context)
                {
                    return context.Reply(new CompleteSagaResponse());
                }
            }
        }

        public class StartSaga : ICommand
        {
            public Guid MyId { get; set; }
        }

        public class CompleteSagaRequest : IMessage
        {
        }

        public class CompleteSagaResponse : IMessage
        {
        }
    }
}