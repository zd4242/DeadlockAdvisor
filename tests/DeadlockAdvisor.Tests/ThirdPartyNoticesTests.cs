using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// THIRD-PARTY-NOTICES.txt, which the app carries (Help → Third-Party Notices), is made here from the packages
/// the app ships with: what its build's deps.json loads, read out of the NuGet cache. A package added, removed
/// or upgraded changes it, so it's regenerated with DEADLOCK_UPDATE_GOLDENS=1 and the diff reviewed.
/// </summary>
public class ThirdPartyNoticesTests
{
    private const string FileName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>Only in Debug builds, which are never released.</summary>
    private static readonly HashSet<string> _debugOnly = ["Avalonia.Diagnostics"];

    private static readonly string _rule = new('=', 80);

    private sealed record Package(string Id, string Version, string License, string? LicenseFile, string Copyright, string Url, string Folder);

    [Fact]
    public void TheNoticesListEveryPackageTheAppShipsWith()
    {
        var path = Path.Combine(UiHarness.RepoRoot(), FileName);
        var expected = Generate();
        if (Golden.Updating)
        {
            File.WriteAllText(path, expected, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path) && File.ReadAllText(path) == expected,
            $"{FileName} is out of date with the app's packages: regenerate it with DEADLOCK_UPDATE_GOLDENS=1 and review the diff.");
    }

    [Fact]
    public void TheAppCarriesTheNotices() =>
        Assert.Equal(File.ReadAllBytes(Path.Combine(UiHarness.RepoRoot(), FileName)), Core.ThirdPartyNotices.Bytes());

    private static string Generate()
    {
        var packages = ShippedPackages();
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        void Heading(string title)
        {
            Line();
            Line(_rule);
            Line(title);
            Line(_rule);
            Line();
        }

        Line("THIRD-PARTY NOTICES");
        Line();
        Line("Deadlock Item Advisor is under the MIT license (LICENSE). The program you download also");
        Line("contains the software listed below, each under its own license, which this file reproduces.");
        Line("Deadlock, its heroes, items and art belong to Valve; the match statistics and the hero and");
        Line("item data come from deadlock-api.com.");
        Line();
        Line("Generated from the packages the app ships with (ThirdPartyNoticesTests).");

        Heading("SOFTWARE");
        Line(".NET runtime and libraries");
        Line("    License: MIT");
        Line("    Copyright (c) .NET Foundation and Contributors");
        Line("    https://github.com/dotnet/runtime");
        foreach (var package in packages)
        {
            Line();
            Line($"{package.Id} {package.Version}");
            Line($"    License: {package.License}");
            Line($"    {package.Copyright}");
            Line($"    {package.Url}");
        }

        Heading("MIT LICENSE");
        Line("The software above under the MIT license is provided under these terms, with the");
        Line("copyright notice given for it:");
        Line();
        // Normalised like the license files: this source file's line endings depend on how it was checked out.
        Line(Normalised(Mit));

        var apache = packages.Where(package => package.License == "Apache-2.0").ToList();
        if (apache.Count > 0)
        {
            Heading($"APACHE LICENSE 2.0: {string.Join(", ", apache.Select(package => package.Id))}");
            Line(Normalised(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Notices", "Apache-2.0.txt"))).Trim('\n'));
        }

        // License files for packages licensed by file, and every notices file a package carries, each text once.
        var texts = new List<(string Text, List<string> Packages)>();
        void Add(string file, string package)
        {
            var content = Normalised(File.ReadAllText(file)).Trim('\n');
            var known = texts.FindIndex(entry => Hash(entry.Text) == Hash(content));
            if (known >= 0)
                texts[known].Packages.Add(package);
            else
                texts.Add((content, [package]));
        }
        foreach (var package in packages)
        {
            if (package.LicenseFile is { } licenseFile)
                Add(Path.Combine(package.Folder, licenseFile), package.Id);
            foreach (var notices in Directory.GetFiles(package.Folder).Where(file => Path.GetFileName(file).StartsWith("THIRD-PARTY-NOTICES", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                Add(notices, package.Id);
        }
        foreach (var (content, owners) in texts)
        {
            Heading($"FROM {string.Join(", ", owners)}");
            Line(content);
        }
        return text.ToString();
    }

    /// <summary>The packages the app's build loads code or native libraries from, by id.</summary>
    private static List<Package> ShippedPackages()
    {
        // tests/DeadlockAdvisor.Tests/bin/<configuration>/<framework>/ → the app's own build of the same configuration.
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var deps = Path.Combine(UiHarness.RepoRoot(), "bin", testBin.Parent!.Name, testBin.Name, "DeadlockAdvisor.deps.json");
        using var json = JsonDocument.Parse(File.ReadAllBytes(deps));
        var root = json.RootElement;
        var libraries = root.GetProperty("libraries");
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var packages = new List<Package>();
        foreach (var target in root.GetProperty("targets").EnumerateObject().First().Value.EnumerateObject())
        {
            var library = libraries.GetProperty(target.Name);
            var ships = target.Value.TryGetProperty("runtime", out _) || target.Value.TryGetProperty("native", out _)
                        || target.Value.TryGetProperty("runtimeTargets", out _);
            if (library.GetProperty("type").GetString() != "package" || !ships)
                continue;
            var (id, version) = (target.Name.Split('/')[0], target.Name.Split('/')[1]);
            if (_debugOnly.Contains(id))
                continue;
            packages.Add(Describe(id, version, Path.Combine(nuget, library.GetProperty("path").GetString()!)));
        }
        return packages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Package Describe(string id, string version, string folder)
    {
        var nuspec = XDocument.Load(Directory.GetFiles(folder, "*.nuspec").Single()).Root!;
        XElement? Element(string name) => nuspec.Descendants().FirstOrDefault(element => element.Name.LocalName == name);

        var license = Element("license") ?? throw new InvalidOperationException($"{id} names no license: check it by hand before shipping it.");
        var isFile = license.Attribute("type")?.Value == "file";
        var expression = isFile ? "see below" : license.Value;
        if (!isFile && expression is not ("MIT" or "Apache-2.0"))
            throw new InvalidOperationException($"{id} is under {expression}: check it allows shipping in the app, then add it here.");

        var copyright = Element("copyright")?.Value is { Length: > 0 } stated
            ? stated
            : $"By {Element("authors")?.Value}";
        var url = Element("repository")?.Attribute("url")?.Value is { Length: > 0 } repository
            ? repository.Replace(".git", "", StringComparison.Ordinal)
            : Element("projectUrl")?.Value is { Length: > 0 } project
                ? project.Split('?')[0]
                : $"https://www.nuget.org/packages/{id}/{version}";
        return new Package(id, version, expression, isFile ? license.Value : null, copyright, url, folder);
    }

    private static string Normalised(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()));

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private const string Mit =
        """
        Permission is hereby granted, free of charge, to any person obtaining a copy of this software
        and associated documentation files (the "Software"), to deal in the Software without
        restriction, including without limitation the rights to use, copy, modify, merge, publish,
        distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
        Software is furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all copies or
        substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
        BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
        NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
        DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
        """;
}
