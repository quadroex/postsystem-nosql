using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace PostalTracking;

public sealed class MongoStore : ITrackingStore
{
    private const string CollectionName = "shipments";
    private static readonly BsonDocument RecentSort = new()
    {
        { "updated_ms", -1 }, { "_id", 1 }
    };
    private static readonly BsonDocument RecentProjection = new("events", 0);
    private static readonly BsonDocument LatestProjection = new()
    {
        { "_id", 0 }, { "last_branch_id", 0 }, { "last_status", 0 }, { "updated_ms", 0 }, { "revision", 0 },
        { "events", new BsonDocument("$slice", -1) }
    };

    private readonly MongoClient client;
    private readonly IMongoDatabase database;
    private readonly IMongoCollection<BsonDocument> shipments;
    private IReadOnlyDictionary<int, string> branchNames = new Dictionary<int, string>();

    public MongoStore(string uri, string databaseName)
    {
        StoreRules.CheckDatabase(databaseName);
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.RetryWrites = false;
        settings.RetryReads = false;
        settings.WriteConcern = WriteConcern.W1.With(journal: true);
        settings.MinConnectionPoolSize = Math.Min(settings.MaxConnectionPoolSize, Math.Max(settings.MinConnectionPoolSize, 8));
        client = new MongoClient(settings);
        database = client.GetDatabase(databaseName);
        shipments = database.GetCollection<BsonDocument>(CollectionName);
    }

    public string Name => "MongoDB";

    public async Task ResetAsync()
    {
        await database.DropCollectionAsync(CollectionName);
        await database.CreateCollectionAsync(CollectionName);
        branchNames = new Dictionary<int, string>();
    }

    public async Task LoadAsync(Branch[] branches, IEnumerable<Shipment> fixture)
    {
        var names = StoreRules.BranchNames(branches);
        foreach (var batch in fixture.Chunk(1_000))
        {
            var documents = new BsonDocument[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                StoreRules.CheckShipment(batch[i], names);
                documents[i] = ShipmentDocument(batch[i]);
            }
            await shipments.InsertManyAsync(documents);
        }
        var index = Builders<BsonDocument>.IndexKeys.Ascending("last_branch_id").Descending("updated_ms").Ascending("_id");
        await shipments.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index,
            new CreateIndexOptions { Name = "shipments_branch_updated_idx" }));
        branchNames = names;
    }

    public async Task<ITrackingSession> OpenAsync()
    {
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        return new Session(shipments, branchNames);
    }

    public async Task<StoreInfo> InfoAsync()
    {
        var build = await database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("fsync", 1));
        var stats = await database.RunCommandAsync<BsonDocument>(new BsonDocument("collStats", CollectionName));
        var group = new BsonDocument("$group", new BsonDocument
        {
            { "_id", BsonNull.Value }, { "shipments", new BsonDocument("$sum", 1) },
            { "events", new BsonDocument("$sum", new BsonDocument("$size", "$events")) }
        });
        var counts = await shipments.Aggregate<BsonDocument>(new[] { group }).FirstOrDefaultAsync();
        long bytes = stats["storageSize"].ToInt64() + stats["totalIndexSize"].ToInt64();
        return new StoreInfo(build["version"].AsString, counts?["shipments"].ToInt64() ?? 0,
            counts?["events"].ToInt64() ?? 0, bytes, "w=1; journal=true; retryWrites=false");
    }

    public async Task<string> PlansAsync(int id, int branchId)
    {
        var result = new StringBuilder();
        await ExplainAsync(result, "Timeline", new BsonDocument("_id", id), 1);
        await ExplainAsync(result, "Latest", new BsonDocument("_id", id), 1, LatestProjection);
        await ExplainAsync(result, "Recent", new BsonDocument("last_branch_id", branchId), 20, RecentProjection, RecentSort);
        return result.ToString();
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ExplainAsync(StringBuilder result, string title, BsonDocument filter, int limit,
        BsonDocument? projection = null, BsonDocument? sort = null)
    {
        var find = new BsonDocument
        {
            { "find", CollectionName }, { "filter", filter }, { "limit", limit }
        };
        if (projection is not null) find.Add("projection", projection);
        if (sort is not null) find.Add("sort", sort);
        var plan = await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "explain", find }, { "verbosity", "executionStats" }
        });
        result.AppendLine(title).AppendLine(plan.ToJson(new JsonWriterSettings { Indent = true })).AppendLine();
    }

    private static BsonDocument ShipmentDocument(Shipment shipment) => new()
    {
        { "_id", shipment.Id }, { "last_branch_id", shipment.LastBranchId }, { "last_status", shipment.LastStatus },
        { "updated_ms", shipment.UpdatedMs }, { "revision", shipment.Revision },
        { "events", new BsonArray(shipment.Events.Select(ScanDocument)) }
    };

    private static BsonDocument ScanDocument(Scan scan) => new()
    {
        { "sequence", scan.Sequence }, { "branch_id", scan.BranchId }, { "branch_name", scan.BranchName },
        { "status", scan.Status }, { "occurred_ms", scan.OccurredMs }
    };

    private static Scan ReadScan(BsonValue value)
    {
        var scan = value.AsBsonDocument;
        return new Scan(scan["sequence"].AsInt32, scan["branch_id"].AsInt32, scan["branch_name"].AsString,
            scan["status"].AsString, scan["occurred_ms"].AsInt64);
    }

    private static Summary ReadSummary(BsonDocument document) => new(document["_id"].AsInt32,
        document["last_branch_id"].AsInt32, document["last_status"].AsString,
        document["updated_ms"].AsInt64, document["revision"].AsInt32);

    private sealed class Session(IMongoCollection<BsonDocument> shipments,
        IReadOnlyDictionary<int, string> branchNames) : ITrackingSession
    {
        public async Task<Shipment?> TimelineAsync(int id)
        {
            if (id <= 0) return null;
            var document = await shipments.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();
            if (document is null) return null;
            var summary = ReadSummary(document);
            var scans = document["events"].AsBsonArray.Select(ReadScan).ToArray();
            return new Shipment(summary.Id, summary.LastBranchId, summary.LastStatus, summary.UpdatedMs, summary.Revision, scans);
        }

        public async Task<Scan?> LatestAsync(int id)
        {
            if (id <= 0) return null;
            var document = await shipments.Find(new BsonDocument("_id", id))
                .Project(LatestProjection).FirstOrDefaultAsync();
            if (document is null) return null;
            var scans = document["events"].AsBsonArray;
            return scans.Count == 0 ? null : ReadScan(scans[0]);
        }

        public async Task<Summary[]> RecentAsync(int branchId, int limit)
        {
            if (branchId <= 0 || limit <= 0) return [];
            var rows = await shipments.Find(new BsonDocument("last_branch_id", branchId), new FindOptions { BatchSize = limit })
                .Sort(RecentSort).Limit(limit).Project(RecentProjection).ToListAsync();
            return rows.Select(ReadSummary).ToArray();
        }

        public async Task<bool> AppendAsync(int id, int expectedRevision, Scan scan)
        {
            if (!StoreRules.CanAppend(id, expectedRevision, scan) || !StoreRules.MatchesBranch(scan, branchNames)) return false;
            var filter = new BsonDocument
            {
                { "_id", id }, { "revision", expectedRevision }, { "updated_ms", new BsonDocument("$lt", scan.OccurredMs) }
            };
            var update = new BsonDocument
            {
                { "$inc", new BsonDocument("revision", 1) }, { "$push", new BsonDocument("events", ScanDocument(scan)) },
                { "$set", new BsonDocument
                    {
                        { "last_branch_id", scan.BranchId }, { "last_status", scan.Status }, { "updated_ms", scan.OccurredMs }
                    }
                }
            };
            var result = await shipments.UpdateOneAsync(filter, update);
            return result.ModifiedCount == 1;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
