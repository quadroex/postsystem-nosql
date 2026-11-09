using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace PostalTracking;

public sealed class PostgresStore : ITrackingStore
{
    private const string TimelineSql = """
        SELECT s.id, s.last_branch_id, s.last_status, s.updated_ms, s.revision,
            e.sequence, e.branch_id, b.name, e.status, e.occurred_ms
        FROM tracking.shipments s
        JOIN tracking.tracking_events e ON e.shipment_id = s.id
        JOIN tracking.branches b ON b.id = e.branch_id
        WHERE s.id = @id ORDER BY e.sequence
        """;

    private const string LatestSql = """
        SELECT e.sequence, e.branch_id, b.name, e.status, e.occurred_ms
        FROM (SELECT sequence, branch_id, status, occurred_ms FROM tracking.tracking_events
              WHERE shipment_id = @id ORDER BY sequence DESC LIMIT 1) e
        JOIN tracking.branches b ON b.id = e.branch_id
        """;

    private const string RecentSql = """
        SELECT id, last_branch_id, last_status, updated_ms, revision
        FROM tracking.shipments WHERE last_branch_id = @branch
        ORDER BY updated_ms DESC, id ASC LIMIT @limit
        """;

    private const string AppendSql = """
        WITH moved AS (
            UPDATE tracking.shipments AS s
            SET last_branch_id = b.id, last_status = @status, updated_ms = @occurred,
                revision = s.revision + 1
            FROM tracking.branches b
            WHERE s.id = @id AND s.revision = @expected AND s.revision < 64
                AND @sequence = s.revision + 1 AND @occurred > s.updated_ms
                AND b.id = @branch AND b.name = @branch_name
            RETURNING s.id
        )
        INSERT INTO tracking.tracking_events (shipment_id, sequence, branch_id, status, occurred_ms)
        SELECT id, @sequence, @branch, @status, @occurred FROM moved
        """;

    private readonly NpgsqlDataSource source;

    public PostgresStore(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        StoreRules.CheckDatabase(settings.Database);
        settings.MaxAutoPrepare = 8;
        settings.AutoPrepareMinUsages = 2;
        source = NpgsqlDataSource.Create(settings.ConnectionString);
    }

    public string Name => "PostgreSQL";

    public async Task ResetAsync()
    {
        string schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "schema.sql"));
        await using var connection = await ConnectAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS tracking CASCADE;\n" + schema,
            connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task LoadAsync(Branch[] branches, IEnumerable<Shipment> shipments)
    {
        var names = StoreRules.BranchNames(branches);
        await using var connection = await ConnectAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var copy = await connection.BeginBinaryImportAsync(
            "COPY tracking.branches (id, name) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var branch in branches)
            {
                await copy.StartRowAsync();
                await copy.WriteAsync(branch.Id, NpgsqlDbType.Integer);
                await copy.WriteAsync(branch.Name, NpgsqlDbType.Text);
            }
            await copy.CompleteAsync();
        }
        foreach (var batch in shipments.Chunk(2_000))
        {
            foreach (var shipment in batch) StoreRules.CheckShipment(shipment, names);
            await using (var copy = await connection.BeginBinaryImportAsync("""
                COPY tracking.shipments (id, last_branch_id, last_status, updated_ms, revision)
                FROM STDIN (FORMAT BINARY)
                """))
            {
                foreach (var shipment in batch)
                {
                    await copy.StartRowAsync();
                    await copy.WriteAsync(shipment.Id, NpgsqlDbType.Integer);
                    await copy.WriteAsync(shipment.LastBranchId, NpgsqlDbType.Integer);
                    await copy.WriteAsync(shipment.LastStatus, NpgsqlDbType.Text);
                    await copy.WriteAsync(shipment.UpdatedMs, NpgsqlDbType.Bigint);
                    await copy.WriteAsync(shipment.Revision, NpgsqlDbType.Integer);
                }
                await copy.CompleteAsync();
            }
            await using (var copy = await connection.BeginBinaryImportAsync("""
                COPY tracking.tracking_events (shipment_id, sequence, branch_id, status, occurred_ms)
                FROM STDIN (FORMAT BINARY)
                """))
            {
                foreach (var shipment in batch)
                    foreach (var scan in shipment.Events)
                    {
                        await copy.StartRowAsync();
                        await copy.WriteAsync(shipment.Id, NpgsqlDbType.Integer);
                        await copy.WriteAsync(scan.Sequence, NpgsqlDbType.Integer);
                        await copy.WriteAsync(scan.BranchId, NpgsqlDbType.Integer);
                        await copy.WriteAsync(scan.Status, NpgsqlDbType.Text);
                        await copy.WriteAsync(scan.OccurredMs, NpgsqlDbType.Bigint);
                    }
                await copy.CompleteAsync();
            }
        }
        await transaction.CommitAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE tracking.branches, tracking.shipments, tracking.tracking_events", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    public async Task<ITrackingSession> OpenAsync() => new Session(await ConnectAsync());

    public async Task<StoreInfo> InfoAsync()
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("""
            SELECT current_setting('server_version'),
                (SELECT count(*) FROM tracking.shipments), (SELECT count(*) FROM tracking.tracking_events),
                pg_total_relation_size('tracking.branches') + pg_total_relation_size('tracking.shipments')
                    + pg_total_relation_size('tracking.tracking_events'),
                'fsync=' || current_setting('fsync') || '; synchronous_commit=' || current_setting('synchronous_commit')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new StoreInfo(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
    }

    public async Task<string> PlansAsync(int id, int branchId)
    {
        await using var connection = await ConnectAsync();
        var result = new StringBuilder();
        await ExplainAsync(connection, result, "Timeline", TimelineSql, ("id", id));
        await ExplainAsync(connection, result, "Latest", LatestSql, ("id", id));
        await ExplainAsync(connection, result, "Recent", RecentSql, ("branch", branchId), ("limit", 20));
        return result.ToString();
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();

    private async Task<NpgsqlConnection> ConnectAsync()
    {
        var connection = await source.OpenConnectionAsync();
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('fsync') || ':' || set_config('synchronous_commit', 'on', false)
                """, connection);
            if ((string?)await command.ExecuteScalarAsync() != "on:on")
                throw new InvalidOperationException("PostgreSQL requires fsync=on and synchronous_commit=on.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExplainAsync(NpgsqlConnection connection, StringBuilder result,
        string title, string sql, params (string Name, int Value)[] parameters)
    {
        result.AppendLine(title);
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) " + sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.AppendLine(reader.GetString(0));
        result.AppendLine();
    }

    private sealed class Session(NpgsqlConnection connection) : ITrackingSession
    {
        public async Task<Shipment?> TimelineAsync(int id)
        {
            if (id <= 0) return null;
            await using var command = new NpgsqlCommand(TimelineSql, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            var branch = reader.GetInt32(1);
            var status = reader.GetString(2);
            var updated = reader.GetInt64(3);
            var revision = reader.GetInt32(4);
            var scans = new List<Scan>(revision);
            do
            {
                scans.Add(new Scan(reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9)));
            } while (await reader.ReadAsync());
            return new Shipment(id, branch, status, updated, revision, scans.ToArray());
        }

        public async Task<Scan?> LatestAsync(int id)
        {
            if (id <= 0) return null;
            await using var command = new NpgsqlCommand(LatestSql, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new Scan(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
        }

        public async Task<Summary[]> RecentAsync(int branchId, int limit)
        {
            if (branchId <= 0 || limit <= 0) return [];
            await using var command = new NpgsqlCommand(RecentSql, connection);
            command.Parameters.AddWithValue("branch", branchId);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Summary>();
            while (await reader.ReadAsync())
                rows.Add(new Summary(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4)));
            return rows.ToArray();
        }

        public async Task<bool> AppendAsync(int id, int expectedRevision, Scan scan)
        {
            if (!StoreRules.CanAppend(id, expectedRevision, scan)) return false;
            await using var command = new NpgsqlCommand(AppendSql, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("expected", expectedRevision);
            command.Parameters.AddWithValue("sequence", scan.Sequence);
            command.Parameters.AddWithValue("branch", scan.BranchId);
            command.Parameters.AddWithValue("branch_name", scan.BranchName);
            command.Parameters.AddWithValue("status", scan.Status);
            command.Parameters.AddWithValue("occurred", scan.OccurredMs);
            return await command.ExecuteNonQueryAsync() == 1;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

internal static class StoreRules
{
    internal const string DatabaseName = "postsystem_nosql";
    internal const int EventLimit = 64;

    internal static void CheckDatabase(string? name)
    {
        if (name != DatabaseName) throw new ArgumentException($"The benchmark requires the {DatabaseName} database.");
    }

    internal static Dictionary<int, string> BranchNames(IEnumerable<Branch> branches)
    {
        var names = new Dictionary<int, string>();
        foreach (var branch in branches)
        {
            if (branch.Id <= 0 || string.IsNullOrWhiteSpace(branch.Name) || !names.TryAdd(branch.Id, branch.Name))
                throw new ArgumentException("The branch fixture is invalid.");
        }
        return names;
    }

    internal static bool CanAppend(int id, int revision, Scan scan) =>
        id > 0 && revision is >= 1 and < EventLimit && scan.Sequence == revision + 1 && ValidScan(scan);

    internal static bool MatchesBranch(Scan scan, IReadOnlyDictionary<int, string> names) =>
        names.TryGetValue(scan.BranchId, out var name) && name == scan.BranchName;

    internal static void CheckShipment(Shipment shipment, IReadOnlyDictionary<int, string> names)
    {
        if (shipment.Id <= 0 || shipment.Revision is < 1 or > EventLimit || shipment.Revision != shipment.Events.Length)
            throw new ArgumentException($"Invalid shipment fixture {shipment.Id}.");
        long previous = -1;
        for (int i = 0; i < shipment.Events.Length; i++)
        {
            var scan = shipment.Events[i];
            if (scan.Sequence != i + 1 || scan.OccurredMs <= previous || !ValidScan(scan) || !MatchesBranch(scan, names))
                throw new ArgumentException($"Invalid event fixture for shipment {shipment.Id}.");
            previous = scan.OccurredMs;
        }
        var latest = shipment.Events[^1];
        if (shipment.LastBranchId != latest.BranchId || shipment.LastStatus != latest.Status || shipment.UpdatedMs != latest.OccurredMs)
            throw new ArgumentException($"Invalid shipment summary fixture {shipment.Id}.");
    }

    private static bool ValidScan(Scan scan) => scan.BranchId > 0 && scan.OccurredMs >= 0
        && !string.IsNullOrWhiteSpace(scan.BranchName) && !string.IsNullOrWhiteSpace(scan.Status)
        && scan.Status.Length <= 32 && scan.Status == scan.Status.Trim();
}
