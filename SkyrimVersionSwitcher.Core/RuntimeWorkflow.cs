namespace SkyrimVersionSwitcher.Core;

public sealed record SetupReport(string[] Ready, string[] NeedsSkse, string[] Failed);

/// <summary>Downloads only on demand. A ready switch does not initialize the network client.</summary>
public sealed class LazySteamDepotClient(Func<Task<ISteamDepotClient>> create) : ISteamDepotClient
{
    private Task<ISteamDepotClient>? client;
    private Task<ISteamDepotClient> Get() => client ??= create();
    public async Task<SteamManifest> Manifest(DepotSource source, string output) => await (await Get()).Manifest(source, output);
    public async Task Download(DepotSource source, IReadOnlyList<string> files, string output) => await (await Get()).Download(source, files, output);
}

public sealed class RuntimeWorkflow(string game, CompatibilityCatalog catalog, ISteamDepotClient steam,
    Func<OverlayManager, RuntimeDefinition, Task<bool>> ensureSkse, Action<string>? progress = null, Action<WorkProgress>? feedback = null)
{
    private bool sharedDataPrepared;
    private async Task PrepareSharedData(OverlayManager manager)
    {
        if (sharedDataPrepared) return;
        feedback?.Invoke(new("Shared game data", "Checking installed data and preparing missing files…"));
        await manager.PrepareNormalization(steam, progress, feedback);
        sharedDataPrepared = true;
    }
    public async Task<bool> Prepare(RuntimeDefinition version, string profile)
    {
        var manager = new OverlayManager(game, catalog, profile);
        await Recover(manager);
        await PrepareSharedData(manager);
        progress?.Invoke($"Preparing Skyrim {version.Version}…");
        feedback?.Invoke(new("Runtime files", $"Preparing Skyrim {version.Version}…"));
        await manager.Prepare(version, steam, progress, feedback: feedback);
        feedback?.Invoke(new("SKSE", $"Checking SKSE {version.SkseVersion} for Skyrim {version.Version}…"));
        if (!await ensureSkse(manager, version)) return false;
        feedback?.Invoke(new("Verification", $"Verifying cached Skyrim {version.Version} and SKSE files…"));
        manager.Seal(version);
        feedback?.Invoke(new("Ready", $"Skyrim {version.Version} is downloaded and verified.", 1, 1, "version"));
        progress?.Invoke($"Skyrim {version.Version} is ready for offline switching.");
        return true;
    }
    public async Task<SetupReport> DownloadEverything(IEnumerable<RuntimeDefinition> versions, bool includeLegacy)
    {
        List<string> ready = [], needsSkse = [], failed = [];
        foreach (var version in versions)
        foreach (var profile in catalog.Runtimes.Single(r => r.Version == version.Version).Profiles.Where(p => p.Id == "runtime" || includeLegacy))
        {
            var name = version.Version + "/" + profile.Id;
            try
            {
                if (await Prepare(version, profile.Id)) ready.Add(name);
                else { needsSkse.Add(name); progress?.Invoke($"{version.Version}: one-time Nexus SKSE import needed."); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { failed.Add(name + ": " + ex.Message); progress?.Invoke(failed[^1]); }
        }
        return new SetupReport(ready.ToArray(), needsSkse.ToArray(), failed.ToArray());
    }
    public async Task<bool> Switch(RuntimeDefinition version, string profile)
    {
        var manager = new OverlayManager(game, catalog, profile);
        await Recover(manager);
        await PrepareSharedData(manager);
        var status = manager.Status(version);
        if (status == "Active (verified)") return true;
        if (status != "Ready (offline)" && !await Prepare(version, profile)) return false;
        feedback?.Invoke(new("Switching", $"Preserving originals and installing Skyrim {version.Version}…"));
        manager.Feedback = feedback;
        manager.Activate(version);
        feedback?.Invoke(new("Complete", $"Skyrim {version.Version} is active.", 1, 1, "version"));
        progress?.Invoke("Switched to Skyrim " + version.Version + ".");
        return true;
    }
    public async Task RestoreSharedBaseline()
    {
        var manager = new OverlayManager(game, catalog);
        await Recover(manager);
        await PrepareSharedData(manager);
        manager.Feedback = feedback;
        feedback?.Invoke(new("Restoring shared data", "Preserving current files and restoring the supported baseline…"));
        manager.RestoreBaseline();
    }
    private static Task Recover(OverlayManager manager)
    {
        if (File.Exists(Path.Combine(manager.Root, "journal.json"))) new RuntimeManager(manager.Game).Recover();
        if (manager.RecoveryPending) manager.Recover();
        return Task.CompletedTask;
    }
}

public static class SkseSetup
{
    public static async Task<bool> Ensure(OverlayManager manager, RuntimeDefinition version, Action<WorkProgress>? feedback = null)
    {
        if (manager.HasSkse(version)) return true;
        var legacy = RuntimeManager.Safe(manager.Root, "versions/" + version.Version);
        if (File.Exists(Path.Combine(legacy, version.SkseDll))) { manager.ImportSkse(version, legacy); return true; }
        if (version.SkseUrl == null) return false;
        var output = RuntimeManager.Safe(manager.Root, "downloads/skse/" + version.Version);
        var archive = Path.Combine(output, "skse-" + version.SkseVersion + ".7z");
        if (!File.Exists(archive) || !File.Exists(archive + ".sha256") || RuntimeManager.Hash(archive) != File.ReadAllText(archive + ".sha256")) archive = await Acquisition.DownloadSkse(version, output, feedback);
        feedback?.Invoke(new("SKSE", $"Extracting and caching SKSE {version.SkseVersion}…"));
        Import(manager, version, archive);
        return true;
    }
    public static void Import(OverlayManager manager, RuntimeDefinition version, string archive)
    {
        var folder = SkseArchive.Extract(archive, version.SkseDll);
        try { manager.ImportSkse(version, folder); }
        finally { SkseArchive.DeleteTemporary(SkseArchive.TemporaryRoot(folder)); }
    }
}
