using System.Text.Json;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// GTA-Welt rund um eine Position laden (zum genauen Platzieren von Interiors):
///  – Index aller YMAPs mit HD-Objekten (Bereich je YMAP), einmalig gebaut und als JSON gespeichert
///  – Umgebung: HD-Objekte (Gelände, Gebäude, Straßen, Props) im Umkreis mit Position, Ausrichtung, Größe
///  – Modelle als GLB mit kleinen Texturen (Cache)
/// </summary>
public static class World
{
    public class YmapRef
    {
        public string A { get; set; }      // Archiv (relativ zum GTA-Ordner)
        public string P { get; set; }      // Pfad im Archiv
        public float[] Min { get; set; }   // Bereich der HD-Objekte (x, y)
        public float[] Max { get; set; }
    }

    static List<YmapRef> index;
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

    static void RequireGta(string gtaPath)
    {
        if (string.IsNullOrEmpty(gtaPath) || !File.Exists(Path.Combine(gtaPath, "GTA5.exe")))
            throw new UserError("Für die 3D-Umgebung wird der GTA-V-Ordner gebraucht (Einstellungen).");
        if (!Keys.Loaded) throw new UserError("Die GTA-Schlüssel sind nicht geladen.");
    }

    static bool IsHd(CEntityDef d) => d.lodLevel == rage__eLodType.LODTYPES_DEPTH_HD || d.lodLevel == rage__eLodType.LODTYPES_DEPTH_ORPHANHD;

    public static object Status(string cacheFile)
    {
        var i = LoadIndex(cacheFile);
        return new { built = i != null, ymaps = i?.Count ?? 0 };
    }

    static List<YmapRef> LoadIndex(string cacheFile)
    {
        if (index != null) return index;
        if (!File.Exists(cacheFile)) return null;
        try { index = JsonSerializer.Deserialize<List<YmapRef>>(File.ReadAllText(cacheFile)); } catch { index = null; }
        return index;
    }

    /// <summary>Index aller YMAPs mit HD-Objekten. Spätere Archive (DLCs, update.rpf) ersetzen gleichnamige frühere.</summary>
    public static object BuildIndex(string gtaPath, string cacheFile)
    {
        RequireGta(gtaPath);
        var archives = Directory.GetFiles(gtaPath, "x64*.rpf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var dlcRoot = Path.Combine(gtaPath, "update", "x64", "dlcpacks");
        if (Directory.Exists(dlcRoot))
            archives.AddRange(Directory.GetDirectories(dlcRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "dlc.rpf")).Where(File.Exists));
        var upd = Path.Combine(gtaPath, "update", "update.rpf");
        if (File.Exists(upd)) archives.Add(upd);
        var byName = new Dictionary<string, YmapRef>(StringComparer.OrdinalIgnoreCase);
        int ai = 0;
        foreach (var a in archives)
        {
            ai++;
            Program.Progress($"GTA-Welt wird eingelesen ({ai}/{archives.Count}) …", (int)(95.0 * ai / archives.Count));
            RpfFile rpf;
            try { rpf = Archive(a); } catch (Exception ex) { Program.Log($"Welt {a}: {ex.Message}"); continue; }
            foreach (var (owner, fe) in Walk(rpf))
            {
                if (!fe.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var data = owner.ExtractFile(fe);
                    if (data == null) continue;
                    var ym = new YmapFile();
                    ym.Load(data, fe);
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    int n = 0;
                    foreach (var e in ym.AllEntities ?? Array.Empty<YmapEntityDef>())
                    {
                        if (e.IsMlo || !IsHd(e._CEntityDef)) continue;
                        var p = e.Position;
                        minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                        n++;
                    }
                    var key = fe.Name.ToLowerInvariant();
                    if (n == 0) { byName.Remove(key); continue; }
                    byName[key] = new YmapRef { A = Path.GetRelativePath(gtaPath, a), P = fe.Path.Replace('\\', '/'), Min = new[] { minX, minY }, Max = new[] { maxX, maxY } };
                }
                catch { /* kaputte/verschlüsselte YMAP überspringen */ }
            }
        }
        index = byName.Values.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(index));
        Program.Progress("Fertig.", 100);
        return new { ymaps = index.Count };
    }

    static YmapFile LoadYmap(string gtaPath, YmapRef r)
    {
        var rpf = Archive(Path.Combine(gtaPath, r.A));
        var hit = Walk(rpf).FirstOrDefault(x => x.fe.Path.Replace('\\', '/').Equals(r.P, StringComparison.OrdinalIgnoreCase));
        if (hit.fe == null) return null;
        var data = hit.owner.ExtractFile(hit.fe);
        if (data == null) return null;
        var ym = new YmapFile();
        ym.Load(data, hit.fe);
        return ym;
    }

    /// <summary>HD-Objekte im Umkreis (Weltkoordinaten, tatsächliche Ausrichtung).</summary>
    public static object Area(string gtaPath, string cacheFile, string propsCache, float x, float y, float radius, int max)
    {
        RequireGta(gtaPath);
        var idx = LoadIndex(cacheFile) ?? throw new UserError("Die GTA-Welt wurde noch nicht eingelesen.");
        Props.Load(propsCache);
        radius = Math.Clamp(radius, 20, 400);
        var hits = idx.Where(r => r.Max[0] >= x - radius - 150 && r.Min[0] <= x + radius + 150 && r.Max[1] >= y - radius - 150 && r.Min[1] <= y + radius + 150).ToList();
        var ents = new List<(float dist, object o, string name)>();
        var seen = new HashSet<string>();
        int i = 0;
        foreach (var r in hits)
        {
            i++;
            Program.Progress($"Umgebung wird geladen ({i}/{hits.Count}) …", (int)(80.0 * i / Math.Max(1, hits.Count)));
            YmapFile ym;
            try { ym = LoadYmap(gtaPath, r); } catch { continue; }
            if (ym == null) continue;
            foreach (var e in ym.AllEntities ?? Array.Empty<YmapEntityDef>())
            {
                if (e.IsMlo || !IsHd(e._CEntityDef)) continue;
                var p = e.Position;
                float d = MathF.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
                if (d > radius + 60) continue;   // große Objekte (Gelände) etwas weiter
                var h = e._CEntityDef.archetypeName.Hash;
                var name = Props.NameOf(h) ?? JenkIndex.TryGetString(h);
                if (string.IsNullOrEmpty(name)) continue;
                if (!seen.Add($"{h}|{MathF.Round(p.X, 1)}|{MathF.Round(p.Y, 1)}|{MathF.Round(p.Z, 1)}")) continue; // gleiche Stelle aus mehreren YMAPs
                var q = e.Orientation;
                ents.Add((d, new
                {
                    id = $"w{ents.Count}", name = name.ToLowerInvariant(),
                    position = new[] { p.X, p.Y, p.Z }, rotation = new[] { q.X, q.Y, q.Z, q.W },
                    scale = new[] { e._CEntityDef.scaleXY, e._CEntityDef.scaleZ },
                }, name.ToLowerInvariant()));
            }
        }
        var list = ents.OrderBy(t => t.dist).Take(Math.Clamp(max, 50, 4000)).ToList();
        return new
        {
            entities = list.Select(t => t.o).ToList(),
            archetypes = list.Select(t => t.name).Distinct().ToList(),
            ymaps = hits.Count,
            total = ents.Count,
        };
    }

    /// <summary>GLB eines GTA-Objekts für die Umgebungsansicht (kleine Texturen; Modell-Pakete .ydd werden übersprungen).</summary>
    public static object ArchGlb(string gtaPath, string propsCache, string propsDir, string name, string outFile)
    {
        if (File.Exists(outFile)) return new { file = outFile, cached = true };
        var (model, ytds) = Props.Extract(gtaPath, propsCache, name, Path.Combine(propsDir, Session.SafeName(name)));
        if (model == null || model.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase)) throw new UserError($"{name}: kein einzelnes Modell.");
        var old = Model3D.TextureLimit;
        Model3D.TextureLimit = 128;
        try { return Model3D.Export(model, ytds, outFile); }
        finally { Model3D.TextureLimit = old; }
    }
}
