using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace SkyrimVersionSwitcher.Core;

public sealed record GithubRelease(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] GithubAsset[] Assets);
public sealed record GithubAsset(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("browser_download_url")] string Url,
    [property: JsonPropertyName("digest")] string? Digest);
public sealed record InstalledDepotDownloader(string Tag, string Directory, Dictionary<string, string> Files, string ArchiveSha256, bool UpstreamDigestVerified);

/// <summary>Downloads the official standalone Windows tool into versioned user-local storage.</summary>
public sealed class DepotDownloaderInstaller
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimVersionSwitcher", "tools");
    private static readonly HttpClient SharedClient = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1);
    private readonly string root;
    private readonly HttpClient client;
    public DepotDownloaderInstaller(string? root = null, HttpClient? client = null)
    {
        this.root = Path.GetFullPath(root ?? DefaultRoot);
        this.client = client ?? SharedClient;
    }
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyrimVersionSwitcher/1.0");
        return client;
    }
    public static GithubAsset SelectAsset(GithubRelease release, Architecture architecture)
    {
        if (release.Draft || release.Prerelease || release.Id <= 0) throw new IOException("GitHub did not return a stable DepotDownloader release.");
        var platform = architecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new IOException("DepotDownloader requires 64-bit Windows.") };
        var asset = release.Assets.SingleOrDefault(a => a.Name.Equals($"DepotDownloader-windows-{platform}.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("The latest official release has no standalone Windows " + platform + " build.");
        if (asset.Id <= 0 || asset.Size is <= 0 or > 134217728) throw new IOException("Invalid DepotDownloader release asset size or identity.");
        var uri = new Uri(asset.Url);
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/SteamRE/DepotDownloader/releases/download/", StringComparison.Ordinal))
            throw new IOException("DepotDownloader must come from the official SteamRE GitHub release.");
        if (asset.Digest != null && (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || asset.Digest.Length != 71)) throw new IOException("Unrecognized GitHub asset digest.");
        return asset;
    }
    public async Task<string> EnsureLatest(Action<string>? progress = null, CancellationToken cancellationToken = default, Action<WorkProgress>? feedback = null)
    {
        await Gate.WaitAsync(cancellationToken);
        string? staging = null;
        try
        {
            RuntimeManager.Safe(Path.GetDirectoryName(root)!, Path.GetFileName(root));
            Directory.CreateDirectory(root);
            using var installLock = new FileStream(RuntimeManager.Safe(root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var existing = ReadInstalled();
            GithubRelease release;
            try
            {
                progress?.Invoke("Checking the latest official DepotDownloader release…");
                feedback?.Invoke(new("Download tool", "Checking the official DepotDownloader release…"));
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/SteamRE/DepotDownloader/releases/latest");
                request.Headers.UserAgent.ParseAdd("SkyrimVersionSwitcher/1.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                release = await response.Content.ReadFromJsonAsync<GithubRelease>(cancellationToken) ?? throw new IOException("GitHub returned no release information.");
            }
            catch (Exception ex) when (existing != null && !cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                progress?.Invoke("Update check unavailable; using verified cached DepotDownloader " + existing.Tag + ".");
                return Executable(existing);
            }
            var asset = SelectAsset(release, RuntimeInformation.OSArchitecture);
            if (existing?.Tag == release.Tag && existing.Directory.StartsWith($"asset-{asset.Id}-", StringComparison.Ordinal))
            { progress?.Invoke("DepotDownloader " + release.Tag + " is ready."); return Executable(existing); }
            staging = RuntimeManager.Safe(root, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var archive = RuntimeManager.Safe(staging, "release.zip");
            progress?.Invoke("Downloading DepotDownloader " + release.Tag + "…");
            using (var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var final = response.RequestMessage?.RequestUri;
                if (final == null || final.Scheme != "https" || final.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new IOException("Unexpected GitHub release download redirect.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(archive);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > asset.Size) throw new IOException("DepotDownloader archive exceeds its published size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    feedback?.Invoke(new("Download tool", "Downloading DepotDownloader…", total, asset.Size, "bytes"));
                }
                if (total != asset.Size) throw new IOException("DepotDownloader download was incomplete. Retry to download it again.");
            }
            var archiveHash = RuntimeManager.Hash(archive);
            if (asset.Digest != null && !archiveHash.Equals(asset.Digest[7..], StringComparison.OrdinalIgnoreCase)) throw new IOException("GitHub's DepotDownloader archive digest did not match.");
            var payload = RuntimeManager.Safe(staging, "payload"); Directory.CreateDirectory(payload);
            feedback?.Invoke(new("Download tool", "Verifying and installing DepotDownloader…"));
            Extract(archive, payload);
            if (!File.Exists(RuntimeManager.Safe(payload, "DepotDownloader.exe"))) throw new IOException("The official archive is missing DepotDownloader.exe.");
            var files = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(payload, f).Replace('\\', '/'), RuntimeManager.Hash);
            var directory = $"asset-{asset.Id}-" + Guid.NewGuid().ToString("N");
            var destination = RuntimeManager.Safe(root, directory);
            // Publish only complete validated directories; preserve the previous installation on failure.
            Directory.Move(payload, destination);
            var installed = new InstalledDepotDownloader(release.Tag, directory, files, archiveHash, asset.Digest != null);
            Storage.Write(RuntimeManager.Safe(root, "installed.json"), installed);
            progress?.Invoke("DepotDownloader " + release.Tag + " installed." + (asset.Digest == null ? " Official release provides no digest; HTTPS download and local integrity hashes recorded." : " GitHub SHA-256 verified."));
            return Executable(installed);
        }
        finally
        {
            try
            {
                if (staging != null && Directory.Exists(staging))
                {
                    // Resolve again before recursive cleanup; only this operation's private staging directory.
                    var checkedPath = RuntimeManager.Safe(root, Path.GetFileName(staging));
                    if (checkedPath != staging) throw new IOException("Invalid tool staging directory.");
                    Directory.Delete(checkedPath, true);
                }
            }
            finally { Gate.Release(); }
        }
    }
    private InstalledDepotDownloader? ReadInstalled()
    {
        try
        {
            var receipt = RuntimeManager.Safe(root, "installed.json");
            if (!File.Exists(receipt)) return null;
            var installed = Storage.Read<InstalledDepotDownloader>(receipt);
            var directory = RuntimeManager.Safe(root, installed.Directory);
            if (!installed.Files.ContainsKey("DepotDownloader.exe")) return null;
            foreach (var (file, hash) in installed.Files)
                if (!File.Exists(RuntimeManager.Safe(directory, file)) || !RuntimeManager.Hash(RuntimeManager.Safe(directory, file)).Equals(hash, StringComparison.OrdinalIgnoreCase)) return null;
            return installed;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; }
    }
    private string Executable(InstalledDepotDownloader installed) => RuntimeManager.Safe(RuntimeManager.Safe(root, installed.Directory), "DepotDownloader.exe");
    internal static void Extract(string archive, string output)
    {
        using var zip = ZipFile.OpenRead(archive);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        if (zip.Entries.Count > 4096) throw new IOException("Too many tool archive entries.");
        foreach (var entry in zip.Entries)
        {
            var path = RuntimeManager.Safe(output, entry.FullName);
            if (!paths.Add(path) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("Duplicate paths or links in tool archive.");
            total = checked(total + entry.Length);
            if (total > 512L * 1024 * 1024) throw new IOException("Expanded tool archive is too large.");
        }
        foreach (var entry in zip.Entries)
        {
            var path = RuntimeManager.Safe(output, entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
    }
}
