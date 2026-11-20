using System.Text.Json;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>
/// Freemode-Peds aus der GTA-Installation:
///  – Grundkörper (head/uppr/lowr/feet/hand _000) + Texturen + Skelett (.yft) als Referenz
///  – Katalog aller Original-Kleidungsstücke (Basisspiel + DLCs) zum Übernehmen
/// Geschrieben wird nur in den Cache-Ordner, die Spieldateien bleiben unverändert.
/// </summary>
public static class Freemode
{
    public static readonly string[] Peds = { "mp_m_freemode_01", "mp_f_freemode_01" };
    static readonly string[] BodyComps = { "head", "uppr", "lowr", "feet", "hand" };

    static readonly Dictionary<string, RpfFile> OpenArchives = new(StringComparer.OrdinalIgnoreCase);

    static RpfFile Archive(string path)
    {
        if (OpenArchives.TryGetValue(path, out var r)) return r;
        r = new RpfFile(path, Path.GetFileName(path));
        r.ScanStructure(null, null);
        OpenArchives[path] = r;
        return r;
    }

    static IEnumerable<(RpfFile owner, RpfFileEntry entry)> Walk(RpfFile f)
    {
        foreach (var e in f.AllEntries ?? new List<RpfEntry>())
            if (e is RpfFileEntry fe) yield return (f, fe);
        foreach (var c in f.Children ?? new List<RpfFile>())
            foreach (var x in Walk(c)) yield return x;
    }

    static byte[] Extract(RpfFile owner, RpfFileEntry fe)
    {
        var data = owner.ExtractFile(fe);
        if (data != null && fe is RpfResourceFileEntry rr) data = ResourceBuilder.AddResourceHeader(rr, ResourceBuilder.Compress(data));
        return data;
    }

    static void RequireGta(string gtaPath)
    {
        if (string.IsNullOrEmpty(gtaPath) || !File.Exists(Path.Combine(gtaPath, "GTA5.exe")))
            throw new UserError("Bitte in den Einstellungen den GTA-V-Ordner (mit GTA5.exe) angeben.");
        if (!Keys.Loaded) throw new UserError("Die GTA-Schlüssel sind nicht geladen (GTA-V-Ordner in den Einstellungen prüfen).");
    }

    // ------------------------------------------------------------------ Grundkörper

    /// <summary>Grundkörper beider Freemode-Peds in den Cache holen (einmalig).</summary>
    public static object EnsureBase(string gtaPath, string cacheDir)
    {
        var result = new Dictionary<string, object>();
        bool complete = Peds.All(p => File.Exists(Path.Combine(cacheDir, p, p + ".yft")) && BodyComps.All(c => Directory.GetFiles(Path.Combine(cacheDir, p), $"{c}_000_*.ydd").Length > 0));
        if (!complete)
        {
            RequireGta(gtaPath);
            Program.Progress("Freemode-Körper werden aus GTA gelesen …", 10);
            var x64v = Path.Combine(gtaPath, "x64v.rpf");
            if (!File.Exists(x64v)) throw new UserError("x64v.rpf wurde im GTA-Ordner nicht gefunden.");
            var rpf = Archive(x64v);
            var rx = new Regex(@"streamedpeds_mp\.rpf[\\/](mp_[mf]_freemode_01)(?:\.(yft|ymt)|[\\/]((head|uppr|lowr|feet|hand|hair)_(000_[a-z]\.ydd|diff_000_a_[a-z]+\.ytd)))$", RegexOptions.IgnoreCase);
            foreach (var (owner, fe) in Walk(rpf))
            {
                var m = rx.Match(fe.Path);
                if (!m.Success) continue;
                var ped = m.Groups[1].Value.ToLowerInvariant();
                var dir = Path.Combine(cacheDir, ped);
                Directory.CreateDirectory(dir);
                var name = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? $"{ped}.{m.Groups[2].Value.ToLowerInvariant()}" : m.Groups[3].Value.ToLowerInvariant();
                var data = Extract(owner, fe);
                if (data != null) File.WriteAllBytes(Path.Combine(dir, name), data);
            }
        }
        foreach (var p in Peds)
        {
            var dir = Path.Combine(cacheDir, p);
            result[p] = new
            {
                dir,
                skeleton = Path.Combine(dir, p + ".yft"),
                parts = BodyComps.Select(c => new
                {
                    comp = c,
                    model = Directory.GetFiles(dir, $"{c}_000_*.ydd").FirstOrDefault(),
                    ytd = Directory.GetFiles(dir, $"{c}_diff_000_a_*.ytd").FirstOrDefault(),
                }).Where(x => x.model != null).ToList(),
            };
        }
        return result;
    }

    // ------------------------------------------------------------------ Katalog der Original-Kleidung

    public class VanillaItem
    {
        public string Ped { get; set; }
        public string Coll { get; set; }        // "" = Basisspiel
        public string Comp { get; set; }        // jbib, p_head …
        public bool Prop { get; set; }
        public int Index { get; set; }
        public string Suffix { get; set; }
        public string Archive { get; set; }
        public string Entry { get; set; }
        public List<string> Textures { get; set; } = new();
    }

    static List<VanillaItem> catalog;

    public static object CatalogStatus(string cacheFile)
    {
        var c = LoadCatalog(cacheFile);
        return new { built = c != null, count = c?.Count ?? 0 };
    }

    static List<VanillaItem> LoadCatalog(string cacheFile)
    {
        if (catalog != null) return catalog;
        if (!File.Exists(cacheFile)) return null;
        try { catalog = JsonSerializer.Deserialize<List<VanillaItem>>(File.ReadAllText(cacheFile), Program.JsonOpts); }
        catch { catalog = null; }
        return catalog;
    }

    public static object BuildCatalog(string gtaPath, string cacheFile)
    {
        RequireGta(gtaPath);
        var archives = new List<string> { Path.Combine(gtaPath, "x64v.rpf") };
        var dlcRoot = Path.Combine(gtaPath, "update", "x64", "dlcpacks");
        if (Directory.Exists(dlcRoot))
            archives.AddRange(Directory.GetDirectories(dlcRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "dlc.rpf")).Where(File.Exists));

        var items = new Dictionary<string, VanillaItem>();
        var texList = new List<(string key, string entry)>();
        int i = 0;
        foreach (var a in archives)
        {
            i++;
            Program.Progress($"GTA-Kleidung wird gelesen ({i}/{archives.Count}) …", (int)(95.0 * i / archives.Count));
            RpfFile rpf;
            try { rpf = Archive(a); } catch (Exception ex) { Program.Log($"Katalog {a}: {ex.Message}"); continue; }
            foreach (var (_, fe) in Walk(rpf))
            {
                var path = fe.Path.Replace('\\', '/');
                var parts = path.Split('/');
                if (parts.Length < 2) continue;
                var folder = parts[^2].ToLowerInvariant();
                var fm = Regex.Match(folder, @"^(mp_[mf]_freemode_01)(?:_(.+))?$");
                if (!fm.Success) continue;
                var ped = fm.Groups[1].Value;
                var coll = fm.Groups[2].Success ? fm.Groups[2].Value : "";
                var n = Clothing.ParseName(fe.Name.ToLowerInvariant());
                if (n == null) continue;
                var key = $"{ped}|{coll}|{n.Comp}|{n.Index}";
                if (n.IsTexture) { texList.Add((key, Path.GetRelativePath(gtaPath, a) + "::" + path)); continue; }
                items[key] = new VanillaItem
                {
                    Ped = ped, Coll = coll, Comp = n.Comp, Prop = n.Prop, Index = n.Index, Suffix = n.Suffix,
                    Archive = Path.GetRelativePath(gtaPath, a), Entry = path,
                };
            }
        }
        // Texturen: gleiche Dateinamen aus späteren Archiven (Patches) ersetzen frühere
        foreach (var (key, entry) in texList)
        {
            if (!items.TryGetValue(key, out var it)) continue;
            var fileName = entry[(entry.LastIndexOf('/') + 1)..];
            it.Textures.RemoveAll(t => t.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase));
            it.Textures.Add(entry);
        }
        static string FileOf(string e) => e[(e.LastIndexOf('/') + 1)..];
        foreach (var it in items.Values) it.Textures.Sort((x, y) => string.Compare(FileOf(x), FileOf(y), StringComparison.OrdinalIgnoreCase));
        catalog = items.Values.OrderBy(x => x.Ped).ThenBy(x => x.Coll).ThenBy(x => x.Comp).ThenBy(x => x.Index).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(catalog, Program.JsonOpts));
        Program.Progress("Fertig.", 100);
        return new { count = catalog.Count };
    }

    public static object Browse(string cacheFile, string ped, string comp, string coll)
    {
        var c = LoadCatalog(cacheFile) ?? throw new UserError("Der Katalog der GTA-Kleidung wurde noch nicht eingelesen.");
        var list = c.Where(x => x.Ped == ped && x.Comp == comp).ToList();
        var colls = list.Select(x => x.Coll).Distinct().OrderBy(x => x == "" ? "" : x).ToList();
        if (coll != null) list = list.Where(x => x.Coll == coll).ToList();
        return new
        {
            collections = colls,
            items = list.Take(2000).Select(x => new { id = $"{x.Ped}|{x.Coll}|{x.Comp}|{x.Index}", coll = x.Coll, index = x.Index, textures = x.Textures.Count }).ToList(),
        };
    }

    /// <summary>Original-Kleidungsstück (Modell + Texturen) in einen Ordner holen.</summary>
    public static (string model, List<string> textures, VanillaItem item) ExtractItem(string gtaPath, string cacheFile, string id, string outDir)
    {
        RequireGta(gtaPath);
        var c = LoadCatalog(cacheFile) ?? throw new UserError("Der Katalog der GTA-Kleidung wurde noch nicht eingelesen.");
        var it = c.FirstOrDefault(x => $"{x.Ped}|{x.Coll}|{x.Comp}|{x.Index}" == id) ?? throw new UserError("Kleidungsstück nicht im Katalog.");
        // Einträge nach Archiv gruppieren (Modell und Texturen können aus verschiedenen Archiven kommen)
        var byArchive = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Want(string archive, string entry)
        {
            if (!byArchive.TryGetValue(archive, out var set)) byArchive[archive] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(entry);
        }
        Want(it.Archive, it.Entry);
        foreach (var t in it.Textures)
        {
            var sep = t.IndexOf("::", StringComparison.Ordinal);
            if (sep > 0) Want(t[..sep], t[(sep + 2)..]); else Want(it.Archive, t);
        }
        Directory.CreateDirectory(outDir);
        string model = null;
        var tex = new List<string>();
        foreach (var (archive, wanted) in byArchive)
        foreach (var (owner, fe) in Walk(Archive(Path.Combine(gtaPath, archive))))
        {
            var p = fe.Path.Replace('\\', '/');
            if (!wanted.Contains(p)) continue;
            var target = Path.Combine(outDir, fe.Name.ToLowerInvariant());
            File.WriteAllBytes(target, Extract(owner, fe));
            if (p.Equals(it.Entry, StringComparison.OrdinalIgnoreCase) && archive.Equals(it.Archive, StringComparison.OrdinalIgnoreCase)) model = target; else tex.Add(target);
        }
        if (model == null) throw new UserError("Das Modell konnte nicht aus GTA gelesen werden.");
        tex.Sort(StringComparer.OrdinalIgnoreCase);
        return (model, tex, it);
    }

    public static object PreviewItem(string gtaPath, string cacheFile, string id, string outDir)
    {
        var (model, textures, it) = ExtractItem(gtaPath, cacheFile, id, outDir);
        return new { model, textures, ped = it.Ped, comp = it.Comp, prop = it.Prop };
    }
}
