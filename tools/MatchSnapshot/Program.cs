using DeadlockAdvisor.Services;

// MatchSnapshot --restore <last run's files> --out <folder to publish>
// Exits non-zero on any failure, so the workflow publishes nothing.

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

var output = Option("--out");
if (output is null)
{
    Console.Error.WriteLine("Usage: MatchSnapshot [--restore <dir>] --out <dir>");
    return 2;
}

var work = Directory.CreateTempSubdirectory("match-snapshot-");
try
{
    using var api = new DeadlockApi();
    var snapshot = await new MatchSnapshotJob(api, Console.Out).RunAsync(work.FullName, Option("--restore"), output);
    foreach (var patch in snapshot.Patches)
        Console.WriteLine($"{patch.File}: {patch.Bytes / 1000} KB{(patch.Ranks ? ", with rank groups" : "")}{(patch.Complete ? ", complete" : "")}");
    Console.WriteLine($"{api.BytesReceived / 1000} KB received from deadlock-api.com.");
    return 0;
}
finally
{
    work.Delete(recursive: true);
}
