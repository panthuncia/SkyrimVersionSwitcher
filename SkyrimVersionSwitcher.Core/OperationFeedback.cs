namespace SkyrimVersionSwitcher.Core;

/// <summary>Progress is local to the named stage, never an invented overall download percentage.</summary>
public sealed record WorkProgress(string Stage, string Message, long Completed = 0, long? Total = null, string Unit = "files")
{
    public int? Percent => Total is > 0 ? (int)Math.Clamp(Completed * 100d / Total.Value, 0, 100) : null;
    public string Counter => Total is > 0 ? Unit == "bytes" ? $"{FormatBytes(Completed)} / {FormatBytes(Total.Value)}" : $"{Completed:N0} / {Total.Value:N0} {Unit}" : "";
    private static string FormatBytes(long value) => value >= 1048576 ? $"{value / 1048576d:0.0} MB" : $"{value / 1024d:0} KB";
}
public sealed record FriendlyError(string Title, string Explanation, string NextStep);
public static class ErrorMessages
{
    public static FriendlyError Describe(Exception error, bool recoveryPending = false)
    {
        if (recoveryPending) return new("File recovery is required", "The previous switch could not finish restoring files. Its recovery journal and backups have been kept.", "Close Skyrim and its launcher, then click Recover files. Keep RuntimeManager intact.");
        if (error is AcquisitionException acquisition)
            return acquisition.Failure switch
            {
                AcquisitionFailure.SteamAccessOrTransport => new("Steam download could not finish", "Steam could not supply the requested files. This can be a sign-in, network, or historical-manifest access problem.", "Check the DepotDownloader console for Steam's exact error. Complete Steam Guard if requested, confirm this account owns Skyrim, then Retry. Completed files are reused."),
                AcquisitionFailure.MissingFile or AcquisitionFailure.InvalidManifest => new("Game download definition does not match Steam", "A required file or manifest does not match the compatibility catalog.", "Retry once. If this repeats, save the error details so the catalog/downloader format can be corrected. Do not delete your backups."),
                _ => new("A downloaded file failed verification", "The downloaded content does not match its expected hash or size.", "Retry to verify or download the affected file again. Check free disk space and your connection if it keeps failing.")
            };
        if (error is HttpRequestException or TaskCanceledException) return new("Download service is unavailable", "The app could not complete an HTTPS request to GitHub or the official SKSE download service.", "Check your internet connection and Retry. Previously downloaded files remain cached.");
        if (error.Message.StartsWith("Executable version mismatch:", StringComparison.Ordinal)) return new("The executable is for a different Skyrim runtime", error.Message, "Click Download beside the selected version to verify its files. If this repeats, copy the error details so the catalog can be checked. Keep your cache and backups.");
        if (error is UnauthorizedAccessException) return new("Cannot write to the selected folder", "Windows denied access to a game, cache, or settings file.", "Check the folder's write permissions and Windows security protection. Close applications using the files, then Retry.");
        var code = error.HResult & 0xffff;
        if (error is IOException && code is 112 or 39) return new("Not enough disk space", "There is not enough space to download, preserve originals, or stage the replacement files.", "Free space on the game/cache drive, then Retry. Keep RuntimeManager backups and recovery files.");
        if (error is IOException && (code is 32 or 33 || error.Message.StartsWith("Close Skyrim", StringComparison.OrdinalIgnoreCase))) return new("Game files are in use", "Skyrim, its launcher, or another application is holding a required file open.", "Close Skyrim, SKSE, and any active Steam update or mod deployment, then Retry.");
        if (error.Message.Contains("Steam username", StringComparison.OrdinalIgnoreCase)) return new("Steam username is missing", "Game downloads require the Steam account that owns Skyrim.", "Enter your Steam username above, then Retry. Your password is entered only in DepotDownloader's console.");
        if (error is ArgumentException || error.Message.StartsWith("Choose a Skyrim", StringComparison.OrdinalIgnoreCase)) return new("Choose a Skyrim Special Edition installation", "The selected folder must contain SkyrimSE.exe and a Data folder. Any installed runtime version is supported as a starting point.", "Use Browse to select the game's installation folder, then Retry.");
        if (error is FileNotFoundException or DirectoryNotFoundException) return new("A required file is missing", "A game, tool, archive, or cached file could not be found.", "Check the game folder. Click Download beside this version to prepare missing content, or Retry the operation.");
        if (error is System.Text.Json.JsonException) return new("Saved cache information could not be read", "A settings or cache metadata file is damaged or has an unexpected format.", "Restart and retry once. If it repeats, copy the error details for repair. Keep the object store, backups, and recovery journal intact.");
        if (error.Message.Contains("Cached object", StringComparison.OrdinalIgnoreCase)) return new("A cached file is missing or damaged", "A previously saved game file failed its local integrity check.", "Click Download beside this version to verify and reacquire its content, then Switch again. Keep your original-file backups.");
        if (error.Message.Contains("SKSE", StringComparison.OrdinalIgnoreCase) && (error.Message.Contains("archive", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("Select an extracted", StringComparison.OrdinalIgnoreCase))) return new("The SKSE archive is not the expected release", "The selected archive is incomplete or does not contain the matching runtime files.", "Download the matching SKSE release, then import its .7z or .zip archive again.");
        var reason = error.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "A file or compatibility check failed.";
        return new("The operation could not finish", reason.Length > 260 ? reason[..257] + "…" : reason, "Open Show details for the affected file and full error. Retry after addressing the condition; completed downloads are kept.");
    }
}
