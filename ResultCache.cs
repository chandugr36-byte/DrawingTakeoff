using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DrawingTakeoff;

/// <summary>Disk cache of model replies keyed by SHA-256(model + prompt version + candidates + text). Re-runs of the same drawings cost ~0.</summary>
public sealed class ResultCache
{
    public const string PromptVersion = "v10-fast-single-pass-live-results";
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrawingTakeoff", "cache");

    public bool Enabled { get; }

    public ResultCache(bool enabled)
    {
        Enabled = enabled;
        if (enabled) { try { Directory.CreateDirectory(Dir); } catch { } }
    }

    public static string Key(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts))));

    public bool TryGet(string key, out string value)
    {
        value = "";
        if (!Enabled) return false;
        try
        {
            var f = Path.Combine(Dir, key + ".json");
            if (!File.Exists(f)) return false;
            value = File.ReadAllText(f, Encoding.UTF8);
            return true;
        }
        catch { return false; }
    }

    public async Task PutAsync(string key, string value)
    {
        if (!Enabled) return;
        try { await File.WriteAllTextAsync(Path.Combine(Dir, key + ".json"), value, Encoding.UTF8); } catch { }
    }

    public static int Clear()
    {
        try
        {
            if (!Directory.Exists(Dir)) return 0;
            var files = Directory.GetFiles(Dir, "*.json");
            foreach (var f in files) try { File.Delete(f); } catch { }
            return files.Length;
        }
        catch { return 0; }
    }
}
