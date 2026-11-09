using System.Text;
using PostalTracking;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("Usage: dotnet run --project src -c Release -- verify|quick|benchmark|show [output-directory]");
    Console.WriteLine("verify checks the two stores on 512 shipments.");
    Console.WriteLine("quick runs one benchmark round on 1,000 shipments.");
    Console.WriteLine("benchmark runs three rounds on 10,000 and 100,000 shipments.");
    Console.WriteLine("show compares a shipment and its branch results in the loaded stores without resetting them.");
    Console.WriteLine("verify, quick and benchmark reset the tracking data in the dedicated databases.");
    Console.WriteLine("Set POSTAL_PG, POSTAL_MONGO and POSTAL_MONGO_DB for dedicated benchmark databases.");
    return 0;
}

if (args.Length > 2 || args[0] is not ("verify" or "quick" or "benchmark" or "show"))
{
    Console.Error.WriteLine("Invalid arguments. Run with --help.");
    return 1;
}

try
{
    var postgres = Required("POSTAL_PG");
    var mongo = Required("POSTAL_MONGO");
    var mongoDatabase = Required("POSTAL_MONGO_DB");
    var mode = args[0];
    var output = Path.GetFullPath(args.Length == 2 ? args[1] : Path.Combine("results", mode));

    await using var pgStore = new PostgresStore(postgres);
    await using var mongoStore = new MongoStore(mongo, mongoDatabase);
    ITrackingStore[] stores = [pgStore, mongoStore];

    Directory.CreateDirectory(output);
    if (mode == "verify")
        await Correctness.RunAsync(stores, output);
    else if (mode == "show")
        await Correctness.ShowAsync(stores, output);
    else
        await Benchmark.RunAsync(stores, mode == "quick" ? BenchmarkSettings.Quick : BenchmarkSettings.Full, output);

    Console.WriteLine($"Results: {output}");
    return 0;
}
catch (Exception error)
{
    var message = error.Message;
    foreach (var variable in new[] { "POSTAL_PG", "POSTAL_MONGO" })
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(value))
            message = message.Replace(value, "[redacted]", StringComparison.Ordinal);
    }
    Console.Error.WriteLine($"{error.GetType().Name}: {message}");
    return 1;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value &&
    !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"Set {name} before running this command.");
