using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using DeadlockAdvisor.Services;

// dotnet run --project tools/PublishModel -- [--from <data folder>] [--note "what changed"] [--commit] [--push]
//
// Copies the hero ratings, item formulas and the game data they go with from your data folder (the
// app's, unless --from names another) into src/Assets/SeedData and dates model.json today. --note adds
// a line the update shows people. --commit commits just the seed; --push pushes it too, and once CI's
// tests pass on main, it publishes it to every install.
//
// dotnet run --project tools/PublishModel -- --assets <dir>
//
// What CI runs: lays out the seed's files as the "model" release holds them.
//
// dotnet run --project tools/PublishModel -- --add-new-heroes
//
// What CI's New heroes workflow runs: adds the heroes deadlock-api.com lists and the seed lacks, every trait
// at 0, and dates model.json. Only edits src/Assets/SeedData: committing and pushing it is the caller's.

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

var push = args.Contains("--push");
var commit = push || args.Contains("--commit");
var note = Option("--note");

var repo = new DirectoryInfo(Directory.GetCurrentDirectory());
while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "DeadlockAdvisor.sln")))
    repo = repo.Parent;
if (repo is null)
{
    Console.Error.WriteLine("Run this from inside the DeadlockAdvisor repository.");
    return 2;
}
var seedDir = Path.Combine(repo.FullName, "src", "Assets", "SeedData");

if (Option("--assets") is { } assetsDir)
{
    try
    {
        foreach (var name in ModelPublisher.WriteAssets(seedDir, assetsDir))
            Console.WriteLine(name);
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args.Contains("--add-new-heroes"))
{
    try
    {
        using var api = new DeadlockApi();
        var added = await ModelPublisher.AddNewHeroesAsync(new GameApiService(api), seedDir, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Console.WriteLine(added.Added.Count > 0
            ? $"Added: {string.Join(", ", added.Added)}"
            : "No new heroes: the seed has every hero the game lists.");
        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or InvalidOperationException or IOException)
    {
        Console.Error.WriteLine($"Couldn't add the new heroes: {ex.Message}");
        return 1;
    }
}

var dataDir = Option("--from") ?? AppDataDir();
Console.WriteLine($"Publishing from {dataDir}");

ModelPublished result;
try
{
    result = ModelPublisher.Publish(dataDir, seedDir, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), note);
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
if (result.Note is { } noted)
    Console.WriteLine($"  Note: {noted.Text}");
else if (note is not null)
    Console.WriteLine("  The note wasn't added: no formula file changed, so there's no new version to add it to.");
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
var message = $"Publish the formulas: {what}" + (result.Note is { } said ? $"\n\n{said.Text}" : "");
if (!Git(repo.FullName, "add", "--", "src/Assets/SeedData") || !Git(repo.FullName, "commit", "-m", message, "--", "src/Assets/SeedData"))
    return 1;
if (!push)
{
    Console.WriteLine("Committed. Push to publish it.");
    return 0;
}
if (!Git(repo.FullName, "push"))
    return 1;
return WaitForCi(repo.FullName);

// CI publishes the model once its tests pass (.github/workflows/ci.yml): wait for it and say how it went.
static int WaitForCi(string repo)
{
    const string actions = "https://github.com/zd4242/DeadlockAdvisor/actions";
    var sha = Output(repo, "git", "rev-parse", "HEAD");
    if (Output(repo, "gh", "--version") is null)
    {
        Console.WriteLine($"Pushed. Once CI's tests pass (about 10 minutes), installs take it at their next startup: {actions}");
        Console.WriteLine("(Install the GitHub CLI, gh, to have this wait for CI and say how it went.)");
        return 0;
    }

    Console.WriteLine("Pushed. Waiting for CI to test it and publish it, about 10 minutes. Ctrl+C stops waiting; CI carries on.");
    string? run = null;
    for (var tries = 0; tries < 24 && string.IsNullOrEmpty(run); tries++)
    {
        Thread.Sleep(TimeSpan.FromSeconds(5));
        run = Output(repo, "gh", "run", "list", "--workflow", "ci.yml", "--commit", sha!, "--json", "databaseId", "--jq", ".[0].databaseId");
    }
    if (string.IsNullOrEmpty(run))
    {
        Console.Error.WriteLine($"CI didn't start for {sha![..7]} within two minutes: check {actions}");
        return 1;
    }

    var url = $"{actions}/runs/{run}";
    Console.WriteLine(url);
    Run(repo, "gh", "run", "watch", run, "--interval", "30");
    var published = Output(repo, "gh", "run", "view", run, "--json", "jobs", "--jq", ".jobs[] | select(.name == \"publish-model\") | .conclusion");
    if (published == "success")
    {
        Console.WriteLine("Published: installs take it at their next startup.");
        return 0;
    }
    Console.Error.WriteLine(published == "skipped"
        ? $"CI's tests failed, so nothing was published. Fix them and push again: {url}"
        : $"CI didn't publish it ({published ?? "unknown"}): {url}. A newer push cancels an older run; gh workflow run ci.yml runs it again.");
    return 1;
}

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

static bool Git(string repo, params string[] arguments) => Run(repo, "git", arguments);

static bool Run(string repo, string program, params string[] arguments)
{
    using var process = Process.Start(Start(repo, program, arguments, capture: false))!;
    process.WaitForExit();
    return process.ExitCode == 0;
}

/// <summary>What a command printed, trimmed; null when it failed or isn't installed.</summary>
static string? Output(string repo, string program, params string[] arguments)
{
    try
    {
        using var process = Process.Start(Start(repo, program, arguments, capture: true))!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

static ProcessStartInfo Start(string repo, string program, string[] arguments, bool capture)
{
    var start = new ProcessStartInfo(program) { WorkingDirectory = repo, RedirectStandardOutput = capture, RedirectStandardError = capture };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    return start;
}
