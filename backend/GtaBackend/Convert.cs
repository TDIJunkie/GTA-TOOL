using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace GtaBackend;

/// <summary>Replace → Addon umwandeln und Fahrzeuge umbenennen (Spawn-Name, Anzeigename, Marke).</summary>
public static class VehicleConvert
{
    static readonly Regex ValidName = new(@"^[a-z][a-z0-9_]{1,29}$", RegexOptions.Compiled);

    // ------------------------------------------------------------------ Anzeigenamen (labels.json)

    static string LabelsFile(string sessionDir) => Path.Combine(sessionDir, "labels.json");

    public static Dictionary<string, string> LoadLabels(string sessionDir)
    {
        try
        {
            var f = LabelsFile(sessionDir);
            if (File.Exists(f))
                return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f)) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static void SetLabel(string sessionDir, string key, string text)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text)) return;
        var l = LoadLabels(sessionDir);
        l[key] = text.Trim();
        File.WriteAllText(LabelsFile(sessionDir), JsonSerializer.Serialize(l, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string LabelKey(string text) =>
        new string(text.ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()) is { Length: > 0 } k ? (k.Length > 20 ? k[..20] : k) : null;

    // ------------------------------------------------------------------ Hilfen

    static void CheckNewName(string sessionDir, string oldModel, string newModel)
    {
        if (!ValidName.IsMatch(newModel))
            throw new UserError("Der Spawn-Name darf nur Kleinbuchstaben, Zahlen und „_“ enthalten, muss mit einem Buchstaben beginnen und 2–30 Zeichen lang sein.");
        if (newModel.Equals(oldModel, StringComparison.OrdinalIgnoreCase)) return;
        var stream = Path.Combine(sessionDir, "stream");
        foreach (var f in new[] { newModel + ".yft", newModel + "_hi.yft", newModel + ".ytd" })
            if (File.Exists(Path.Combine(stream, f))) throw new UserError($"Es gibt schon eine Datei {f}. Bitte einen anderen Namen wählen.");
    }

    /// <summary>Modell- und Texturdateien umbenennen (inkl. _hi und +hi).</summary>
    static List<string> RenameFiles(string sessionDir, string oldModel, string oldTxd, string newModel, string backupDir)
    {
        var stream = Path.Combine(sessionDir, "stream");
        var renamed = new List<string>();
        var pairs = new List<(string from, string to)>
        {
            (oldModel + ".yft", newModel + ".yft"),
            (oldModel + "_hi.yft", newModel + "_hi.yft"),
            ((oldTxd ?? oldModel) + ".ytd", newModel + ".ytd"),
            ((oldTxd ?? oldModel) + "+hi.ytd", newModel + "+hi.ytd"),
        };
        if (!string.Equals(oldTxd, oldModel, StringComparison.OrdinalIgnoreCase)) pairs.Add((oldModel + "+hi.ytd", newModel + "+hi.ytd"));
        var index = Session.LoadIndex(sessionDir);
        foreach (var (from, to) in pairs.Distinct())
        {
            var src = Path.Combine(stream, from);
            var dst = Path.Combine(stream, to);
            if (!File.Exists(src) || from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
            Session.Backup(src, backupDir);
            File.Move(src, dst);
            renamed.Add($"{from} → {to}");
            foreach (var e in index.Where(e => e.File.Equals("stream/" + from, StringComparison.OrdinalIgnoreCase))) e.File = "stream/" + to;
        }
        File.WriteAllText(Path.Combine(sessionDir, "index.json"), JsonSerializer.Serialize(index, Program.JsonOpts));
        return renamed;
    }

    static void SetVal(XElement item, string name, string value, string after = null)
    {
        var el = item.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (el == null)
        {
            el = new XElement(name);
            var anchor = after != null ? item.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(after, StringComparison.OrdinalIgnoreCase)) : null;
            if (anchor != null) anchor.AddAfterSelf(el); else item.Add(el);
        }
        el.RemoveAttributes();
        el.Value = value;
    }

    static string GetVal(XElement item, string name)
    {
        var el = item.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return el == null ? null : (el.Attribute("value")?.Value ?? el.Value)?.Trim();
    }

    static void SaveXml(XDocument doc, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(file, settings);
        doc.Save(w);
    }

    // ------------------------------------------------------------------ Replace → Addon

    public static object ReplaceToAddon(string sessionDir, string cacheFile, string oldModel, string newModel,
        string displayName, string makeName, bool ownHandling, string backupDir)
    {
        oldModel = (oldModel ?? "").Trim().ToLowerInvariant();
        newModel = (newModel ?? "").Trim().ToLowerInvariant();
        var idx = Vanilla.Load(cacheFile) ?? throw new UserError("Die GTA-Fahrzeugdaten wurden noch nicht eingelesen.");
        if (!idx.Vehicles.TryGetValue(oldModel, out var van))
            throw new UserError($"„{oldModel}“ ist kein GTA-Originalfahrzeug. Bitte das Original-Fahrzeug auswählen, das dieses Replace ersetzt.");
        if (idx.Vehicles.ContainsKey(newModel))
            throw new UserError($"„{newModel}“ ist bereits ein GTA-Fahrzeug. Der neue Spawn-Name muss eindeutig sein (z. B. {oldModel}2 oder mein{oldModel}).");
        CheckNewName(sessionDir, oldModel, newModel);
        if (!File.Exists(Path.Combine(sessionDir, "stream", oldModel + ".yft")))
            throw new UserError($"{oldModel}.yft wurde nicht gefunden.");

        var renamed = RenameFiles(sessionDir, oldModel, oldModel, newModel, backupDir);
        string gameName = LabelKey(newModel);
        string handlingId = ownHandling ? newModel.ToUpperInvariant() : van.HandlingId;

        // vehicles.meta
        var init = XElement.Parse(van.InitData);
        SetVal(init, "modelName", newModel);
        SetVal(init, "txdName", newModel, "modelName");
        SetVal(init, "handlingId", handlingId, "txdName");
        SetVal(init, "gameName", gameName, "handlingId");
        // Motorsound des Originals behalten
        if (string.IsNullOrEmpty(GetVal(init, "audioNameHash"))) SetVal(init, "audioNameHash", oldModel.ToUpperInvariant(), "gameName");
        string makeKey = GetVal(init, "vehicleMakeName");
        if (!string.IsNullOrWhiteSpace(makeName))
        {
            makeKey = LabelKey(makeName);
            SetVal(init, "vehicleMakeName", makeKey, "gameName");
            SetLabel(sessionDir, makeKey, makeName);
        }
        var rels = new XElement("txdRelationships");
        foreach (var p in van.TxdParents)
            rels.Add(new XElement("Item", new XElement("parent", p), new XElement("child", newModel)));
        var vehDoc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("CVehicleModelInfo__InitDataList",
                new XElement("residentTxd", "vehshare"),
                new XElement("residentAnims"),
                new XElement("InitDatas", init),
                rels));
        var dataDir = Path.Combine(sessionDir, "data", newModel);
        SaveXml(vehDoc, Path.Combine(dataDir, "vehicles.meta"));

        // carvariations.meta
        if (van.Variation != null)
        {
            var vari = NormalizeVariation(XElement.Parse(van.Variation));
            SetVal(vari, "modelName", newModel);
            SaveXml(new XDocument(new XDeclaration("1.0", "UTF-8", null),
                new XElement("CVehicleModelInfoVariation", new XElement("variationData", vari))), Path.Combine(dataDir, "carvariations.meta"));
        }

        // handling.meta (eigene Kopie zum Anpassen)
        bool handlingCopied = false;
        if (ownHandling && van.HandlingId != null && idx.Handling.TryGetValue(van.HandlingId, out var hxml))
        {
            var h = XElement.Parse(hxml);
            SetVal(h, "handlingName", handlingId);
            SaveXml(new XDocument(new XDeclaration("1.0", "UTF-8", null),
                new XElement("CHandlingDataMgr", new XElement("HandlingData", h))), Path.Combine(dataDir, "handling.meta"));
            handlingCopied = true;
        }
        else if (ownHandling)
        {
            // Original-Handling nicht gefunden → vorhandenes nutzen
            SetVal(init, "handlingId", van.HandlingId ?? oldModel.ToUpperInvariant());
            SaveXml(vehDoc, Path.Combine(dataDir, "vehicles.meta"));
        }

        SetLabel(sessionDir, gameName, string.IsNullOrWhiteSpace(displayName) ? newModel : displayName);

        // index.json: Herkunft der neuen Metas
        var index = Session.LoadIndex(sessionDir);
        foreach (var f in Directory.GetFiles(dataDir))
        {
            var rel = Path.GetRelativePath(sessionDir, f).Replace('\\', '/');
            if (!index.Any(e => e.File.Equals(rel, StringComparison.OrdinalIgnoreCase)))
                index.Add(new Session.IndexEntry { File = rel, Origin = "erzeugt (Replace → Addon)", Source = newModel });
        }
        File.WriteAllText(Path.Combine(sessionDir, "index.json"), JsonSerializer.Serialize(index, Program.JsonOpts));

        var notes = new List<string>();
        var leftovers = Directory.GetFiles(Path.Combine(sessionDir, "stream"), oldModel + "_*")
            .Select(Path.GetFileName).Where(n => !n.StartsWith(newModel)).ToList();
        if (leftovers.Count > 0)
            notes.Add($"Diese Dateien heißen noch wie das Original und ersetzen es weiterhin: {string.Join(", ", leftovers.Take(6))}{(leftovers.Count > 6 ? " …" : "")}");
        return new
        {
            model = newModel,
            renamed,
            handling = handlingCopied ? "eigene Kopie (handling.meta)" : $"Original ({van.HandlingId})",
            sound = GetVal(init, "audioNameHash"),
            notes,
            model2 = Session.Analyze(sessionDir),
        };
    }

    /// <summary>
    /// Variation aus carvariations.ymt (PSO-XML) in das übliche .meta-Format bringen:
    /// indices als char_array, liveries als bool-Liste, ungelöste Hash-Namen entfernen.
    /// </summary>
    static XElement NormalizeVariation(XElement v)
    {
        foreach (var el in v.DescendantsAndSelf()) el.Attribute("itemType")?.Remove();
        foreach (var ind in v.Descendants().Where(e => e.Name.LocalName == "indices").ToList())
        {
            if (ind.Attribute("content") != null) continue;
            var nums = ind.Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            ind.RemoveAll();
            ind.SetAttributeValue("content", "char_array");
            ind.Add(new XText("\n" + string.Concat(nums.Select(n => "            " + n + "\n")) + "          "));
        }
        foreach (var liv in v.Descendants().Where(e => e.Name.LocalName == "liveries").ToList())
        {
            if (liv.Elements().Any()) continue;
            var nums = liv.Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            liv.RemoveAll();
            foreach (var n in nums) liv.Add(new XElement("Item", new XAttribute("value", n != "0" ? "true" : "false")));
        }
        // Kits mit unbekanntem Namen entfernen (sonst falscher Hash)
        var kits = v.Elements().FirstOrDefault(e => e.Name.LocalName == "kits");
        if (kits != null)
        {
            foreach (var k in kits.Elements().Where(k => k.Value.StartsWith("hash_", StringComparison.OrdinalIgnoreCase)).ToList()) k.Remove();
            if (!kits.Elements().Any()) kits.Add(new XElement("Item", "0_default_modkit"));
        }
        foreach (var name in v.Descendants().Where(e => e.Name.LocalName == "Name" && e.Value.StartsWith("hash_", StringComparison.OrdinalIgnoreCase)))
            name.Value = "Standard White";
        return v;
    }

    // ------------------------------------------------------------------ Addon umbenennen

    public static object RenameAddon(string sessionDir, string oldModel, string newModel, string displayName, string makeName, string backupDir)
    {
        oldModel = (oldModel ?? "").Trim().ToLowerInvariant();
        newModel = (newModel ?? "").Trim().ToLowerInvariant();
        CheckNewName(sessionDir, oldModel, newModel);
        var dataDir = Path.Combine(sessionDir, "data");
        if (!Directory.Exists(dataDir)) throw new UserError("Keine Meta-Dateien vorhanden.");

        string oldTxd = null, gameName = null, makeKey = null;
        bool changedName = !oldModel.Equals(newModel, StringComparison.OrdinalIgnoreCase);
        int metasChanged = 0;
        foreach (var f in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            XDocument doc;
            try { doc = MetaData.LoadXml(f); } catch { continue; }
            bool changed = false;
            var root = doc.Root;
            if (root?.Name.LocalName == "CVehicleModelInfo__InitDataList")
            {
                foreach (var it in root.Descendants().Where(e => e.Name.LocalName == "Item" && GetVal(e, "modelName")?.Equals(oldModel, StringComparison.OrdinalIgnoreCase) == true).ToList())
                {
                    oldTxd = GetVal(it, "txdName") ?? oldModel;
                    if (changedName)
                    {
                        SetVal(it, "modelName", newModel);
                        SetVal(it, "txdName", newModel, "modelName");
                    }
                    gameName = GetVal(it, "gameName");
                    if (!string.IsNullOrWhiteSpace(displayName) && (string.IsNullOrEmpty(gameName) || gameName.Equals("null", StringComparison.OrdinalIgnoreCase) || changedName))
                    {
                        gameName = LabelKey(newModel);
                        SetVal(it, "gameName", gameName, "handlingId");
                    }
                    if (!string.IsNullOrWhiteSpace(makeName))
                    {
                        makeKey = LabelKey(makeName);
                        SetVal(it, "vehicleMakeName", makeKey, "gameName");
                    }
                    changed = true;
                }
                if (changedName && oldTxd != null)
                    foreach (var c in root.Descendants().Where(e => e.Name.LocalName == "child" && e.Value.Trim().Equals(oldTxd, StringComparison.OrdinalIgnoreCase)))
                    { c.Value = newModel; changed = true; }
            }
            else if (root?.Name.LocalName == "CVehicleModelInfoVariation" && changedName)
            {
                foreach (var m in root.Descendants().Where(e => e.Name.LocalName == "modelName" && e.Value.Trim().Equals(oldModel, StringComparison.OrdinalIgnoreCase)))
                { m.Value = newModel; changed = true; }
            }
            if (changed)
            {
                Session.Backup(f, backupDir);
                SaveXml(doc, f);
                metasChanged++;
            }
        }
        if (oldTxd == null) throw new UserError($"{oldModel} wurde in keiner vehicles.meta gefunden.");

        var renamed = changedName ? RenameFiles(sessionDir, oldModel, oldTxd, newModel, backupDir) : new List<string>();
        if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrEmpty(gameName)) SetLabel(sessionDir, gameName, displayName);
        if (!string.IsNullOrWhiteSpace(makeName) && makeKey != null) SetLabel(sessionDir, makeKey, makeName);

        return new { model = newModel, renamed, metasChanged, model2 = Session.Analyze(sessionDir) };
    }
}
