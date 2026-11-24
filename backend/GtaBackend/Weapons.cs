using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>
/// Weapon Studio: Addon- und Replace-Waffen.
///  – Erkennen: Waffen aus weapons.meta (CWeaponInfo), Aufsätze aus weaponcomponents.meta,
///    Modell ↔ Texturdatei aus weaponarchetypes.meta (txdName, „+hi“-Texturen), Anzeigenamen (GXT2, AddTextEntry, eigene)
///  – Replace ohne Metas: alle w_*-Modelle ersetzen die gleichnamigen GTA-Waffen
///  – Anzeigename ändern
///  – FiveM-Export mit den Waffen-data_files in der richtigen Reihenfolge, Namen und optionalem Test-Befehl /waffe
/// </summary>
public static class Weapons
{
    static string Stream(string sessionDir) => Path.Combine(sessionDir, "stream");

    // Reihenfolge im fxmanifest: Aufsätze und Archetypen vor den Waffen
    static readonly (string kind, string dataFile)[] DataFiles =
    {
        ("weaponcomponents", "WEAPONCOMPONENTSINFO_FILE"),
        ("weaponarchetypes", "WEAPON_METADATA_FILE"),
        ("weaponanimations", "WEAPON_ANIMATIONS_FILE"),
        ("pedpersonality", "PED_PERSONALITY_FILE"),
        ("weapons", "WEAPONINFO_FILE"),
    };

    static string RootKind(string root) => root switch
    {
        "CWeaponInfoBlob" => "weapons",
        "CWeaponComponentInfoBlob" => "weaponcomponents",
        "CWeaponModelInfo__InitDataList" => "weaponarchetypes",
        "CWeaponAnimationsSets" => "weaponanimations",
        "CPedModelInfo__PersonalityDataList" => "pedpersonality",
        _ => "other",
    };

    static string Val(XElement e, string name)
    {
        var c = e.Element(name);
        if (c == null) return null;
        var v = c.Attribute("value")?.Value ?? c.Attribute("ref")?.Value ?? c.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    static float? Num(XElement e, string name) =>
        float.TryParse(Val(e, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;

    class MetaFile { public string Rel, Full, Kind; public XDocument Doc; }

    static List<MetaFile> Metas(string sessionDir, List<string> warnings)
    {
        var res = new List<MetaFile>();
        var dataDir = Path.Combine(sessionDir, "data");
        if (!Directory.Exists(dataDir)) return res;
        foreach (var f in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = "data/" + Path.GetRelativePath(dataDir, f).Replace('\\', '/');
            XDocument doc = null;
            try { doc = MetaData.LoadXml(f); }
            catch (Exception ex) { warnings?.Add($"{Path.GetFileName(f)} ist fehlerhaft und wurde übersprungen ({ex.Message.Split('\n')[0]})."); continue; }
            var kind = RootKind(doc.Root?.Name.LocalName ?? "");
            res.Add(new MetaFile { Rel = rel, Full = f, Kind = kind, Doc = doc });
        }
        return res;
    }

    /// <summary>Anzeigenamen: eigene (labels.json) vor AddTextEntry aus Original-Skripten vor GXT2 (american zuerst).</summary>
    class LabelSet { public Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase); public Dictionary<uint, string> Hashes = new(); }

    static LabelSet Labels(string sessionDir, List<string> warnings)
    {
        var byHash = new Dictionary<uint, string>();
        var langDir = Path.Combine(sessionDir, "lang");
        if (Directory.Exists(langDir))
        {
            foreach (var f in Directory.GetFiles(langDir, "*.gxt2", SearchOption.AllDirectories).OrderBy(f => Path.GetFileName(f).StartsWith("american") ? 0 : 1))
            {
                try
                {
                    var g = new Gxt2File();
                    g.Load(File.ReadAllBytes(f), null);
                    foreach (var e in g.TextEntries ?? Array.Empty<Gxt2Entry>()) byHash.TryAdd(e.Hash, e.Text);
                }
                catch (Exception ex) { warnings?.Add($"{Path.GetFileName(f)}: Texte nicht lesbar ({ex.Message})"); }
            }
        }
        var res = new LabelSet { Hashes = byHash };
        var orig = Path.Combine(sessionDir, "original");
        if (Directory.Exists(orig))
        {
            var rx = new Regex(@"AddTextEntry\s*\(\s*['""]([^'""]+)['""]\s*,\s*['""]([^'""]*)['""]");
            foreach (var f in Directory.EnumerateFiles(orig, "*.lua", SearchOption.AllDirectories))
            {
                try { foreach (Match m in rx.Matches(File.ReadAllText(f))) res.Keys[m.Groups[1].Value] = m.Groups[2].Value; } catch { }
            }
        }
        foreach (var kv in VehicleConvert.LoadLabels(sessionDir)) res.Keys[kv.Key] = kv.Value;
        return res;
    }

    static string LabelText(LabelSet labels, string key)
    {
        if (string.IsNullOrEmpty(key) || key.Equals("WT_INVALID", StringComparison.OrdinalIgnoreCase)) return null;
        if (labels.Keys.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t)) return t;
        if (labels.Hashes.TryGetValue(JenkHash.GenHash(key.ToLowerInvariant()), out t) && !string.IsNullOrWhiteSpace(t)) return t;
        return null;
    }

    class Arch { public string Model, Txd; }

    // ------------------------------------------------------------------ Analyse

    public static object Analyze(string sessionDir)
    {
        var warnings = new List<string>();
        var s = Stream(sessionDir);
        Directory.CreateDirectory(s);
        var files = Directory.GetFiles(s).Select(Path.GetFileName).ToList();
        var lower = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var metas = Metas(sessionDir, warnings);
        var labels = Labels(sessionDir, warnings);

        // Archetypen: Modell → Texturdatei
        var archs = new Dictionary<string, Arch>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metas.Where(m => m.Kind == "weaponarchetypes"))
            foreach (var it in m.Doc.Root.Element("InitDatas")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var model = Val(it, "modelName")?.ToLowerInvariant();
                if (model == null) continue;
                var txd = Val(it, "txdName")?.ToLowerInvariant();
                archs[model] = new Arch { Model = model, Txd = txd == "null" ? null : txd };
            }

        // Modelle (.ydr, ohne _hi)
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        List<string> YtdsOf(string model)
        {
            var list = new List<string>();
            void Add(string n) { if (n != null && lower.Contains(n + ".ytd") && !list.Contains("stream/" + n + ".ytd")) list.Add("stream/" + n + ".ytd"); }
            var txd = archs.TryGetValue(model, out var a) && a.Txd != null ? a.Txd : model;
            Add(txd); Add(txd + "+hi"); Add(model); Add(model + "+hi");
            return list;
        }
        foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!f.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            if (name.EndsWith("_hi") && lower.Contains(name[..^3] + ".ydr")) continue;
            var rel = "stream/" + f;
            var ytds = YtdsOf(name);
            foreach (var y in ytds) used.Add(y);
            models[name] = new Dictionary<string, object>
            {
                ["name"] = name, ["file"] = rel, ["size"] = new FileInfo(Path.Combine(s, f)).Length,
                ["hi"] = lower.Contains(name + "_hi.ydr") ? $"stream/{name}_hi.ydr" : null,
                ["ytds"] = ytds, ["encrypted"] = Session.IsEncrypted(sessionDir, rel),
                ["usedBy"] = new List<string>(),
            };
            if (new FileInfo(Path.Combine(s, f)).Length > 16 * 1024 * 1024) warnings.Add($"{f} ist größer als 16 MB – FiveM lädt solche Dateien oft nicht zuverlässig.");
        }
        object ModelRef(string model, string user)
        {
            if (string.IsNullOrEmpty(model)) return null;
            if (!models.TryGetValue(model, out var m)) return null;
            var ub = (List<string>)m["usedBy"];
            if (!ub.Contains(user)) ub.Add(user);
            return m;
        }

        // Aufsätze
        var comps = new Dictionary<string, (string name, string model, string kind, string loc)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metas.Where(m => m.Kind == "weaponcomponents"))
            foreach (var it in m.Doc.Root.Element("Infos")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var n = Val(it, "Name");
                if (n == null) continue;
                var type = (it.Attribute("type")?.Value ?? "").Replace("CWeaponComponent", "").Replace("Info", "");
                comps[n] = (n, Val(it, "Model")?.ToLowerInvariant(), type, Val(it, "LocName"));
            }

        // Waffen
        var weapons = new List<object>();
        foreach (var m in metas.Where(m => m.Kind == "weapons"))
        {
            foreach (var it in m.Doc.Descendants("Item").Where(x => (string)x.Attribute("type") == "CWeaponInfo"))
            {
                var name = Val(it, "Name");
                if (name == null) continue;
                var model = Val(it, "Model")?.ToLowerInvariant();
                var label = Val(it, "HumanNameHash");
                var ammoRef = it.Element("AmmoInfo")?.Attribute("ref")?.Value;
                string ammoModel = null;
                if (!string.IsNullOrEmpty(ammoRef))
                {
                    var ammo = m.Doc.Descendants("Item").FirstOrDefault(x => ((string)x.Attribute("type") ?? "").StartsWith("CAmmo") && Val(x, "Name") == ammoRef);
                    if (ammo != null) ammoModel = Val(ammo, "Model")?.ToLowerInvariant();
                }
                var wcomps = new List<object>();
                foreach (var ap in it.Element("AttachPoints")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                {
                    var bone = Val(ap, "AttachBone");
                    foreach (var ci in ap.Element("Components")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                    {
                        var cn = Val(ci, "Name");
                        if (cn == null) continue;
                        comps.TryGetValue(cn, out var c);
                        wcomps.Add(new
                        {
                            name = cn, bone, isDefault = Val(ci, "Default") == "true", kind = c.kind, known = c.name != null,
                            label = LabelText(labels, c.loc), model = ModelRef(c.model, name),
                        });
                    }
                }
                var mainModel = ModelRef(model, name);
                ModelRef(ammoModel, name);
                int? clip = null;
                if (Num(it, "ClipSize") is float cs && cs > 0) clip = (int)cs;
                weapons.Add(new
                {
                    name, model, label, labelText = LabelText(labels, label),
                    meta = m.Rel, file = mainModel != null ? ((Dictionary<string, object>)mainModel)["file"] : null,
                    hi = mainModel != null ? ((Dictionary<string, object>)mainModel)["hi"] : null,
                    ytds = mainModel != null ? ((Dictionary<string, object>)mainModel)["ytds"] : new List<string>(),
                    encrypted = mainModel != null && (bool)((Dictionary<string, object>)mainModel)["encrypted"],
                    ammoModel = ammoModel != null && models.ContainsKey(ammoModel) ? ammoModel : null,
                    group = Val(it, "Group")?.Replace("GROUP_", ""), damageType = Val(it, "DamageType"), fireType = Val(it, "FireType"),
                    damage = Num(it, "Damage"), range = Num(it, "WeaponRange"), timeBetweenShots = Num(it, "TimeBetweenShots"), clipSize = clip,
                    components = wcomps,
                });
                if (mainModel == null && !string.IsNullOrEmpty(model) && !model.Equals("null", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"{name}: Modell „{model}.ydr“ ist nicht im Paket (evtl. ein GTA-Modell).");
            }
        }

        // Modelle ohne Meta-Eintrag: Replace einer GTA-Waffe bzw. lose Teile
        var unused = models.Values.Where(x => ((List<string>)x["usedBy"]).Count == 0).ToList();
        var replaces = unused.Where(x => ((string)x["name"]).StartsWith("w_")).ToList();
        var others = unused.Where(x => !((string)x["name"]).StartsWith("w_")).ToList();
        if (weapons.Count == 0 && models.Count == 0) warnings.Add("Keine Waffenmodelle (.ydr) gefunden.");
        foreach (var y in files.Where(f => f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)))
            if (!used.Contains("stream/" + y))
            {
                // Texturdatei eines Modells ohne eigenen Eintrag (z. B. w_pi_pistol+hi.ytd zu einer ersetzten Waffe)
                var b = Path.GetFileNameWithoutExtension(y).ToLowerInvariant().Replace("+hi", "");
                if (models.ContainsKey(b)) used.Add("stream/" + y);
            }
        if (metas.Any(m => m.Kind == "weapons") && !metas.Any(m => m.Kind == "weaponarchetypes"))
            warnings.Add("Keine weaponarchetypes.meta gefunden – ohne sie lädt das Spiel Addon-Waffenmodelle nicht.");

        return new
        {
            weapons,
            models = models.Values.ToList(),
            replaces = replaces.Select(x => x["name"]).ToList(),
            others = others.Select(x => x["name"]).ToList(),
            ytds = files.Where(f => f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)).Select(f => new { file = "stream/" + f, used = used.Contains("stream/" + f) }).ToList(),
            metas = metas.Where(m => m.Kind != "other").Select(m => new { file = m.Rel, kind = m.Kind }).ToList(),
            otherMetas = metas.Where(m => m.Kind == "other").Select(m => m.Rel).ToList(),
            warnings,
            original = Session.FindOriginal(sessionDir)?.Name,
        };
    }

    // ------------------------------------------------------------------ Anzeigename

    /// <summary>Anzeigenamen einer Waffe setzen. Hat sie noch keinen Text-Schlüssel, wird einer angelegt (HumanNameHash).</summary>
    public static object SetLabel(string sessionDir, string weapon, string text, string backupDir)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new UserError("Bitte einen Namen eingeben.");
        if (text.Length > 60) throw new UserError("Der Name ist zu lang (max. 60 Zeichen).");
        foreach (var m in Metas(sessionDir, null).Where(m => m.Kind == "weapons"))
        {
            var it = m.Doc.Descendants("Item").FirstOrDefault(x => (string)x.Attribute("type") == "CWeaponInfo" && Val(x, "Name") == weapon);
            if (it == null) continue;
            var key = Val(it, "HumanNameHash");
            if (string.IsNullOrEmpty(key) || key.Equals("WT_INVALID", StringComparison.OrdinalIgnoreCase))
            {
                key = "WT_" + new string(weapon.Replace("WEAPON_", "").ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
                if (key.Length > 20) key = key[..20];
                var el = it.Element("HumanNameHash");
                if (el == null) it.Add(new XElement("HumanNameHash", key)); else el.Value = key;
                Session.Backup(m.Full, backupDir);
                SaveXml(m.Doc, m.Full);
            }
            VehicleConvert.SetLabel(sessionDir, key, text);
            return new { key, text };
        }
        throw new UserError($"Die Waffe {weapon} wurde in keiner weapons.meta gefunden.");
    }

    static void SaveXml(XDocument doc, string file)
    {
        var settings = new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var w = System.Xml.XmlWriter.Create(file, settings);
        doc.Save(w);
    }

    // ------------------------------------------------------------------ Original-GTA-Waffen

    public class GtaWeapon
    {
        public string Model { get; set; }    // w_pi_pistol
        public string Name { get; set; }     // WEAPON_PISTOL bzw. COMPONENT_… (Aufsatz)
        public string Text { get; set; }     // Anzeigename (deutsch, sonst englisch)
        public bool Part { get; set; }       // Aufsatz/Magazin statt Waffe
    }

    public class GtaCatalog { public List<GtaWeapon> Items { get; set; } = new(); }

    static GtaCatalog gtaCatalog;

    static GtaCatalog LoadGta(string cacheFile)
    {
        if (gtaCatalog != null) return gtaCatalog;
        if (!File.Exists(cacheFile)) return null;
        try { gtaCatalog = System.Text.Json.JsonSerializer.Deserialize<GtaCatalog>(File.ReadAllText(cacheFile), Program.JsonOpts); } catch { gtaCatalog = null; }
        return gtaCatalog;
    }

    public static object GtaStatus(string cacheFile, string propsCache) =>
        new { built = LoadGta(cacheFile) != null && Props.Load(propsCache) != null };

    /// <summary>
    /// Einmalig: alle Waffen der GTA-Installation (Modell → WEAPON_… → Anzeigename) aus den Waffen-Metas und Texten lesen.
    /// Dateiorte kommen aus dem Objekt-Katalog (wird bei Bedarf mit eingelesen).
    /// </summary>
    public static object BuildGta(string gtaPath, string cacheFile, string propsCache)
    {
        if (string.IsNullOrEmpty(gtaPath) || !File.Exists(Path.Combine(gtaPath, "GTA5.exe")))
            throw new UserError("Bitte in den Einstellungen den GTA-V-Ordner (mit GTA5.exe) angeben.");
        if (!Keys.Loaded) throw new UserError("Die GTA-Schlüssel sind nicht geladen.");
        if (Props.Load(propsCache) == null) Props.Build(gtaPath, propsCache);
        var files = Props.Load(propsCache).Files;

        var info = new Dictionary<string, (string name, string label, bool part)>(StringComparer.OrdinalIgnoreCase);
        var de = new Dictionary<uint, string>();
        var en = new Dictionary<uint, string>();
        var archives = Props.GameArchives(gtaPath);
        int i = 0;
        foreach (var a in archives)
        {
            Program.Progress($"Waffen werden gesucht ({++i}/{archives.Count}) …", (int)(95.0 * i / archives.Count));
            RpfFile rpf;
            try { rpf = Props.Archive(a); } catch { continue; }
            foreach (var (owner, fe) in Props.Walk(rpf))
            {
                var n = fe.Name.ToLowerInvariant();
                try
                {
                    if (n.EndsWith(".meta") && n.Contains("weapon"))
                    {
                        var data = owner.ExtractFile(fe);
                        if (data == null) continue;
                        string text;
                        if (data.Length > 4 && data[0] == 'R' && data[1] == 'B' && data[2] == 'F' && data[3] == '0') { var rbf = new RbfFile(); rbf.Load(data); text = RbfXml.GetXml(rbf); }
                        else text = Encoding.UTF8.GetString(data);
                        var doc = MetaData.ParseXml(text);
                        foreach (var it in doc.Descendants("Item"))
                        {
                            var type = (string)it.Attribute("type") ?? "";
                            var model = Val(it, "Model")?.ToLowerInvariant();
                            if (string.IsNullOrEmpty(model) || model == "null") continue;
                            if (type == "CWeaponInfo") info[model] = (Val(it, "Name"), Val(it, "HumanNameHash"), false);
                            else if (type.StartsWith("CWeaponComponent") && !info.ContainsKey(model)) info[model] = (Val(it, "Name"), Val(it, "LocName"), true);
                        }
                    }
                    else if (n.EndsWith(".gxt2"))
                    {
                        var p = fe.Path.ToLowerInvariant();
                        var target = p.Contains("german") ? de : p.Contains("american") ? en : null;
                        if (target == null) continue;
                        var data = owner.ExtractFile(fe);
                        if (data == null) continue;
                        var g = new Gxt2File();
                        g.Load(data, fe);
                        foreach (var e in g.TextEntries ?? Array.Empty<Gxt2Entry>()) target[e.Hash] = e.Text;
                    }
                }
                catch { /* einzelne kaputte Datei überspringen */ }
            }
        }

        string Text(string key)
        {
            if (string.IsNullOrEmpty(key) || key.EndsWith("_INVALID", StringComparison.OrdinalIgnoreCase)) return null;
            var h = JenkHash.GenHash(key.ToLowerInvariant());
            return de.TryGetValue(h, out var t) && !string.IsNullOrWhiteSpace(t) ? t : en.TryGetValue(h, out t) && !string.IsNullOrWhiteSpace(t) ? t : null;
        }

        var cat = new GtaCatalog();
        foreach (var f in files.Keys.Where(k => k.StartsWith("w_") && k.EndsWith(".ydr")).OrderBy(k => k))
        {
            var model = f[..^4];
            if (model.EndsWith("_hi") && files.ContainsKey(model[..^3] + ".ydr")) continue;
            info.TryGetValue(model, out var x);
            cat.Items.Add(new GtaWeapon { Model = model, Name = x.name, Text = Text(x.label), Part = x.name == null || x.part });
        }
        gtaCatalog = cat;
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        File.WriteAllText(cacheFile, System.Text.Json.JsonSerializer.Serialize(cat, Program.JsonOpts));
        Program.Progress("Fertig.", 100);
        return new { weapons = cat.Items.Count(x => !x.Part), parts = cat.Items.Count(x => x.Part) };
    }

    public static object GtaSearch(string cacheFile, string term, bool parts)
    {
        var c = LoadGta(cacheFile) ?? throw new UserError("Die GTA-Waffen wurden noch nicht eingelesen.");
        term = (term ?? "").Trim();
        var list = c.Items.Where(x => parts || !x.Part)
            .Where(x => term.Length == 0 || x.Model.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (x.Name ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) || (x.Text ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Part).ThenBy(x => x.Text ?? x.Name ?? x.Model, StringComparer.OrdinalIgnoreCase).Take(400).ToList();
        return new { items = list };
    }

    /// <summary>Dateien einer GTA-Waffe: Modell, _hi-Modell, Texturdatei und +hi-Texturdatei (soweit vorhanden).</summary>
    static List<string> GtaFiles(string gtaPath, string propsCache, string model, string cacheDir)
    {
        var res = new List<string>();
        foreach (var f in new[] { model + ".ydr", model + "_hi.ydr", model + ".ytd", model + "+hi.ytd" })
        {
            var p = Props.ExtractFile(gtaPath, propsCache, f, cacheDir);
            if (p != null) res.Add(p);
        }
        return res;
    }

    /// <summary>Für die Vorschau: Waffe in den Cache holen → (Modell, Texturdateien).</summary>
    public static object GtaExtract(string gtaPath, string propsCache, string model, string cacheDir)
    {
        model = model.ToLowerInvariant();
        var files = GtaFiles(gtaPath, propsCache, model, Path.Combine(cacheDir, Session.SafeName(model)));
        var ydr = files.FirstOrDefault(f => Path.GetFileName(f).Equals(model + ".ydr", StringComparison.OrdinalIgnoreCase))
                  ?? throw new UserError($"Das Modell {model}.ydr wurde in der GTA-Installation nicht gefunden.");
        return new { model = ydr, ytds = files.Where(f => f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)).ToList() };
    }

    /// <summary>Original-GTA-Waffe als Replace ins Paket kopieren (gleiche Dateinamen → ersetzt die Waffe im Spiel).</summary>
    public static object AddFromGta(string sessionDir, string gtaPath, string propsCache, string model, string cacheDir)
    {
        model = model.ToLowerInvariant();
        var s = Stream(sessionDir);
        Directory.CreateDirectory(s);
        if (File.Exists(Path.Combine(s, model + ".ydr"))) throw new UserError($"{model} ist schon geöffnet.");
        var files = GtaFiles(gtaPath, propsCache, model, Path.Combine(cacheDir, Session.SafeName(model)));
        if (!files.Any(f => f.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase))) throw new UserError($"Das Modell {model}.ydr wurde in der GTA-Installation nicht gefunden.");
        var index = Session.LoadIndex(sessionDir);
        foreach (var f in files)
        {
            var name = Path.GetFileName(f).ToLowerInvariant();
            File.Copy(f, Path.Combine(s, name), true);
            index.RemoveAll(e => e.File.Equals("stream/" + name, StringComparison.OrdinalIgnoreCase));
            index.Add(new Session.IndexEntry { File = "stream/" + name, Origin = "gta/" + name, Source = "gta" });
        }
        Session.SaveIndex(sessionDir, index);
        return new { model, files = files.Select(f => "stream/" + Path.GetFileName(f).ToLowerInvariant()).ToList() };
    }

    // ------------------------------------------------------------------ FiveM-Export

    public static object Export(string sessionDir, string outDir, string resourceName, bool testCommand)
    {
        resourceName = ExportMerge.ResName(string.IsNullOrWhiteSpace(resourceName) ? "waffen" : resourceName);
        var res = Path.Combine(outDir, resourceName);
        if (Directory.Exists(res) && Directory.EnumerateFileSystemEntries(res).Any())
        {
            if (!File.Exists(Path.Combine(res, "fxmanifest.lua"))) throw new UserError($"{res} existiert schon und ist keine FiveM-Resource.");
            Directory.Delete(res, true);
        }
        var warnings = new List<string>();
        var s = Stream(sessionDir);
        var outStream = Path.Combine(res, "stream");
        Directory.CreateDirectory(outStream);
        int n = 0;
        foreach (var f in Directory.GetFiles(s))
        {
            File.Copy(f, Path.Combine(outStream, Path.GetFileName(f).ToLowerInvariant()), true);
            n++;
            if (new FileInfo(f).Length > 16 * 1024 * 1024) warnings.Add($"{Path.GetFileName(f)} ist größer als 16 MB.");
            if (Session.IsEncrypted(sessionDir, "stream/" + Path.GetFileName(f))) warnings.Add($"{Path.GetFileName(f)} ist verschlüsselt (Escrow) – läuft nur auf dem Server des Käufers.");
        }

        // Metas: nur die Waffen-Dateien (andere DLC-Dateien wie Shops/Statistiken braucht FiveM nicht)
        var metas = new List<(string rel, string kind)>();
        foreach (var m in Metas(sessionDir, warnings).Where(m => m.Kind != "other"))
        {
            var rel = m.Rel.ToLowerInvariant();
            var target = Path.Combine(res, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(m.Full, target, true);
            metas.Add((rel, m.Kind));
            n++;
        }

        var model = (dynamic)Analyze(sessionDir);
        var weaponList = new List<(string name, string label, string text)>();
        foreach (var w in model.weapons) weaponList.Add(((string)w.name, (string)w.label, (string)w.labelText));

        // Namen
        var names = new StringBuilder();
        names.AppendLine("-- Automatisch erzeugt vom GTA Tool: Anzeigenamen der Waffen");
        names.AppendLine("Citizen.CreateThread(function()");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, label, text) in weaponList)
            if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(text) && seen.Add(label))
                names.AppendLine($"    AddTextEntry('{Lua(label)}', '{Lua(text)}')");
        names.AppendLine("end)");
        bool hasNames = seen.Count > 0;
        if (hasNames)
        {
            Directory.CreateDirectory(Path.Combine(res, "client"));
            File.WriteAllText(Path.Combine(res, "client", "names.lua"), names.ToString(), new UTF8Encoding(false));
        }
        if (testCommand)
        {
            Directory.CreateDirectory(Path.Combine(res, "client"));
            File.WriteAllText(Path.Combine(res, "client", "test.lua"), TestLua, new UTF8Encoding(false));
        }

        var fx = new StringBuilder();
        fx.AppendLine("fx_version 'cerulean'");
        fx.AppendLine("game 'gta5'");
        fx.AppendLine("lua54 'yes'");
        fx.AppendLine();
        fx.AppendLine("author 'GTA Tool'");
        fx.AppendLine($"description '{Lua(resourceName)} – Waffen, erstellt mit dem GTA Tool'");
        fx.AppendLine("version '1.0.0'");
        fx.AppendLine();
        if (metas.Count > 0)
        {
            fx.AppendLine("files {");
            foreach (var m in metas) fx.AppendLine($"    '{m.rel}',");
            fx.AppendLine("}");
            fx.AppendLine();
            foreach (var (kind, dataFile) in DataFiles)
                foreach (var m in metas.Where(m => m.kind == kind))
                    fx.AppendLine($"data_file '{dataFile}' '{m.rel}'");
            fx.AppendLine();
        }
        if (hasNames) fx.AppendLine("client_script 'client/names.lua'");
        if (testCommand) fx.AppendLine("client_script 'client/test.lua'");
        File.WriteAllText(Path.Combine(res, "fxmanifest.lua"), fx.ToString(), new UTF8Encoding(false));

        var rd = new StringBuilder();
        rd.AppendLine($"Resource „{resourceName}“ – erstellt mit dem GTA Tool am {DateTime.Now:dd.MM.yyyy HH:mm}");
        rd.AppendLine();
        rd.AppendLine("Installation:");
        rd.AppendLine($"  1. Den Ordner „{resourceName}“ in den resources-Ordner des Servers kopieren.");
        rd.AppendLine($"  2. In der server.cfg eintragen:  ensure {resourceName}");
        rd.AppendLine();
        if (weaponList.Count > 0)
        {
            rd.AppendLine("Enthaltene Waffen:");
            foreach (var (name, _, text) in weaponList) rd.AppendLine($"  {name}{(text != null ? $"  ({text})" : "")}");
            rd.AppendLine();
            rd.AppendLine("In eigenen Scripts (Lua):  GiveWeaponToPed(PlayerPedId(), joaat('WEAPON_NAME'), 250, false, true)");
            rd.AppendLine("Inventar-Systeme (ox_inventory, qb-core …) brauchen zusätzlich einen Eintrag mit dem Waffennamen.");
        }
        else
        {
            rd.AppendLine("Diese Resource ersetzt vorhandene GTA-Waffen (Replace):");
            foreach (var x in model.replaces) rd.AppendLine($"  {x}");
        }
        if (testCommand)
        {
            rd.AppendLine();
            rd.AppendLine("Test im Spiel (F8-Konsole oder Chat):  /waffe WEAPON_NAME");
        }
        File.WriteAllText(Path.Combine(res, "LIESMICH.txt"), rd.ToString(), new UTF8Encoding(true));
        return new { folder = res, files = n, weapons = weaponList.Count, warnings, model = Analyze(sessionDir) };
    }

    static string Lua(string s) => (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");

    const string TestLua = """
-- Test-Befehl (erstellt mit dem GTA Tool)
--   /waffe WEAPON_NAME   gibt dir die Waffe mit 250 Schuss

RegisterCommand('waffe', function(_, args)
    local name = args[1]
    if not name then
        print('Benutzung: /waffe WEAPON_NAME')
        return
    end
    local hash = joaat(name:upper())
    if not IsWeaponValid(hash) then
        print(('Waffe „%s“ wurde nicht gefunden.'):format(name))
        return
    end
    GiveWeaponToPed(PlayerPedId(), hash, 250, false, true)
end, false)
""";
}
