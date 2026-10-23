using SkyrimVersionSwitcher.Core;
var root = Path.Combine(Path.GetTempPath(), "SkyrimSwitcherTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "Data"));
File.WriteAllText(Path.Combine(root, "SkyrimSE.exe"), "original exe");
File.WriteAllText(Path.Combine(root, "skse64_9_9_9.dll"), "old skse");
var manager = new RuntimeManager(root);
var version = new RuntimeDefinition("1.5.97", "123", "2.0.20", null, ["SkyrimSE.exe", "SkyrimSELauncher.exe", "Data/Skyrim - Shaders.bsa"]);
var cache = manager.Cache(version);
void Put(string relative, string content) { var path = RuntimeManager.Safe(cache, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
void MustFail(Action action, string name) { try { action(); } catch (IOException) { Console.WriteLine("PASS: " + name); return; } throw new Exception("FAIL: " + name); }
try
{
    var source = Path.Combine(root, "import-source"); Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, version.SkseDll), "skse dll");
    File.WriteAllText(Path.Combine(source, "skse64_loader.exe"), "loader");
    manager.ImportSkse(version, source);
    Assert(File.Exists(Path.Combine(cache, version.SkseDll)), "first SKSE import creates a new cache");
    MustFail(() => RuntimeManager.Safe(root, "../outside"), "reject traversal");
    MustFail(() => manager.SealForTesting(version), "reject incomplete cache");
    foreach (var file in version.RequiredFiles.Concat(new[] { "skse64_loader.exe", version.SkseDll, "Data/Scripts/test.pex" })) Put(file, "new:" + file);
    manager.SealForTesting(version);
    Put("SkyrimSE.exe", "tampered");
    MustFail(() => manager.Activate(version), "reject changed cache before touching game");
    Assert(File.ReadAllText(Path.Combine(root, "SkyrimSE.exe")) == "original exe", "failed validation preserves installation");
    Put("SkyrimSE.exe", "new exe"); manager.SealForTesting(version);
    MustFail(() => manager.Activate(version, count => { if (count == 2) throw new IOException("Injected I/O failure"); }), "mid-switch failure reported");
    Assert(File.ReadAllText(Path.Combine(root, "SkyrimSE.exe")) == "original exe" && !File.Exists(Path.Combine(root, "SkyrimSELauncher.exe")), "rollback restores originals and removes newly created files");
    Assert(File.Exists(Path.Combine(root, "skse64_9_9_9.dll")) && !File.Exists(manager.JournalPath), "rollback restores obsolete DLL and clears journal");
    manager.Activate(version);
    Assert(manager.Status(version) == "Active (verified)", "successful activation verifies installed hashes");
    Assert(!File.Exists(Path.Combine(root, "skse64_9_9_9.dll")), "obsolete SKSE DLL removed");
    Assert(Directory.EnumerateDirectories(Path.Combine(manager.Root, "backups")).Any(), "backups retained");
    File.WriteAllText(Path.Combine(root, "SkyrimSE.exe"), "steam update");
    Assert(manager.Status(version) == "Ready", "external Steam update detected");
    var backup = "backups/interrupted";
    var saved = RuntimeManager.Safe(manager.Root, backup + "/SkyrimSE.exe"); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.WriteAllText(saved, "pre-interruption");
    Storage.Write(manager.JournalPath, new Journal(backup, new() { ["SkyrimSE.exe"] = RuntimeManager.Hash(saved), ["skse64_loader.exe"] = null }));
    MustFail(() => manager.Activate(version), "pending journal blocks activation");
    manager.Recover();
    Assert(File.ReadAllText(Path.Combine(root, "SkyrimSE.exe")) == "pre-interruption" && !File.Exists(Path.Combine(root, "skse64_loader.exe")), "restart recovery restores interrupted transaction");
    File.WriteAllText(saved, "corrupt");
    Storage.Write(manager.JournalPath, new Journal(backup, new() { ["SkyrimSE.exe"] = "BAD HASH" }));
    MustFail(manager.Recover, "corrupt recovery backup rejected");
    Assert(File.Exists(manager.JournalPath), "failed recovery retains journal");
    if (args.Contains("--smoke-skse"))
    {
        var official = version with { SkseUrl = "https://skse.silverlock.org/beta/skse64_2_00_20.7z" };
        var archive = await Acquisition.DownloadSkse(official, Path.Combine(root, "archive-test"));
        var extracted = SkseArchive.Extract(archive, version.SkseDll);
        Assert(File.Exists(Path.Combine(extracted, "skse64_loader.exe")), "official SKSE HTTP download and managed 7z extraction");
        manager.ImportSkse(version, extracted);
        Assert(Directory.EnumerateFiles(Path.Combine(cache, "Data", "Scripts"), "*.pex").Count() > 1, "official SKSE scripts imported");
        SkseArchive.DeleteTemporary(SkseArchive.TemporaryRoot(extracted));
    }
    Console.WriteLine("All integration checks passed.");
}
finally { Directory.Delete(root, true); }
await OverlayTests.Run();
await ToolInstallerTests.Run(args.Contains("--smoke-tools"));
await WorkflowTests.Run();
await NormalizationTests.Run();
FeedbackTests.Run();
var executableCheck = Array.IndexOf(args, "--verify-executable");
if (executableCheck >= 0)
{
    ExecutableVersion.Require(args[executableCheck + 1], args[executableCheck + 2]);
    Console.WriteLine("PASS: supplied executable passes runtime verification without modifying game or cache");
}
if (args.Contains("--smoke-ui")) UiSmokeTests.Run(args.Contains("--preview-ui"));
