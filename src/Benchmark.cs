using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PostalTracking;

internal sealed record BenchmarkSettings(string Profile, int[] Sizes, int Rounds, int ReadOps,
    int AppendOps, int ReadWarmup, int AppendWarmup)
{
    public static BenchmarkSettings Full { get; } = new("benchmark", [10_000, 100_000], 3, 1_000, 480, 64, 24);
    public static BenchmarkSettings Quick { get; } = new("quick", [1_000], 1, 120, 48, 24, 24);
}

internal enum Workload { Timeline, Latest, Recent, Append }
internal readonly record struct Request(int Key, int Revision = 0, Scan? Scan = null);
internal sealed record RunResult(string Engine, int Shipments, string Workload, int Clients, int Round,
    int EngineOrder, int Operations, double Seconds, double OperationsPerSecond, double P50Ms, double P95Ms);
internal sealed record Measurement(RunResult Result, Request[] Requests, double[] LatencyMs);
internal sealed record AggregateResult(string Engine, int Shipments, string Workload, int Clients,
    int Rounds, double MedianOperationsPerSecond, double MedianP50Ms, double MedianP95Ms);
internal sealed record LoadResult(string Engine, double Seconds);
internal sealed record DatasetResult(int Shipments, int WarmupAppends, int MeasuredAppends, LoadResult[] Loads,
    StateCheck Before, StateCheck After);

internal static class Benchmark
{
    private const int WarmupClients = 8;
    private static readonly int[] ClientCounts = [1, 8];
    private static readonly Workload[] Workloads = Enum.GetValues<Workload>();

    public static async Task RunAsync(ITrackingStore[] stores, BenchmarkSettings settings, string output)
    {
        var startedUtc = DateTime.UtcNow;
        var measurements = new List<Measurement>();
        var datasets = new List<DatasetResult>();

        for (var scale = 0; scale < settings.Sizes.Length; scale++)
        {
            var size = settings.Sizes[scale];
            var loads = new List<LoadResult>();
            foreach (var store in OrderedStores(stores, scale))
            {
                await store.ResetAsync();
                var loadStart = Stopwatch.GetTimestamp();
                await store.LoadAsync(TrackingData.CreateBranches(), TrackingData.CreateShipments(size));
                var seconds = Stopwatch.GetElapsedTime(loadStart).TotalSeconds;
                loads.Add(new LoadResult(store.Name, seconds));
                Console.WriteLine(FormattableString.Invariant($"Loaded {size} shipments into {store.Name} in {seconds:F2} s"));
            }

            var appends = new Dictionary<int, Scan>();
            var before = await Correctness.CheckStateAsync(stores, size, appends);
            await Correctness.SavePlansAsync(stores, size, output);
            var writePool = Enumerable.Range(WarmIdCount(size) + 1, size - WarmIdCount(size))
                .Where(TrackingData.CanAppend).OrderBy(id => TrackingData.Mix((uint)id ^ 0x3E1BF213U)).ToArray();
            var writeIndex = 0;
            var warmAppends = 0;
            var measuredAppends = 0;

            for (var round = 1; round <= settings.Rounds; round++)
            {
                foreach (var workload in Workloads)
                {
                    for (var clientIndex = 0; clientIndex < ClientCounts.Length; clientIndex++)
                    {
                        var clients = ClientCounts[clientIndex];
                        Request[] warmup;
                        Request[] timed;
                        if (workload == Workload.Append)
                        {
                            warmup = NextWrites(settings.AppendWarmup);
                            timed = NextWrites(settings.AppendOps);
                            warmAppends += warmup.Length;
                            measuredAppends += timed.Length;
                        }
                        else
                        {
                            warmup = ReadRequests(size, workload, round, settings.ReadWarmup, true);
                            timed = ReadRequests(size, workload, round, settings.ReadOps, false);
                        }

                        var offset = scale + round - 1 + (int)workload + clientIndex;
                        var order = 0;
                        foreach (var store in OrderedStores(stores, offset))
                        {
                            order++;
                            await RunRequestsAsync(store, workload, WarmupClients, warmup);
                            var (seconds, durations) = await RunRequestsAsync(store, workload, clients, timed);
                            var sorted = durations.Order().ToArray();
                            var result = new RunResult(store.Name, size, NameOf(workload), clients, round, order,
                                timed.Length, seconds, timed.Length / seconds, Percentile(sorted, 0.50), Percentile(sorted, 0.95));
                            measurements.Add(new Measurement(result, timed, durations));
                            Console.WriteLine(FormattableString.Invariant(
                                $"{size} {store.Name} {result.Workload} clients={clients} round={round} ops/s={result.OperationsPerSecond:F0} p50={result.P50Ms:F3} ms p95={result.P95Ms:F3} ms"));
                        }
                    }
                }
            }

            var after = await Correctness.CheckStateAsync(stores, size, appends);
            datasets.Add(new DatasetResult(size, warmAppends, measuredAppends, loads.ToArray(), before, after));
            Console.WriteLine($"PASS post-run state for {size} shipments and {appends.Count} appends per store");

            Request[] NextWrites(int count)
            {
                if (writeIndex + count > writePool.Length)
                    throw new InvalidOperationException("Not enough distinct shipment IDs for the append schedule.");
                var requests = new Request[count];
                for (var i = 0; i < requests.Length; i++)
                {
                    var id = writePool[writeIndex++];
                    var next = TrackingData.NextScan(id, size, writeIndex);
                    appends.Add(id, next);
                    requests[i] = new Request(id, TrackingData.EventCount(id), next);
                }
                return requests;
            }
        }

        var aggregate = measurements.Select(m => m.Result)
            .GroupBy(r => (r.Engine, r.Shipments, r.Workload, r.Clients))
            .Select(g => new AggregateResult(g.Key.Engine, g.Key.Shipments, g.Key.Workload, g.Key.Clients, g.Count(),
                Median(g.Select(r => r.OperationsPerSecond)), Median(g.Select(r => r.P50Ms)), Median(g.Select(r => r.P95Ms))))
            .OrderBy(r => r.Shipments).ThenBy(r => r.Workload, StringComparer.Ordinal).ThenBy(r => r.Clients)
            .ThenBy(r => r.Engine, StringComparer.Ordinal).ToArray();

        await Results.WriteJsonAsync(Path.Combine(output, "benchmark.json"), new
        {
            StartedUtc = startedUtc,
            FinishedUtc = DateTime.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            ClientOs = RuntimeInformation.OSDescription,
            ClientArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ClientProcessors = Environment.ProcessorCount,
            Settings = settings,
            Clients = ClientCounts,
            WarmupClients,
            Branches = TrackingData.BranchCount,
            InitialEventsPerShipment = new[] { 8, 20 },
            RecentLimit = TrackingData.RecentLimit,
            Schedule = "Deterministic schedules shared by both stores; workers use positions modulo client count.",
            ReadMix = "About 75% of ID lookups target a 1024-ID hot range; about 70% of recent queries target branch 1.",
            Warmup = "Separate sessions and IDs; recent warmup targets branches 29 to 32, timed recent targets 1 to 28.",
            WriteMix = "Every write has a different shipment ID, including warmup writes. Timestamps are prepared before timing.",
            Timing = "Wall time covers dispatch to completion; per-operation latency includes request, response and decoding. Sessions open outside timing.",
            Percentiles = "Nearest-rank per round. Summary columns are medians of round values.",
            Datasets = datasets,
            Runs = measurements.Select(m => m.Result).ToArray(),
            Summary = aggregate
        });
        await SaveCsvAsync(output, measurements, aggregate);
    }

    private static IEnumerable<ITrackingStore> OrderedStores(ITrackingStore[] stores, int offset)
    {
        for (var i = 0; i < stores.Length; i++)
            yield return stores[(offset + i) % stores.Length];
    }

    private static Request[] ReadRequests(int size, Workload workload, int round, int count, bool warmup)
    {
        var requests = new Request[count];
        for (var i = 0; i < count; i++)
        {
            var value = TrackingData.Mix(unchecked((uint)size ^ (uint)round * 0x9E3779B9U ^
                (uint)workload * 0x1D872B41U ^ (uint)i ^ (warmup ? 0x81D92B69U : 0U)));
            var key = workload == Workload.Recent ? BranchKey(value, warmup) : ShipmentKey(value, size, warmup);
            requests[i] = new Request(key);
        }
        return requests;
    }

    private static int BranchKey(uint value, bool warmup) => warmup ? 29 + (int)(value % 4)
        : value % 100 < 70 ? 1 : 2 + (int)((value >> 8) % 27);

    private static int ShipmentKey(uint value, int size, bool warmup)
    {
        var warmCount = WarmIdCount(size);
        if (warmup) return 1 + (int)(value % (uint)warmCount);
        var available = size - warmCount;
        var range = value % 100 < 75 ? Math.Min(1024, available) : available;
        return warmCount + 1 + (int)((value >> 8) % (uint)range);
    }

    private static int WarmIdCount(int size) => Math.Min(256, size / 4);

    private static async Task<(double Seconds, double[] Latencies)> RunRequestsAsync(ITrackingStore store,
        Workload workload, int clients, Request[] requests)
    {
        var sessions = new List<ITrackingSession>(clients);
        try
        {
            for (var i = 0; i < clients; i++) sessions.Add(await store.OpenAsync());
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var durations = new double[requests.Length];
            var workers = sessions.Select(ExecuteWorkerAsync).ToArray();
            var started = Stopwatch.GetTimestamp();
            gate.SetResult();
            await Task.WhenAll(workers);
            return (Stopwatch.GetElapsedTime(started).TotalSeconds, durations);

            async Task ExecuteWorkerAsync(ITrackingSession session, int worker)
            {
                await gate.Task;
                for (var i = worker; i < requests.Length; i += clients)
                {
                    var start = Stopwatch.GetTimestamp();
                    var success = await ExecuteAsync(session, workload, requests[i]);
                    durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (!success)
                        throw new InvalidOperationException($"{store.Name} {NameOf(workload)} failed for key {requests[i].Key}.");
                }
            }
        }
        finally
        {
            foreach (var session in sessions) await session.DisposeAsync();
        }
    }

    private static async Task<bool> ExecuteAsync(ITrackingSession session, Workload workload, Request request) => workload switch
    {
        Workload.Timeline => (await session.TimelineAsync(request.Key))?.Id == request.Key,
        Workload.Latest => await session.LatestAsync(request.Key) is not null,
        Workload.Recent => (await session.RecentAsync(request.Key, TrackingData.RecentLimit)).Length <= TrackingData.RecentLimit,
        Workload.Append => await session.AppendAsync(request.Key, request.Revision, request.Scan!),
        _ => throw new ArgumentOutOfRangeException(nameof(workload))
    };

    private static string NameOf(Workload workload) => workload.ToString().ToLowerInvariant();

    private static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private static async Task SaveCsvAsync(string output, List<Measurement> measurements, AggregateResult[] summary)
    {
        await using var runs = File.CreateText(Path.Combine(output, "results.csv"));
        await using var samples = File.CreateText(Path.Combine(output, "samples.csv"));
        await using var aggregates = File.CreateText(Path.Combine(output, "summary.csv"));
        await runs.WriteLineAsync("engine,shipments,workload,clients,round,engine_order,operations,seconds,operations_per_second,p50_ms,p95_ms");
        await samples.WriteLineAsync("engine,shipments,workload,clients,round,position,worker,key,latency_ms");
        await aggregates.WriteLineAsync("engine,shipments,workload,clients,rounds,median_operations_per_second,median_p50_ms,median_p95_ms");

        foreach (var measurement in measurements)
        {
            var r = measurement.Result;
            var prefix = FormattableString.Invariant($"{r.Engine},{r.Shipments},{r.Workload},{r.Clients},{r.Round}");
            await runs.WriteLineAsync(FormattableString.Invariant($"{prefix},{r.EngineOrder},{r.Operations},{r.Seconds:F9},{r.OperationsPerSecond:F6},{r.P50Ms:F6},{r.P95Ms:F6}"));
            for (var i = 0; i < measurement.Requests.Length; i++)
                await samples.WriteLineAsync(FormattableString.Invariant($"{prefix},{i + 1},{i % r.Clients + 1},{measurement.Requests[i].Key},{measurement.LatencyMs[i]:F6}"));
        }

        foreach (var r in summary)
            await aggregates.WriteLineAsync(FormattableString.Invariant($"{r.Engine},{r.Shipments},{r.Workload},{r.Clients},{r.Rounds},{r.MedianOperationsPerSecond:F6},{r.MedianP50Ms:F6},{r.MedianP95Ms:F6}"));
    }
}
