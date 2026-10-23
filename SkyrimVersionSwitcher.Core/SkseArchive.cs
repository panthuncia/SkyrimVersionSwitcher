using SharpCompress.Archives;
using SharpCompress.Common;

namespace SkyrimVersionSwitcher.Core;

/// <summary>Managed SKSE extraction; does not need an installed 7-Zip executable.</summary>
public static class SkseArchive
{
    public static string Extract(string file, string expectedDll)
    {
        var root = Path.Combine(Path.GetTempPath(), "SkyrimSwitcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var archive = ArchiveFactory.Open(file);
            var runtimeEntries = archive.Entries.Where(e => e.Key != null && !e.IsDirectory && Path.GetFileName(e.Key.Replace('\\', '/')).Equals(expectedDll, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (runtimeEntries.Length != 1) throw new IOException("Archive must contain exactly one " + expectedDll);
            var runtimeKey = runtimeEntries[0].Key!.Replace('\\', '/');
            var prefix = runtimeKey[..^expectedDll.Length];
            bool Selected(IEntry entry)
            {
                var key = entry.Key?.Replace('\\', '/');
                if (key == null || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                var relative = key[prefix.Length..];
                return relative.Equals(expectedDll, StringComparison.OrdinalIgnoreCase) || relative is "skse64_loader.exe" or "skse64_steam_loader.dll"
                    || (relative.StartsWith("Data/Scripts/", StringComparison.OrdinalIgnoreCase) && RuntimeManager.Allowed(relative));
            }
            long total = 0; var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.Key == null || !string.IsNullOrEmpty(entry.LinkTarget) || (Selected(entry) && entry.IsEncrypted)) throw new IOException("Encrypted runtime files and archive links are unsupported.");
                var target = RuntimeManager.Safe(root, entry.Key.Replace('\\', '/'));
                if (!paths.Add(target)) throw new IOException("Duplicate SKSE archive paths.");
                total = checked(total + entry.Size);
                if (total > 256L * 1024 * 1024 || paths.Count > 10000) throw new IOException("SKSE archive exceeds extraction limits.");
            }
            long written = 0;
            void ExtractEntry(IEntry entry, Stream? source)
            {
                var target = RuntimeManager.Safe(root, entry.Key!.Replace('\\', '/'));
                if (entry.IsDirectory || !Selected(entry)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                var buffer = new byte[81920]; int read;
                while ((read = source!.Read(buffer)) > 0)
                {
                    written += read;
                    if (written > 256L * 1024 * 1024) throw new IOException("SKSE archive exceeds extraction limits.");
                    output.Write(buffer, 0, read);
                }
            }
            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry())
                {
                    using var source = reader.Entry.IsDirectory || !Selected(reader.Entry) ? null : reader.OpenEntryStream();
                    ExtractEntry(reader.Entry, source);
                }
            }
            else
                foreach (var entry in archive.Entries)
                {
                    using var source = entry.IsDirectory || !Selected(entry) ? null : entry.OpenEntryStream();
                    ExtractEntry(entry, source);
                }
            var matches = Directory.EnumerateFiles(root, expectedDll, SearchOption.AllDirectories).ToArray();
            if (matches.Length != 1) throw new IOException("Archive must contain exactly one " + expectedDll);
            return Path.GetDirectoryName(matches[0])!;
        }
        catch { DeleteTemporary(root); throw; }
    }
    public static void DeleteTemporary(string root)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(root));
        if (!string.Equals(parent?.TrimEnd(Path.DirectorySeparatorChar), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("SkyrimSwitcher-", StringComparison.Ordinal)) throw new IOException("Invalid extraction cleanup path.");
        var checkedPath = RuntimeManager.Safe(Path.GetTempPath(), Path.GetFileName(root));
        if (Directory.Exists(checkedPath)) Directory.Delete(checkedPath, true);
    }
    public static string TemporaryRoot(string extractedFolder)
    {
        var folder = new DirectoryInfo(Path.GetFullPath(extractedFolder));
        while (folder.Parent != null && !string.Equals(folder.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) folder = folder.Parent;
        return folder.FullName;
    }
}
