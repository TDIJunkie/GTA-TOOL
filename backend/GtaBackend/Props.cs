using System.Text.Json;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>
/// Katalog der GTA-Original-Objekte (Archetypen) für MLOs:
///   Name → Modell-Datei (ydr/ydd/yft) + Texturdatei (ytd) in der GTA-Installation.
/// Wird einmalig aus allen Archiven gelesen und als JSON zwischengespeichert.
/// </summary>
public static class Props
{
    public class Arch
    {
        public string Txd { get; set; }
        public string Dd { get; set; }        // drawableDictionary (für .ydd)
        public float[] BbMin { get; set; }
        public float[] BbMax { get; set; }
    }

    public class FileRef { public string A { get; set; } public string P { get; set; } }

    public class Catalog
    {
        public Dictionary<string, Arch> Archetypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FileRef> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase); // "name.ydr" → Ort
    }

    static Catalog catalog;
    static readonly Dictionary<string, RpfFile> Open = new(StringComparer.OrdinalIgnoreCase);

    static RpfFile Archive(string path)
    {
        if (Open.TryGetValue(path, out var r)) return r;
        r = new RpfFile(path, Path.GetFileName(path));
        r.ScanStructure(null, null);
        Open[path] = r;
        return r;
    }

    static IEnumerable<(RpfFile owner, RpfFileEntry fe)> Walk(RpfFile f)
    {
        foreach (var e in f.AllEntries ?? new List<RpfEntry>()) if (e is RpfFileEntry fe) yield return (f, fe);
        foreach (var c in f.Children ?? new List<RpfFile>()) foreach (var x in Walk(c)) yield return x;
    }

    public static Catalog Load(string cacheFile)
    {
        if (catalog != null) return catalog;
        if (!File.Exists(cacheFile)) return null;
        try
        {
            var c = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(cacheFile), Program.JsonOpts);
            c.Archetypes = new Dictionary<string, Arch>(c.Archetypes, StringComparer.OrdinalIgnoreCase);
            c.Files = new Dictionary<string, FileRef>(c.Files, StringComparer.OrdinalIgnoreCase);
            catalog = c;
        }
        catch { catalog = null; }
        return catalog;
    }

    static Dictionary<uint, string> byHash;

    /// <summary>Name eines Objekts aus seinem Hash (über den Katalog), sonst null.</summary>
    public static string NameOf(uint hash)
    {
        if (catalog == null) return null;
        if (byHash == null)
        {
            byHash = new Dictionary<uint, string>();
            foreach (var k in catalog.Archetypes.Keys) byHash[JenkHash.GenHash(k.ToLowerInvariant())] = k;
        }
        return byHash.TryGetValue(hash, out var n) ? n : null;
    }

    public static object Status(string cacheFile)
    {
        var c = Load(cacheFile);
        return new { built = c != null, archetypes = c?.Archetypes.Count ?? 0 };
    }

    static readonly HashSet<string> ModelExt = new(StringComparer.OrdinalIgnoreCase) { ".ydr", ".ydd", ".yft", ".ytd" };

    public static object Build(string gtaPath, string cacheFile)
    {
        if (string.IsNullOrEmpty(gtaPath) || !File.Exists(Path.Combine(gtaPath, "GTA5.exe")))
            throw new UserError("Bitte in den Einstellungen den GTA-V-Ordner (mit GTA5.exe) angeben.");
        if (!Keys.Loaded) throw new UserError("Die GTA-Schlüssel sind nicht geladen.");
        var archives = Directory.GetFiles(gtaPath, "x64*.rpf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var dlc = Path.Combine(gtaPath, "update", "x64", "dlcpacks");
        if (Directory.Exists(dlc)) archives.AddRange(Directory.GetDirectories(dlc).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "dlc.rpf")).Where(File.Exists));
        foreach (var u in new[] { "update.rpf", "update2.rpf" })
        {
            var p = Path.Combine(gtaPath, "update", u);
            if (File.Exists(p)) archives.Add(p);
        }

        var c = new Catalog();
        var ytyps = new List<(RpfFile owner, RpfFileEntry fe)>();
        int i = 0;
        foreach (var a in archives)
        {
            i++;
            Program.Progress($"GTA-Dateien werden gelesen ({i}/{archives.Count}) …", (int)(60.0 * i / archives.Count));
            RpfFile rpf;
            try { rpf = Archive(a); } catch (Exception ex) { Program.Log($"Props {a}: {ex.Message}"); continue; }
            var rel = Path.GetRelativePath(gtaPath, a);
            foreach (var (owner, fe) in Walk(rpf))
            {
                var ext = Path.GetExtension(fe.Name);
                if (ModelExt.Contains(ext))
                {
                    c.Files[fe.Name.ToLowerInvariant()] = new FileRef { A = rel, P = fe.Path.Replace('\\', '/') };
                    JenkIndex.Ensure(Path.GetFileNameWithoutExtension(fe.Name).ToLowerInvariant());
                }
                else if (ext.Equals(".ytyp", StringComparison.OrdinalIgnoreCase)) ytyps.Add((owner, fe));
            }
        }
        i = 0;
        foreach (var (owner, fe) in ytyps)
        {
            if (++i % 200 == 0) Program.Progress($"Objekt-Definitionen werden ausgewertet ({i}/{ytyps.Count}) …", 60 + (int)(35.0 * i / ytyps.Count));
            {
                try
                {
                    var data = owner.ExtractFile(fe);
                    if (data == null) continue;
                    var y = new YtypFile();
                    y.Load(data, fe);
                    foreach (var arch in y.AllArchetypes ?? Array.Empty<Archetype>())
                    {
                        if (arch is MloArchetype) continue;
                        var name = arch.Name;
                        if (string.IsNullOrEmpty(name)) continue;
                        var bd = arch._BaseArchetypeDef;
                        c.Archetypes[name] = new Arch
                        {
                            Txd = bd.textureDictionary.Hash != 0 ? bd.textureDictionary.ToString() : null,
                            Dd = bd.drawableDictionary.Hash != 0 ? bd.drawableDictionary.ToString() : null,
                            BbMin = new[] { bd.bbMin.X, bd.bbMin.Y, bd.bbMin.Z },
                            BbMax = new[] { bd.bbMax.X, bd.bbMax.Y, bd.bbMax.Z },
                        };
                    }
                }
                catch { }
            }
        }
        catalog = c;
        byHash = null;
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(c, Program.JsonOpts));
        Program.Progress("Fertig.", 100);
        return new { archetypes = c.Archetypes.Count, files = c.Files.Count };
    }

    public static object Search(string cacheFile, string term, int max)
    {
        var c = Load(cacheFile) ?? throw new UserError("Der Katalog der GTA-Objekte wurde noch nicht eingelesen.");
        term = (term ?? "").Trim().ToLowerInvariant();
        var list = c.Archetypes.Keys.Where(k => term.Length == 0 || k.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Where(k => c.Files.ContainsKey(k + ".ydr") || c.Files.ContainsKey(k + ".yft"))
            .OrderBy(k => k.Length).ThenBy(k => k).Take(Math.Max(1, max)).ToList();
        return new { items = list };
    }

    /// <summary>Modell + Texturen eines GTA-Objekts in den Cache holen (einmalig je Objekt).</summary>
    public static (string model, List<string> ytds) Extract(string gtaPath, string cacheFile, string name, string cacheDir)
    {
        var c = Load(cacheFile);
        if (c == null) return (null, new List<string>());
        name = name.ToLowerInvariant();
        c.Archetypes.TryGetValue(name, out var arch);
        var wanted = new List<string>();
        string modelName = null;
        foreach (var ext in new[] { ".ydr", ".yft" })
            if (c.Files.ContainsKey(name + ext)) { modelName = name + ext; break; }
        if (modelName == null && arch?.Dd != null && c.Files.ContainsKey(arch.Dd.ToLowerInvariant() + ".ydd")) modelName = arch.Dd.ToLowerInvariant() + ".ydd";
        if (modelName == null) return (null, new List<string>());
        wanted.Add(modelName);
        if (arch?.Txd != null && c.Files.ContainsKey(arch.Txd.ToLowerInvariant() + ".ytd")) wanted.Add(arch.Txd.ToLowerInvariant() + ".ytd");

        Directory.CreateDirectory(cacheDir);
        var outFiles = new List<string>();
        foreach (var w in wanted)
        {
            var target = Path.Combine(cacheDir, w);
            if (!File.Exists(target))
            {
                var fr = c.Files[w];
                var rpf = Archive(Path.Combine(gtaPath, fr.A));
                var hit = Walk(rpf).FirstOrDefault(x => x.fe.Path.Replace('\\', '/').Equals(fr.P, StringComparison.OrdinalIgnoreCase));
                if (hit.fe == null) continue;
                var data = hit.owner.ExtractFile(hit.fe);
                if (data == null) continue;
                if (hit.fe is RpfResourceFileEntry rr) data = ResourceBuilder.AddResourceHeader(rr, ResourceBuilder.Compress(data));
                File.WriteAllBytes(target, data);
            }
            outFiles.Add(target);
        }
        var model = outFiles.FirstOrDefault(f => f.EndsWith(modelName, StringComparison.OrdinalIgnoreCase));
        return (model, outFiles.Where(f => f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)).ToList());
    }
}
