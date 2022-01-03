﻿namespace NServiceBus.Persistence.CosmosDB
{
    using System;
    using System.Net;
    using Extensibility;
    using Microsoft.Azure.Cosmos;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    abstract class Operation : IDisposable
    {
        protected Operation(PartitionKey partitionKey, JsonSerializer serializer, ContextBag context)
        {
            PartitionKey = partitionKey;
            Serializer = serializer;
            Context = context;
        }

        public ContextBag Context { get; }
        public PartitionKey PartitionKey { get; }
        public JsonSerializer Serializer { get; }

        public virtual void Success(TransactionalBatchOperationResult result)
        {
        }

        public virtual void Conflict(TransactionalBatchOperationResult result)
        {
            if ((int)result.StatusCode == 424) // HttpStatusCode.FailedDependency:
            {
                return;
            }

#pragma warning disable IDE0010 // We have the default case and don't need to list 300 HTTP codes
            switch (result.StatusCode)
            {
                case HttpStatusCode.BadRequest:
                    throw new TransactionalBatchOperationException("Bad request. Likely the partition key did not match.", result);
                case HttpStatusCode.Conflict:
                case HttpStatusCode.PreconditionFailed:
                    throw new TransactionalBatchOperationException("Concurrency conflict.", result);
                default:
                    throw new TransactionalBatchOperationException(result);
            }
#pragma warning restore IDE0010
        }

        public abstract void Apply(TransactionalBatch transactionalBatch, PartitionKeyPath partitionKeyPath);
        public virtual void Dispose() { }

        protected void EnrichWithPartitionKeyIfNecessary(JObject toBeEnriched, PartitionKeyPath partitionKeyPath)
        {
            var partitionKeyAsJArray = JArray.Parse(PartitionKey.ToString())[0];
            // we should probably optimize this a bit and the result might be cacheable but let's worry later
            var pathToMatch = partitionKeyPath.ToString().Replace("/", ".");
            var segments = pathToMatch.Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);

            var start = new JObject();
            var current = start;
            for (var i = 0; i < segments.Length; i++)
            {
                var segmentName = segments[i];

                if (i == segments.Length - 1)
                {
                    current[segmentName] = partitionKeyAsJArray;
                    continue;
                }

                current[segmentName] = new JObject();
                current = (JObject)current[segmentName];
            }

            // promote it if not there, what if the user has it and the key doesn't match?
            var matchToken = toBeEnriched.SelectToken(pathToMatch);
            if (matchToken == null)
            {
                toBeEnriched.Merge(start);
            }
        }
    }

    class ThrowOnConflictOperation : Operation
    {
        ThrowOnConflictOperation() : base(PartitionKey.Null, null, null)
        {
        }

        public static Operation Instance { get; } = new ThrowOnConflictOperation();

        public override void Apply(TransactionalBatch transactionalBatch, PartitionKeyPath partitionKeyPath)
        {
        }
    }
}