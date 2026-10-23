using System.Diagnostics;
using System.Security.Cryptography;

namespace SkyrimVersionSwitcher.Core;

public sealed class RuntimeManager
{
    public string Game { get; }
    public string Root => Path.Combine(Game, "RuntimeManager");
    public string JournalPath => Path.Combine(Root, "journal.json");
    public RuntimeManager(string game)
    {
        Game = Path.GetFullPath(game);
        if (!File.Exists(Path.Combine(Game, "SkyrimSE.exe")) || !Directory.Exists(Path.Combine(Game, "Data")))
            throw new IOException("Choose a Skyrim Special Edition installation containing SkyrimSE.exe and Data.");
        RejectLinks(Game);
    }
    public string Cache(RuntimeDefinition version) => Safe(Root, "versions/" + version.Version);
    public static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static string Safe(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("Invalid relative path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path escapes storage root.");
        RejectLinks(path);
        return path;
    }
    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if ((Directory.Exists(current.FullName) || File.Exists(current.FullName)) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Managed paths must not contain junctions or symbolic links: " + current.FullName);
    }
    private FileStream Lock()
    {
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static void CheckProcesses()
    {
        foreach (var name in new[] { "SkyrimSE", "SkyrimSELauncher", "skse64_loader" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) throw new IOException("Close Skyrim, its launcher and SKSE before switching."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public static bool Allowed(string path) => new[] { "SkyrimSE.exe", "SkyrimSELauncher.exe", "steam_api64.dll", "binkw64.dll", "bink2w64.dll", "Data/Skyrim - Shaders.bsa", "skse64_loader.exe", "skse64_steam_loader.dll" }.Contains(path, StringComparer.OrdinalIgnoreCase)
        || (path.StartsWith("skse64_", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !path.Contains('/') && !path.Contains('\\'))
        || (path.StartsWith("Data/Scripts/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase) && !path[13..].Contains('/') && !path.Contains('\\'));
    public void ImportGame(RuntimeDefinition version, string source)
    {
        using var guard = Lock();
        var exe = Path.Combine(source, "SkyrimSE.exe");
        ExecutableVersion.Require(exe, version.Version);
        var manifestPath = Path.Combine(Cache(version), "cache.json");
        if (File.Exists(manifestPath)) File.Delete(manifestPath);
        foreach (var name in version.RequiredFiles.Concat(new[] { "steam_api64.dll", "binkw64.dll", "bink2w64.dll" }).Distinct())
        {
            var input = Safe(source, name);
            if (File.Exists(input)) Copy(input, Safe(Cache(version), name));
        }
    }
    public void ImportSkse(RuntimeDefinition version, string source)
    {
        using var guard = Lock();
        if (!File.Exists(Path.Combine(source, version.SkseDll)) || !File.Exists(Path.Combine(source, "skse64_loader.exe")))
            throw new IOException("Select the extracted SKSE folder containing " + version.SkseDll);
        var manifestPath = Path.Combine(Cache(version), "cache.json");
        if (File.Exists(manifestPath)) File.Delete(manifestPath);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (Allowed(relative) && (relative.StartsWith("skse64_", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Data/Scripts/", StringComparison.OrdinalIgnoreCase)))
                Copy(Safe(source, relative), Safe(Cache(version), relative));
        }
    }
    public CacheManifest Seal(RuntimeDefinition version) => SealForTesting(version, true);
    internal CacheManifest SealForTesting(RuntimeDefinition version, bool checkExe = false)
    {
        using var guard = Lock();
        var root = Cache(version);
        foreach (var name in version.RequiredFiles.Concat(new[] { "skse64_loader.exe", version.SkseDll }))
            if (!File.Exists(Safe(root, name))) throw new IOException("Missing cached file: " + name + ". Import the matching game/depot files first.");
        if (!Directory.Exists(Safe(root, "Data/Scripts")) || !Directory.EnumerateFiles(Safe(root, "Data/Scripts"), "*.pex").Any())
            throw new IOException("SKSE Data/Scripts files are missing.");
        if (checkExe)
        {
            ExecutableVersion.Require(Safe(root, "SkyrimSE.exe"), version.Version);
        }
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(Allowed)
            .ToDictionary(p => p, p => Hash(Safe(root, p)), StringComparer.OrdinalIgnoreCase);
        var manifest = new CacheManifest(version.Version, files);
        Storage.Write(Path.Combine(root, "cache.json"), manifest);
        return manifest;
    }
    public CacheManifest Verify(RuntimeDefinition version)
    {
        var manifest = Storage.Read<CacheManifest>(Path.Combine(Cache(version), "cache.json"));
        if (manifest.Version != version.Version) throw new IOException("Cache version mismatch.");
        foreach (var required in version.RequiredFiles.Concat(new[] { "skse64_loader.exe", version.SkseDll }))
            if (!manifest.Files.ContainsKey(required)) throw new IOException("Incomplete cache manifest: " + required);
        foreach (var (name, hash) in manifest.Files)
            if (!Allowed(name) || Hash(Safe(Cache(version), name)) != hash) throw new IOException("Cache verification failed: " + name);
        return manifest;
    }
    public string Status(RuntimeDefinition version)
    {
        try
        {
            var manifest = Verify(version);
            return manifest.Files.All(p => File.Exists(Safe(Game, p.Key)) && Hash(Safe(Game, p.Key)) == p.Value) ? "Active (verified)" : "Ready";
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return "Incomplete / not verified"; }
    }
    public void Activate(RuntimeDefinition version, Action<int>? afterReplace = null)
    {
        using var guard = Lock();
        CheckProcesses();
        if (File.Exists(JournalPath)) throw new IOException("Recover the interrupted operation first.");
        var manifest = Verify(version);
        var backup = "backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        var affected = manifest.Files.Keys.Concat(Directory.EnumerateFiles(Game, "skse64_*.dll").Select(Path.GetFileName).OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activePath = Path.Combine(Root, "active.json");
        if (File.Exists(activePath))
            foreach (var name in Storage.Read<CacheManifest>(activePath).Files.Keys)
            { if (!Allowed(name)) throw new IOException("Invalid previous manifest."); affected.Add(name); }
        var originals = new Dictionary<string, string?>();
        foreach (var name in affected)
        {
            var path = Safe(Game, name);
            originals[name] = File.Exists(path) ? Hash(path) : null;
            if (File.Exists(path)) Copy(path, Safe(Root, backup + "/" + name));
        }
        // Backups are complete before the durable journal authorizes any game changes.
        var journal = new Journal(backup, originals);
        Storage.Write(JournalPath, journal);
        try
        {
            int count = 0;
            foreach (var name in affected)
            {
                CheckProcesses();
                var target = Safe(Game, name);
                if (manifest.Files.TryGetValue(name, out var hash))
                {
                    Replace(Safe(Cache(version), name), target);
                    if (Hash(target) != hash) throw new IOException("Installed hash mismatch: " + name);
                }
                else File.Delete(target);
                afterReplace?.Invoke(++count);
            }
            Storage.Write(activePath, manifest);
            File.Delete(JournalPath);
        }
        catch { RecoverInternal(journal); throw; }
    }
    public void Recover()
    {
        using var guard = Lock(); CheckProcesses();
        if (File.Exists(JournalPath)) RecoverInternal(Storage.Read<Journal>(JournalPath));
    }
    private void RecoverInternal(Journal journal)
    {
        // Validate every backup before changing anything. Keep the journal on any failure.
        foreach (var (name, hash) in journal.Originals)
            if (!Allowed(name) || (hash != null && Hash(Safe(Root, journal.Backup + "/" + name)) != hash)) throw new IOException("Recovery backup invalid: " + name);
        foreach (var (name, hash) in journal.Originals)
        {
            if (hash == null)
            {
                var path = Safe(Game, name);
                if (File.Exists(path)) File.Delete(path);
            }
            else Replace(Safe(Root, journal.Backup + "/" + name), Safe(Game, name));
        }
        File.Delete(Path.Combine(Root, "active.json"));
        File.Delete(JournalPath);
    }
    private static void Copy(string source, string target) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true); }
    private static void Replace(string source, string target)
    {
        var temp = target + ".runtime-switch.tmp";
        Copy(source, temp);
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
        File.Move(temp, target, true);
    }
}
