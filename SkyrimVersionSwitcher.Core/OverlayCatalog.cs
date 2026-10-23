using System.Text.Json.Serialization;

namespace SkyrimVersionSwitcher.Core;

public sealed record DepotSource(uint App, uint Depot, string Manifest, string Release);
public sealed record OverlayFile(string Path, string Policy, string? Source = null, string? Sha256 = null, string? Reason = null);
public sealed record CompatibilityProfile(string Id, string Name, string DataBaseline, string Validation, OverlayFile[] Files, bool HideCreationClub = false)
{
    public override string ToString() => Name;
}
public sealed record RuntimeOverlay(string Version, CompatibilityProfile[] Profiles);
public sealed record NormalizationDefinition(string Version, string[] Sources);
public sealed record CompatibilityCatalog(int Schema, Dictionary<string, DepotSource> Sources, RuntimeOverlay[] Runtimes, Dictionary<string, string>? BaselineHashes = null, NormalizationDefinition? Normalization = null)
{
    public CompatibilityProfile Resolve(string version, string profile)
    {
        Validate();
        return Runtimes.SingleOrDefault(r => r.Version == version)?.Profiles.SingleOrDefault(p => p.Id == profile)
            ?? throw new IOException($"No compatibility definition for {version}/{profile}.");
    }
    public void Validate()
    {
        if (Schema != 1) throw new IOException("Unsupported compatibility catalog schema.");
        foreach (var (path, hash) in BaselineHashes ?? [])
        { if (!OverlayPaths.Allowed(path)) throw new IOException("Invalid baseline path."); ObjectStore.ValidateHash(hash); }
        if (Runtimes.Select(r => r.Version).Distinct().Count() != Runtimes.Length) throw new IOException("Duplicate runtime definitions.");
        foreach (var source in Sources.Values)
            if (source.App != 489830 || source.Depot is not (489831 or 489832 or 489833) || !ulong.TryParse(source.Manifest, out var id) || id == 0)
                throw new IOException("Invalid Skyrim Steam source.");
        if (Normalization != null)
        {
            ValidateId(Normalization.Version);
            if (Normalization.Sources.Length == 0 || Normalization.Sources.Distinct().Count() != Normalization.Sources.Length || Normalization.Sources.Any(s => !Sources.ContainsKey(s))) throw new IOException("Invalid normalization source catalog.");
            if (Normalization.Sources.Any(s => Sources[s].Release != Normalization.Version)) throw new IOException("Normalization sources must describe the same release.");
            if (Runtimes.SelectMany(r => r.Profiles).Any(p => p.DataBaseline != Normalization.Version)) throw new IOException("Runtime profiles must match the normalized data baseline.");
        }
        foreach (var runtime in Runtimes)
        {
            ValidateId(runtime.Version);
            if (runtime.Profiles.Select(p => p.Id).Distinct().Count() != runtime.Profiles.Length) throw new IOException("Duplicate profiles.");
            foreach (var profile in runtime.Profiles)
            {
                ValidateId(profile.Id);
                if (profile.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Files.Length) throw new IOException("Duplicate overlay paths.");
                if (!profile.Files.Any(f => f.Path == "SkyrimSE.exe" && f.Policy == "versioned")) throw new IOException("A runtime requires a versioned executable.");
                foreach (var file in profile.Files)
                {
                    if (!OverlayPaths.Allowed(file.Path) || file.Path.Contains('\\')) throw new IOException("Unsupported overlay path: " + file.Path);
                    if (file.Policy is not ("versioned" or "shared" or "absent")) throw new IOException("Unknown file policy: " + file.Policy);
                    if (file.Policy == "versioned" && (file.Source == null || !Sources.ContainsKey(file.Source))) throw new IOException("Missing source: " + file.Path);
                    if (file.Sha256 != null) ObjectStore.ValidateHash(file.Sha256);
                }
            }
        }
    }
    internal static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) throw new IOException("Invalid catalog identifier.");
    }
    public static string Fingerprint(CompatibilityProfile profile, CompatibilityCatalog catalog) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Profile = profile, catalog.BaselineHashes, Normalization = catalog.Normalization == null ? null : BaselineNormalization.Identity(catalog), Sources = profile.Files.Where(f => f.Policy == "versioned").Select(f => f.Source!).Distinct().Order().ToDictionary(s => s, s => catalog.Sources[s]) })));
}
public static class OverlayPaths
{
    public static bool Allowed(string path) => RuntimeManager.Allowed(path)
        || new[] { "Data/Skyrim - Interface.bsa", "Data/Skyrim.esm", "Data/Update.esm", "Data/Dawnguard.esm", "Data/HearthFires.esm", "Data/Dragonborn.esm", "ContentCatalog.txt", "Data/ContentCatalog.txt" }.Contains(path, StringComparer.OrdinalIgnoreCase)
        || IsCreationClub(path);
    public static bool IsCreationClub(string path) => path.StartsWith("Data/cc", StringComparison.OrdinalIgnoreCase)
        && !path[5..].Contains('/') && !path.Contains('\\')
        && new[] { ".bsa", ".esl", ".esm", ".esp" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
public sealed record OverlayReceipt(string Version, string Profile, string CatalogHash, Dictionary<string, string> Files);
public sealed record OverlayState(string Version, string Profile, Dictionary<string, string?> Files);
public sealed record SharedBaseline(string Version, Dictionary<string, string?> Files);
public sealed record OverlayJournal(Dictionary<string, string?> Originals, OverlayState? PreviousState);
