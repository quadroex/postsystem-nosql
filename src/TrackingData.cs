namespace PostalTracking;

public sealed record Branch(int Id, string Name);
public sealed record Scan(int Sequence, int BranchId, string BranchName, string Status, long OccurredMs);
public sealed record Shipment(int Id, int LastBranchId, string LastStatus, long UpdatedMs, int Revision, Scan[] Events);
public sealed record Summary(int Id, int LastBranchId, string LastStatus, long UpdatedMs, int Revision);
public sealed record StoreInfo(string Version, long Shipments, long Events, long Bytes, string Durability);

public interface ITrackingStore : IAsyncDisposable
{
    string Name { get; }
    Task ResetAsync();
    Task LoadAsync(Branch[] branches, IEnumerable<Shipment> shipments);
    Task<ITrackingSession> OpenAsync();
    Task<StoreInfo> InfoAsync();
    Task<string> PlansAsync(int id, int branchId);
}

public interface ITrackingSession : IAsyncDisposable
{
    Task<Shipment?> TimelineAsync(int id);
    Task<Scan?> LatestAsync(int id);
    Task<Summary[]> RecentAsync(int branchId, int limit);
    Task<bool> AppendAsync(int id, int expectedRevision, Scan scan);
}

internal static class TrackingData
{
    public const int BranchCount = 32;
    public const int RecentLimit = 20;
    public const long EpochMs = 1_767_225_600_000;
    private static readonly Branch[] Branches = Enumerable.Range(1, BranchCount)
        .Select(id => new Branch(id, FormattableString.Invariant($"Branch {id:00}"))).ToArray();

    public static Branch[] CreateBranches() => [.. Branches];

    public static IEnumerable<Shipment> CreateShipments(int count)
    {
        for (var id = 1; id <= count; id++)
            yield return GetShipment(id);
    }

    public static int EventCount(int id) => 8 + (int)(Mix((uint)id ^ 0xDC39A14BU) % 13);

    public static long TotalEvents(int size)
    {
        var total = 0L;
        for (var id = 1; id <= size; id++)
            total += EventCount(id);
        return total;
    }

    public static Shipment GetShipment(int id, Scan? append = null)
    {
        var count = EventCount(id);
        var events = new Scan[count + (append is null ? 0 : 1)];
        for (var sequence = 1; sequence <= count; sequence++)
            events[sequence - 1] = GetScan(id, sequence, count);
        if (append is not null)
            events[^1] = append;
        var last = events[^1];
        return new Shipment(id, last.BranchId, last.Status, last.OccurredMs, last.Sequence, events);
    }

    public static Summary GetSummary(int id, Scan? append = null)
    {
        var count = EventCount(id);
        var last = append ?? GetScan(id, count, count);
        return new Summary(id, last.BranchId, last.Status, last.OccurredMs, last.Sequence);
    }

    public static bool CanAppend(int id) => !EndsDelivered(id);

    public static Scan NextScan(int id, int size, int ordinal)
    {
        var sequence = EventCount(id) + 1;
        var branch = GetBranch(Mix((uint)id ^ 0xEEA50B13U));
        var previous = GetSummary(id);
        var status = previous.LastStatus == "arrived" ? "in_transit" : "arrived";
        return new Scan(sequence, branch.Id, branch.Name, status,
            EpochMs + (size + 1L) * 25_000 + ordinal * 1_000L);
    }

    public static uint Mix(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7FEB352DU;
            value ^= value >> 15;
            value *= 0x846CA68BU;
            return value ^ (value >> 16);
        }
    }

    private static bool EndsDelivered(int id) => Mix((uint)id ^ 0xB05CA337U) % 5 == 0;

    private static Scan GetScan(int id, int sequence, int count)
    {
        var branch = GetBranch(Mix((uint)id ^ (uint)sequence * 0x9E3779B9U));
        var status = sequence == 1 ? "accepted" : sequence == count && EndsDelivered(id) ? "delivered"
            : sequence % 2 == 0 ? "in_transit" : "arrived";
        return new Scan(sequence, branch.Id, branch.Name, status, EpochMs + id * 25_000L + sequence * 700L);
    }

    private static Branch GetBranch(uint key) => key % 100 < 40 ? Branches[0] : Branches[1 + (key >> 8) % (BranchCount - 1)];
}
