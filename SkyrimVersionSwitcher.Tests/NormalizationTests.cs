using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SkyrimVersionSwitcher.Core;

internal static class NormalizationTests
{
    public static async Task Run()
    {
        foreach (var startingVersion in new[] { "1.5.97", "1.6.1170", "1.7.104", "unknown", "mixed" }) await Scenario(startingVersion);
        await RetryScenario();
    }
    private static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static async Task Scenario(string startingVersion)
    {
        using var fixture = new Fixture(startingVersion);
        var manager = fixture.Manager;
        var before = fixture.CurrentFiles();
        var flow = fixture.Workflow(fixture.Client);
        var report = await flow.DownloadEverything([fixture.Runtime], false);
        Assert(report.Ready.Length == 1 && report.Failed.Length == 0, startingVersion + ": setup automatically prepares shared data without version input");
        Assert(before.All(p => File.ReadAllBytes(Path.Combine(fixture.Root, p.Key)).SequenceEqual(p.Value)) && !File.Exists(Path.Combine(manager.Root, "original-installation.json")), startingVersion + ": downloads preserve installed files until switching");
        Assert(!fixture.Client.Downloaded.Contains("Data/Skyrim - Textures0.bsa"), startingVersion + ": unchanged textures reused without download");
        if (startingVersion == "unknown")
        {
            try { manager.Activate(fixture.Runtime, n => { if (n == 3) throw new IOException("injected normalization failure"); }); throw new Exception("Expected normalization failure"); }
            catch (IOException) { }
            Assert(before.All(p => File.ReadAllBytes(Path.Combine(fixture.Root, p.Key)).SequenceEqual(p.Value)) && !manager.RecoveryPending, "normalization and overlay share one rollback transaction");
        }
        var offline = fixture.Workflow(new OfflineSteam());
        Assert(await offline.Switch(fixture.Runtime, "runtime"), startingVersion + ": one-click switching works offline after setup");
        Assert(File.ReadAllText(Path.Combine(fixture.Root, "Data", "Skyrim.esm")) == "modern master" && File.ReadAllText(Path.Combine(fixture.Root, "Data", "Skyrim - Meshes0.bsa")) == "modern meshes" && File.Exists(Path.Combine(fixture.Root, "Data", "_ResourcePack.esl")), startingVersion + ": masters, meshes, scripts and resource pack normalized together");
        Assert(File.ReadAllText(Path.Combine(fixture.Root, "Data", "Skyrim - Shaders.bsa")) == "runtime shader", startingVersion + ": runtime overlay applied over normalized shared data");
        Assert(File.ReadAllText(Path.Combine(fixture.Root, "Data", "MyCustomMod.esp")) == "leave this mod alone" && !File.Exists(manager.Objects.PathFor(fixture.TextureHash)), startingVersion + ": unlisted mods retained and unchanged bulk assets not duplicated in cache");
        if (startingVersion == "unknown")
        {
            File.WriteAllText(Path.Combine(fixture.Root, "Data", "Skyrim.esm"), "external game update");
            Assert(await fixture.Workflow(new OfflineSteam()).Switch(fixture.Runtime, "runtime") && File.ReadAllText(Path.Combine(fixture.Root, "Data", "Skyrim.esm")) == "modern master", "outside game-data changes normalized automatically on next switch");
            var conflicts = Directory.EnumerateFiles(Path.Combine(manager.Root, "transactions", "history"), "*.json").Select(Storage.Read<OverlayJournal>);
            Assert(conflicts.Any(j => j.Originals.TryGetValue("Data/Skyrim.esm", out var h) && h != null && File.ReadAllText(manager.Objects.Require(h)) == "external game update"), "automatically replaced outside changes retained in conflict history");
            await fixture.Workflow(fixture.Client).Prepare(fixture.Runtime, "legacy");
            await fixture.Workflow(new OfflineSteam()).Switch(fixture.Runtime, "legacy");
            Assert(!File.Exists(Path.Combine(fixture.Root, "Data", "ccTest.esl")), "legacy mode hides CC supplied by normalization, even if originally absent");
            await fixture.Workflow(new OfflineSteam()).Switch(fixture.Runtime, "runtime");
            Assert(File.Exists(Path.Combine(fixture.Root, "Data", "ccTest.esl")), "leaving legacy mode restores normalized official CC");
        }
        manager.RestoreBaseline();
        Assert(File.ReadAllText(Path.Combine(fixture.Root, "Data", "Skyrim - Shaders.bsa")) == "modern shaders", startingVersion + ": canonical baseline restore is distinct from original installation");
        manager.RestoreOriginal();
        Assert(before.All(p => File.ReadAllBytes(Path.Combine(fixture.Root, p.Key)).SequenceEqual(p.Value)), startingVersion + ": original starting file content restored exactly");
        Assert(!fixture.OriginallyHadResources || File.Exists(Path.Combine(fixture.Root, "Data", "_ResourcePack.esl")), startingVersion + ": original resource presence retained");
        if (!fixture.OriginallyHadResources) Assert(!File.Exists(Path.Combine(fixture.Root, "Data", "_ResourcePack.esl")) && !File.Exists(Path.Combine(fixture.Root, "Data", "ccTest.esl")), startingVersion + ": files introduced by normalization removed on original restore");
        Assert(await fixture.Workflow(new OfflineSteam()).Switch(fixture.Runtime, "runtime"), startingVersion + ": can normalize and switch again offline after restoring original files");
    }
    private static async Task RetryScenario()
    {
        using var fixture = new Fixture("unknown");
        var before = fixture.CurrentFiles(); fixture.Client.FailDownload = true;
        try { await fixture.Manager.PrepareNormalization(fixture.Client); throw new Exception("Expected Steam failure"); } catch (AcquisitionException) { }
        Assert(before.All(p => File.ReadAllBytes(Path.Combine(fixture.Root, p.Key)).SequenceEqual(p.Value)), "denied/failed baseline acquisition does not modify installed game");
        fixture.Client.FailDownload = false; await fixture.Manager.PrepareNormalization(fixture.Client);
        await fixture.Manager.Prepare(fixture.Runtime, fixture.Client);
        Assert(await fixture.Workflow(new OfflineSteam()).Switch(fixture.Runtime, "runtime") == false, "retry reuses normalization plan; missing SKSE is still reported separately");
        Storage.Write(Path.Combine(fixture.Manager.Root, "active.json"), new CacheManifest("old-prototype", new()));
        Assert(await fixture.Workflow(fixture.Client).Switch(fixture.Runtime, "runtime"), "old prototype activation migrates without manual Steam update or metadata deletion");
    }
    private sealed class OfflineSteam : ISteamDepotClient
    {
        public Task<SteamManifest> Manifest(DepotSource source, string output) => throw new Exception("Unexpected online manifest lookup");
        public Task Download(DepotSource source, IReadOnlyList<string> files, string output) => throw new Exception("Unexpected online file download");
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SkyrimNormalizationTests-" + Guid.NewGuid().ToString("N"));
        public RuntimeDefinition Runtime { get; }
        public CompatibilityCatalog Catalog { get; }
        public FixtureSteam Client { get; }
        public OverlayManager Manager => new(Root, Catalog);
        public bool OriginallyHadResources { get; }
        public string TextureHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("unchanged textures")));
        public Fixture(string start)
        {
            Directory.CreateDirectory(Path.Combine(Root, "Data"));
            var assembly = File.ReadAllBytes(typeof(Fixture).Assembly.Location);
            var info = FileVersionInfo.GetVersionInfo(typeof(Fixture).Assembly.Location);
            Runtime = new RuntimeDefinition($"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}", "3", "fixture", null, []);
            var sources = new Dictionary<string, DepotSource> { ["disk"] = new(489830, 489831, "1", "1.7.104"), ["core"] = new(489830, 489832, "2", "1.7.104"), ["exe"] = new(489830, 489833, "3", "1.7.104"), ["runtime"] = new(489830, 489832, "4", "historical") };
            byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
            var payloads = new Dictionary<string, Dictionary<string, byte[]>>
            {
                ["1"] = new() { ["Data/Skyrim - Meshes0.bsa"] = Text("modern meshes"), ["Data/Skyrim - Misc.bsa"] = Text("modern scripts"), ["Data/_ResourcePack.esl"] = Text("resource pack"), ["Data/ccTest.esl"] = Text("official free CC") },
                ["2"] = new() { ["Data/Skyrim.esm"] = Text("modern master"), ["Data/Skyrim - Shaders.bsa"] = Text("modern shaders"), ["Data/Skyrim - Textures0.bsa"] = Text("unchanged textures"), ["steam_api64.dll"] = Text("modern steam"), ["bink2w64.dll"] = Text("modern video") },
                ["3"] = new() { ["SkyrimSE.exe"] = assembly },
                ["4"] = new() { ["Data/Skyrim - Shaders.bsa"] = Text("runtime shader") }
            };
            Client = new FixtureSteam(payloads);
            var profile = new CompatibilityProfile("runtime", "Runtime", "1.7.104", "fixture", [new("SkyrimSE.exe", "versioned", "exe"), new("Data/Skyrim - Shaders.bsa", "versioned", "runtime"), new("steam_api64.dll", "shared"), new("bink2w64.dll", "shared"), new("binkw64.dll", "absent")]);
            var pinned = new Dictionary<string, string> { ["SkyrimSE.exe"] = Convert.ToHexString(SHA256.HashData(assembly)), ["Data/Skyrim.esm"] = Convert.ToHexString(SHA256.HashData(Text("modern master"))) };
            Catalog = new CompatibilityCatalog(1, sources, [new(Runtime.Version, [profile, profile with { Id = "legacy", HideCreationClub = true }])], pinned, new("1.7.104", ["disk", "core", "exe"]));
            OriginallyHadResources = start == "1.7.104";
            if (OriginallyHadResources)
                foreach (var file in payloads.Where(p => p.Key != "4").SelectMany(p => p.Value)) Write(file.Key, file.Value);
            else
            {
                Write("SkyrimSE.exe", Text("unrecognized or modified executable: " + start));
                Write("Data/Skyrim.esm", Text(start == "mixed" ? "modern master" : "historical master " + start));
                Write("Data/Skyrim - Meshes0.bsa", Text("historical meshes " + start));
                Write("Data/Skyrim - Misc.bsa", Text("historical scripts " + start));
                Write("steam_api64.dll", Text("old steam"));
                Write("binkw64.dll", Text("legacy video"));
            }
            Write("Data/Skyrim - Textures0.bsa", Text("unchanged textures"));
            Write("Data/MyCustomMod.esp", Text("leave this mod alone"));
        }
        private void Write(string path, byte[] bytes) { var file = RuntimeManager.Safe(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes); }
        public Dictionary<string, byte[]> CurrentFiles() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(Path.Combine(Root, "RuntimeManager"))).ToDictionary(p => Path.GetRelativePath(Root, p), File.ReadAllBytes);
        public RuntimeWorkflow Workflow(ISteamDepotClient client) => new(Root, Catalog, client, (manager, runtime) =>
        {
            if (client is OfflineSteam && !manager.HasSkse(runtime)) return Task.FromResult(false);
            if (!manager.HasSkse(runtime))
            {
                var skse = Path.Combine(Root, "RuntimeManager", "test-skse"); Directory.CreateDirectory(Path.Combine(skse, "Data", "Scripts"));
                File.WriteAllText(Path.Combine(skse, runtime.SkseDll), "SKSE runtime"); File.WriteAllText(Path.Combine(skse, "skse64_loader.exe"), "SKSE loader"); File.WriteAllText(Path.Combine(skse, "Data", "Scripts", "test.pex"), "SKSE script"); manager.ImportSkse(runtime, skse);
            }
            return Task.FromResult(true);
        });
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class FixtureSteam(Dictionary<string, Dictionary<string, byte[]>> payloads) : ISteamDepotClient
    {
        public bool FailDownload;
        public HashSet<string> Downloaded { get; } = [];
        public Task<SteamManifest> Manifest(DepotSource source, string output) => Task.FromResult(new SteamManifest(source.Depot, source.Manifest, payloads[source.Manifest].ToDictionary(p => p.Key, p => new SteamFile(p.Key, p.Value.Length, Convert.ToHexString(SHA1.HashData(p.Value))), StringComparer.OrdinalIgnoreCase)));
        public Task Download(DepotSource source, IReadOnlyList<string> files, string output)
        {
            if (FailDownload) throw new AcquisitionException(AcquisitionFailure.SteamAccessOrTransport, "fixture access denied");
            foreach (var path in files) { var file = RuntimeManager.Safe(output, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, payloads[source.Manifest][path]); Downloaded.Add(path); }
            return Task.CompletedTask;
        }
    }
}
