namespace SkyrimVersionSwitcher.Core;

/// <summary>Immutable SHA-256 objects. Active files are always copies, never hardlinks.</summary>
public sealed class ObjectStore(string root)
{
    public string Root { get; } = root;
    public static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new IOException("Invalid SHA-256 object identifier.");
    }
    public string PathFor(string hash) { ValidateHash(hash); return RuntimeManager.Safe(Root, "objects/" + hash.ToUpperInvariant()); }
    public bool Contains(string hash)
    {
        var path = PathFor(hash);
        return File.Exists(path) && RuntimeManager.Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    public string Require(string hash) => Contains(hash) ? PathFor(hash) : throw new IOException("Cached object missing or corrupt: " + hash);
    public string Add(string source, string? expected = null)
    {
        var hash = RuntimeManager.Hash(source);
        if (expected != null && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Catalog SHA-256 mismatch: " + source);
        var target = PathFor(hash);
        if (Contains(hash)) return hash;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp);
            if (RuntimeManager.Hash(temp) != hash) throw new IOException("Source changed while caching: " + source);
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return hash;
    }
}
