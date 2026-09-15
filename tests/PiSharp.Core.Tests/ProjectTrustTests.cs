using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class ProjectTrustTests
{
    [Fact]
    public void TrustStoreSupportsExactInheritedOverrideAndRemoval()
    {
        using var temporary = TempDirectory.Create();
        var store = new ProjectTrustStore(Path.Combine(temporary.Path, "trust"));
        var parent = Path.Combine(temporary.Path, "projects");
        var child = Path.Combine(parent, "foo", "bar");
        Directory.CreateDirectory(child);

        Assert.Null(store.Get(child));
        store.Set(parent, true);
        Assert.True(store.Get(child));
        store.Set(child, false);
        Assert.False(store.Get(child));
        store.Set(child, null);
        Assert.True(store.Get(child));
        Assert.Equal(ProjectTrustDecision.Trusted, store.GetDecision(child));
    }

    [Fact]
    public void TrustStoreCanonicalizesPathsAndWritesDeterministically()
    {
        using var temporary = TempDirectory.Create();
        var trustDirectory = Path.Combine(temporary.Path, "trust");
        var store = new ProjectTrustStore(trustDirectory);
        var first = Path.Combine(temporary.Path, "z", "..", "project");
        var second = Path.Combine(temporary.Path, "alpha");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "project"));
        Directory.CreateDirectory(second);

        store.Set(first, true);
        store.Set(second, false);

        var lines = File.ReadAllLines(store.TrustPath);
        Assert.Equal("{", lines[0]);
        Assert.True(lines[1].Contains(JsonSerializer.Serialize(Path.GetFullPath(second)), StringComparison.Ordinal));
        Assert.True(lines[2].Contains(JsonSerializer.Serialize(Path.GetFullPath(Path.Combine(temporary.Path, "project"))), StringComparison.Ordinal));
        Assert.Equal(Environment.NewLine, File.ReadAllText(store.TrustPath)[^Environment.NewLine.Length..]);
    }

    [Fact]
    public void TrustStoreRejectsMalformedAndNonBooleanFiles()
    {
        using var temporary = TempDirectory.Create();
        var store = new ProjectTrustStore(Path.Combine(temporary.Path, "trust"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.TrustPath)!);

        File.WriteAllText(store.TrustPath, "{not-json");
        Assert.Throws<InvalidDataException>(() => store.Get(temporary.Path));

        File.WriteAllText(store.TrustPath, "{\"/tmp/project\":null}");
        Assert.Throws<InvalidDataException>(() => store.Get(temporary.Path));

        File.WriteAllText(store.TrustPath, "[]");
        Assert.Throws<InvalidDataException>(() => store.Get(temporary.Path));
    }

    [Fact]
    public async Task TrustStoreConcurrentUpdatesPreserveUnrelatedDecisions()
    {
        using var temporary = TempDirectory.Create();
        var store = new ProjectTrustStore(Path.Combine(temporary.Path, "trust"));
        var projects = Enumerable.Range(0, 12)
            .Select(index => Path.Combine(temporary.Path, "project", index.ToString()))
            .ToArray();
        foreach (var project in projects)
        {
            Directory.CreateDirectory(project);
        }

        await Task.WhenAll(projects.Select((project, index) => Task.Run(() => store.Set(project, index % 2 == 0))));

        foreach (var project in projects)
        {
            Assert.NotNull(store.Get(project));
        }
    }

    [Fact]
    public void DetectorMatchesPiResourcesButIgnoresGlobalAgentsSkills()
    {
        using var temporary = TempDirectory.Create();
        var home = Path.Combine(temporary.Path, "home");
        var project = Path.Combine(temporary.Path, "project");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(project);
        var detector = new ProjectTrustResourceDetector();

        Assert.False(detector.Detect(project, home).TrustRequired);
        Directory.CreateDirectory(Path.Combine(home, ".agents", "skills"));
        Assert.False(detector.Detect(home, home).TrustRequired);

        var piDirectory = Path.Combine(project, ".pi");
        Directory.CreateDirectory(piDirectory);
        foreach (var resource in new[] { "settings.json", "extensions", "skills", "prompts", "themes", "SYSTEM.md", "APPEND_SYSTEM.md", "packages" })
        {
            var path = Path.Combine(piDirectory, resource);
            if (Path.GetExtension(resource).Length == 0)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, "{}");
            }
            Assert.True(detector.Detect(project, home).TrustRequired);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }

        Directory.CreateDirectory(Path.Combine(project, ".agents", "skills"));
        Assert.True(detector.Detect(project, home).TrustRequired);
    }

    [Fact]
    public void ResolverUsesOverrideSavedDefaultThenHeadlessAsk()
    {
        using var temporary = TempDirectory.Create();
        var home = Path.Combine(temporary.Path, "home");
        var project = Path.Combine(temporary.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".pi", "extensions"));
        var store = new ProjectTrustStore(Path.Combine(temporary.Path, "trust"));
        var resolver = new ProjectTrustResolver();

        Assert.False(resolver.Resolve(project, store, null, DefaultProjectTrust.Ask, ProjectTrustMode.NonInteractive, homeDirectory: home).Trusted);
        Assert.True(resolver.Resolve(project, store, true, DefaultProjectTrust.Never, ProjectTrustMode.NonInteractive, homeDirectory: home).Trusted);
        Assert.False(resolver.Resolve(project, store, false, DefaultProjectTrust.Always, ProjectTrustMode.NonInteractive, homeDirectory: home).Trusted);
        Assert.Null(store.Get(project));

        store.Set(Path.Combine(temporary.Path, "parent"), true);
        var nestedProject = Path.Combine(temporary.Path, "parent", "nested");
        Directory.CreateDirectory(Path.Combine(nestedProject, ".pi", "skills"));
        Assert.True(resolver.Resolve(nestedProject, store, null, DefaultProjectTrust.Never, ProjectTrustMode.NonInteractive, homeDirectory: home).Trusted);

        store.Set(nestedProject, false);
        Assert.False(resolver.Resolve(nestedProject, store, null, DefaultProjectTrust.Always, ProjectTrustMode.NonInteractive, homeDirectory: home).Trusted);
    }

    [Fact]
    public void InteractiveSessionOnlyTrustDoesNotWrite()
    {
        using var temporary = TempDirectory.Create();
        var project = Path.Combine(temporary.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".pi", "prompts"));
        var store = new ProjectTrustStore(Path.Combine(temporary.Path, "trust"));
        var resolver = new ProjectTrustResolver();
        var result = resolver.Resolve(
            project,
            store,
            null,
            DefaultProjectTrust.Ask,
            ProjectTrustMode.Interactive,
            options => options.Single(option => option.Label == "Trust (this session only)"));

        Assert.True(result.Trusted);
        Assert.Null(store.Get(project));
    }

    [Fact]
    public void InteractiveTrustAndDenyChoicesPersistDecisions()
    {
        using var temporary = TempDirectory.Create();
        var project = Path.Combine(temporary.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".pi", "extensions"));
        var store = new ProjectTrustStore(Path.Combine(temporary.Path, "trust"));
        var resolver = new ProjectTrustResolver();

        var trusted = resolver.Resolve(
            project,
            store,
            null,
            DefaultProjectTrust.Ask,
            ProjectTrustMode.Interactive,
            options => options.Single(option => option.Label == "Trust"));
        Assert.True(trusted.Trusted);
        Assert.True(store.Get(project));

        store.Set(project, null);
        var denied = resolver.Resolve(
            project,
            store,
            null,
            DefaultProjectTrust.Ask,
            ProjectTrustMode.Interactive,
            options => options.Single(option => option.Label == "Do not trust"));
        Assert.False(denied.Trusted);
        Assert.False(store.Get(project));
    }

    [Fact]
    public void ParentTrustOptionStoresParentAndRemovesChildDecision()
    {
        using var temporary = TempDirectory.Create();
        var parent = Path.Combine(temporary.Path, "parent");
        var project = Path.Combine(parent, "project");
        Directory.CreateDirectory(Path.Combine(project, ".pi", "skills"));
        var store = new ProjectTrustStore(Path.Combine(temporary.Path, "trust"));
        store.Set(project, false);
        var option = ProjectTrustResolver.GetOptions(project, includeSessionOnly: false)
            .Single(candidate => candidate.Label.StartsWith("Trust parent folder", StringComparison.Ordinal));

        store.SetMany(option.Updates);

        Assert.True(store.Get(project));
        Assert.Equal(Path.GetFullPath(parent), store.GetEntry(project)?.Path);
    }

    [Fact]
    public void GlobalDefaultProjectTrustIsReadWithoutProjectSettings()
    {
        using var temporary = TempDirectory.Create();
        var globalPath = Path.Combine(temporary.Path, "settings.json");
        File.WriteAllText(globalPath, "{\"defaultProjectTrust\":\"always\"}");
        var result = GlobalProjectTrustSettings.ReadDefaultProjectTrust(globalPath);
        Assert.Equal(DefaultProjectTrust.Always, result.Value);
        Assert.Null(result.Warning);

        File.WriteAllText(globalPath, "{\"defaultProjectTrust\":\"sometimes\"}");
        result = GlobalProjectTrustSettings.ReadDefaultProjectTrust(globalPath);
        Assert.Equal(DefaultProjectTrust.Ask, result.Value);
        Assert.NotNull(result.Warning);

        File.WriteAllText(globalPath, "{not-json");
        result = GlobalProjectTrustSettings.ReadDefaultProjectTrust(globalPath);
        Assert.Equal(DefaultProjectTrust.Ask, result.Value);
        Assert.NotNull(result.Warning);
    }
}
