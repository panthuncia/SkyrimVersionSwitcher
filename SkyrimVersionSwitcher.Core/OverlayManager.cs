using System.Diagnostics;

namespace SkyrimVersionSwitcher.Core;

/// <summary>Catalog resolution, acquisition and activation share no version-specific branches.</summary>
public sealed partial class OverlayManager
{
    public string Game { get; }
    public string Root => Path.Combine(Game, "RuntimeManager");
    public ObjectStore Objects { get; }
    public CompatibilityCatalog Catalog { get; }
    public Action<WorkProgress>? Feedback { get; set; }
    public string Profile { get; }
    public string JournalPath => RuntimeManager.Safe(Root, "transactions/overlay-journal.json");
    public bool RecoveryPending => File.Exists(JournalPath) || File.Exists(Path.Combine(Root, "journal.json"));
    private string StatePath => RuntimeManager.Safe(Root, "state.json");
    private string BaselinePath => RuntimeManager.Safe(Root, "baseline.json");
    public OverlayManager(string game, CompatibilityCatalog catalog, string profile = "runtime")
    {
        Game = new RuntimeManager(game).Game;
        Catalog = catalog; Catalog.Validate(); CompatibilityCatalog.ValidateId(profile); Profile = profile;
        Objects = new ObjectStore(Root);
    }
    private FileStream Lock()
    {
        RuntimeManager.Safe(Game, "RuntimeManager/operation.lock");
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void RequireNoRecovery()
    {
        if (RecoveryPending) throw new IOException("Recover the interrupted switch before modifying the cache or game.");
        if (Catalog.Normalization == null && File.Exists(Path.Combine(Root, "active.json")) && !File.Exists(StatePath))
            throw new IOException("An earlier prototype activation exists. Restore your modern Steam installation before using overlays; retain RuntimeManager/backups. The old active.json must be archived after restoration.");
    }
    private string ReceiptPath(RuntimeDefinition version, string name)
    {
        CompatibilityCatalog.ValidateId(version.Version);
        return RuntimeManager.Safe(Root, $"catalog/{version.Version}/{Profile}/{name}.json");
    }
    private string SksePath(RuntimeDefinition version)
    {
        CompatibilityCatalog.ValidateId(version.Version);
        return RuntimeManager.Safe(Root, $"catalog/{version.Version}/skse.json");
    }
    private string SourceDirectory(DepotSource source) => RuntimeManager.Safe(Root, $"downloads/{source.Depot}/{source.Manifest}");
    public CompatibilityProfile Definition(RuntimeDefinition version) => Catalog.Resolve(version.Version, Profile);
    private string Fingerprint(RuntimeDefinition version) => CompatibilityCatalog.Fingerprint(Definition(version), Catalog);
    public bool HasSkse(RuntimeDefinition version)
    {
        if (!File.Exists(SksePath(version))) return false;
        var manifest = Storage.Read<CacheManifest>(SksePath(version));
        return manifest.Version == version.Version && manifest.Files.ContainsKey(version.SkseDll) && manifest.Files.ContainsKey("skse64_loader.exe") && manifest.Files.Keys.Any(IsScript) && manifest.Files.Values.All(Objects.Contains);
    }

    /// <summary>Never changes active game files. Only missing, selected Steam files are requested.</summary>
    public async Task Prepare(RuntimeDefinition version, ISteamDepotClient client, Action<string>? progress = null, string? importFolder = null, Action<WorkProgress>? feedback = null)
    {
        using var guard = Lock(); RequireNoRecovery();
        var profile = Definition(version); var fingerprint = Fingerprint(version);
        var receipt = ReadReceipt(version, "steam", fingerprint) ?? new OverlayReceipt(version.Version, Profile, fingerprint, new(StringComparer.OrdinalIgnoreCase));
        var indexPath = RuntimeManager.Safe(Root, "steam-objects.json");
        var index = File.Exists(indexPath) ? Storage.Read<Dictionary<string, string>>(indexPath) : new Dictionary<string, string>();
        foreach (var group in profile.Files.Where(f => f.Policy == "versioned").GroupBy(f => f.Source!))
        {
            var rules = group.ToArray();
            if (rules.All(f => receipt.Files.TryGetValue(f.Path, out var hash) && Objects.Contains(hash) && (f.Sha256 == null || hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))))
            { progress?.Invoke("Using verified cache: " + group.Key); continue; }
            var source = Catalog.Sources[group.Key]; var output = SourceDirectory(source);
            feedback?.Invoke(new("Runtime files", $"Reading the download manifest for Skyrim {version.Version}…"));
            progress?.Invoke($"Reading Steam manifest {source.Depot}/{source.Manifest} ({source.Release})…");
            var manifest = await client.Manifest(source, output);
            if (manifest.Depot != source.Depot || manifest.Id != source.Manifest) throw new AcquisitionException(AcquisitionFailure.InvalidManifest, "Downloader returned the wrong manifest.");
            var missing = new List<string>();
            var resolved = new Dictionary<string, SteamFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                feedback?.Invoke(new("Runtime files", "Checking " + rule.Path, resolved.Count, rules.Length));
                if (!manifest.Files.TryGetValue(rule.Path, out var entry)) throw new AcquisitionException(AcquisitionFailure.MissingFile, $"Catalog/source mismatch: {rule.Path} is absent from depot {source.Depot}, manifest {source.Manifest}. No activation performed.");
                resolved.Add(rule.Path, entry);
                string? local = null;
                var key = entry.Sha1 + ":" + entry.Size;
                if (index.TryGetValue(key, out var hash) && Objects.Contains(hash) && SteamManifest.Matches(Objects.Require(hash), entry)) local = Objects.Require(hash);
                foreach (var candidateRoot in new[] { importFolder, Game, RuntimeManager.Safe(Root, "versions/" + version.Version), output }.OfType<string>())
                {
                    if (local != null) break;
                    var candidate = RuntimeManager.Safe(candidateRoot, entry.Path);
                    if (SteamManifest.Matches(candidate, entry)) local = candidate;
                }
                if (local == null) missing.Add(entry.Path);
                else
                {
                    var objectHash = Objects.Add(local, rule.Sha256);
                    receipt.Files[rule.Path] = objectHash; index[key] = objectHash;
                    progress?.Invoke("Reused verified file: " + rule.Path);
                }
            }
            // Persist reusable objects before a potentially interrupted download.
            Storage.Write(indexPath, index);
            if (missing.Count > 0)
            {
                progress?.Invoke($"Downloading {missing.Count} selected file(s) from depot {source.Depot}…");
                feedback?.Invoke(new("Runtime download", $"Downloading {missing.Count} required file(s) for Skyrim {version.Version}…"));
                await client.Download(source, missing, output);
                var verified = 0;
                foreach (var name in missing)
                {
                    feedback?.Invoke(new("Verifying download", name, verified++, missing.Count));
                    var entry = manifest.Files[name]; var local = RuntimeManager.Safe(output, entry.Path);
                    if (!SteamManifest.Matches(local, entry)) throw new AcquisitionException(AcquisitionFailure.ContentMismatch, "Downloaded size/Steam SHA-1 mismatch: " + name + ". Retry acquisition; active game files were not changed.");
                    var rule = rules.Single(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var hash = Objects.Add(local, rule.Sha256);
                    receipt.Files[rule.Path] = hash; index[entry.Sha1 + ":" + entry.Size] = hash;
                }
            }
            Storage.Write(indexPath, index);
            Storage.Write(ReceiptPath(version, "steam"), receipt);
            // Only our verified requested payloads are disposable. Keep manifest and downloader
            // metadata for retries, but do not retain duplicate completed game payloads.
            foreach (var name in rules.Select(f => f.Path))
            {
                var path = RuntimeManager.Safe(output, name);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        Storage.Write(ReceiptPath(version, "steam"), receipt);
        progress?.Invoke("Game overlay cached. Shared game data was not modified.");
    }
    private OverlayReceipt? ReadReceipt(RuntimeDefinition version, string name, string fingerprint)
    {
        var path = ReceiptPath(version, name);
        if (!File.Exists(path)) return null;
        var receipt = Storage.Read<OverlayReceipt>(path);
        return receipt.Version == version.Version && receipt.Profile == Profile && receipt.CatalogHash == fingerprint ? receipt with { Files = new(receipt.Files, StringComparer.OrdinalIgnoreCase) } : null;
    }
    public void ImportSkse(RuntimeDefinition version, string source)
    {
        using var guard = Lock(); RequireNoRecovery();
        if (!File.Exists(RuntimeManager.Safe(source, version.SkseDll)) || !File.Exists(RuntimeManager.Safe(source, "skse64_loader.exe"))) throw new IOException("Select an extracted SKSE folder containing " + version.SkseDll);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.Equals(version.SkseDll, StringComparison.OrdinalIgnoreCase) || relative is "skse64_loader.exe" or "skse64_steam_loader.dll" || IsScript(relative))
                files[relative] = Objects.Add(RuntimeManager.Safe(source, relative));
        }
        if (!files.Keys.Any(IsScript)) throw new IOException("SKSE compiled Data/Scripts files are missing.");
        Storage.Write(SksePath(version), new CacheManifest(version.Version, files));
        foreach (var profile in Catalog.Runtimes.Single(r => r.Version == version.Version).Profiles)
        {
            var ready = RuntimeManager.Safe(Root, $"catalog/{version.Version}/{profile.Id}/ready.json");
            if (File.Exists(ready)) File.Delete(ready);
        }
    }
    private static bool IsScript(string path) => path.StartsWith("Data/Scripts/", StringComparison.OrdinalIgnoreCase) && RuntimeManager.Allowed(path);
    public OverlayReceipt Seal(RuntimeDefinition version) => SealInternal(version, true);
    internal OverlayReceipt SealForTesting(RuntimeDefinition version) => SealInternal(version, false);
    private OverlayReceipt SealInternal(RuntimeDefinition version, bool checkExe)
    {
        using var guard = Lock(); RequireNoRecovery();
        var profile = Definition(version); var fingerprint = Fingerprint(version);
        var steam = ReadReceipt(version, "steam", fingerprint) ?? throw new IOException("Prepare the selected game-data profile first.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in profile.Files.Where(f => f.Policy == "versioned"))
        {
            if (!steam.Files.TryGetValue(file.Path, out var hash)) throw new IOException("Missing cached game file: " + file.Path);
            Objects.Require(hash);
            if (file.Sha256 != null && !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Catalog hash mismatch: " + file.Path);
            files.Add(file.Path, hash);
        }
        var skse = Storage.Read<CacheManifest>(SksePath(version));
        if (skse.Version != version.Version || !skse.Files.ContainsKey(version.SkseDll) || !skse.Files.ContainsKey("skse64_loader.exe") || !skse.Files.Keys.Any(IsScript)) throw new IOException("Incomplete or mismatched SKSE cache.");
        foreach (var (name, hash) in skse.Files)
        {
            if (name != version.SkseDll && name is not ("skse64_loader.exe" or "skse64_steam_loader.dll") && !IsScript(name)) throw new IOException("Unsupported SKSE payload: " + name);
            Objects.Require(hash); files.Add(name, hash);
        }
        if (checkExe)
        {
            ExecutableVersion.Require(Objects.Require(files["SkyrimSE.exe"]), version.Version);
        }
        var receipt = new OverlayReceipt(version.Version, Profile, fingerprint, files);
        Storage.Write(ReceiptPath(version, "ready"), receipt);
        return receipt;
    }
    private OverlayReceipt Verify(RuntimeDefinition version)
    {
        var receipt = ReadReceipt(version, "ready", Fingerprint(version)) ?? throw new IOException("Prepare and verify this runtime/profile before activation. The catalog may have changed.");
        foreach (var file in Definition(version).Files.Where(f => f.Policy == "versioned"))
            if (!receipt.Files.TryGetValue(file.Path, out var hash) || (file.Sha256 != null && !file.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))) throw new IOException("Incomplete overlay receipt: " + file.Path);
        if (!receipt.Files.ContainsKey(version.SkseDll) || !receipt.Files.ContainsKey("skse64_loader.exe") || !receipt.Files.Keys.Any(IsScript)) throw new IOException("SKSE files missing from overlay.");
        var permitted = Definition(version).Files.Where(f => f.Policy == "versioned").Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in receipt.Files)
        {
            if (!permitted.Contains(name) && name != version.SkseDll && name is not ("skse64_loader.exe" or "skse64_steam_loader.dll") && !IsScript(name)) throw new IOException("Unexpected cached overlay file: " + name);
            Objects.Require(hash);
        }
        return receipt;
    }
    public string Status(RuntimeDefinition version)
    {
        try
        {
            RequireNoRecovery();
            Verify(version);
            if (Catalog.Normalization != null)
            {
                if (!File.Exists(NormalizationPath)) return "Incomplete / shared data not prepared";
                ValidateNormalization(Storage.Read<NormalizationReceipt>(NormalizationPath), true);
            }
            if (!File.Exists(StatePath)) return "Ready (offline)";
            var state = Storage.Read<OverlayState>(StatePath);
            var ccAbsent = !Definition(version).HideCreationClub || !Directory.EnumerateFiles(Path.Combine(Game, "Data")).Any(p => OverlayPaths.IsCreationClub("Data/" + Path.GetFileName(p)));
            return state.Version == version.Version && state.Profile == Profile && ccAbsent && state.Files.All(p => MatchesInstalled(p.Key, p.Value)) ? "Active (verified)" : "Ready (offline)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return "Incomplete / catalog changed"; }
    }
    private bool MatchesInstalled(string name, string? hash)
    {
        var path = RuntimeManager.Safe(Game, name);
        return hash == null ? !File.Exists(path) : File.Exists(path) && RuntimeManager.Hash(path) == hash;
    }
    public void Activate(RuntimeDefinition version, Action<int>? afterReplace = null) => ActivateInternal(version, afterReplace, true);
    internal void ActivateForTesting(RuntimeDefinition version, Action<int>? afterReplace = null) => ActivateInternal(version, afterReplace, false);
    private void ActivateInternal(RuntimeDefinition version, Action<int>? afterReplace, bool validateBaseline)
    {
        using var guard = Lock(); RequireNoRecovery(); CheckProcesses();
        var receipt = Verify(version); var profile = Definition(version);
        var previous = File.Exists(StatePath) ? Storage.Read<OverlayState>(StatePath) : null;
        if (Catalog.Normalization != null)
        {
            ActivateNormalized(version, receipt, profile, previous, afterReplace);
            return;
        }
        if (previous != null && previous.Files.Any(p => !MatchesInstalled(p.Key, p.Value)))
            throw new IOException("Managed files changed outside the switcher (Steam update or mod installation). Restore the saved baseline before activating another profile. Changed originals will be preserved in transaction history.");
        if (!File.Exists(BaselinePath) && validateBaseline)
        {
            if (ExecutableVersion.Read(Path.Combine(Game, "SkyrimSE.exe")) != profile.DataBaseline)
                throw new IOException($"Initial shared baseline must be Skyrim {profile.DataBaseline}. Restore/verify that Steam installation before the first overlay activation.");
            foreach (var (path, hash) in Catalog.BaselineHashes ?? [])
                if (!MatchesInstalled(path, hash.ToUpperInvariant())) throw new IOException("Shared baseline does not match the catalog: " + path + ". Verify game files with Steam before initial activation.");
        }
        var baseline = File.Exists(BaselinePath) ? Storage.Read<SharedBaseline>(BaselinePath) : new SharedBaseline(profile.DataBaseline, new(StringComparer.OrdinalIgnoreCase));
        if (baseline.Version != profile.DataBaseline) throw new IOException("Profile expects a different shared baseline. Restore the baseline and update the compatibility catalog first.");
        var desired = receipt.Files.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in profile.Files.Where(f => f.Policy == "absent")) desired[rule.Path] = null;
        foreach (var dll in Directory.EnumerateFiles(Game, "skse64_*.dll").Select(p => Path.GetFileName(p))) if (!desired.ContainsKey(dll)) desired[dll] = null;
        if (profile.HideCreationClub)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Game, "Data")).Select(p => "Data/" + Path.GetFileName(p)).Where(OverlayPaths.IsCreationClub)) desired[file] = null;
        var affected = desired.Keys.Concat(Catalog.Runtimes.SelectMany(r => r.Profiles).SelectMany(p => p.Files).Select(f => f.Path))
            .Concat(previous?.Files.Keys.AsEnumerable() ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Capture untouched shared paths once, before any overlay can modify them.
        foreach (var path in affected)
            if (!baseline.Files.ContainsKey(path)) baseline.Files[path] = File.Exists(RuntimeManager.Safe(Game, path)) ? Objects.Add(RuntimeManager.Safe(Game, path)) : null;
        foreach (var rule in profile.Files.Where(f => f.Policy == "shared"))
            if (baseline.Files[rule.Path] == null) throw new IOException("Required shared baseline file is absent: " + rule.Path);
        Storage.Write(BaselinePath, baseline);
        // Files no longer owned by the new profile return to the original shared baseline.
        foreach (var path in affected)
            if (!desired.ContainsKey(path)) desired[path] = baseline.Files[path];
        Transact(new OverlayState(version.Version, Profile, desired), previous, afterReplace);
    }
    public void RestoreBaseline(Action<int>? afterReplace = null)
    {
        using var guard = Lock(); RequireNoRecovery(); CheckProcesses();
        if (!File.Exists(BaselinePath)) throw new IOException("No baseline has been captured yet.");
        var baseline = Storage.Read<SharedBaseline>(BaselinePath);
        var previous = File.Exists(StatePath) ? Storage.Read<OverlayState>(StatePath) : null;
        Transact(new OverlayState(baseline.Version, "baseline", baseline.Files), previous, afterReplace);
    }
    private void Transact(OverlayState desired, OverlayState? previous, Action<int>? afterReplace)
    {
        Feedback?.Invoke(new("Checking files", "Verifying replacement files before changing the game…"));
        foreach (var (name, hash) in desired.Files)
        {
            ValidateManaged(name); if (hash != null && !MatchesInstalled(name, hash)) Objects.Require(hash);
        }
        var originals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in desired.Files.Keys)
        {
            Feedback?.Invoke(new("Preserving originals", "Checking and preserving " + name));
            if (MatchesInstalled(name, desired.Files[name])) continue;
            var path = RuntimeManager.Safe(Game, name);
            originals[name] = File.Exists(path) ? Objects.Add(path) : null;
        }
        // Append-only history retains unknown/modified originals after successful switches.
        var journal = new OverlayJournal(originals, previous);
        Storage.Write(RuntimeManager.Safe(Root, "transactions/history/" + Guid.NewGuid().ToString("N") + ".json"), journal);
        Storage.Write(JournalPath, journal);
        try
        {
            int count = 0;
            foreach (var (name, hash) in desired.Files)
            {
                Feedback?.Invoke(new("Installing files", name, count, desired.Files.Count));
                CheckProcesses();
                if (!MatchesInstalled(name, hash)) Install(name, hash);
                afterReplace?.Invoke(++count);
            }
            var verified = 0;
            foreach (var (name, hash) in desired.Files)
            {
                Feedback?.Invoke(new("Verifying installation", name, verified++, desired.Files.Count));
                if (!MatchesInstalled(name, hash)) throw new IOException("Installed verification failed: " + name);
            }
            Storage.Write(StatePath, desired);
            File.Delete(JournalPath);
        }
        catch (Exception failure)
        {
            Feedback?.Invoke(new("Restoring files", "The switch failed. Restoring the previous installation…"));
            try { RecoverInternal(journal); }
            catch (Exception recovery) { throw new IOException("The switch failed and automatic recovery could not finish. The journal has been retained.", new AggregateException(failure, recovery)); }
            throw;
        }
    }
    public void Recover()
    {
        using var guard = Lock(); CheckProcesses();
        if (File.Exists(Path.Combine(Root, "journal.json"))) throw new IOException("Use the earlier prototype recovery first; its journal and backups have been retained.");
        if (File.Exists(JournalPath)) RecoverInternal(Storage.Read<OverlayJournal>(JournalPath));
    }
    private void RecoverInternal(OverlayJournal journal)
    {
        foreach (var (name, hash) in journal.Originals) { ValidateManaged(name); if (hash != null) Objects.Require(hash); }
        foreach (var (name, hash) in journal.Originals) Install(name, hash);
        if (journal.PreviousState == null) { if (File.Exists(StatePath)) File.Delete(StatePath); }
        else Storage.Write(StatePath, journal.PreviousState);
        File.Delete(JournalPath);
    }
    private void Install(string name, string? hash)
    {
        ValidateManaged(name); var target = RuntimeManager.Safe(Game, name);
        if (hash == null) { if (File.Exists(target)) File.Delete(target); return; }
        var source = Objects.Require(hash); var temp = target + ".overlay.tmp";
        RuntimeManager.Safe(Game, name + ".overlay.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            File.Copy(source, temp, true);
            if (RuntimeManager.Hash(temp) != hash) throw new IOException("Staged hash mismatch: " + name);
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void ValidateManaged(string name) { if (!OverlayPaths.Allowed(name) && !OfficialGamePaths.Allowed(name)) throw new IOException("Unsupported managed file: " + name); }
    private static void CheckProcesses()
    {
        foreach (var name in new[] { "SkyrimSE", "SkyrimSELauncher", "skse64_loader" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) throw new IOException("Close Skyrim, its launcher and SKSE first."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
