using System.Security.Cryptography;
using System.Text;
using SkyrimVersionSwitcher.Core;

internal static class OverlayTests
{
    public static async Task Run()
    {
        var shipped = Storage.Read<CompatibilityCatalog>(Path.Combine(AppContext.BaseDirectory, "compatibility.json"));
        shipped.Validate();
        if (shipped.Resolve("1.5.97", "runtime").Files.Single(f => f.Path == "Data/Skyrim - Shaders.bsa").Source != "core-1170") throw new Exception("Wrong compatible shader source.");
        if (shipped.Runtimes.SelectMany(r => r.Profiles).SelectMany(p => p.Files).Any(f => f.Policy == "versioned" && f.Sha256 == null)) throw new Exception("Bundled game payload lacks pinned content hash.");
        Console.WriteLine("PASS: shipped compatibility catalog validates with pinned hashes and cross-release shader mapping");
        var root = Path.Combine(Path.GetTempPath(), "SkyrimOverlayTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        void Put(string name, string content) { var path = RuntimeManager.Safe(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        string Read(string name) => File.ReadAllText(RuntimeManager.Safe(root, name));
        void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        void Fail(Action action, string name) { try { action(); } catch (IOException) { Console.WriteLine("PASS: " + name); return; } throw new Exception("FAIL: " + name); }
        async Task FailAsync(Func<Task> action, string name) { try { await action(); } catch (IOException) { Console.WriteLine("PASS: " + name); return; } throw new Exception("FAIL: " + name); }
        Put("SkyrimSE.exe", "modern exe"); Put("steam_api64.dll", "modern steam"); Put("Data/Skyrim.esm", "modern master"); Put("Data/cc-example.esl", "owned CC");
        Put("Data/Skyrim - Textures0.bsa", "shared textures");
        var version = new RuntimeDefinition("1.5.97", "11", "2.0.20", null, []);
        var newer = version with { Version = "1.6.1170" };
        var sharedShader = "shared historical shaders";
        var source = new DepotSource(489830, 489832, "11", "fixture core");
        var exe = new DepotSource(489830, 489833, "12", "fixture exe");
        var client = new FakeSteam(new()
        {
            ["11"] = new() { ["SkyrimSELauncher.exe"] = "launcher", ["Data/Skyrim - Shaders.bsa"] = sharedShader, ["steam_api64.dll"] = "old steam", ["binkw64.dll"] = "legacy video", ["Data/Skyrim.esm"] = "old master", ["Data/Skyrim - Textures0.bsa"] = "SHOULD NEVER DOWNLOAD" },
            ["12"] = new() { ["SkyrimSE.exe"] = "old exe" }
        });
        OverlayFile[] rules = [new("SkyrimSE.exe", "versioned", "exe"), new("SkyrimSELauncher.exe", "versioned", "core"), new("Data/Skyrim - Shaders.bsa", "versioned", "core"), new("steam_api64.dll", "versioned", "core"), new("binkw64.dll", "versioned", "core")];
        var normal = new CompatibilityProfile("runtime", "Runtime", "1.7.104", "fixture", rules);
        var legacy = normal with { Id = "legacy", Files = [..rules, new("Data/Skyrim.esm", "versioned", "core")], HideCreationClub = true };
        var modernRules = rules.Where(f => f.Path is not ("steam_api64.dll" or "binkw64.dll")).Concat([new OverlayFile("steam_api64.dll", "shared"), new OverlayFile("binkw64.dll", "absent")]).ToArray();
        var catalog = new CompatibilityCatalog(1, new() { ["core"] = source, ["exe"] = exe }, [new(version.Version, [normal, legacy]), new(newer.Version, [normal with { Files = modernRules }])]);
        var manager = new OverlayManager(root, catalog);
        var legacyManager = new OverlayManager(root, catalog, "legacy");
        var modernManager = new OverlayManager(root, catalog);
        try
        {
            await manager.Prepare(version, client);
            Assert(Read("SkyrimSE.exe") == "modern exe", "preparation never changes active game");
            Assert(!client.Downloaded.Contains("Data/Skyrim - Textures0.bsa"), "selective download excludes unrelated data");
            Assert(client.Requests.Any(r => r.Depot == 489832) && client.Requests.Any(r => r.Depot == 489833), "core and executable sources downloaded independently");
            var requests = client.Downloaded.Count;
            await manager.Prepare(version, new OfflineSteam());
            Assert(client.Downloaded.Count == requests, "complete verified cache prepares entirely offline");
            var skse = Path.Combine(root, "skse-input"); Directory.CreateDirectory(Path.Combine(skse, "Data", "Scripts"));
            File.WriteAllText(Path.Combine(skse, version.SkseDll), "skse old"); File.WriteAllText(Path.Combine(skse, "skse64_loader.exe"), "loader"); File.WriteAllText(Path.Combine(skse, "Data", "Scripts", "test.pex"), "script");
            manager.ImportSkse(version, skse);
            manager.SealForTesting(version);
            Fail(() => manager.ActivateForTesting(version, n => { if (n == 2) throw new IOException("injected"); }), "overlay transaction rolls back injected failure");
            Assert(Read("SkyrimSE.exe") == "modern exe" && !File.Exists(manager.JournalPath), "failed first activation restores original state");
            manager.ActivateForTesting(version);
            Assert(Read("steam_api64.dll") == "old steam" && Read("Data/Skyrim.esm") == "modern master", "runtime overlay keeps modern masters");
            await legacyManager.Prepare(version, client); legacyManager.SealForTesting(version); legacyManager.ActivateForTesting(version);
            Assert(Read("Data/Skyrim.esm") == "old master" && !File.Exists(Path.Combine(root, "Data", "cc-example.esl")), "legacy profile replaces master and preserves CC outside active layout");
            manager.ActivateForTesting(version);
            Assert(Read("Data/Skyrim.esm") == "modern master" && Read("Data/cc-example.esl") == "owned CC", "leaving legacy profile restores shared master and CC");
            await modernManager.Prepare(newer, client);
            File.WriteAllText(Path.Combine(skse, newer.SkseDll), "skse newer"); modernManager.ImportSkse(newer, skse); modernManager.SealForTesting(newer);
            var receiptA = Storage.Read<OverlayReceipt>(RuntimeManager.Safe(manager.Root, "catalog/1.5.97/runtime/ready.json"));
            var receiptB = Storage.Read<OverlayReceipt>(RuntimeManager.Safe(manager.Root, "catalog/1.6.1170/runtime/ready.json"));
            Assert(receiptA.Files["Data/Skyrim - Shaders.bsa"] == receiptB.Files["Data/Skyrim - Shaders.bsa"], "two runtime receipts share one shader object");
            modernManager.ImportSkse(newer, skse);
            Fail(() => modernManager.ActivateForTesting(newer), "SKSE reimport invalidates sealed profile receipts");
            modernManager.SealForTesting(newer);
            modernManager.ActivateForTesting(newer);
            Assert(Read("steam_api64.dll") == "modern steam" && !File.Exists(Path.Combine(root, "binkw64.dll")), "shared DLL restored and legacy DLL explicitly absent");
            Put("SkyrimSE.exe", "Steam overwrite");
            Assert(modernManager.Status(newer) == "Ready (offline)", "Steam modification changes active status");
            Fail(() => manager.ActivateForTesting(version), "external managed changes block mixing baselines");
            modernManager.RestoreBaseline();
            Assert(Read("SkyrimSE.exe") == "modern exe" && Read("Data/Skyrim - Textures0.bsa") == "shared textures", "baseline restore preserves unowned textures");
            var preserved = Directory.EnumerateFiles(Path.Combine(manager.Root, "transactions", "history"), "*.json").Select(Storage.Read<OverlayJournal>).Any(j => j.Originals.Values.Any(h => h != null && File.ReadAllText(manager.Objects.Require(h)) == "Steam overwrite"));
            Assert(preserved, "unknown Steam-modified file retained in history objects");
            var previous = Storage.Read<OverlayState>(Path.Combine(manager.Root, "state.json"));
            var hash = manager.Objects.Add(Path.Combine(root, "SkyrimSE.exe"));
            Storage.Write(manager.JournalPath, new OverlayJournal(new() { ["SkyrimSE.exe"] = hash, ["binkw64.dll"] = null }, previous));
            Assert(manager.RecoveryPending && manager.Status(version) != "Active (verified)", "pending recovery prevents active launch eligibility");
            Put("SkyrimSE.exe", "interrupted write"); Put("binkw64.dll", "partial"); manager.Recover();
            Assert(Read("SkyrimSE.exe") == "modern exe" && !File.Exists(Path.Combine(root, "binkw64.dll")), "overlay restart recovery restores files and previous state");
            var args = SteamDepotClient.Arguments(source, "user with spaces", root, Path.Combine(root, "files.txt"));
            Assert(args.Contains("-filelist") && args.Contains("-validate") && args.Contains("user with spaces") && !args.Contains("-password"), "Steam subprocess arguments retain literal paths and separate credentials");
            Assert(args.Contains("-remember-password") && SteamDepotClient.Arguments(source, "user", root, null).Contains("-remember-password"), "both file and manifest downloads reuse Steam login by default");
            Assert(!SteamDepotClient.Arguments(source, "user", root, null, false).Contains("-remember-password"), "explicit Steam login opt-out remains supported");
            var text = "Content Manifest for Depot 489832 \nManifest ID / date : 11 / fixture\n 4 1 " + new string('a', 40) + " 0 Data/Skyrim.esm\n";
            var parsed = SteamManifest.Parse(text, source);
            Assert(parsed.Files["Data/Skyrim.esm"].Size == 4, "Steam text manifest parser extracts content metadata");
            Fail(() => SteamManifest.Parse(text, source with { Manifest = "99" }), "wrong Steam manifest identity rejected");
            Fail(() => SteamManifest.Parse(text.Replace("Data/Skyrim.esm", "../escape"), source), "manifest traversal rejected");
            var changed = parsed with { Files = new() { ["Data/Skyrim.esm"] = new("Data/Skyrim.esm", 5, new string('b', 40)) } };
            Assert(SteamManifest.Compare(parsed, changed).Single().Classification == "Unclassified", "manifest comparison never assumes changed means incompatible");
            var recoveryHash = manager.Objects.Add(Path.Combine(root, "SkyrimSE.exe"));
            Storage.Write(manager.JournalPath, new OverlayJournal(new() { ["SkyrimSE.exe"] = recoveryHash }, previous));
            var recoveryObject = manager.Objects.Require(recoveryHash); var recoveryBytes = File.ReadAllBytes(recoveryObject);
            File.WriteAllText(recoveryObject, "corrupt recovery object");
            Fail(manager.Recover, "corrupt overlay recovery object blocks restoration");
            Assert(File.Exists(manager.JournalPath) && Read("SkyrimSE.exe") == "modern exe", "failed overlay recovery retains journal without changing installation");
            File.WriteAllBytes(recoveryObject, recoveryBytes); manager.Recover();
            var changedCatalog = catalog with { Runtimes = [new(version.Version, [normal with { Validation = "changed definition" }])] };
            Fail(() => new OverlayManager(root, changedCatalog).ActivateForTesting(version), "catalog edit invalidates prepared receipt");
            var objectPath = manager.Objects.Require(receiptA.Files["Data/Skyrim - Shaders.bsa"]); File.WriteAllText(objectPath, "cache corruption");
            Fail(() => manager.ActivateForTesting(version), "corrupt shared object blocks activation");
            // A fresh fixture source that lies about content fails before touching any active file.
            var freshCatalog = catalog with { Sources = new() { ["core"] = source with { Manifest = "21" }, ["exe"] = exe with { Manifest = "22" } } };
            var badClient = new FakeSteam(new() { ["21"] = new() { ["SkyrimSELauncher.exe"] = "different", ["Data/Skyrim - Shaders.bsa"] = "different", ["steam_api64.dll"] = "different", ["binkw64.dll"] = "different" }, ["22"] = new() { ["SkyrimSE.exe"] = "different" } }) { CorruptDownloads = true };
            await FailAsync(() => new OverlayManager(root, freshCatalog).Prepare(version, badClient), "download content mismatch rejected");
            Assert(Read("SkyrimSE.exe") == "modern exe", "bad acquisition leaves installation unchanged");
            await FailAsync(() => new OverlayManager(root, freshCatalog).Prepare(version, new OfflineSteam()), "Steam acquisition failures remain distinct from compatibility checks");
            Console.WriteLine("Overlay integration checks passed.");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class OfflineSteam : ISteamDepotClient
    {
        public Task<SteamManifest> Manifest(DepotSource source, string output) => throw new AcquisitionException(AcquisitionFailure.SteamAccessOrTransport, "fixture: offline");
        public Task Download(DepotSource source, IReadOnlyList<string> files, string output) => throw new IOException("Unexpected download");
    }
    private sealed class FakeSteam(Dictionary<string, Dictionary<string, string>> payloads) : ISteamDepotClient
    {
        public List<string> Downloaded { get; } = [];
        public List<DepotSource> Requests { get; } = [];
        public bool CorruptDownloads { get; init; }
        public Task<SteamManifest> Manifest(DepotSource source, string output) => Task.FromResult(new SteamManifest(source.Depot, source.Manifest, payloads[source.Manifest].ToDictionary(p => p.Key, p => new SteamFile(p.Key, Encoding.UTF8.GetByteCount(p.Value), Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(p.Value)))), StringComparer.OrdinalIgnoreCase)));
        public Task Download(DepotSource source, IReadOnlyList<string> files, string output)
        {
            Requests.Add(source);
            foreach (var file in files)
            {
                Downloaded.Add(file); var path = RuntimeManager.Safe(output, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, CorruptDownloads ? "WRONG CONTENT" : payloads[source.Manifest][file]);
            }
            return Task.CompletedTask;
        }
    }
}
