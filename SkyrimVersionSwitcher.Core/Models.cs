using System.Text.Json;

namespace SkyrimVersionSwitcher.Core;

public sealed record RuntimeDefinition(string Version, string Manifest, string SkseVersion, string? SkseUrl, string[] RequiredFiles)
{
    public string SkseDll => $"skse64_{Version.Replace('.', '_')}.dll";
    public override string ToString() => $"Skyrim {Version}  ·  SKSE {SkseVersion}";
}
public sealed record CacheManifest(string Version, Dictionary<string, string> Files);
public sealed record Journal(string Backup, Dictionary<string, string?> Originals);
public static class Storage
{
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new IOException("Invalid JSON: " + path);
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}
