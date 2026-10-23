using System.Diagnostics;

namespace SkyrimVersionSwitcher.Core;

internal static class ExecutableVersion
{
    public static string Read(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return Resolve(info.FileVersion, info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    // Skyrim's string resource reports its runtime; its fixed numeric resource can remain 1.0.0.0.
    internal static string Resolve(string? fileVersion, int major, int minor, int build)
    {
        if (string.IsNullOrWhiteSpace(fileVersion)) return $"{major}.{minor}.{build}";
        if (!Version.TryParse(fileVersion.Trim(), out var parsed) || parsed.Build < 0)
            throw new IOException($"Executable has an unreadable file version: '{fileVersion}'.");
        return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
    }

    public static void Require(string path, string expected)
    {
        var actual = Read(path);
        if (actual != expected)
            throw new IOException($"Executable version mismatch: expected Skyrim {expected}, found {actual}. File: {path}");
    }
}
