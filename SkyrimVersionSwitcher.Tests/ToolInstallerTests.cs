using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using SkyrimVersionSwitcher.Core;

internal static class ToolInstallerTests
{
    public static async Task Run(bool live)
    {
        var root = Path.Combine(Path.GetTempPath(), "SkyrimToolsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        async Task Fails(Func<Task> action, string name) { try { await action(); } catch (Exception ex) when (ex is IOException or HttpRequestException) { Console.WriteLine("PASS: " + name); return; } throw new Exception("FAIL: " + name); }
        try
        {
            using var handler = new ReleaseHandler(); using var http = new HttpClient(handler);
            var installer = new DepotDownloaderInstaller(root, http);
            var executable = await installer.EnsureLatest();
            Assert(File.ReadAllText(executable) == "fixture executable" && handler.ArchiveRequests == 1, "official Windows asset automatically installed");
            Assert(Storage.Read<InstalledDepotDownloader>(Path.Combine(root, "installed.json")).UpstreamDigestVerified, "published tool archive SHA-256 verified");
            var second = await installer.EnsureLatest();
            Assert(second == executable && handler.ArchiveRequests == 1, "latest installed tool reused without another archive download");
            handler.Offline = true;
            Assert(await installer.EnsureLatest() == executable, "offline update check falls back to intact downloaded tool");
            File.WriteAllText(executable, "corrupt tool");
            await Fails(async () => { await installer.EnsureLatest(); }, "corrupt cached tool cannot be used offline");
            handler.Offline = false;
            var repaired = await installer.EnsureLatest();
            Assert(repaired != executable && File.ReadAllText(repaired) == "fixture executable", "corrupt tool repaired through a fresh validated install");
            handler.BadDigest = true; handler.AssetId = 2;
            await Fails(async () => { await installer.EnsureLatest(); }, "tool digest mismatch blocks publication");
            Assert(Storage.Read<InstalledDepotDownloader>(Path.Combine(root, "installed.json")).Directory == Path.GetFileName(Path.GetDirectoryName(repaired)), "failed tool update retains previous installed version");
            handler.BadDigest = false; handler.Traversal = true;
            await Fails(async () => { await installer.EnsureLatest(); }, "tool ZIP traversal rejected before publication");
            Assert(!File.Exists(Path.Combine(root, "escape.exe")), "tool ZIP cannot escape staging directory");
            var release = new GithubRelease(1, "test", false, false,
                [new(1, "DepotDownloader-windows-x64.zip", 10, "https://github.com/SteamRE/DepotDownloader/releases/download/test/x64.zip", null), new(2, "DepotDownloader-windows-arm64.zip", 10, "https://github.com/SteamRE/DepotDownloader/releases/download/test/arm64.zip", null)]);
            Assert(DepotDownloaderInstaller.SelectAsset(release, Architecture.Arm64).Id == 2, "ARM64 selects official native Windows asset");
            await Fails(() => Task.FromResult(DepotDownloaderInstaller.SelectAsset(release with { Prerelease = true }, Architecture.X64)), "unstable tool releases rejected");
            await Fails(() => Task.FromResult(DepotDownloaderInstaller.SelectAsset(release with { Assets = [release.Assets[0] with { Url = "https://example.com/tool.zip" }] }, Architecture.X64)), "unofficial tool download URLs rejected");
            // Validate managed archive extraction without an external executable.
            var skse = Path.Combine(root, "skse.zip");
            using (var zip = ZipFile.Open(skse, ZipArchiveMode.Create))
                foreach (var name in new[] { "skse/skse64_1_5_97.dll", "skse/skse64_loader.exe", "skse/Data/Scripts/test.pex" }) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("payload"); }
            var folder = SkseArchive.Extract(skse, "skse64_1_5_97.dll");
            Assert(File.Exists(Path.Combine(folder, "Data", "Scripts", "test.pex")), "SKSE archives extracted in process without 7-Zip");
            SkseArchive.DeleteTemporary(SkseArchive.TemporaryRoot(folder));
            var badSkse = Path.Combine(root, "bad-skse.zip");
            using (var zip = ZipFile.Open(badSkse, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.dll").Open()); writer.Write("bad"); }
            await Fails(() => Task.FromResult(SkseArchive.Extract(badSkse, "skse64_1_5_97.dll")), "managed SKSE extractor rejects traversal");
            if (live)
            {
                var liveRoot = Path.Combine(root, "live");
                var tool = await new DepotDownloaderInstaller(liveRoot).EnsureLatest(Console.WriteLine);
                var start = new ProcessStartInfo(tool) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = liveRoot };
                start.ArgumentList.Add("--version");
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert(process.ExitCode == 0 && (await output).Contains("DepotDownloader"), "live latest official DepotDownloader download installs and runs --version");
                await error;
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class ReleaseHandler : HttpMessageHandler
    {
        public bool Offline, BadDigest, Traversal;
        public int ArchiveRequests, AssetId = 1;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("fixture offline");
            var bytes = Archive();
            HttpContent content;
            if (request.RequestUri!.Host == "api.github.com")
            {
                var platform = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
                var asset = new GithubAsset(AssetId, $"DepotDownloader-windows-{platform}.zip", bytes.Length, "https://github.com/SteamRE/DepotDownloader/releases/download/test/tool.zip", "sha256:" + (BadDigest ? new string('0', 64) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
                content = new StringContent(JsonSerializer.Serialize(new GithubRelease(1, "test", false, false, [asset])), System.Text.Encoding.UTF8, "application/json");
            }
            else { ArchiveRequests++; content = new ByteArrayContent(bytes); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
        private byte[] Archive()
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry(Traversal ? "../escape.exe" : "DepotDownloader.exe");
                // Stable timestamp makes the release metadata's digest match the next response.
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open()); writer.Write("fixture executable");
            }
            return stream.ToArray();
        }
    }
}
