using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SkyrimVersionSwitcher.Core;

public enum AcquisitionFailure { SteamAccessOrTransport, InvalidManifest, MissingFile, ContentMismatch }
public sealed class AcquisitionException(AcquisitionFailure failure, string message) : IOException(message)
{
    public AcquisitionFailure Failure { get; } = failure;
}
public sealed record SteamFile(string Path, long Size, string Sha1);
public sealed record SteamManifest(uint Depot, string Id, Dictionary<string, SteamFile> Files)
{
    // DepotDownloader -manifest-only's documented human-readable dump.
    public static SteamManifest Parse(string text, DepotSource source)
    {
        if (!Regex.IsMatch(text, @"Content Manifest for Depot\s+" + source.Depot + @"\s") || !Regex.IsMatch(text, @"Manifest ID / date\s*:\s*" + source.Manifest + @"\s*/"))
            throw new AcquisitionException(AcquisitionFailure.InvalidManifest, "Steam manifest identity does not match requested source.");
        var files = new Dictionary<string, SteamFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^\s*(\d+)\s+\d+\s+([a-fA-F0-9]{40})\s+([a-fA-F0-9]+)\s+(.+)$");
            if (!match.Success) continue;
            var flags = Convert.ToUInt32(match.Groups[3].Value, 16);
            if ((flags & 64) != 0) continue; // Steam directory flag.
            var name = match.Groups[4].Value.Replace('\\', '/');
            // Validate even paths not selected by our overlay.
            RuntimeManager.Safe(System.IO.Path.GetTempPath(), name);
            if (!files.TryAdd(name, new SteamFile(name, long.Parse(match.Groups[1].Value), match.Groups[2].Value.ToUpperInvariant())))
                throw new AcquisitionException(AcquisitionFailure.InvalidManifest, "Duplicate Steam manifest path: " + name);
        }
        if (files.Count == 0) throw new AcquisitionException(AcquisitionFailure.InvalidManifest, "Steam manifest contains no recognized file records. DepotDownloader's format may have changed.");
        return new SteamManifest(source.Depot, source.Manifest, files);
    }
    public static bool Matches(string path, SteamFile expected)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Size) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).Equals(expected.Sha1, StringComparison.OrdinalIgnoreCase);
    }
    public static ManifestDifference[] Compare(SteamManifest before, SteamManifest after)
    {
        if (before.Depot != after.Depot) throw new IOException("Compare manifests from the same depot.");
        return before.Files.Keys.Union(after.Files.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ManifestDifference(path, !before.Files.ContainsKey(path) ? "Added" : !after.Files.ContainsKey(path) ? "Removed" : before.Files[path].Sha1 != after.Files[path].Sha1 || before.Files[path].Size != after.Files[path].Size ? "Changed" : "Unchanged", "Unclassified"))
            .Where(d => d.Change != "Unchanged").ToArray();
    }
}
public sealed record ManifestDifference(string Path, string Change, string Classification);
public interface ISteamDepotClient
{
    Task<SteamManifest> Manifest(DepotSource source, string output);
    Task Download(DepotSource source, IReadOnlyList<string> files, string output);
}
public sealed class SteamDepotClient(string tool, string username, bool rememberLogin = true, string? sessionDirectory = null, Action<WorkProgress>? feedback = null) : ISteamDepotClient
{
    public static string[] Arguments(DepotSource source, string username, string output, string? fileList, bool rememberLogin = true)
    {
        var result = new List<string> { "-app", source.App.ToString(), "-depot", source.Depot.ToString(), "-manifest", source.Manifest, "-username", username, "-dir", System.IO.Path.GetFullPath(output) };
        if (rememberLogin) result.Add("-remember-password");
        if (fileList == null) result.Add("-manifest-only");
        else { result.AddRange(["-filelist", System.IO.Path.GetFullPath(fileList), "-validate"]); }
        return result.ToArray();
    }
    public async Task<SteamManifest> Manifest(DepotSource source, string output)
    {
        Directory.CreateDirectory(output);
        var path = RuntimeManager.Safe(output, $"manifest_{source.Depot}_{source.Manifest}.txt");
        if (!File.Exists(path)) await Run(Arguments(source, username, output, null, rememberLogin));
        if (!File.Exists(path)) throw new AcquisitionException(AcquisitionFailure.SteamAccessOrTransport, "Steam did not return the requested manifest. Check the downloader console for authentication, historical access or network errors.");
        return SteamManifest.Parse(await File.ReadAllTextAsync(path), source);
    }
    public async Task Download(DepotSource source, IReadOnlyList<string> files, string output)
    {
        if (files.Count == 0) return;
        Directory.CreateDirectory(output);
        var list = RuntimeManager.Safe(output, "selected-files.txt");
        foreach (var file in files)
        {
            RuntimeManager.Safe(output, file);
            if (file.Contains('\n') || file.Contains('\r') || file.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) throw new IOException("Only literal Steam file paths are supported.");
        }
        await File.WriteAllLinesAsync(list, files, new System.Text.UTF8Encoding(false));
        await Run(Arguments(source, username, output, list, rememberLogin));
    }
    private async Task Run(string[] args)
    {
        feedback?.Invoke(new("Steam download", "Steam is working in its console window. Complete password / Steam Guard there if requested; download progress is shown there."));
        if (!File.Exists(tool) || !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Select DepotDownloader.exe.");
        if (string.IsNullOrWhiteSpace(username)) throw new IOException("Enter your Steam username.");
        var workingDirectory = sessionDirectory ?? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(tool))!;
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo(System.IO.Path.GetFullPath(tool)) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = workingDirectory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot launch DepotDownloader.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new AcquisitionException(AcquisitionFailure.SteamAccessOrTransport, $"DepotDownloader exited with {process.ExitCode}. This is an acquisition failure, not a compatibility verdict. Check its console for Steam access/authentication or network errors; retry resumes partial files.");
    }
}
