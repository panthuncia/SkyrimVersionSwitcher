using System.Security.Cryptography;
using System.Text.Json;

namespace SkyrimVersionSwitcher.Core;

public sealed record BaselineAsset(string Source, long Size, string Sha1, string? Sha256, bool Verified = false);
public sealed record NormalizationReceipt(string Version, string CatalogHash, Dictionary<string, BaselineAsset> Files);
public static class BaselineNormalization
{
    public static string Identity(CompatibilityCatalog catalog) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        catalog.Normalization, catalog.BaselineHashes,
        Sources = catalog.Normalization!.Sources.Order().ToDictionary(s => s, s => catalog.Sources[s])
    })));
}
public static class OfficialGamePaths
{
    // The full path must also occur in an acquired, identity-checked Steam manifest.
    public static bool Allowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':'))) return false;
        if (new[] { "SkyrimSE.exe", "SkyrimSELauncher.exe", "steam_api64.dll", "bink2w64.dll", "High.ini", "Low.ini", "Medium.ini", "Ultra.ini", "Skyrim_Default.ini", "Skyrim.ccc", "Skyrim/SkyrimPrefs.ini", "installscript.vdf" }.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
        return path.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("Data/SKSE/", StringComparison.OrdinalIgnoreCase)
            && new[] { ".bsa", ".esm", ".esl", ".esp", ".bik", ".strings", ".ilstrings", ".dlstrings", ".pex" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}

public sealed partial class OverlayManager
{
    private string NormalizationPath => RuntimeManager.Safe(Root, "normalization.json");
    private string OriginalPath => RuntimeManager.Safe(Root, "original-installation.json");

    /// <summary>Inspect all pinned vanilla depot files; cache only missing/changed content.
    /// Matching installed archives remain shared in place rather than copied into the cache.</summary>
    public async Task PrepareNormalization(ISteamDepotClient client, Action<string>? progress = null, Action<WorkProgress>? feedback = null)
    {
        if (Catalog.Normalization == null) return;
        using var guard = Lock(); RequireNoRecovery();
        var definition = Catalog.Normalization;
        var identity = BaselineNormalization.Identity(Catalog);
        var receipt = File.Exists(NormalizationPath) ? Storage.Read<NormalizationReceipt>(NormalizationPath) : null;
        if (receipt?.CatalogHash != identity)
        {
            var files = new Dictionary<string, BaselineAsset>(StringComparer.OrdinalIgnoreCase);
            progress?.Invoke("Checking installed game data against the supported Steam release…");
            foreach (var key in definition.Sources)
            {
                feedback?.Invoke(new("Shared game data", "Reading the list of required game files…"));
                var source = Catalog.Sources[key];
                var manifest = await client.Manifest(source, SourceDirectory(source));
                if (manifest.Depot != source.Depot || manifest.Id != source.Manifest || manifest.Files.Count == 0) throw new AcquisitionException(AcquisitionFailure.InvalidManifest, "Wrong or empty baseline Steam manifest.");
                foreach (var (path, file) in manifest.Files)
                {
                    if (!OfficialGamePaths.Allowed(path)) throw new IOException("Unsupported official baseline path: " + path);
                    RuntimeManager.Safe(Game, path);
                    var pinned = Catalog.BaselineHashes?.FirstOrDefault(p => p.Key.Equals(path, StringComparison.OrdinalIgnoreCase)).Value;
                    if (!files.TryAdd(path, new BaselineAsset(key, file.Size, file.Sha1, pinned?.ToUpperInvariant())))
                        throw new IOException("Conflicting baseline depots contain the same path: " + path);
                }
            }
            receipt = new NormalizationReceipt(definition.Version, identity, files);
            ValidateNormalization(receipt, false);
            // A failed download can reuse the complete, validated manifest plan on retry.
            Storage.Write(NormalizationPath, receipt);
        }
        receipt = receipt! with { Files = new(receipt!.Files, StringComparer.OrdinalIgnoreCase) };
        ValidateNormalization(receipt, false);
        var indexPath = RuntimeManager.Safe(Root, "steam-objects.json");
        var index = File.Exists(indexPath) ? Storage.Read<Dictionary<string, string>>(indexPath) : new Dictionary<string, string>();
        var checkedFiles = 0;
        foreach (var group in receipt.Files.GroupBy(p => p.Value.Source).ToArray())
        {
            var source = Catalog.Sources[group.Key]; var output = SourceDirectory(source);
            var missing = new List<string>();
            foreach (var (path, asset) in group)
            {
                feedback?.Invoke(new("Checking shared data", path, checkedFiles++, receipt.Files.Count));
                if (asset.Verified && asset.Sha256 != null && (Objects.Contains(asset.Sha256) || MatchesInstalled(path, asset.Sha256))) continue;
                var expected = new SteamFile(path, asset.Size, asset.Sha1);
                var installed = RuntimeManager.Safe(Game, path);
                var downloaded = RuntimeManager.Safe(output, path);
                if (asset.Sha256 != null && Objects.Contains(asset.Sha256))
                {
                    if (!SteamManifest.Matches(Objects.Require(asset.Sha256), expected)) throw new AcquisitionException(AcquisitionFailure.ContentMismatch, "Catalog content disagrees with the Steam baseline manifest: " + path);
                    receipt.Files[path] = asset with { Verified = true };
                }
                else if (SteamManifest.Matches(installed, expected))
                {
                    var hash = RuntimeManager.Hash(installed);
                    if (asset.Sha256 != null && !asset.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Baseline catalog hash disagrees with Steam content: " + path);
                    receipt.Files[path] = asset with { Sha256 = hash, Verified = true };
                    progress?.Invoke("Reusing installed game file: " + path);
                }
                else if (SteamManifest.Matches(downloaded, expected))
                    receipt.Files[path] = asset with { Sha256 = Objects.Add(downloaded, asset.Sha256), Verified = true };
                else missing.Add(path);
            }
            Storage.Write(NormalizationPath, receipt);
            if (missing.Count > 0)
            {
                progress?.Invoke($"Preparing shared game data: downloading {missing.Count} missing or changed file(s)…");
                feedback?.Invoke(new("Shared data download", $"Downloading {missing.Count} missing or changed game file(s)…"));
                await client.Download(source, missing, output);
                var verified = 0;
                foreach (var path in missing)
                {
                    feedback?.Invoke(new("Verifying shared data", path, verified++, missing.Count));
                    var asset = receipt.Files[path]; var file = RuntimeManager.Safe(output, path);
                    if (!SteamManifest.Matches(file, new SteamFile(path, asset.Size, asset.Sha1))) throw new AcquisitionException(AcquisitionFailure.ContentMismatch, "Shared game-data download failed verification: " + path + ". Your installed files have not changed.");
                    receipt.Files[path] = asset with { Sha256 = Objects.Add(file, asset.Sha256), Verified = true };
                    Storage.Write(NormalizationPath, receipt);
                }
            }
            foreach (var (path, _) in group)
            {
                var asset = receipt.Files[path];
                if (asset.Sha256 is string objectHash && Objects.Contains(objectHash)) index[asset.Sha1 + ":" + asset.Size] = objectHash;
                var file = RuntimeManager.Safe(output, path);
                if (File.Exists(file) && receipt.Files[path].Sha256 is string hash && Objects.Contains(hash)) File.Delete(file);
            }
            Storage.Write(indexPath, index);
        }
        ValidateNormalization(receipt, true);
        Storage.Write(NormalizationPath, receipt);
        progress?.Invoke("Shared game data is prepared. Your installed version has not changed.");
        feedback?.Invoke(new("Shared data ready", "Shared game data is verified; your installed version has not changed.", receipt.Files.Count, receipt.Files.Count));
    }
    private void ValidateNormalization(NormalizationReceipt receipt, bool complete)
    {
        var definition = Catalog.Normalization ?? throw new IOException("No normalization definition.");
        if (receipt.Version != definition.Version || receipt.CatalogHash != BaselineNormalization.Identity(Catalog) || !receipt.Files.ContainsKey("SkyrimSE.exe")) throw new IOException("Invalid shared-data receipt. Prepare game data again.");
        foreach (var (path, asset) in receipt.Files)
        {
            if (!OfficialGamePaths.Allowed(path) || !definition.Sources.Contains(asset.Source) || asset.Size < 0 || asset.Sha1.Length != 40 || asset.Sha1.Any(c => !Uri.IsHexDigit(c))) throw new IOException("Invalid shared-data content record: " + path);
            RuntimeManager.Safe(Game, path);
            if (asset.Sha256 != null) ObjectStore.ValidateHash(asset.Sha256);
            if (complete && (asset.Sha256 == null || !asset.Verified)) throw new IOException("Shared game data is incomplete: " + path);
        }
        foreach (var (path, hash) in Catalog.BaselineHashes ?? [])
            if (!receipt.Files.TryGetValue(path, out var asset) || asset.Sha256 == null || !asset.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Pinned baseline file missing or mismatched: " + path);
    }
    private NormalizationReceipt RequireNormalization()
    {
        if (!File.Exists(NormalizationPath)) throw new IOException("Shared game data must be prepared automatically before switching.");
        var receipt = Storage.Read<NormalizationReceipt>(NormalizationPath);
        ValidateNormalization(receipt, true);
        foreach (var (path, asset) in receipt.Files)
            if (!Objects.Contains(asset.Sha256!) && !MatchesInstalled(path, asset.Sha256)) throw new IOException("Shared game data needs preparation: " + path);
        return receipt;
    }
    private void ActivateNormalized(RuntimeDefinition version, OverlayReceipt overlay, CompatibilityProfile profile, OverlayState? previous, Action<int>? afterReplace)
    {
        var normalized = RequireNormalization();
        if (profile.DataBaseline != normalized.Version) throw new IOException("Runtime and shared-data definitions disagree.");
        var oldBaseline = File.Exists(BaselinePath) ? Storage.Read<SharedBaseline>(BaselinePath) : null;
        var baseline = new SharedBaseline(normalized.Version, normalized.Files.ToDictionary(p => p.Key, p => p.Value.Sha256, StringComparer.OrdinalIgnoreCase));
        var desired = new Dictionary<string, string?>(baseline.Files, StringComparer.OrdinalIgnoreCase);
        var affected = overlay.Files.Keys.Concat(Catalog.Runtimes.SelectMany(r => r.Profiles).SelectMany(p => p.Files).Select(f => f.Path))
            .Concat(previous?.Files.Keys.AsEnumerable() ?? []).Concat(Directory.EnumerateFiles(Game, "skse64_*.dll").Select(Path.GetFileName).OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (profile.HideCreationClub)
            affected.UnionWith(Directory.EnumerateFiles(Path.Combine(Game, "Data")).Select(p => "Data/" + Path.GetFileName(p)).Where(OverlayPaths.IsCreationClub));
        foreach (var path in affected)
        {
            if (desired.ContainsKey(path)) continue;
            ValidateManaged(path);
            // Vanilla has no legacy bink or SKSE payload. Loose SKSE scripts owned by this
            // tool must not survive a transition into another runtime.
            var remove = path.Equals("binkw64.dll", StringComparison.OrdinalIgnoreCase) || path.StartsWith("skse64_", StringComparison.OrdinalIgnoreCase) || IsScript(path);
            var hash = remove ? null : oldBaseline?.Files.GetValueOrDefault(path) ?? (File.Exists(RuntimeManager.Safe(Game, path)) ? Objects.Add(RuntimeManager.Safe(Game, path)) : null);
            desired[path] = hash; baseline.Files[path] = hash;
        }
        foreach (var (path, hash) in overlay.Files) desired[path] = hash;
        foreach (var rule in profile.Files.Where(f => f.Policy == "absent")) desired[rule.Path] = null;
        if (profile.HideCreationClub)
            foreach (var path in desired.Keys.Where(OverlayPaths.IsCreationClub).ToArray()) desired[path] = null;
        foreach (var rule in profile.Files.Where(f => f.Policy == "shared"))
            if (!baseline.Files.TryGetValue(rule.Path, out var hash) || hash == null) throw new IOException("Shared-data source does not supply " + rule.Path);
        CaptureOriginals(desired, oldBaseline, previous);
        Storage.Write(BaselinePath, baseline);
        Transact(new OverlayState(version.Version, Profile, desired), previous, afterReplace);
    }
    private void CaptureOriginals(Dictionary<string, string?> desired, SharedBaseline? previousBaseline, OverlayState? previous)
    {
        var original = File.Exists(OriginalPath) ? Storage.Read<SharedBaseline>(OriginalPath) : new SharedBaseline("original", new(StringComparer.OrdinalIgnoreCase));
        foreach (var (path, hash) in desired)
        {
            if (original.Files.ContainsKey(path)) continue;
            // Unchanged bulk vanilla assets remain in place. Capture all overlay-owned files
            // and every file normalization actually changes, including original absence.
            if (!OverlayPaths.Allowed(path) && MatchesInstalled(path, hash)) continue;
            ValidateManaged(path);
            if (previous != null && previousBaseline?.Files.TryGetValue(path, out var prior) == true) original.Files[path] = prior;
            else original.Files[path] = File.Exists(RuntimeManager.Safe(Game, path)) ? Objects.Add(RuntimeManager.Safe(Game, path)) : null;
        }
        Storage.Write(OriginalPath, original);
    }
    public void RestoreOriginal(Action<int>? afterReplace = null)
    {
        using var guard = Lock(); RequireNoRecovery(); CheckProcesses();
        if (!File.Exists(OriginalPath)) throw new IOException("No original installation has been captured yet. Files are preserved automatically on your first switch.");
        var original = Storage.Read<SharedBaseline>(OriginalPath);
        var previous = File.Exists(StatePath) ? Storage.Read<OverlayState>(StatePath) : null;
        Transact(new OverlayState("original", "original", original.Files), previous, afterReplace);
    }
}
