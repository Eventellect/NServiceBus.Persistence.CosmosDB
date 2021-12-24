﻿namespace NServiceBus.Persistence.CosmosDB
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Extensibility;
    using Microsoft.Azure.Cosmos;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Sagas;

    class SagaPersister : ISagaPersister
    {
        public SagaPersister(JsonSerializer serializer, bool migrationModeEnabled, bool usePessimisticsLockingMode, TimeSpan leaseLockTime, TimeSpan leaseLockAcquisitionMaximumRefreshDelay)
        {
            this.serializer = serializer;
            this.migrationModeEnabled = migrationModeEnabled;
            this.usePessimisticsLockingMode = usePessimisticsLockingMode;
            this.leaseLockTime = leaseLockTime;
            acquireLeaseLockRefreshMaximumDelayTicks = (int)leaseLockAcquisitionMaximumRefreshDelay.Ticks;
        }

        public Task Save(IContainSagaData sagaData, SagaCorrelationProperty correlationProperty, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default)
        {
            var storageSession = (StorageSession)session;
            var partitionKey = GetPartitionKey(context, sagaData.Id);

            storageSession.AddOperation(new SagaSave(sagaData, partitionKey, serializer, context));
            return Task.CompletedTask;
        }

        public Task Update(IContainSagaData sagaData, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default)
        {
            var storageSession = (StorageSession)session;
            var partitionKey = GetPartitionKey(context, sagaData.Id);

            storageSession.AddOperation(new SagaUpdate(sagaData, partitionKey, serializer, context));
            return Task.CompletedTask;
        }

        public async Task<TSagaData> Get<TSagaData>(Guid sagaId, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default) where TSagaData : class, IContainSagaData
        {
            var storageSession = (StorageSession)session;

            // reads need to go directly
            var container = storageSession.ContainerHolder.Container;
            var partitionKey = GetPartitionKey(context, sagaId);

            if (!usePessimisticsLockingMode)
            {
                using (var responseMessage = await container.ReadItemStreamAsync(sagaId.ToString(), partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    var sagaStream = responseMessage.Content;

                    var sagaNotFound = responseMessage.StatusCode == HttpStatusCode.NotFound || sagaStream == null;

                    // if the previous lookup by id wasn't successful and the migration mode is enabled try to query for the saga data because the saga id probably represents
                    // the saga id of the migrated saga.
                    if (sagaNotFound && migrationModeEnabled)
                    {
                        return await FindSagaInMigrationMode<TSagaData>(sagaId, context, container, responseMessage, cancellationToken).ConfigureAwait(false);
                    }

                    return sagaNotFound ? default : ReadSagaFromStream<TSagaData>(context, sagaStream, responseMessage);
                }
            }

            var patchRes = await Patch<TSagaData>(sagaId, context, container, partitionKey, cancellationToken).ConfigureAwait(false);

            //NotFound
            if (!patchRes.Item1 && migrationModeEnabled)
            {
                //TODO: I was trying to refactor the code to extract a common code as much as possible, but... needs to be reviewed by second pair of eyes..
                //Possible potential simplest solution - just skip it
                //return await FindSagaInMigrationMode<TSagaData>(sagaId, context, container, responseMessage, cancellationToken);
            }

            return patchRes.Item2;
        }

        async Task<TSagaData> FindSagaInMigrationMode<TSagaData>(Guid sagaId, ContextBag context, Container container,
            ResponseMessage responseMessage, CancellationToken cancellationToken) where TSagaData : class, IContainSagaData
        {
            var query =
                $@"SELECT TOP 1 * FROM c WHERE c[""{MetadataExtensions.MetadataKey}""][""{MetadataExtensions.SagaDataContainerMigratedSagaIdMetadataKey}""] = '{sagaId}'";
            var queryDefinition = new QueryDefinition(query);
            var queryStreamIterator = container.GetItemQueryStreamIterator(queryDefinition);

            using (var iteratorResponse = await queryStreamIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                iteratorResponse.EnsureSuccessStatusCode();

                using (var streamReader = new StreamReader(iteratorResponse.Content))
                using (var jsonReader = new JsonTextReader(streamReader))
                {
                    var iteratorResult = await JObject.LoadAsync(jsonReader, cancellationToken).ConfigureAwait(false);

                    if (!(iteratorResult["Documents"] is JArray documents) || !documents.HasValues)
                    {
                        return default;
                    }

                    var sagaData = documents[0].ToObject<TSagaData>(serializer);
                    context.Set($"cosmos_etag:{sagaData.Id}", responseMessage.Headers.ETag);
                    context.Set($"cosmos_migratedsagaid:{sagaData.Id}", sagaId);
                    return sagaData;
                }
            }
        }

        public async Task<TSagaData> Get<TSagaData>(string propertyName, object propertyValue, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default)
            where TSagaData : class, IContainSagaData
        {
            var storageSession = (StorageSession)session;

            // Saga ID needs to be calculated the same way as in SagaIdGenerator does
            var sagaId = CosmosSagaIdGenerator.Generate(typeof(TSagaData), propertyName, propertyValue);

            // reads need to go directly
            var container = storageSession.ContainerHolder.Container;
            var partitionKey = GetPartitionKey(context, sagaId);

            if (!usePessimisticsLockingMode)
            {
                using (var responseMessage = await container.ReadItemStreamAsync(sagaId.ToString(), partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    var sagaStream = responseMessage.Content;

                    var sagaNotFound = responseMessage.StatusCode == HttpStatusCode.NotFound || sagaStream == null;

                    return sagaNotFound ? default : ReadSagaFromStream<TSagaData>(context, sagaStream, responseMessage);
                }
            }

            //else
            var patchRes = await Patch<TSagaData>(sagaId, context, container, partitionKey, cancellationToken).ConfigureAwait(false);

            return patchRes.Item2;
        }

        async Task<Tuple<bool, TSagaData>> Patch<TSagaData>(Guid sagaId, ContextBag context, Container container, PartitionKey partitionKey, CancellationToken cancellationToken)
            where TSagaData : class, IContainSagaData
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long unixTimeNow = (long)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;

                IReadOnlyList<PatchOperation> patchOperations = new List<PatchOperation>
                {
                    PatchOperation.Add("/ReserveUntil", unixTimeNow + leaseLockTime.Seconds)
                };
                var requestOptions = new PatchItemRequestOptions
                {
                    FilterPredicate = $"from c where (NOT IS_DEFINED(c.ReserveUntil) OR c.ReserveUntil < {unixTimeNow})"
                };

                using (var responseMessage = await container.PatchItemStreamAsync(sagaId.ToString(), partitionKey, patchOperations, requestOptions, cancellationToken).ConfigureAwait(false))
                {
                    var sagaStream = responseMessage.Content;

                    if (responseMessage.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        await Task.Delay(TimeSpan.FromTicks(5 + random.Next(acquireLeaseLockRefreshMaximumDelayTicks)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var sagaNotFound = responseMessage.StatusCode == HttpStatusCode.NotFound || sagaStream == null;

                    return new Tuple<bool, TSagaData>(!sagaNotFound, sagaNotFound ? default : ReadSagaFromStream<TSagaData>(context, sagaStream, responseMessage));
                }
            }

            throw new TimeoutException($"Unable to acquire exclusive write lock for saga with id '{sagaId}' within allocated time '{leaseLockTime}'.");
        }

        TSagaData ReadSagaFromStream<TSagaData>(ContextBag context, Stream sagaStream, ResponseMessage responseMessage) where TSagaData : class, IContainSagaData
        {
            using (sagaStream)
            using (var streamReader = new StreamReader(sagaStream))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                var sagaData = serializer.Deserialize<TSagaData>(jsonReader);

                context.Set($"cosmos_etag:{sagaData.Id}", responseMessage.Headers.ETag);

                return sagaData;
            }
        }

        public Task Complete(IContainSagaData sagaData, ISynchronizedStorageSession session, ContextBag context, CancellationToken cancellationToken = default)
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
        readonly bool usePessimisticsLockingMode;
        readonly TimeSpan leaseLockTime;
        readonly int acquireLeaseLockRefreshMaximumDelayTicks;
        static readonly Random random = new Random();
    }
}