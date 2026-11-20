using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>
/// Original-Fahrzeugdaten aus der GTA-Installation (für Replace → Addon).
/// Liest vehicles.meta, carvariations.meta und handling.meta aus update.rpf, update2.rpf und allen DLC-Packs
/// und speichert pro Fahrzeug die XML-Einträge in einer Cache-Datei.
/// </summary>
public static class Vanilla
{
    public class Entry
    {
        public string Model { get; set; }
        public string InitData { get; set; }      // <Item> aus vehicles.meta
        public string Variation { get; set; }     // <Item> aus carvariations.meta
        public string HandlingId { get; set; }
        public string GameName { get; set; }
        public string Make { get; set; }
        public string Type { get; set; }
        public List<string> TxdParents { get; set; } = new();
        public string Source { get; set; }
    }

    public class Index
    {
        public string GtaPath { get; set; }
        public string Stamp { get; set; }
        public Dictionary<string, Entry> Vehicles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Handling { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase); // gameName/Make → Text
    }

    static Index cached;

    static string Stamp(string gta)
    {
        var sb = new StringBuilder();
        foreach (var f in new[] { "update\\update.rpf", "update\\update2.rpf" })
        {
            var p = Path.Combine(gta, f);
            if (File.Exists(p)) sb.Append(new FileInfo(p).Length).Append(';').Append(File.GetLastWriteTimeUtc(p).Ticks).Append(';');
        }
        var dlc = Path.Combine(gta, "update", "x64", "dlcpacks");
        if (Directory.Exists(dlc)) sb.Append(Directory.GetDirectories(dlc).Length);
        return sb.ToString();
    }

    public static Index Load(string cacheFile, string gtaPath = null)
    {
        if (cached != null && (gtaPath == null || cached.GtaPath == gtaPath)) return cached;
        if (!File.Exists(cacheFile)) return null;
        try
        {
            var idx = JsonSerializer.Deserialize<Index>(File.ReadAllText(cacheFile), Program.JsonOpts);
            if (idx == null) return null;
            idx.Vehicles = new Dictionary<string, Entry>(idx.Vehicles, StringComparer.OrdinalIgnoreCase);
            idx.Handling = new Dictionary<string, string>(idx.Handling, StringComparer.OrdinalIgnoreCase);
            idx.Labels = new Dictionary<string, string>(idx.Labels ?? new(), StringComparer.OrdinalIgnoreCase);
            if (gtaPath != null && idx.GtaPath != gtaPath) return null;
            cached = idx;
            return idx;
        }
        catch { return null; }
    }

    public static object Status(string cacheFile, string gtaPath)
    {
        var idx = Load(cacheFile, gtaPath);
        bool outdated = idx != null && !string.IsNullOrEmpty(gtaPath) && Directory.Exists(gtaPath) && idx.Stamp != Stamp(gtaPath);
        return new { built = idx != null, outdated, count = idx?.Vehicles.Count ?? 0 };
    }

    // ------------------------------------------------------------------ Einlesen

    static readonly HashSet<string> Wanted = new(StringComparer.OrdinalIgnoreCase) { "vehicles.meta", "carvariations.meta", "handling.meta", "carvariations.ymt" };

    public static object Build(string gtaPath, string cacheFile)
    {
        if (string.IsNullOrEmpty(gtaPath) || !File.Exists(Path.Combine(gtaPath, "GTA5.exe")))
            throw new UserError("Bitte in den Einstellungen den GTA-V-Ordner (mit GTA5.exe) angeben.");
        if (!Keys.Loaded) throw new UserError("Die GTA-Schlüssel sind nicht geladen (GTA-V-Ordner in den Einstellungen prüfen).");

        // Reihenfolge = Priorität (spätere überschreiben frühere)
        var archives = new List<string>();
        var upd = Path.Combine(gtaPath, "update", "update.rpf");
        var dlcRoot = Path.Combine(gtaPath, "update", "x64", "dlcpacks");
        if (Directory.Exists(dlcRoot))
            archives.AddRange(Directory.GetDirectories(dlcRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "dlc.rpf")).Where(File.Exists));
        if (File.Exists(upd)) archives.Insert(0, upd);
        var upd2 = Path.Combine(gtaPath, "update", "update2.rpf");
        if (File.Exists(upd2)) archives.Add(upd2);

        // Dateien sammeln: (Priorität, Pfad, Inhalt)
        var files = new List<(int prio, string path, string text)>();
        int ai = 0;
        foreach (var a in archives)
        {
            ai++;
            Program.Progress($"GTA-Fahrzeugdaten werden gelesen ({ai}/{archives.Count}) …", (int)(90.0 * ai / archives.Count));
            try
            {
                var rpf = new RpfFile(a, Path.GetFileName(a));
                rpf.ScanStructure(null, null);
                Collect(rpf, a, files, ai * 10);
            }
            catch (Exception ex) { Program.Log($"Vanilla {a}: {ex.Message}"); }
        }

        Program.Progress("Fahrzeugdaten werden ausgewertet …", 92);
        if (PendingYmt.Count > 0)
        {
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rxModel = new System.Text.RegularExpressions.Regex(@"<modelName>\s*([^<\s]+)\s*</modelName>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var rxKit = new System.Text.RegularExpressions.Regex(@"<kitName>\s*([^<\s]+)\s*</kitName>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var f in files)
            {
                foreach (System.Text.RegularExpressions.Match m in rxModel.Matches(f.text)) models.Add(m.Groups[1].Value.ToLowerInvariant());
                foreach (System.Text.RegularExpressions.Match m in rxKit.Matches(f.text)) kits.Add(m.Groups[1].Value);
            }
            RegisterNames(models, kits);
            foreach (var (prio, path, fe, data) in PendingYmt)
            {
                try
                {
                    var text = MetaXml.GetXml(fe, data, out _);
                    if (!string.IsNullOrEmpty(text)) files.Add((prio, path, text));
                }
                catch (Exception ex) { Program.Log($"Vanilla {path}: {ex.Message}"); }
            }
            PendingYmt.Clear();
        }
        var idx = new Index { GtaPath = gtaPath, Stamp = Stamp(gtaPath) };
        // update.rpf: dlc_patch-Dateien haben Vorrang vor den DLC-Packs → Priorität anheben
        foreach (var (prio, path, text) in files.OrderBy(f => f.prio + (f.path.Contains("dlc_patch", StringComparison.OrdinalIgnoreCase) ? 100000 : 0)))
        {
            XDocument doc;
            try { doc = MetaData.ParseXml(text); } catch { continue; }
            var root = doc.Root;
            switch (root?.Name.LocalName)
            {
                case "CVehicleModelInfo__InitDataList":
                    var parents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rel in Items(root, "txdRelationships"))
                    {
                        var p = Val(rel, "parent"); var c = Val(rel, "child");
                        if (p == null || c == null) continue;
                        if (!parents.TryGetValue(c, out var l)) parents[c] = l = new List<string>();
                        l.Add(p);
                    }
                    foreach (var it in Items(root, "InitDatas"))
                    {
                        var model = Val(it, "modelName")?.ToLowerInvariant();
                        if (model == null) continue;
                        var txd = Val(it, "txdName") ?? model;
                        var e = idx.Vehicles.TryGetValue(model, out var old) ? old : new Entry { Model = model };
                        e.InitData = it.ToString();
                        e.HandlingId = Val(it, "handlingId");
                        e.GameName = Val(it, "gameName");
                        e.Make = Val(it, "vehicleMakeName");
                        e.Type = Val(it, "type");
                        e.Source = path;
                        if (parents.TryGetValue(txd, out var pl)) e.TxdParents = pl;
                        idx.Vehicles[model] = e;
                    }
                    break;
                case "CVehicleModelInfoVariation":
                    foreach (var it in Items(root, "variationData"))
                    {
                        var model = Val(it, "modelName")?.ToLowerInvariant();
                        if (model == null) continue;
                        if (!idx.Vehicles.TryGetValue(model, out var e)) idx.Vehicles[model] = e = new Entry { Model = model };
                        e.Variation = it.ToString();
                    }
                    break;
                case "CHandlingDataMgr":
                    foreach (var it in Items(root, "HandlingData"))
                    {
                        var name = Val(it, "handlingName");
                        if (name != null) idx.Handling[name] = it.ToString();
                    }
                    break;
            }
        }
        // Fahrzeuge ohne InitData (nur Variation) entfernen
        foreach (var k in idx.Vehicles.Where(kv => kv.Value.InitData == null).Select(kv => kv.Key).ToList()) idx.Vehicles.Remove(k);

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(idx, Program.JsonOpts));
        cached = idx;
        Program.Progress("Fertig.", 100);
        return new { count = idx.Vehicles.Count, handling = idx.Handling.Count, archives = archives.Count };
    }

    static readonly List<(int prio, string path, RpfFileEntry fe, byte[] data)> PendingYmt = new();

    static readonly string[] PlateNames =
    {
        "Standard White", "White Plate 2", "White Plate 3", "Blue Plate", "Yellow Plate", "Yellow Plate 2", "Black Plate",
        "Yankton White", "Standard White 2", "Blue White", "Blue White 2", "Blue White 3", "Yellow Black", "Yellow Blue",
    };

    /// <summary>Namen registrieren, damit PSO-Hashes (Kits, Kennzeichen) als Text herauskommen.</summary>
    static void RegisterNames(IEnumerable<string> models, IEnumerable<string> extra)
    {
        foreach (var n in PlateNames) CodeWalker.GameFiles.JenkIndex.Ensure(n);
        foreach (var n in extra) CodeWalker.GameFiles.JenkIndex.Ensure(n);
        foreach (var m in models)
        {
            CodeWalker.GameFiles.JenkIndex.Ensure(m);
            for (int id = 0; id < 1000; id++) CodeWalker.GameFiles.JenkIndex.Ensure($"{id}_{m}_modkit");
        }
        for (int id = 0; id < 1000; id++) CodeWalker.GameFiles.JenkIndex.Ensure($"{id}_default_modkit");
    }

    static void Collect(RpfFile rpf, string archive, List<(int, string, string)> files, int prio)
    {
        foreach (var entry in rpf.AllEntries ?? new List<RpfEntry>())
        {
            if (entry is not RpfFileEntry fe || !Wanted.Contains(fe.Name)) continue;
            var path = fe.Path.Replace('\\', '/');
            if (path.Contains("/dlctext", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var data = rpf.ExtractFile(fe);
                if (data == null || data.Length < 10) continue;
                string text;
                if (fe.Name.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase))
                {
                    // Basisspiel: carvariations.ymt (PSO) – wird nach dem Registrieren der Namen umgewandelt
                    PendingYmt.Add((prio, path, fe, data));
                    continue;
                }
                else if (data[0] == 'R' && data[1] == 'B' && data[2] == 'F' && data[3] == '0')
                {
                    var rbf = new RbfFile();
                    rbf.Load(data);
                    text = RbfXml.GetXml(rbf);
                }
                else text = Encoding.UTF8.GetString(data);
                files.Add((prio, path, text));
            }
            catch (Exception ex) { Program.Log($"Vanilla {path}: {ex.Message}"); }
        }
        if (rpf.Children != null)
            foreach (var c in rpf.Children)
            {
                // Nur Daten-Archive durchsuchen (keine Modell-/Audio-Archive)
                Collect(c, archive, files, prio);
            }
    }

    static XElement Child(XElement e, string name) =>
        e?.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
    static IEnumerable<XElement> Items(XElement e, string listName) =>
        Child(e, listName)?.Elements().Where(x => x.Name.LocalName == "Item") ?? Enumerable.Empty<XElement>();
    static string Val(XElement e, string name)
    {
        var c = Child(e, name);
        if (c == null) return null;
        var v = c.Attribute("value")?.Value ?? c.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    // ------------------------------------------------------------------ Abfragen

    public static object Lookup(string cacheFile, List<string> models)
    {
        var idx = Load(cacheFile);
        return new
        {
            built = idx != null,
            vehicles = models.Select(m =>
            {
                Entry e = null;
                idx?.Vehicles.TryGetValue(m, out e);
                return new { model = m, found = e != null, gameName = e?.GameName, make = e?.Make, handlingId = e?.HandlingId, type = e?.Type };
            }).ToList(),
        };
    }
}
