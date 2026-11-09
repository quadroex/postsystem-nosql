using System.Text.Json;

namespace PostalTracking;

internal sealed record CheckedStore(string Engine, StoreInfo Store);
internal sealed record StateCheck(int ShipmentSamples, int Branches, CheckedStore[] Stores);

internal static class Correctness
{
    public static async Task ShowAsync(ITrackingStore[] stores, string output)
    {
        const int id = 1;
        var snapshots = new List<LiveSample>();
        foreach (var store in stores)
        {
            var info = await store.InfoAsync();
            await using var session = await store.OpenAsync();
            var timeline = await session.TimelineAsync(id) ?? throw new InvalidOperationException($"{store.Name} has no shipment {id}.");
            var latest = await session.LatestAsync(id) ?? throw new InvalidOperationException($"{store.Name} has no latest scan for shipment {id}.");
            var recent = await session.RecentAsync(timeline.LastBranchId, TrackingData.RecentLimit);
            Require(timeline.Events.Length > 0 && latest == timeline.Events[^1], store, "latest scan must match the timeline");
            snapshots.Add(new LiveSample(store.Name, info, timeline, latest, recent));
            Console.WriteLine($"{store.Name}: {info.Shipments} shipments, {info.Events} events");
        }

        var first = snapshots[0];
        foreach (var sample in snapshots.Skip(1))
        {
            if (sample.Store.Shipments != first.Store.Shipments || sample.Store.Events != first.Store.Events ||
                sample.Timeline.Id != first.Timeline.Id || sample.Timeline.LastBranchId != first.Timeline.LastBranchId ||
                sample.Timeline.LastStatus != first.Timeline.LastStatus || sample.Timeline.UpdatedMs != first.Timeline.UpdatedMs ||
                sample.Timeline.Revision != first.Timeline.Revision || !sample.Timeline.Events.SequenceEqual(first.Timeline.Events) ||
                sample.Latest != first.Latest || !sample.Recent.SequenceEqual(first.Recent))
                throw new InvalidOperationException($"The live sample differs in {sample.Engine}.");
        }

        await Results.WriteJsonAsync(Path.Combine(output, "sample.json"), new
        {
            CheckedUtc = DateTime.UtcNow,
            ShipmentId = id,
            BranchId = first.Timeline.LastBranchId,
            Equivalent = true,
            Samples = snapshots
        });
        Console.WriteLine($"PASS shipment {id}: {first.Timeline.Events.Length} scans, latest status {first.Latest.Status}, {first.Recent.Length} recent results for branch {first.Timeline.LastBranchId}");
    }

    public static async Task RunAsync(ITrackingStore[] stores, string output)
    {
        const int size = 512;
        foreach (var store in stores)
        {
            await store.ResetAsync();
            await store.LoadAsync(TrackingData.CreateBranches(), TrackingData.CreateShipments(size));
        }

        var appends = new Dictionary<int, Scan>();
        var before = await CheckStateAsync(stores, size, appends);
        Console.WriteLine("PASS initial counts, timeline, latest scan and recent branch results");
        var targetId = Enumerable.Range(1, size).First(TrackingData.CanAppend);
        var raceId = Enumerable.Range(targetId + 1, size - targetId).First(TrackingData.CanAppend);
        var target = TrackingData.GetShipment(targetId);
        var scan = TrackingData.NextScan(targetId, size, 1);
        var race = TrackingData.NextScan(raceId, size, 2);

        foreach (var store in stores)
        {
            await using var first = await store.OpenAsync();
            await using var second = await store.OpenAsync();
            var missing = size + 99;
            Require(await first.TimelineAsync(missing) is null, store, "timeline for a missing shipment");
            Require(await first.LatestAsync(missing) is null, store, "latest scan for a missing shipment");
            Require((await first.RecentAsync(TrackingData.BranchCount + 1, TrackingData.RecentLimit)).Length == 0,
                store, "recent results for an unknown branch");
            Require(!await first.AppendAsync(missing, target.Revision, scan), store, "append to a missing shipment");
            Require(!await first.AppendAsync(targetId, target.Revision, scan with { Sequence = scan.Sequence + 1 }),
                store, "append with an invalid sequence");
            Require(!await first.AppendAsync(targetId, target.Revision, scan with { OccurredMs = target.UpdatedMs }),
                store, "append with a non-increasing timestamp");
            Require(!await first.AppendAsync(targetId, target.Revision, scan with { BranchId = TrackingData.BranchCount + 1 }),
                store, "append with an unknown branch");
            Require(!await first.AppendAsync(targetId, target.Revision, scan with { BranchName = "Unknown branch" }),
                store, "append with an inconsistent branch name");
            Require(!await first.AppendAsync(targetId, target.Revision, scan with { Status = " " }),
                store, "append with an invalid status");
            Require(await first.AppendAsync(targetId, target.Revision, scan), store, "valid append");
            Require(!await second.AppendAsync(targetId, target.Revision, scan), store, "stale revision rejection");

            var raceRevision = TrackingData.EventCount(raceId);
            var competing = await Task.WhenAll(first.AppendAsync(raceId, raceRevision, race), second.AppendAsync(raceId, raceRevision, race));
            Require(competing.Count(result => result) == 1, store, "exactly one concurrent append must succeed");
        }

        Console.WriteLine("PASS missing shipment, unknown branch, invalid append and stale revision checks");
        Console.WriteLine("PASS one valid append and one winner among two concurrent appends");
        appends.Add(targetId, scan);
        appends.Add(raceId, race);
        var after = await CheckStateAsync(stores, size, appends);
        Console.WriteLine("PASS post-write event counts, timeline, latest scan and recent branch results");

        await SavePlansAsync(stores, size, output);
        await Results.WriteJsonAsync(Path.Combine(output, "verify.json"), new
        {
            VerifiedUtc = DateTime.UtcNow,
            Shipments = size,
            Branches = TrackingData.BranchCount,
            Before = before,
            After = after,
            Cases = new[] { "initial counts", "timeline", "latest scan", "recent branch results", "missing shipment",
                "unknown branch", "invalid sequence", "non-increasing timestamp", "invalid branch ID", "invalid branch name",
                "invalid status", "valid append", "stale revision", "concurrent append", "post-write event counts" }
        });
    }

    public static async Task<StateCheck> CheckStateAsync(ITrackingStore[] stores, int size, IReadOnlyDictionary<int, Scan> appends)
    {
        var expectedEventCount = TrackingData.TotalEvents(size) + appends.Count;
        var samples = new SortedSet<int> { 1, size };
        for (uint i = 0; i < 64; i++)
            samples.Add(1 + (int)(TrackingData.Mix(i ^ 0x2A9B6C45U) % (uint)size));
        foreach (var id in appends.Keys.Order().Take(32).Concat(appends.Keys.OrderDescending().Take(32)))
            samples.Add(id);

        var expectedShipments = samples.Select(id => TrackingData.GetShipment(id, appends.GetValueOrDefault(id))).ToArray();
        var branches = Enumerable.Range(0, TrackingData.BranchCount + 1).Select(_ => new List<Summary>()).ToArray();
        for (var id = 1; id <= size; id++)
        {
            var shipment = TrackingData.GetSummary(id, appends.GetValueOrDefault(id));
            branches[shipment.LastBranchId].Add(shipment);
        }
        var expectedRecent = branches.Select(group => group.OrderByDescending(s => s.UpdatedMs).ThenBy(s => s.Id)
            .Take(TrackingData.RecentLimit).ToArray()).ToArray();

        var reports = new List<CheckedStore>();
        foreach (var store in stores)
        {
            var info = await store.InfoAsync();
            Require(info.Shipments == size && info.Events == expectedEventCount, store,
                $"expected {size} shipments and {expectedEventCount} events; got {info.Shipments} and {info.Events}");
            Require(info.Bytes > 0, store, "storage size must be positive");
            await using var session = await store.OpenAsync();
            foreach (var expected in expectedShipments)
            {
                var timeline = await session.TimelineAsync(expected.Id);
                Require(timeline is not null && timeline.Id == expected.Id && timeline.LastBranchId == expected.LastBranchId &&
                    timeline.LastStatus == expected.LastStatus && timeline.UpdatedMs == expected.UpdatedMs &&
                    timeline.Revision == expected.Revision && timeline.Events.SequenceEqual(expected.Events), store, $"timeline {expected.Id}");
                Require(await session.LatestAsync(expected.Id) == expected.Events[^1], store, $"latest scan {expected.Id}");
            }

            for (var branch = 1; branch <= TrackingData.BranchCount; branch++)
            {
                var recent = await session.RecentAsync(branch, TrackingData.RecentLimit);
                Require(recent.SequenceEqual(expectedRecent[branch]), store, $"recent results for branch {branch}");
            }
            reports.Add(new CheckedStore(store.Name, info));
        }

        return new StateCheck(samples.Count, TrackingData.BranchCount, reports.ToArray());
    }

    public static async Task SavePlansAsync(ITrackingStore[] stores, int size, string output)
    {
        foreach (var store in stores)
        {
            var plan = await store.PlansAsync(size / 2, 1);
            Require(!string.IsNullOrWhiteSpace(plan), store, "query plans must be available");
            var slug = new string(store.Name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
            await File.WriteAllTextAsync(Path.Combine(output, $"plans-{size}-{slug}.txt"), plan);
        }
    }

    private static void Require(bool condition, ITrackingStore store, string check)
    {
        if (!condition)
            throw new InvalidOperationException($"{store.Name} failed: {check}.");
    }

    private sealed record LiveSample(string Engine, StoreInfo Store, Shipment Timeline, Scan Latest, Summary[] Recent);
}

internal static class Results
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }
}
