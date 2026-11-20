using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>Liest die Meta-Dateien eines Fahrzeug-Packs (vehicles, carvariations, carcols) und GXT2-Namen.</summary>
public class MetaData
{
    public class KitPart
    {
        public string Kit { get; set; }
        public string ModType { get; set; }
        public string Label { get; set; }
        public string Bone { get; set; }
        public List<string> TurnOffBones { get; set; } = new();
    }

    public List<Session.VehicleInfo> Vehicles { get; } = new();
    public Dictionary<string, KitPart> KitParts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TxdParent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<uint, string> Labels { get; } = new();

    public IEnumerable<string> TxdParents(string txd)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (txd != null && TxdParent.TryGetValue(txd, out var p) && seen.Add(p))
        {
            yield return p;
            txd = p;
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

    public static XDocument LoadXml(string file) => ParseXml(File.ReadAllText(file));

    public static XDocument ParseXml(string text)
    {
        // manche Packs haben Müll vor dem XML-Kopf
        var i = text.IndexOf('<');
        if (i > 0) text = text[i..];
        return XDocument.Parse(text);
    }

    public static string MetaKind(string file)
    {
        var n = Path.GetFileName(file).ToLowerInvariant();
        try
        {
            var root = LoadXml(file).Root?.Name.LocalName ?? "";
            return root switch
            {
                "CVehicleModelInfo__InitDataList" => "vehicles",
                "CVehicleModelInfoVariation" => "carvariations",
                "CVehicleModelInfoVarGlobal" => "carcols",
                "CHandlingDataMgr" => "handling",
                "CVehicleMetadataMgr" => "vehiclelayouts",
                "CVehicleModColsGen9" => "carmodcols",
                "CContentUnlocks" => "contentunlocks",
                "CExtraTextMetaFile" => "dlctext",
                _ => GuessByName(n)
            };
        }
        catch { return GuessByName(n); }
    }

    static string GuessByName(string n)
    {
        if (n.Contains("handling")) return "handling";
        if (n.Contains("carvariation")) return "carvariations";
        if (n.Contains("carcols")) return "carcols";
        if (n.Contains("vehiclelayout") || n.Contains("layout")) return "vehiclelayouts";
        if (n.Contains("vehicles")) return "vehicles";
        return "other";
    }

    public static MetaData Read(string sessionDir, List<string> warnings)
    {
        var md = new MetaData();
        var dataDir = Path.Combine(sessionDir, "data");
        var langDir = Path.Combine(sessionDir, "lang");

        foreach (var kv in VehicleConvert.LoadLabels(sessionDir))
            md.Labels[JenkHash.GenHash(kv.Key.ToLowerInvariant())] = kv.Value;

        if (Directory.Exists(langDir))
        {
            // american zuerst, dann german überschreibt nicht – englische Namen sind GTA-Standard
            foreach (var f in Directory.GetFiles(langDir, "*.gxt2", SearchOption.AllDirectories).OrderBy(f => Path.GetFileName(f).StartsWith("american") ? 0 : 1))
            {
                try
                {
                    var g = new Gxt2File();
                    g.Load(File.ReadAllBytes(f), null);
                    foreach (var e in g.TextEntries ?? Array.Empty<Gxt2Entry>())
                        md.Labels.TryAdd(e.Hash, e.Text);
                }
                catch (Exception ex) { warnings.Add($"{Path.GetFileName(f)}: Texte nicht lesbar ({ex.Message})"); }
            }
        }

        if (!Directory.Exists(dataDir)) return md;
        var metaFiles = Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();

        var variations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in metaFiles)
        {
            XDocument doc;
            try { doc = LoadXml(f); }
            catch (System.Xml.XmlException ex)
            {
                warnings.Add($"{Path.GetFileName(f)} ist fehlerhaft (XML-Fehler in Zeile {ex.LineNumber}). FiveM kann diese Datei so nicht laden – Fehler im Mod selbst, bitte die Datei prüfen.");
                continue;
            }
            catch (Exception ex) { warnings.Add($"{Path.GetFileName(f)} konnte nicht gelesen werden: {ex.Message}"); continue; }
            var root = doc.Root;
            switch (root?.Name.LocalName)
            {
                case "CVehicleModelInfo__InitDataList":
                    foreach (var it in Items(root, "InitDatas"))
                    {
                        var model = Val(it, "modelName")?.ToLowerInvariant();
                        if (model == null) continue;
                        md.Vehicles.Add(new Session.VehicleInfo
                        {
                            Model = model,
                            TxdName = Val(it, "txdName")?.ToLowerInvariant() ?? model,
                            HandlingId = Val(it, "handlingId"),
                            AudioName = Val(it, "audioNameHash"),
                            GameName = Val(it, "gameName"),
                            Make = Val(it, "vehicleMakeName"),
                            Type = Val(it, "type"),
                            HasMeta = true,
                        });
                    }
                    foreach (var it in Items(root, "txdRelationships"))
                    {
                        var parent = Val(it, "parent"); var child = Val(it, "child");
                        if (parent != null && child != null) md.TxdParent[child.ToLowerInvariant()] = parent.ToLowerInvariant();
                    }
                    break;
                case "CVehicleModelInfoVariation":
                    foreach (var it in Items(root, "variationData"))
                    {
                        var model = Val(it, "modelName");
                        if (model == null) continue;
                        var kits = Items(it, "kits").Select(k => k.Value.Trim()).Where(k => k.Length > 0).ToList();
                        variations[model] = kits;
                    }
                    break;
                case "CVehicleModelInfoVarGlobal":
                    foreach (var kit in Items(root, "Kits"))
                    {
                        var kitName = Val(kit, "kitName");
                        foreach (var mod in Items(kit, "visibleMods"))
                        {
                            var mn = Val(mod, "modelName")?.ToLowerInvariant();
                            if (mn == null) continue;
                            md.KitParts[mn] = new KitPart
                            {
                                Kit = kitName, ModType = Val(mod, "type"), Label = Val(mod, "modShopLabel"), Bone = Val(mod, "bone"),
                                TurnOffBones = Items(mod, "turnOffBones").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToList(),
                            };
                        }
                    }
                    break;
            }
        }

        foreach (var v in md.Vehicles)
        {
            if (variations.TryGetValue(v.Model, out var kits)) v.Kits = kits;
            var l = Label(md, v.GameName);
            var mk = Label(md, v.Make);
            v.DisplayName = l == null ? v.Model
                : (mk != null && !l.Contains(mk, StringComparison.OrdinalIgnoreCase) ? $"{mk} {l}" : l);
        }
        // Nur echte (übersetzte) Shop-Namen anzeigen, keine internen Kürzel
        foreach (var kp in md.KitParts.Values)
            kp.Label = kp.Label != null ? Label(md, kp.Label) : null;
        return md;
    }

    static string Label(MetaData md, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return md.Labels.TryGetValue(JenkHash.GenHash(key.ToLowerInvariant()), out var t) && !string.IsNullOrWhiteSpace(t) ? t : null;
    }

    public static string LabelText(MetaData md, string key) => Label(md, key);
}
