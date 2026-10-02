using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DeadlockAdvisor.Services;

// dotnet run --project tools/PublishModel -- [--from <data folder>] [--commit] [--push]
//
// Copies the hero ratings, item formulas and the game data they go with from your data folder (the
// app's, unless --from names another) into src/Assets/SeedData and dates model.json today. --commit
// commits just the seed; --push pushes it too, which publishes it to every install.

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

var push = args.Contains("--push");
var commit = push || args.Contains("--commit");

var repo = new DirectoryInfo(Directory.GetCurrentDirectory());
while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "DeadlockAdvisor.sln")))
    repo = repo.Parent;
if (repo is null)
{
    Console.Error.WriteLine("Run this from inside the DeadlockAdvisor repository.");
    return 2;
}
var seedDir = Path.Combine(repo.FullName, "src", "Assets", "SeedData");
var dataDir = Option("--from") ?? AppDataDir();
Console.WriteLine($"Publishing from {dataDir}");

ModelPublished result;
try
{
    result = ModelPublisher.Publish(dataDir, seedDir, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

foreach (var file in result.Files)
    Console.WriteLine($"  {ModelManifest.Title(file)} ({file})");
if (result.MatchData)
    Console.WriteLine("  Match data, as the starting point for new installs");
if (result.MatchDataSkipped is { } why)
    Console.WriteLine($"  Match data left as it was: {why}.");
if (!result.Changed)
{
    Console.WriteLine("Nothing to publish: the seed already matches.");
    return 0;
}
if (!commit)
{
    Console.WriteLine("Copied into src/Assets/SeedData. Review it with git diff, then commit and push, or run again with --push.");
    return 0;
}

var what = string.Join(", ", result.Files.Select(ModelManifest.Title).Concat(result.MatchData ? ["match data"] : []));
var message = $"Publish the formulas: {what}";
if (!Git(repo.FullName, "add", "--", "src/Assets/SeedData") || !Git(repo.FullName, "commit", "-m", message, "--", "src/Assets/SeedData"))
    return 1;
if (push && !Git(repo.FullName, "push"))
    return 1;
Console.WriteLine(push ? "Pushed: installs take it at their next startup." : "Committed. Push to publish it.");
return 0;

// The app's data folder: the one Settings → Data names, else the default.
static string AppDataDir()
{
    var settings = Path.Combine(JsonSettingsService.AppDataPath, "settings.json");
    if (File.Exists(settings))
    {
        using var json = JsonDocument.Parse(File.ReadAllText(settings));
        if (json.RootElement.TryGetProperty("DataRoot", out var root) && root.GetString() is { Length: > 0 } dataRoot)
            return Path.Combine(dataRoot, "data");
    }
    return Path.Combine(JsonSettingsService.AppDataPath, "data");
}

static bool Git(string repo, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var git = Process.Start(start)!;
    git.WaitForExit();
    return git.ExitCode == 0;
}
