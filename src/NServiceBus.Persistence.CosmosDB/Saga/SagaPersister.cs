﻿namespace NServiceBus.Persistence.CosmosDB
{
    using System;
    using System.IO;
    using System.Net;
    using System.Threading.Tasks;
    using Extensibility;
    using Microsoft.Azure.Cosmos;
    using Newtonsoft.Json;
    using Sagas;

    class SagaPersister : ISagaPersister
    {
        public SagaPersister(JsonSerializer serializer, bool migrationModeEnabled)
        {
            this.serializer = serializer;
            this.migrationModeEnabled = migrationModeEnabled;
        }

        public Task Save(IContainSagaData sagaData, SagaCorrelationProperty correlationProperty, SynchronizedStorageSession session, ContextBag context)
        {
            var storageSession = (StorageSession)session;
            var partitionKey = GetPartitionKey(context, sagaData.Id);

            storageSession.AddOperation(new SagaSave(sagaData, partitionKey, serializer, context));
            return Task.CompletedTask;
        }

        public Task Update(IContainSagaData sagaData, SynchronizedStorageSession session, ContextBag context)
        {
            var storageSession = (StorageSession)session;
            var partitionKey = GetPartitionKey(context, sagaData.Id);

            storageSession.AddOperation(new SagaUpdate(sagaData, partitionKey, serializer, context));
            return Task.CompletedTask;
        }

        async Task<TSagaData> Get<TSagaData>(Guid sagaId, SynchronizedStorageSession session, ContextBag context, bool generatedSagaId) where TSagaData : class, IContainSagaData
        {
            var storageSession = (StorageSession)session;

            // reads need to go directly
            var container = storageSession.ContainerHolder.Container;
            var partitionKey = GetPartitionKey(context, sagaId);

            var responseMessage = await GetOrQuerySagaData(sagaId, partitionKey, container, generatedSagaId).ConfigureAwait(false);

            if (responseMessage.StatusCode == HttpStatusCode.NotFound || responseMessage.Content == null)
            {
                return default;
            }

            using (var streamReader = new StreamReader(responseMessage.Content))
            {
                using (var jsonReader = new JsonTextReader(streamReader))
                {
                    var sagaData = serializer.Deserialize<TSagaData>(jsonReader);

                    context.Set($"cosmos_etag:{sagaId}", responseMessage.Headers.ETag);

                    return sagaData;
                }
            }
        }

        async Task<ResponseMessage> GetOrQuerySagaData(Guid sagaId, PartitionKey partitionKey, Container container, bool generatedSagaId)
        {
            var responseMessage = await container.ReadItemStreamAsync(sagaId.ToString(), partitionKey).ConfigureAwait(false);

            var sagaFound = responseMessage.StatusCode != HttpStatusCode.NotFound && responseMessage.Content != null;

            if (sagaFound || !migrationModeEnabled || generatedSagaId)
            {
                return responseMessage;
            }

            var query = $@"SELECT * FROM c WHERE c[""{MetadataExtensions.MetadataKey}""][""{MetadataExtensions.SagaDataContainerMigratedSagaIdMetadataKey}""] = '{sagaId}'";
            var queryDefinition = new QueryDefinition(query);
            var queryStreamIterator = container.GetItemQueryStreamIterator(queryDefinition);

            return await queryStreamIterator.ReadNextAsync().ConfigureAwait(false);
        }

        public Task<TSagaData> Get<TSagaData>(Guid sagaId, SynchronizedStorageSession session, ContextBag context) where TSagaData : class, IContainSagaData => Get<TSagaData>(sagaId, session, context, false);

        public Task<TSagaData> Get<TSagaData>(string propertyName, object propertyValue, SynchronizedStorageSession session, ContextBag context) where TSagaData : class, IContainSagaData
        {
            // Saga ID needs to be calculated the same way as in SagaIdGenerator does
            var sagaId = SagaIdGenerator.Generate(typeof(TSagaData), propertyName, propertyValue);

            return Get<TSagaData>(sagaId, session, context, true);
        }

        public Task Complete(IContainSagaData sagaData, SynchronizedStorageSession session, ContextBag context)
        {
            var storageSession = (StorageSession)session;
            var partitionKey = GetPartitionKey(context, sagaData.Id);

            storageSession.AddOperation(new SagaDelete(sagaData, partitionKey, context));

            return Task.CompletedTask;
        }

        static PartitionKey GetPartitionKey(ContextBag context, Guid sagaDataId)
        {
            if (!context.TryGet<PartitionKey>(out var partitionKey))
            {
                partitionKey = new PartitionKey(sagaDataId.ToString());
            }

            return partitionKey;
        }

        JsonSerializer serializer;
        readonly bool migrationModeEnabled;
    }
}