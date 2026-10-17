using PiSharp.Cli;
using PiSharp.Cli.Startup;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Packages;

/// <summary>Package maintenance entry point. It deliberately does not compose a model or session runtime.</summary>
internal static class ResourcePackageCommand
{
    private enum ActionKind { Install, Remove, List, Update }

    private sealed record Parsed(ActionKind Action, string? Source, bool Local, bool? TrustOverride,
        bool Help, string? InvalidOption, string? InvalidArgument, string? MissingOptionValue,
        string? Conflict, string? UnsupportedUpdateTarget, bool UpdatePackages);

    public static async Task<bool> TryRunAsync(string[] args, string agentDirectory, string workingDirectory,
        TextReader input, TextWriter output, TextWriter error, bool interactive,
        CancellationToken cancellationToken = default)
    {
        var parsed = Parse(args);
        if (parsed is null) return false;
        Environment.ExitCode = await RunAsync(parsed, agentDirectory, workingDirectory, input, output, error,
            interactive, cancellationToken);
        return true;
    }

    private static Parsed? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        var action = args[0] switch
        {
            "install" => ActionKind.Install,
            "remove" or "uninstall" => ActionKind.Remove,
            "list" => ActionKind.List,
            "update" => ActionKind.Update,
            _ => (ActionKind?)null
        };
        if (action is null) return null;

        var local = false;
        bool? trustOverride = null;
        var help = false;
        string? source = null;
        string? invalidOption = null;
        string? invalidArgument = null;
        string? missingOptionValue = null;
        string? conflict = null;
        string? extensionSource = null;
        var updatePackages = false;
        var unsupportedUpdateTarget = (string?)null;
        var positionalCount = 0;
        var selfFlag = false;
        var modelsFlag = false;
        var allFlag = false;
        var extensionsFlag = false;

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "-h" or "--help") { help = true; continue; }
            if (argument is "-l" or "--local")
            {
                if (action is ActionKind.Install or ActionKind.Remove) local = true;
                else invalidOption ??= argument;
                continue;
            }
            if (argument is "-a" or "--approve") { trustOverride = true; continue; }
            if (argument is "-na" or "--no-approve") { trustOverride = false; continue; }
            if (argument == "--extensions")
            {
                if (action == ActionKind.Update) extensionsFlag = true;
                else invalidOption ??= argument;
                continue;
            }
            if (argument == "--extension")
            {
                if (action != ActionKind.Update) { invalidOption ??= argument; continue; }
                var value = index + 1 < args.Length ? args[index + 1] : null;
                if (string.IsNullOrEmpty(value) || value.StartsWith("-", StringComparison.Ordinal))
                    missingOptionValue ??= argument;
                else if (extensionSource is not null)
                {
                    conflict ??= "--extension can only be provided once";
                    index++;
                }
                else { extensionSource = value; index++; }
                continue;
            }
            if (argument is "--self" or "--models" or "--all")
            {
                if (action != ActionKind.Update) { invalidOption ??= argument; continue; }
                if (argument == "--self") selfFlag = true;
                if (argument == "--models") modelsFlag = true;
                if (argument == "--all") allFlag = true;
                continue;
            }
            if (argument == "--force")
            {
                if (action != ActionKind.Update) invalidOption ??= argument;
                continue;
            }
            if (argument.StartsWith("-", StringComparison.Ordinal)) { invalidOption ??= argument; continue; }

            positionalCount++;
            if (source is null) source = argument;
            else invalidArgument ??= argument;
        }

        if (action == ActionKind.Update)
        {
            if (modelsFlag && (selfFlag || extensionsFlag || allFlag || extensionSource is not null || source is not null))
                conflict ??= "--models cannot be combined with --self, --extensions, --all, --extension, or a positional source";
            if (allFlag && (selfFlag || extensionsFlag || modelsFlag || extensionSource is not null || source is not null))
                conflict ??= "--all cannot be combined with --self, --extensions, --models, --extension, or a positional source";
            if (extensionSource is not null && (selfFlag || extensionsFlag || allFlag || modelsFlag || source is not null))
                conflict ??= "--extension cannot be combined with --self, --extensions, --models, --all, or a positional source";
            if (extensionsFlag && source is not null && source is not ("self" or "pi"))
                conflict ??= "positional update targets cannot be combined with --self, --extensions, or --all";
            if (selfFlag && source is not null && source is not ("self" or "pi"))
                conflict ??= "positional update targets cannot be combined with --self, --extensions, or --all";
            if (source is "self" or "pi") unsupportedUpdateTarget ??= "self update";
            else if (selfFlag || modelsFlag || allFlag) unsupportedUpdateTarget ??= selfFlag ? "self update" : "model catalog update";
            else if (extensionSource is not null)
            {
                source = extensionSource;
                updatePackages = true;
            }
            else if (extensionsFlag) updatePackages = true;
            else if (source is not null) updatePackages = true;
            else unsupportedUpdateTarget ??= "self update";
        }

        // Pi accepts and ignores the first positional argument to list; only later positionals are errors.
        if (action == ActionKind.List && positionalCount <= 1) invalidArgument = null;
        return new(action.Value, source, local, trustOverride, help, invalidOption, invalidArgument,
            missingOptionValue, conflict, unsupportedUpdateTarget, updatePackages);
    }

    private static async Task<int> RunAsync(Parsed parsed, string agentDirectory, string workingDirectory,
        TextReader input, TextWriter output, TextWriter error, bool interactive, CancellationToken cancellationToken)
    {
        var actionName = parsed.Action.ToString().ToLowerInvariant();
        if (parsed.Help)
        {
            await output.WriteLineAsync(Help(actionName));
            return 0;
        }
        if (parsed.InvalidOption is { } invalidOption)
            return await FailAsync(error, $"Unknown option {invalidOption} for \"{actionName}\".",
                $"Use \"pisharp --help\" or \"{Usage(actionName)}\".");
        if (parsed.MissingOptionValue is { } missing)
            return await FailAsync(error, $"Missing value for {missing}.", $"Usage: {Usage(actionName)}");
        if (parsed.InvalidArgument is { } invalidArgument)
            return await FailAsync(error, $"Unexpected argument {invalidArgument}.", $"Usage: {Usage(actionName)}");
        if (parsed.Conflict is { } conflict)
            return await FailAsync(error, conflict, $"Usage: {Usage(actionName)}");
        if (parsed.UnsupportedUpdateTarget is { } unsupported)
        {
            var message = unsupported switch
            {
                "self update" => "Self-update is unavailable for this installation.",
                "model catalog update" => "Model catalog refresh is unavailable.",
                _ => "All-target update is unavailable for this installation."
            };
            return await FailAsync(error, message, "Run \"pisharp update --extensions\" to update configured packages.");
        }
        if (parsed.Action is ActionKind.Install or ActionKind.Remove && string.IsNullOrEmpty(parsed.Source))
            return await FailAsync(error, $"Missing {actionName} source.", $"Usage: {Usage(actionName)}");

        try
        {
            var cwd = Path.GetFullPath(workingDirectory);
            var agent = Path.GetFullPath(agentDirectory);
            Func<string, string?> environment = name => Environment.GetEnvironmentVariable(name);
            var userSettings = await UserSettings.LoadAsync(agent, environment, cancellationToken);
            var trustStore = new ProjectTrust(agent);
            bool trusted;
            ExtensionCatalog? bootstrap = null;
            try
            {
                if (parsed.Action == ActionKind.Update)
                {
                    trusted = parsed.TrustOverride ?? await trustStore.GetAsync(cwd, cancellationToken) == true;
                }
                else if (!ProjectTrust.HasProtectedResources(cwd))
                {
                    trusted = true;
                }
                else
                {
                    if (!parsed.TrustOverride.HasValue && HasTrustExtensionSources(agent, userSettings))
                    {
                        var bootstrapStore = new ManagedResourcePackageStore(agent);
                        bootstrapStore.ConfigureNpmCommand(userSettings.NpmCommand);
                        var existingRoots = await bootstrapStore.ResolveAsync(cwd, trusted: false,
                            userPackages: userSettings.Packages, offline: IsOfflineEnvironment(),
                            cancellationToken: cancellationToken);
                        bootstrap = ExtensionCatalog.Load(agent, cwd, projectTrusted: false,
                            discover: true, userPaths: userSettings.Extensions, userPackages: userSettings.Packages,
                            managedPackageRoots: existingRoots);
                        foreach (var loadError in bootstrap.LoadErrors)
                            await error.WriteLineAsync($"Warning: Failed to load extension \"{loadError.Path}\": {loadError.Error}");
                    }
                    var mode = interactive ? "interactive" : "print";
                    trusted = await trustStore.ResolveAsync(cwd, parsed.TrustOverride, interactive, input, output,
                        cancellationToken, userSettings.DefaultProjectTrust ?? "ask", bootstrap?.Registration,
                        mode: mode, extensionUi: new ProjectTrustStartupUi(interactive, mode, output, error));
                }
            }
            finally { bootstrap?.Dispose(); }

            var projectSettings = trusted && ProjectTrust.HasProtectedResources(cwd)
                ? await UserSettings.LoadProjectAsync(cwd, cancellationToken)
                : null;
            if (parsed.Local && parsed.Action is ActionKind.Install or ActionKind.Remove && !trusted)
                return await FailAsync(error, "Project is not trusted. Use --approve to modify local package config.");

            // The full settings load above happens before cache or settings side effects.
            var effectiveSettings = userSettings.Overlay(projectSettings ?? new UserSettings());
            var store = new ManagedResourcePackageStore(agent);
            store.ConfigureNpmCommand(effectiveSettings.NpmCommand);
            var nativeStore = new NativeExtensionPackageStore(agent);
            var userPackages = userSettings.Packages ?? [];
            var projectPackages = trusted ? projectSettings?.Packages ?? [] : [];
            var userNativeExtensions = userSettings.NativeExtensions ?? [];
            var projectNativeExtensions = trusted ? projectSettings?.NativeExtensions ?? [] : [];
            var userSettingsPath = UserSettings.GetSettingsPath(agent, environment);
            var projectSettingsPath = Path.Combine(cwd, ".pi", "settings.json");

            switch (parsed.Action)
            {
                case ActionKind.List:
                    await PrintListAsync(store, nativeStore, cwd, userPackages, projectPackages,
                        userNativeExtensions, projectNativeExtensions, output, cancellationToken);
                    return 0;
                case ActionKind.Install:
                    {
                        var source = parsed.Source!;
                        await output.WriteLineAsync($"Installing {source}...");
                        var scope = parsed.Local ? "project" : "user";
                        var scopeBase = parsed.Local ? Path.Combine(cwd, ".pi") : agent;
                        var settingsPath = parsed.Local ? projectSettingsPath : userSettingsPath;
                        if (IsNativePackageSource(source))
                        {
                            if (parsed.Local) _ = projectSettings ?? await UserSettings.LoadProjectAsync(cwd, cancellationToken);
                            var package = await nativeStore.InstallAsync(cwd, source, scope, cancellationToken);
                            var nativeStoredSource = NormalizeNativeSourceForSettings(package.Source, scopeBase);
                            await UserSettingsWriter.UpdateNativeExtensionAsync(settingsPath, package with { Source = nativeStoredSource },
                                remove: false, userScope: !parsed.Local, cancellationToken);
                            await output.WriteLineAsync($"Installed PiSharp-native extension {package.Id} {package.Version}");
                            return 0;
                        }
                        var storedSource = NormalizeSourceForSettings(source, cwd, scopeBase);
                        if (ManagedResourcePackageStore.GetManagedPackageIdentity(source) is null)
                        {
                            var localPath = ResolveLocalPath(source, cwd);
                            if (!File.Exists(localPath) && !Directory.Exists(localPath))
                                throw new InvalidDataException($"Path does not exist: {localPath}");
                        }
                        // Validate the destination document before making an acquisition attempt.
                        if (parsed.Local) _ = projectSettings ?? await UserSettings.LoadProjectAsync(cwd, cancellationToken);
                        if (ManagedResourcePackageStore.GetManagedPackageIdentity(source) is not null)
                            await store.InstallSelectedAsync(cwd, source, scope, cancellationToken,
                                standardOutput: output, standardError: error);
                        await UserSettingsWriter.UpdatePackageSourceAsync(settingsPath, source, storedSource, cwd, scopeBase,
                            remove: false, userScope: !parsed.Local, cancellationToken);
                        await output.WriteLineAsync($"Installed {source}");
                        return 0;
                    }
                case ActionKind.Remove:
                    {
                        var source = parsed.Source!;
                        await output.WriteLineAsync($"Removing {source}...");
                        var scope = parsed.Local ? "project" : "user";
                        var scopeBase = parsed.Local ? Path.Combine(cwd, ".pi") : agent;
                        var settingsPath = parsed.Local ? projectSettingsPath : userSettingsPath;
                        if (parsed.Local) _ = projectSettings ?? await UserSettings.LoadProjectAsync(cwd, cancellationToken);
                        var selectedNativePackages = parsed.Local ? projectNativeExtensions : userNativeExtensions;
                        var nativePackage = FindNativePackage(selectedNativePackages, source, cwd, scopeBase);
                        if (nativePackage is not null)
                        {
                            var removedNative = await UserSettingsWriter.UpdateNativeExtensionAsync(settingsPath, nativePackage,
                                remove: true, userScope: !parsed.Local, cancellationToken);
                            if (!removedNative) return await FailAsync(error, $"No matching PiSharp-native extension found for {source}");
                            await nativeStore.RemoveAsync(cwd, nativePackage.Id, scope, cancellationToken);
                            await output.WriteLineAsync($"Removed PiSharp-native extension {nativePackage.Id}");
                            return 0;
                        }
                        await store.RemoveAsync(cwd, source, scope, cancellationToken,
                            standardOutput: output, standardError: error);
                        var removed = await UserSettingsWriter.UpdatePackageSourceAsync(settingsPath, source, source, cwd,
                            scopeBase, remove: true, userScope: !parsed.Local, cancellationToken);
                        if (!removed) return await FailAsync(error, $"No matching package found for {source}");
                        await output.WriteLineAsync($"Removed {source}");
                        return 0;
                    }
                case ActionKind.Update:
                    {
                        var source = parsed.Source;
                        if (source is not null && (IsNativePackageSource(source) ||
                            FindNativePackage(userNativeExtensions, source, cwd, agent) is not null ||
                            FindNativePackage(projectNativeExtensions, source, cwd, Path.Combine(cwd, ".pi")) is not null))
                        {
                            var incoming = IsNativePackageSource(source)
                                ? await nativeStore.InspectAsync(cwd, source, cancellationToken)
                                : null;
                            var currentUserNative = incoming is null
                                ? FindNativePackage(userNativeExtensions, source, cwd, agent)
                                : userNativeExtensions.FirstOrDefault(item => string.Equals(item.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
                            var currentProjectNative = !trusted ? null : incoming is null
                                ? FindNativePackage(projectNativeExtensions, source, cwd, Path.Combine(cwd, ".pi"))
                                : projectNativeExtensions.FirstOrDefault(item => string.Equals(item.Id, incoming.Id, StringComparison.OrdinalIgnoreCase));
                            var isUser = currentUserNative is not null;
                            var current = currentUserNative ?? currentProjectNative;
                            if (current is null)
                                return await FailAsync(error, $"No configured PiSharp-native extension matches '{source}'.");
                            var scope = isUser ? "user" : "project";
                            var scopeBase = isUser ? agent : Path.Combine(cwd, ".pi");
                            var settingsPath = isUser ? userSettingsPath : projectSettingsPath;
                            var packageSource = incoming?.Source ?? ResourceLocalPath.Resolve(current.Source, scopeBase);
                            var updated = await nativeStore.InstallAsync(cwd, packageSource, scope, cancellationToken);
                            var storedSource = NormalizeNativeSourceForSettings(updated.Source, scopeBase);
                            await UserSettingsWriter.UpdateNativeExtensionAsync(settingsPath, updated with { Source = storedSource },
                                remove: false, userScope: isUser, cancellationToken);
                            await output.WriteLineAsync($"Updated PiSharp-native extension {updated.Id} {updated.Version}");
                            return 0;
                        }
                        if (source is null && parsed.UpdatePackages)
                        {
                            await UpdateConfiguredNativeExtensionsAsync(nativeStore, cwd, agent, userSettingsPath,
                                projectSettingsPath, userNativeExtensions, projectNativeExtensions, output, cancellationToken);
                        }
                        var configuredForUser = source is not null && ContainsPackage(userPackages, source, cwd, agent);
                        var configuredForProject = source is not null && ContainsPackage(projectPackages, source, cwd,
                            Path.Combine(cwd, ".pi"));
                        if (source is not null && !configuredForUser && !configuredForProject)
                            return await FailAsync(error, $"No configured package matches '{source}'.");
                        if (source is not null && ManagedResourcePackageStore.GetManagedPackageIdentity(source) is null)
                        {
                            await output.WriteLineAsync($"Updated {source}");
                            return 0;
                        }
                        if (IsOfflineEnvironment())
                        {
                            await output.WriteLineAsync(source is null ? "Updated packages" : $"Updated {source}");
                            return 0;
                        }
                        _ = await store.UpdateAsync(cwd, trusted, userPackages, projectPackages, source,
                            offline: IsOfflineEnvironment(), cancellationToken: cancellationToken,
                            standardOutput: output, standardError: error);
                        await output.WriteLineAsync(source is null ? "Updated packages" : $"Updated {source}");
                        return 0;
                    }
                default:
                    return 1;
            }
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(error, "Package command canceled.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
                                           UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                                           System.Text.Json.JsonException)
        {
            return await FailAsync(error, exception is ExtensionStartupFailure ? exception.Message : $"Error: {exception.Message}");
        }
    }

    private static async Task PrintListAsync(ManagedResourcePackageStore store, NativeExtensionPackageStore nativeStore, string cwd,
        IReadOnlyList<ResourcePackageSource> userPackages, IReadOnlyList<ResourcePackageSource> projectPackages,
        IReadOnlyList<NativeExtensionPackageSource> userNativeExtensions,
        IReadOnlyList<NativeExtensionPackageSource> projectNativeExtensions, TextWriter output,
        CancellationToken cancellationToken)
    {
        var configured = new List<(ResourcePackageSource Package, string Scope, string? Path)>();
        foreach (var package in userPackages)
            configured.Add((package, "user", await store.GetInstalledPathAsync(cwd, package.Source, "user", cancellationToken)));
        foreach (var package in projectPackages)
            configured.Add((package, "project", await store.GetInstalledPathAsync(cwd, package.Source, "project", cancellationToken)));
        var nativeRoots = await nativeStore.ResolveAsync(cwd, trusted: true, userNativeExtensions, projectNativeExtensions,
            cancellationToken);
        if (configured.Count == 0 && nativeRoots.Count == 0)
        {
            await output.WriteLineAsync("No packages installed.");
            return;
        }
        var users = configured.Where(entry => entry.Scope == "user").ToArray();
        var projects = configured.Where(entry => entry.Scope == "project").ToArray();
        if (users.Length > 0)
        {
            await output.WriteLineAsync("User packages:");
            foreach (var entry in users) await PrintEntryAsync(entry);
        }
        if (projects.Length > 0)
        {
            if (users.Length > 0) await output.WriteLineAsync();
            await output.WriteLineAsync("Project packages:");
            foreach (var entry in projects) await PrintEntryAsync(entry);
        }

        var userNative = nativeRoots.Where(item => item.Scope == "user").ToArray();
        var projectNative = nativeRoots.Where(item => item.Scope == "project").ToArray();
        if (userNative.Length > 0)
        {
            if (configured.Count > 0) await output.WriteLineAsync();
            await output.WriteLineAsync("User PiSharp-native compiled extensions:");
            foreach (var entry in userNative) await PrintNativeEntryAsync(entry);
        }
        if (projectNative.Length > 0)
        {
            if (configured.Count > 0 || userNative.Length > 0) await output.WriteLineAsync();
            await output.WriteLineAsync("Project PiSharp-native compiled extensions:");
            foreach (var entry in projectNative) await PrintNativeEntryAsync(entry);
        }

        async Task PrintEntryAsync((ResourcePackageSource Package, string Scope, string? Path) entry)
        {
            await output.WriteLineAsync("  " + entry.Package.Source + (entry.Package.HasFilter ? " (filtered)" : ""));
            if (entry.Path is not null) await output.WriteLineAsync("    " + entry.Path);
        }

        async Task PrintNativeEntryAsync(NativeExtensionPackageRoot entry)
        {
            var package = (entry.Scope == "project" ? projectNativeExtensions : userNativeExtensions)
                .FirstOrDefault(item => item.Source == entry.Source);
            var label = package is null ? entry.Source : $"{package.Id} {package.Version}";
            await output.WriteLineAsync("  " + label);
            await output.WriteLineAsync("    " + entry.Path);
        }
    }

    private static async Task UpdateConfiguredNativeExtensionsAsync(NativeExtensionPackageStore store, string cwd,
        string agentDirectory, string userSettingsPath, string projectSettingsPath,
        IReadOnlyList<NativeExtensionPackageSource> userPackages,
        IReadOnlyList<NativeExtensionPackageSource> projectPackages, TextWriter output,
        CancellationToken cancellationToken)
    {
        await UpdateScopeAsync(userPackages, "user", agentDirectory, userSettingsPath, userScope: true);
        await UpdateScopeAsync(projectPackages, "project", Path.Combine(cwd, ".pi"), projectSettingsPath, userScope: false);

        async Task UpdateScopeAsync(IReadOnlyList<NativeExtensionPackageSource> packages, string scope,
            string scopeBase, string settingsPath, bool userScope)
        {
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ResourceLocalPath.Resolve(package.Source, scopeBase);
                if (!File.Exists(source))
                {
                    await output.WriteLineAsync($"Skipped PiSharp-native extension {package.Id}: local package source is unavailable.");
                    continue;
                }
                var updated = await store.InstallAsync(cwd, source, scope, cancellationToken);
                var storedSource = NormalizeNativeSourceForSettings(updated.Source, scopeBase);
                await UserSettingsWriter.UpdateNativeExtensionAsync(settingsPath, updated with { Source = storedSource },
                    remove: false, userScope, cancellationToken);
                await output.WriteLineAsync($"Updated PiSharp-native extension {updated.Id} {updated.Version}");
            }
        }
    }

    private static bool ContainsPackage(IReadOnlyList<ResourcePackageSource> packages, string source,
        string inputBaseDirectory, string settingsBaseDirectory)
    {
        var inputIdentity = PackageIdentity(source, inputBaseDirectory);
        return packages.Any(package => PackageIdentity(package.Source, settingsBaseDirectory) == inputIdentity);
    }

    private static string PackageIdentity(string source, string baseDirectory) =>
        ManagedResourcePackageStore.GetManagedPackageIdentity(source) ?? "local:" + ResourceLocalPath.Resolve(source, baseDirectory);

    private static string NormalizeSourceForSettings(string source, string inputBaseDirectory, string settingsBaseDirectory)
    {
        if (ManagedResourcePackageStore.GetManagedPackageIdentity(source) is not null) return source;
        var resolved = ResourceLocalPath.Resolve(source, inputBaseDirectory);
        var relative = Path.GetRelativePath(Path.GetFullPath(settingsBaseDirectory), resolved);
        return relative.Length == 0 ? "." : relative;
    }

    private static string ResolveLocalPath(string source, string baseDirectory) => ResourceLocalPath.Resolve(source, baseDirectory);

    private static bool IsNativePackageSource(string source) =>
        Path.GetExtension(source).Equals(".nupkg", StringComparison.OrdinalIgnoreCase);

    private static NativeExtensionPackageSource? FindNativePackage(IReadOnlyList<NativeExtensionPackageSource> packages,
        string source, string inputBaseDirectory, string settingsBaseDirectory)
    {
        var byId = packages.FirstOrDefault(package => string.Equals(package.Id, source, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var inputPath = Path.GetFullPath(ResourceLocalPath.Resolve(source, inputBaseDirectory));
        return packages.FirstOrDefault(package => Path.GetFullPath(ResourceLocalPath.Resolve(package.Source, settingsBaseDirectory))
            .Equals(inputPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static string NormalizeNativeSourceForSettings(string source, string settingsBaseDirectory)
    {
        var resolved = Path.GetFullPath(source);
        var relative = Path.GetRelativePath(Path.GetFullPath(settingsBaseDirectory), resolved);
        return relative.Length == 0 ? "." : relative;
    }

    private static bool HasTrustExtensionSources(string agentDirectory, UserSettings settings)
    {
        if (settings.Packages is { Count: > 0 }) return true;
        if (settings.NativeExtensions is { Count: > 0 }) return true;
        if (settings.Extensions?.Any(path => !path.TrimStart('!', '+', '-').StartsWith("builtin:", StringComparison.Ordinal)) == true)
            return true;
        var directory = Path.Combine(agentDirectory, "extensions");
        return Directory.Exists(directory) && (Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateDirectories(directory).Any(item => File.Exists(Path.Combine(item, "index.dll"))));
    }

    private static bool IsOfflineEnvironment() => Environment.GetEnvironmentVariable("PI_OFFLINE") is { } value &&
        value.ToLowerInvariant() is "1" or "true" or "yes";

    private static string Usage(string command) => command switch
    {
        "install" => "pisharp install <source|local.nupkg> [-l] [--approve|--no-approve]",
        "remove" or "uninstall" => "pisharp remove <source|native-extension-id> [-l] [--approve|--no-approve]",
        "list" => "pisharp list [--approve|--no-approve]",
        _ => "pisharp update [source|self|pi] [--self|--extensions|--models|--all] [--extension <source>] [--approve|--no-approve] [--force]"
    };

    private static string Help(string command) => command switch
    {
        "install" => $"Usage:\n  {Usage(command)}\n\nInstall a Pi-compatible resource package or a PiSharp-native compiled extension package. Pi TypeScript/JavaScript extensions are not executable by PiSharp.\n\nOptions:\n  -l, --local       Install project-locally (.pi/settings.json)\n  -a, --approve     Trust project-local files for this command\n  -na, --no-approve Ignore project-local files for this command\n\nExamples:\n  pisharp install npm:@foo/bar\n  pisharp install git:github.com/user/repo\n  pisharp install ./local/package.nupkg",
        "remove" or "uninstall" => $"Usage:\n  {Usage(command)}\n\nRemove a Pi-compatible resource package or PiSharp-native compiled extension.\nAlias: pisharp uninstall <source> [-l]\n\nOptions:\n  -l, --local       Remove from project settings (.pi/settings.json)\n  -a, --approve     Trust project-local files for this command\n  -na, --no-approve Ignore project-local files for this command\n\nExamples:\n  pisharp remove npm:@foo/bar\n  pisharp remove example.extension",
        "update" => $"Usage:\n  {Usage(command)}\n\nUpdate configured package resources and local PiSharp-native compiled extensions.\n\nOptions:\n  --self                  Select the PiSharp self-update target\n  --extensions            Update configured packages only\n  --models                Select the model catalog target\n  --all                   Select all update targets\n  --extension <source>    Update one configured package or native extension only\n  -a, --approve           Trust project-local files for this command\n  -na, --no-approve       Ignore project-local files for this command\n  --force                 Force the self-update target\n\nSelf-update, model catalog refresh, and all-target update are unavailable for this installation.",
        _ => $"Usage:\n  {Usage(command)}\n\nList installed packages from user and project settings.\n\nOptions:\n  -a, --approve      Trust project-local files for this command\n  -na, --no-approve  Ignore project-local files for this command"
    };

    private static async Task<int> FailAsync(TextWriter error, string message, string? usage = null)
    {
        await error.WriteLineAsync(message);
        if (usage is not null) await error.WriteLineAsync(usage);
        return 1;
    }
}
