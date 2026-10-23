using SkyrimVersionSwitcher;
using System.Windows.Forms;

internal static class UiSmokeTests
{
    public static void Run(bool preview = false)
    {
        Exception? failure = null;
        var settingsRoot = Path.Combine(Path.GetTempPath(), "SkyrimUiTests-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new MainForm(Path.Combine(settingsRoot, "settings.json")) { Opacity = 0, ShowInTaskbar = false };
                form.Shown += (_, _) => form.BeginInvoke(() =>
                {
                    try
                    {
                        var controls = Descendants(form).ToArray();
                        var primary = controls.OfType<Button>().ToArray();
                        if (primary.Any(b => b.Name == "downloadEverything") || primary.Count(b => b.Name.StartsWith("download-")) != 3 || primary.Count(b => b.Name.StartsWith("switch-")) != 3) throw new Exception("Per-version download/switch actions are missing.");
                        foreach (var button in primary.Where(b => b.Name.StartsWith("download-")))
                            if (!button.Parent!.Controls.OfType<Button>().Any(b => b.Name == "switch-" + button.Name[9..])) throw new Exception("Download must be beside its matching Switch button.");
                        if (controls.OfType<TextBox>().Any(t => t.Name is "depotPath" or "sevenZipPath")) throw new Exception("External tool path should not be required.");
                        var menu = controls.OfType<MenuStrip>().Single();
                        var advanced = menu.Items.OfType<ToolStripMenuItem>().Single();
                        if (!advanced.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Remember Steam login in DepotDownloader").Checked) throw new Exception("Steam login reuse should be enabled by default.");
                        foreach (var caption in new[] { "Legacy mod compatibility", "Restore shared baseline", "Restore original installation", "Recover interrupted switch", "Import SKSE archive", "Compare Steam manifests…" })
                            if (!advanced.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Text == caption)) throw new Exception("Missing advanced action: " + caption);
                        var details = controls.OfType<TextBox>().Single(t => t.Name == "details");
                        if (details.Visible) throw new Exception("Details should be hidden by default.");
                        var play = primary.Single(b => b.Name == "playSkyrim");
                        if (play.Parent!.Bottom > form.ClientSize.Height) throw new Exception("Primary actions are clipped.");
                        if (preview)
                        {
                            var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(path);
                        }
                        form.ShowProgress(new SkyrimVersionSwitcher.Core.WorkProgress("Checking shared data", "Data/Skyrim.esm", 2, 4));
                        var progress = controls.OfType<ProgressBar>().Single();
                        if (progress.Style != ProgressBarStyle.Continuous || progress.Value != 50) throw new Exception("Determinate progress did not render.");
                        if (progress.Width < form.ClientSize.Width - 110) throw new Exception("Progress bar should span the content area.");
                        form.ShowProgress(new SkyrimVersionSwitcher.Core.WorkProgress("Steam download", "Complete Steam Guard in the console if requested."));
                        if (progress.Style != ProgressBarStyle.Marquee) throw new Exception("Unknown download progress should be indeterminate.");
                        form.PresentError(new SkyrimVersionSwitcher.Core.AcquisitionException(SkyrimVersionSwitcher.Core.AcquisitionFailure.SteamAccessOrTransport, "fixture"));
                        if (!controls.Single(c => c.Name == "operationError").Visible || !primary.Single(b => b.Name == "retryOperation").Visible) throw new Exception("Actionable error and retry controls missing.");
                        if (preview)
                        {
                            var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-error-preview.png");
                            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(path);
                        }
                        Console.WriteLine("PASS: per-version downloads sit beside Switch; progress modes and actionable retry errors render correctly");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                });
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new Exception("UI smoke test timed out.");
        if (Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, true);
        if (failure != null) throw new Exception("UI smoke test failed", failure);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
