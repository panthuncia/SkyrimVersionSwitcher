using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace SkyrimVersionSwitcher.Core;

public static partial class Acquisition
{
    // Use a visible native console so passwords, Steam Guard and QR authentication
    // are handled by DepotDownloader rather than captured by this application.
    public static async Task DownloadDepot(string tool, string username, RuntimeDefinition version, string output)
    {
        if (!File.Exists(tool) || !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Select DepotDownloader.exe.");
        if (string.IsNullOrWhiteSpace(username)) throw new IOException("Enter your Steam username.");
        Directory.CreateDirectory(output);
        var start = new ProcessStartInfo(Path.GetFullPath(tool)) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(tool))! };
        foreach (var arg in new[] { "-app", "489830", "-depot", "489833", "-manifest", version.Manifest, "-username", username, "-dir", Path.GetFullPath(output) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start DepotDownloader.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"DepotDownloader exited with {process.ExitCode}. Check its console; Steam may deny historical manifest access. Partial downloads remain available for retry.");
        if (!File.Exists(Path.Combine(output, "SkyrimSE.exe"))) throw new IOException("Download did not produce SkyrimSE.exe.");
    }
    public static async Task<string> DownloadSkse(RuntimeDefinition version, string output, Action<WorkProgress>? feedback = null)
    {
        if (version.SkseUrl == null) throw new IOException("Download SKSE from Nexus and use Import SKSE archive.");
        var uri = new Uri(version.SkseUrl);
        if (uri.Scheme != "https" || uri.Host != "skse.silverlock.org") throw new IOException("Automatic downloads must use the official SKSE HTTPS host.");
        Directory.CreateDirectory(output);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var file = Path.Combine(output, "skse-" + version.SkseVersion + ".7z");
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var stream = File.Create(file + ".partial"))
        {
            var buffer = new byte[81920]; long total = 0; int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                total += read; if (total > 64 * 1024 * 1024) throw new IOException("Archive exceeds 64 MB limit.");
                await stream.WriteAsync(buffer.AsMemory(0, read));
                feedback?.Invoke(new("SKSE download", $"Downloading SKSE {version.SkseVersion}…", total, response.Content.Headers.ContentLength, "bytes"));
            }
        }
        File.Move(file + ".partial", file, true);
        File.WriteAllText(file + ".sha256", RuntimeManager.Hash(file));
        return file;
    }
    public static string? DetectGame()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam == null) return null;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace("\\\\", "\\")));
        return libraries.Select(p => Path.Combine(p, "steamapps", "common", "Skyrim Special Edition")).FirstOrDefault(p => File.Exists(Path.Combine(p, "SkyrimSE.exe")));
    }
}

