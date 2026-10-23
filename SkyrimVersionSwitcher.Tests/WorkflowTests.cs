using System.Diagnostics;
using System.Security.Cryptography;
using SkyrimVersionSwitcher.Core;

internal static class WorkflowTests
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SkyrimWorkflowTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        var assembly = typeof(WorkflowTests).Assembly.Location;
        var info = FileVersionInfo.GetVersionInfo(assembly);
        var version = new RuntimeDefinition($"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}", "1", "fixture", null, []);
        var missing = version with { Version = "3.0.0" }; var failed = version with { Version = "2.0.0" };
        File.Copy(assembly, Path.Combine(root, "SkyrimSE.exe"));
        var source = new DepotSource(489830, 489833, "1", "fixture");
        var profile = new CompatibilityProfile("runtime", "Default", version.Version, "fixture", [new("SkyrimSE.exe", "versioned", "exe")]);
        var catalog = new CompatibilityCatalog(1, new() { ["exe"] = source }, [new(version.Version, [profile, profile with { Id = "legacy" }]), new(missing.Version, [profile]), new(failed.Version, [profile])]);
        int created = 0, ensured = 0;
        var steam = new LazySteamDepotClient(() => { created++; return Task.FromResult<ISteamDepotClient>(new FixtureSteam(assembly)); });
        Task<bool> Ensure(OverlayManager manager, RuntimeDefinition runtime)
        {
            ensured++;
            if (runtime.Version == missing.Version) return Task.FromResult(false);
            var folder = Path.Combine(root, "skse-input"); Directory.CreateDirectory(Path.Combine(folder, "Data", "Scripts"));
            File.WriteAllText(Path.Combine(folder, runtime.SkseDll), "SKSE"); File.WriteAllText(Path.Combine(folder, "skse64_loader.exe"), "loader"); File.WriteAllText(Path.Combine(folder, "Data", "Scripts", "test.pex"), "script");
            manager.ImportSkse(runtime, folder); return Task.FromResult(true);
        }
        void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        try
        {
            var feedback = new List<WorkProgress>();
            var workflow = new RuntimeWorkflow(root, catalog, steam, Ensure, feedback: feedback.Add);
            Assert(await workflow.Switch(version, "runtime"), "one-click switch prepares, verifies and activates an uncached runtime");
            Assert(created == 1 && ensured == 1, "tool/client initialization is lazy and shared through setup");
            Assert(feedback.Any(p => p.Stage == "Verification") && feedback.Any(p => p.Stage == "Installing files" && p.Total > 0) && feedback.Last().Stage == "Complete", "workflow reports verification, determinate installation progress and completion");
            var offline = new RuntimeWorkflow(root, catalog, new LazySteamDepotClient(() => throw new Exception("Unexpected network access")), (_, _) => throw new Exception("Unexpected SKSE download"));
            var history = Directory.GetFiles(Path.Combine(root, "RuntimeManager", "transactions", "history")).Length;
            Assert(await offline.Switch(version, "runtime") && Directory.GetFiles(Path.Combine(root, "RuntimeManager", "transactions", "history")).Length == history, "clicking the active runtime is an offline no-op");
            new OverlayManager(root, catalog).RestoreBaseline();
            await workflow.Prepare(version, "runtime");
            Assert(!File.Exists(Path.Combine(root, "skse64_loader.exe")), "per-version Download prepares files without switching or launching");
            Assert(await offline.Switch(version, "runtime"), "prepared version switches offline without initializing installer");
            var before = RuntimeManager.Hash(Path.Combine(root, "SkyrimSE.exe"));
            var report = await workflow.DownloadEverything([failed, version, missing], false);
            Assert(report.Failed.Length == 1 && report.Ready.Length == 1 && report.NeedsSkse.Length == 1, "download-everything continues after failure and separately reports Nexus setup");
            Assert(before == RuntimeManager.Hash(Path.Combine(root, "SkyrimSE.exe")), "download-everything never activates a version");
            Assert(!File.Exists(Path.Combine(root, "RuntimeManager", "catalog", version.Version, "legacy", "ready.json")), "default download-everything excludes optional legacy data");
            var expanded = await workflow.DownloadEverything([version], true);
            Assert(expanded.Ready.Length == 2, "advanced option includes all compatibility profiles");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class FixtureSteam(string file) : ISteamDepotClient
    {
        public Task<SteamManifest> Manifest(DepotSource source, string output)
        {
            using var stream = File.OpenRead(file);
            return Task.FromResult(new SteamManifest(source.Depot, source.Manifest, new() { ["SkyrimSE.exe"] = new("SkyrimSE.exe", new FileInfo(file).Length, Convert.ToHexString(SHA1.HashData(stream))) }));
        }
        public Task Download(DepotSource source, IReadOnlyList<string> files, string output) => throw new Exception("The installed fixture executable should be reused.");
    }
}
