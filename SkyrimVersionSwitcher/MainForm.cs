using System.Diagnostics;
using System.Text.Json;
using SkyrimVersionSwitcher.Core;

namespace SkyrimVersionSwitcher;

public sealed record UiSettings(string Game, bool LegacyMode = false, bool IncludeLegacy = false, bool RememberSteam = true, bool SteamLoginConfigured = false);

public sealed class MainForm : Form
{
    private readonly TextBox game = new() { Dock = DockStyle.Fill, Name = "gameFolder" };
    private readonly TextBox username = new() { Width = 240, PlaceholderText = "Steam username", Name = "steamUsername" };
    private readonly Button play = new() { Text = "Play Skyrim", Name = "playSkyrim", AutoSize = true, Enabled = false, Padding = new Padding(12, 4, 12, 4) };
    private readonly Label activity = new() { Text = "Download once, then switch between versions offline.", AutoSize = true, MaximumSize = new Size(690, 0), Margin = new Padding(0, 10, 0, 10) };
    private readonly TextBox log = new() { Name = "details", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Visible = false };
    private readonly LinkLabel details = new() { Text = "Show details", AutoSize = true };
    private readonly ProgressBar progress = new() { Name = "operationProgress", Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, Height = 18, Visible = false };
    private readonly Label progressCaption = new() { Name = "progressCaption", AutoSize = true, Margin = new Padding(0, 4, 0, 6) };
    private readonly FlowLayoutPanel errorPanel = new() { Name = "operationError", AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false, BackColor = Color.FromArgb(255, 242, 239), Padding = new Padding(12), Margin = new Padding(0, 4, 0, 10) };
    private readonly Label errorTitle = new() { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Color.FromArgb(150, 35, 30) };
    private readonly Label errorExplanation = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly Label errorNextStep = new() { AutoSize = true, MaximumSize = new Size(650, 0), Margin = new Padding(3, 4, 3, 6) };
    private readonly Button retry = new() { Name = "retryOperation", Text = "Retry", AutoSize = true };
    private readonly Stopwatch operationClock = new();
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 1000 };
    private WorkProgress? latestProgress;
    private Func<Task>? failedAction;
    private string failedMessage = "Retrying…", errorDetails = "";
    private RuntimeDefinition? failedVersion, workingVersion;
    private bool recoveryRequired;
    private int operationGeneration;
    private readonly MenuStrip menu = new();
    private readonly ToolStripMenuItem advanced = new("Advanced");
    private readonly ToolStripMenuItem legacy = new("Legacy mod compatibility") { CheckOnClick = true };
    private readonly ToolStripMenuItem includeLegacy = new("Include legacy data in downloads") { CheckOnClick = true };
    private readonly ToolStripMenuItem rememberSteam = new("Remember Steam login in DepotDownloader") { CheckOnClick = true, Checked = true };
    private readonly CompatibilityCatalog catalog = Storage.Read<CompatibilityCatalog>(Path.Combine(AppContext.BaseDirectory, "compatibility.json"));
    private readonly RuntimeDefinition[] versions = Storage.Read<RuntimeDefinition[]>(Path.Combine(AppContext.BaseDirectory, "runtimes.json"));
    private readonly Dictionary<string, (Label Status, Button Download, Button Switch)> cards = new();
    private readonly string settings;
    private bool busy;
    private bool loadingSettings;
    private int refreshGeneration;
    private RuntimeDefinition? activeVersion;
    private readonly TableLayoutPanel layout;
    private readonly Control setup;

    public MainForm(string? settingsPath = null)
    {
        settings = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimVersionSwitcher", "settings.json");
        Text = "Skyrim Runtime Manager"; Name = "runtimeManager";
        Font = new Font("Segoe UI", 10); StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 700); Size = new Size(900, 740);
        BackColor = Color.FromArgb(246, 247, 249);
        catalog.Validate();
        layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 18), ColumnCount = 1, RowCount = 9 };
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        menu.Items.Add(advanced); menu.Dock = DockStyle.Top; Controls.Add(menu); MainMenuStrip = menu;
        layout.Controls.Add(new Label { Text = "Skyrim Runtime Manager", AutoSize = true, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Margin = new Padding(0, 4, 0, 16) });
        var setupPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        pathRow.Controls.Add(new Label { Text = "Game folder", AutoSize = true, Margin = new Padding(0, 5, 0, 0) }); pathRow.Controls.Add(game);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += async (_, _) => { var folder = ChooseFolder(); if (folder != null) { game.Text = folder; SaveSettings(); await RefreshStatuses(); } };
        pathRow.Controls.Add(browse); setupPanel.Controls.Add(pathRow);
        var account = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
        account.Controls.Add(new Label { Text = "Steam account", AutoSize = true, Margin = new Padding(0, 5, 8, 0) }); account.Controls.Add(username);
        account.Controls.Add(new Label { Text = "Only needed for downloads", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 5, 0, 0) });
        setupPanel.Controls.Add(account); setup = setupPanel; layout.Controls.Add(setupPanel);
        layout.Controls.Add(activity);
        var progressPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 0, 0, 5);
        progressPanel.Controls.Add(progress); progressPanel.Controls.Add(progressCaption); layout.Controls.Add(progressPanel);
        errorTitle.Font = new Font(Font, FontStyle.Bold);
        errorPanel.Controls.Add(errorTitle); errorPanel.Controls.Add(errorExplanation); errorPanel.Controls.Add(errorNextStep);
        var errorActions = new FlowLayoutPanel { AutoSize = true }; errorActions.Controls.Add(retry);
        var copyError = new LinkLabel { Text = "Copy error details", AutoSize = true, Margin = new Padding(12, 7, 0, 0) };
        copyError.LinkClicked += (_, _) => { if (errorDetails.Length > 0) Clipboard.SetText(errorDetails); };
        errorActions.Controls.Add(copyError); errorPanel.Controls.Add(errorActions); layout.Controls.Add(errorPanel);
        retry.Click += async (_, _) =>
        {
            if (recoveryRequired) await RunOperation("Recovering interrupted switch…", Recover);
            else if (failedAction is { } action) await RunOperation(failedMessage, action, failedVersion);
        };
        var versionRows = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0, 6, 0, 12) };
        foreach (var version in versions)
        {
            var row = new TableLayoutPanel { Name = "version-" + version.Version, AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(14, 10, 14, 10), BackColor = Color.White, Margin = new Padding(0, 0, 0, 8) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            row.Controls.Add(new Label { Text = "Skyrim " + version.Version, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 0) });
            var state = new Label { Text = "Not downloaded", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }; row.Controls.Add(state);
            var downloadButton = new Button { Name = "download-" + version.Version, Text = "Download", Dock = DockStyle.Fill, Height = 34 };
            downloadButton.Click += async (_, _) => await RunOperation("Downloading Skyrim " + version.Version + "…", () => DownloadVersion(version), version);
            row.Controls.Add(downloadButton);
            var button = new Button { Name = "switch-" + version.Version, Text = "Switch", Dock = DockStyle.Fill, Height = 34 };
            button.Click += async (_, _) => await RunOperation("Switching to Skyrim " + version.Version + "…", () => SwitchVersion(version), version);
            row.Controls.Add(button); cards[version.Version] = (state, downloadButton, button); versionRows.Controls.Add(row);
        }
        layout.Controls.Add(versionRows);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        footer.Controls.Add(play); details.Margin = new Padding(16, 10, 0, 0); footer.Controls.Add(details); layout.Controls.Add(footer);
        play.Click += async (_, _) => await RunOperation("Launching Skyrim…", Launch);
        details.LinkClicked += (_, _) => { log.Visible = !log.Visible; details.Text = log.Visible ? "Hide details" : "Show details"; Height = log.Visible ? 1000 : errorPanel.Visible ? 920 : 740; };
        layout.Controls.Add(new Label { Text = "Steam sign-in and a one-time Nexus SKSE download may need your input.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 10, 0, 8), MaximumSize = new Size(690, 0) });
        layout.Controls.Add(log);
        BuildAdvancedMenu(); LoadSettings();
        clockTimer.Tick += (_, _) => UpdateProgressCaption();
        FormClosed += (_, _) => clockTimer.Dispose();
        game.Leave += async (_, _) => { if (!busy) { SaveSettings(); await RefreshStatuses(); } };
        Shown += async (_, _) =>
        {
            await RefreshStatuses();
            try
            {
                var manager = Manager(versions[0]);
                if (manager.RecoveryPending) await RunOperation("Recovering interrupted switch…", Recover);
            }
            catch (IOException) { /* Initial folder selection is handled inline. */ }
        };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; activity.Text = "Wait for the operation to finish. You can close the Steam download console to stop its download."; } };
    }
    private string Profile(RuntimeDefinition version) => legacy.Checked && catalog.Runtimes.Single(r => r.Version == version.Version).Profiles.Any(p => p.Id == "legacy") ? "legacy" : "runtime";
    private OverlayManager Manager(RuntimeDefinition version) => new(game.Text.Trim(), catalog, Profile(version));
    private RuntimeWorkflow Workflow()
    {
        var account = username.Text.Trim(); var remember = rememberSteam.Checked;
        var generation = operationGeneration;
        void Update(WorkProgress value) => ReportProgress(value, generation);
        var client = new LazySteamDepotClient(async () =>
        {
            if (string.IsNullOrWhiteSpace(account)) throw new IOException("Enter your Steam username above to download game files.");
            var tool = await new DepotDownloaderInstaller().EnsureLatest(Write, feedback: Update);
            return new SteamDepotClient(tool, account, remember, Path.Combine(DepotDownloaderInstaller.DefaultRoot, "steam-session"), Update);
        });
        return new RuntimeWorkflow(game.Text.Trim(), catalog, client, (m, v) => SkseSetup.Ensure(m, v, Update), Write, Update);
    }
    private async Task DownloadVersion(RuntimeDefinition version)
    {
        var flow = Workflow(); var profile = Profile(version);
        if (!await Task.Run(() => flow.Prepare(version, profile)))
        {
            ShowProgress(new("Waiting for SKSE", $"Game files for {version.Version} are cached. Import SKSE {version.SkseVersion} to finish."));
            if (!await ImportSkse(version)) { activity.Text = "Game files are cached. Click Download again when you have the matching SKSE archive."; return; }
            await Task.Run(() => flow.Prepare(version, profile));
        }
        activity.Text = "Skyrim " + version.Version + " is downloaded and verified. Click Switch to activate it.";
    }
    private async Task DownloadEverything()
    {
        var workflow = Workflow(); var allProfiles = includeLegacy.Checked || legacy.Checked;
        var report = await Task.Run(() => workflow.DownloadEverything(versions, allProfiles));
        if (report.Failed.Length > 0)
        {
            foreach (var error in report.Failed) Write(error);
            throw new IOException("Some versions could not be downloaded:\n" + string.Join("\n", report.Failed));
        }
        else activity.Text = report.NeedsSkse.Length == 0 ? "Everything is downloaded. Choose Switch beside a version." : "Game files downloaded. Finish the one-time SKSE setup beside Skyrim 1.7.104.";
    }
    private async Task SwitchVersion(RuntimeDefinition version)
    {
        var workflow = Workflow(); var profile = Profile(version);
        if (!await Task.Run(() => workflow.Switch(version, profile)))
        {
            ShowProgress(new("Waiting for SKSE", $"Import SKSE {version.SkseVersion} to finish setting up Skyrim {version.Version}."));
            if (!await ImportSkse(version)) { activity.Text = "Finish SKSE setup when ready, then click Switch."; return; }
            if (!await Task.Run(() => workflow.Switch(version, profile))) throw new IOException("The matching SKSE release is still missing.");
        }
        activity.Text = "Skyrim " + version.Version + " is active. You're ready to play.";
    }
    private async Task<bool> ImportSkse(RuntimeDefinition version)
    {
        if (version.SkseUrl == null)
        {
            using var dialog = new Form { Text = "Finish SKSE setup", Size = new Size(520, 215), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font };
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            body.Controls.Add(new Label { Text = $"Download SKSE {version.SkseVersion} for Skyrim {version.Version} from Nexus, then import the downloaded archive. You only need to do this once.", AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 16) });
            var buttons = new FlowLayoutPanel { AutoSize = true };
            var open = new Button { Text = "Open Nexus", AutoSize = true }; open.Click += (_, _) => Open("https://www.nexusmods.com/skyrimspecialedition/mods/30379?tab=files");
            var import = new Button { Text = "Import downloaded archive…", AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.Add(open); buttons.Controls.Add(import); body.Controls.Add(buttons); dialog.Controls.Add(body);
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        }
        using var picker = new OpenFileDialog { Title = $"Import SKSE {version.SkseVersion} for Skyrim {version.Version}", Filter = "SKSE archive|*.7z;*.zip" };
        if (picker.ShowDialog(this) != DialogResult.OK) return false;
        var manager = Manager(version); var archive = picker.FileName;
        ShowProgress(new("SKSE", $"Extracting and caching SKSE {version.SkseVersion}…"));
        await Task.Run(() => SkseSetup.Import(manager, version, archive));
        Write("SKSE " + version.SkseVersion + " imported.");
        return true;
    }
    private Task Launch()
    {
        if (activeVersion == null) throw new IOException("Switch to a prepared version first.");
        var manager = Manager(activeVersion);
        if (manager.RecoveryPending || manager.Status(activeVersion) != "Active (verified)") throw new IOException("The active files changed. Switch to the version again before launching.");
        Process.Start(new ProcessStartInfo(Path.Combine(manager.Game, "skse64_loader.exe")) { WorkingDirectory = manager.Game, UseShellExecute = true });
        activity.Text = "Skyrim launched."; return Task.CompletedTask;
    }
    private void BuildAdvancedMenu()
    {
        advanced.DropDownItems.Add(legacy); advanced.DropDownItems.Add(includeLegacy); advanced.DropDownItems.Add(rememberSteam);
        legacy.CheckedChanged += async (_, _) => { if (!busy) { SaveSettings(); await RefreshStatuses(); } };
        includeLegacy.CheckedChanged += (_, _) => { if (!busy) SaveSettings(); }; rememberSteam.CheckedChanged += (_, _) => { if (!busy) SaveSettings(); };
        advanced.DropDownItems.Add(new ToolStripSeparator());
        AddMenu("Download all versions", DownloadEverything);
        AddMenu("Restore shared baseline", async () => { var flow = Workflow(); await Task.Run(flow.RestoreSharedBaseline); activity.Text = "Supported shared game data restored."; });
        AddMenu("Restore original installation", async () => { var m = Manager(versions[0]); var generation = operationGeneration; m.Feedback = value => ReportProgress(value, generation); await Task.Run(() => m.RestoreOriginal()); activity.Text = "Your starting game files have been restored."; });
        AddMenu("Recover interrupted switch", Recover);
        var imports = new ToolStripMenuItem("Import SKSE archive"); advanced.DropDownItems.Add(imports);
        foreach (var version in versions)
        {
            var item = new ToolStripMenuItem("Skyrim " + version.Version);
            item.Click += async (_, _) => await RunOperation("Importing SKSE…", async () =>
            {
                if (await ImportSkse(version)) { var flow = Workflow(); var profile = Profile(version); await Task.Run(() => flow.Prepare(version, profile)); activity.Text = "Skyrim " + version.Version + " is ready. Click Switch to activate it."; }
            }); imports.DropDownItems.Add(item);
        }
        var gameImports = new ToolStripMenuItem("Import game files from folder"); advanced.DropDownItems.Add(gameImports);
        foreach (var version in versions)
        {
            var item = new ToolStripMenuItem("Skyrim " + version.Version);
            item.Click += async (_, _) => await RunOperation("Importing game files…", async () =>
            {
                var folder = ChooseFolder(); if (folder == null) return;
                var m = Manager(version); var account = username.Text.Trim(); var remember = rememberSteam.Checked;
                var workflowClient = new LazySteamDepotClient(async () => { var tool = await new DepotDownloaderInstaller().EnsureLatest(Write); return new SteamDepotClient(tool, account, remember, Path.Combine(DepotDownloaderInstaller.DefaultRoot, "steam-session")); });
                await Task.Run(() => m.Prepare(version, workflowClient, Write, folder));
                var flow = Workflow(); var profile = Profile(version); await Task.Run(() => flow.Prepare(version, profile));
                activity.Text = "Import complete.";
            }); gameImports.DropDownItems.Add(item);
        }
        advanced.DropDownItems.Add(new ToolStripSeparator());
        AddMenu("Managed files and compatibility", () => { ShowManifest(); return Task.CompletedTask; });
        AddMenu("Compare Steam manifests…", CompareManifests);
        AddMenu("Update DepotDownloader", async () => { var generation = operationGeneration; await new DepotDownloaderInstaller().EnsureLatest(Write, feedback: value => ReportProgress(value, generation)); activity.Text = "DepotDownloader is up to date."; });
        AddMenu("Refresh status", () => Task.CompletedTask);
    }
    private void AddMenu(string caption, Func<Task> action)
    {
        var item = new ToolStripMenuItem(caption); item.Click += async (_, _) => await RunOperation(caption + "…", action); advanced.DropDownItems.Add(item);
    }
    private async Task Recover()
    {
        var manager = Manager(versions[0]);
        await Task.Run(() => { if (File.Exists(Path.Combine(manager.Root, "journal.json"))) new RuntimeManager(manager.Game).Recover(); if (manager.RecoveryPending) manager.Recover(); });
        activity.Text = "Recovery complete.";
    }
    private void ShowManifest()
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, versions.Select(v =>
        {
            var p = catalog.Resolve(v.Version, Profile(v));
            return "Skyrim " + v.Version + " · " + p.Name + Environment.NewLine + p.Validation + Environment.NewLine + string.Join(Environment.NewLine, p.Files.Select(f => f.Policy + "  " + f.Path));
        }));
        using var dialog = new Form { Text = "Managed files and compatibility", Size = new Size(760, 600), StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text }); dialog.ShowDialog(this);
    }
    private async Task CompareManifests()
    {
        using var first = new OpenFileDialog { Title = "Older Steam manifest", Filter = "Steam manifest|manifest_*.txt" }; if (first.ShowDialog(this) != DialogResult.OK) return;
        using var second = new OpenFileDialog { Title = "Newer manifest from the same depot", Filter = first.Filter }; if (second.ShowDialog(this) != DialogResult.OK) return;
        SteamManifest Parse(string file)
        {
            var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"^manifest_(\d+)_(\d+)\.txt$");
            if (!match.Success) throw new IOException("Expected manifest_<depot>_<id>.txt.");
            return SteamManifest.Parse(File.ReadAllText(file), new DepotSource(489830, uint.Parse(match.Groups[1].Value), match.Groups[2].Value, "comparison"));
        }
        var changes = await Task.Run(() => SteamManifest.Compare(Parse(first.FileName), Parse(second.FileName)));
        using var save = new SaveFileDialog { Filter = "Comparison JSON|*.json", FileName = "compatibility-candidates.json" };
        if (save.ShowDialog(this) == DialogResult.OK) { Storage.Write(save.FileName, changes); activity.Text = "Comparison exported for review."; }
    }
    private async Task RunOperation(string message, Func<Task> action, RuntimeDefinition? version = null)
    {
        if (busy) return; busy = true; ++refreshGeneration; ++operationGeneration;
        workingVersion = version; errorPanel.Visible = false; retry.Enabled = false;
        setup.Enabled = menu.Enabled = play.Enabled = false;
        foreach (var card in cards.Values) card.Download.Enabled = card.Switch.Enabled = false;
        operationClock.Restart(); clockTimer.Start();
        ShowProgress(new("Starting", message));
        try { SaveSettings(); await action(); }
        catch (Exception ex)
        {
            failedAction = action; failedMessage = message; failedVersion = version;
            var pending = false;
            try { pending = Manager(version ?? versions[0]).RecoveryPending; } catch { }
            PresentError(ex, pending);
        }
        finally
        {
            var succeeded = !errorPanel.Visible;
            var waiting = latestProgress?.Stage == "Waiting for SKSE";
            clockTimer.Stop();
            busy = false; workingVersion = null; setup.Enabled = menu.Enabled = true; retry.Enabled = true;
            progress.Style = ProgressBarStyle.Marquee;
            progressCaption.Text = "Checking installed version status…";
            await RefreshStatuses();
            operationClock.Stop(); progress.Style = ProgressBarStyle.Continuous;
            progress.Value = succeeded && !waiting ? 100 : 0;
            progressCaption.Text = (waiting ? "Waiting for SKSE import" : succeeded ? "Finished" : "Stopped") + $" • elapsed {operationClock.Elapsed:mm\\:ss}";
        }
    }
    private async Task RefreshStatuses()
    {
        if (busy) return;
        var generation = ++refreshGeneration; var folder = game.Text.Trim(); var mode = legacy.Checked;
        try
        {
            var states = await Task.Run(() => versions.Select(version =>
            {
                var selectedProfile = mode && catalog.Runtimes.Single(r => r.Version == version.Version).Profiles.Any(p => p.Id == "legacy") ? "legacy" : "runtime";
                var manager = new OverlayManager(folder, catalog, selectedProfile);
                try
                {
                    var state = manager.Status(version);
                    var needsSkse = !manager.HasSkse(version) && version.SkseUrl == null;
                    return (Version: version, State: state, NeedsSkse: needsSkse, Error: (string?)null);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                { return (Version: version, State: "Needs repair", NeedsSkse: false, Error: (string?)ex.Message); }
            }).ToArray());
            if (generation != refreshGeneration || busy || IsDisposed) return;
            activeVersion = null;
            foreach (var item in states)
            {
                var card = cards[item.Version.Version];
                var active = item.State == "Active (verified)";
                card.Status.Text = item.Error != null ? "Needs repair" : active ? "Active" : item.State == "Ready (offline)" ? "Ready · offline" : item.NeedsSkse ? "Needs Nexus SKSE" : "Not downloaded";
                if (item.Error != null) Write(item.Version.Version + ": " + item.Error);
                card.Status.ForeColor = active ? Color.FromArgb(32, 112, 70) : Color.DimGray;
                card.Switch.Text = active ? "Active" : "Switch"; card.Switch.Enabled = !active;
                card.Download.Enabled = true;
                if (active) activeVersion = item.Version;
            }
            play.Enabled = activeVersion != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            if (generation != refreshGeneration || IsDisposed) return;
            foreach (var card in cards.Values) { card.Status.Text = "Choose game folder"; card.Download.Enabled = card.Switch.Enabled = false; }
            activeVersion = null; play.Enabled = false;
        }
    }
    private void SaveSettings()
    {
        if (loadingSettings) return;
        Storage.Write(settings, new UiSettings(game.Text.Trim(), legacy.Checked, includeLegacy.Checked, rememberSteam.Checked, true));
    }
    private void LoadSettings()
    {
        loadingSettings = true;
        game.Text = Acquisition.DetectGame() ?? "";
        try
        {
            if (!File.Exists(settings)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            if (doc.RootElement.ValueKind == JsonValueKind.Array) { game.Text = doc.RootElement[0].GetString() ?? game.Text; return; }
            var saved = Storage.Read<UiSettings>(settings); game.Text = saved.Game; legacy.Checked = saved.LegacyMode; includeLegacy.Checked = saved.IncludeLegacy;
            rememberSteam.Checked = !saved.SteamLoginConfigured || saved.RememberSteam;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Write("Could not load settings: " + ex.Message); }
        finally { loadingSettings = false; }
    }
    private void ShowDetails() { if (log.Visible) return; log.Visible = true; details.Text = "Hide details"; Height = 1000; }
    private void ReportProgress(WorkProgress value, int generation)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ReportProgress(value, generation)); return; }
        if (!busy || generation != operationGeneration) return;
        ShowProgress(value);
    }
    internal void ShowProgress(WorkProgress value)
    {
        latestProgress = value; progress.Visible = true;
        activity.Text = value.Stage + " — " + value.Message;
        if (value.Percent is int percent) { progress.Style = ProgressBarStyle.Continuous; progress.Value = percent; }
        else progress.Style = ProgressBarStyle.Marquee;
        if (workingVersion != null) cards[workingVersion.Version].Status.Text = value.Stage;
        UpdateProgressCaption();
    }
    private void UpdateProgressCaption()
    {
        if (latestProgress == null) return;
        progressCaption.Text = (latestProgress.Percent is int percent ? $"{percent}% • {latestProgress.Counter}" : "Working…") + $" • elapsed {operationClock.Elapsed:mm\\:ss}";
    }
    internal void PresentError(Exception error, bool pendingRecovery = false)
    {
        var friendly = ErrorMessages.Describe(error, pendingRecovery); recoveryRequired = pendingRecovery;
        errorTitle.Text = friendly.Title; errorExplanation.Text = friendly.Explanation; errorNextStep.Text = friendly.NextStep;
        errorPanel.Visible = true; retry.Text = pendingRecovery ? "Recover files" : "Retry";
        progress.Style = ProgressBarStyle.Continuous; progress.Value = 0;
        progressCaption.Text = $"Stopped • elapsed {operationClock.Elapsed:mm\\:ss}";
        activity.Text = (workingVersion == null ? "" : "Skyrim " + workingVersion.Version + ": ") + friendly.Title;
        errorDetails = $"Operation: {failedMessage}\r\nStage: {latestProgress?.Stage}\r\n{error}";
        Write(errorDetails); Height = Math.Max(Height, 920);
    }
    private void Write(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Write(message)); return; }
        log.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
    }
    private string? ChooseFolder() { using var dialog = new FolderBrowserDialog(); return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null; }
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
