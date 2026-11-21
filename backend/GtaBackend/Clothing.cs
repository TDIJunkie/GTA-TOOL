using System.Text;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Kleidung der Freemode-Peds (mp_m_freemode_01 / mp_f_freemode_01).
/// Namen im Sitzungsordner (stream\):
///   Addon:   mp_m_freemode_01_&lt;sammlung&gt;^jbib_000_u.ydd  +  …^jbib_diff_000_a_uni.ytd
///   Replace: mp_m_freemode_01^jbib_015_u.ydd           +  mp_m_freemode_01^jbib_diff_015_a_uni.ytd
///   Requisiten: …^p_head_000.ydd + …^p_head_diff_000_a.ytd
/// </summary>
public static class Clothing
{
    public static readonly string[] Comps = { "head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib" };
    public static readonly string[] Props = { "p_head", "p_eyes", "p_ears", "p_mouth", "p_lhand", "p_rhand", "p_lwrist", "p_rwrist", "p_lhip", "p_lfoot", "p_rfoot", "ph_lhand", "ph_rhand" };
    static readonly string[] Anchors = { "ANCHOR_HEAD", "ANCHOR_EYES", "ANCHOR_EARS", "ANCHOR_MOUTH", "ANCHOR_LEFT_HAND", "ANCHOR_RIGHT_HAND", "ANCHOR_LEFT_WRIST", "ANCHOR_RIGHT_WRIST", "ANCHOR_HIP", "ANCHOR_LEFT_FOOT", "ANCHOR_RIGHT_FOOT", "ANCHOR_PH_L_HAND", "ANCHOR_PH_R_HAND" };
    static readonly string[] Races = { "uni", "whi", "bla", "chi", "lat", "ara", "bal", "jap", "kor", "ita", "pak" };

    public static readonly Dictionary<string, string> Labels = new()
    {
        ["head"] = "Kopf", ["berd"] = "Maske / Bart", ["hair"] = "Haare", ["uppr"] = "Arme / Oberkörper", ["lowr"] = "Hose",
        ["hand"] = "Tasche / Fallschirm", ["feet"] = "Schuhe", ["teef"] = "Accessoire / Kette", ["accs"] = "Unterhemd",
        ["task"] = "Weste / Schutz", ["decl"] = "Aufdruck / Logo", ["jbib"] = "Oberteil / Jacke",
        ["p_head"] = "Hut / Helm", ["p_eyes"] = "Brille", ["p_ears"] = "Ohrring", ["p_mouth"] = "Mund", ["p_lhand"] = "Linke Hand",
        ["p_rhand"] = "Rechte Hand", ["p_lwrist"] = "Uhr (links)", ["p_rwrist"] = "Armband (rechts)", ["p_lhip"] = "Hüfte",
        ["p_lfoot"] = "Linker Fuß", ["p_rfoot"] = "Rechter Fuß", ["ph_lhand"] = "Hand (links)", ["ph_rhand"] = "Hand (rechts)",
    };

    // ------------------------------------------------------------------ Namen

    public class Name
    {
        public string Ped;       // mp_m_freemode_01 … oder null
        public string Coll;      // Sammlung oder null (Replace)
        public string Comp;
        public bool Prop;
        public int Index;
        public string Suffix;    // u/r/m (Komponenten)
        public bool IsTexture;
        public char Letter;
        public string Race;      // uni/whi … (Komponenten)
    }

    static readonly string CompRx = string.Join("|", Comps);
    static readonly string PropRx = string.Join("|", Props);
    static readonly Regex RxPrefix = new(@"^(?:(mp_[mf]_freemode_01)(?:_([^\^]+))?\^)?(.+)$", RegexOptions.Compiled);
    static readonly Regex RxModel = new($@"^({CompRx})_(\d{{3}})_([urm])\.ydd$", RegexOptions.Compiled);
    static readonly Regex RxTex = new($@"^({CompRx})_diff_(\d{{3}})_([a-z])_([a-z]+)\.ytd$", RegexOptions.Compiled);
    static readonly Regex RxPropModel = new($@"^({PropRx})_(\d{{3}})\.ydd$", RegexOptions.Compiled);
    static readonly Regex RxPropTex = new($@"^({PropRx})_diff_(\d{{3}})_([a-z])\.ytd$", RegexOptions.Compiled);

    public static Name ParseName(string file)
    {
        var pm = RxPrefix.Match(file.ToLowerInvariant());
        if (!pm.Success) return null;
        var rest = pm.Groups[3].Value;
        var n = new Name { Ped = pm.Groups[1].Success && pm.Groups[1].Value.Length > 0 ? pm.Groups[1].Value : null, Coll = pm.Groups[2].Success && pm.Groups[2].Value.Length > 0 ? pm.Groups[2].Value : null };
        Match m;
        if ((m = RxModel.Match(rest)).Success) { n.Comp = m.Groups[1].Value; n.Index = int.Parse(m.Groups[2].Value); n.Suffix = m.Groups[3].Value; }
        else if ((m = RxTex.Match(rest)).Success) { n.Comp = m.Groups[1].Value; n.Index = int.Parse(m.Groups[2].Value); n.IsTexture = true; n.Letter = m.Groups[3].Value[0]; n.Race = m.Groups[4].Value; }
        else if ((m = RxPropModel.Match(rest)).Success) { n.Comp = m.Groups[1].Value; n.Prop = true; n.Index = int.Parse(m.Groups[2].Value); }
        else if ((m = RxPropTex.Match(rest)).Success) { n.Comp = m.Groups[1].Value; n.Prop = true; n.Index = int.Parse(m.Groups[2].Value); n.IsTexture = true; n.Letter = m.Groups[3].Value[0]; }
        else return null;
        return n;
    }

    static string Prefix(string ped, string coll) => coll == null ? $"{ped}^" : $"{ped}_{coll}^";

    static string ModelName(string ped, string coll, string comp, int idx, string suffix, bool prop) =>
        prop ? $"{Prefix(ped, coll)}{comp}_{idx:000}.ydd" : $"{Prefix(ped, coll)}{comp}_{idx:000}_{suffix}.ydd";

    static string TexName(string ped, string coll, string comp, int idx, char letter, string race, bool prop) =>
        prop ? $"{Prefix(ped, coll)}{comp}_diff_{idx:000}_{letter}.ytd" : $"{Prefix(ped, coll)}{comp}_diff_{idx:000}_{letter}_{race}.ytd";

    /// <summary>Beim Öffnen: Kleidung ohne Präfix bekommt Ped/Sammlung aus dem Ordnernamen (z. B. DLC-Ordner).</summary>
    public static string CanonicalName(string name, string origin)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains('^')) return lower;
        var n = ParseName(lower);
        if (n == null) return lower;
        var o = (origin ?? "").Replace('\\', '/').ToLowerInvariant();
        var parts = o.Split('/');
        string ped = null, coll = null;
        for (int i = parts.Length - 2; i >= 0 && ped == null; i--)
        {
            var fm = Regex.Match(parts[i], @"^(mp_[mf]_freemode_01)(?:_(.+))?$");
            if (fm.Success) { ped = fm.Groups[1].Value; coll = fm.Groups[2].Success ? fm.Groups[2].Value : null; }
        }
        if (ped == null)
        {
            if (Regex.IsMatch(o, @"(^|/)(female|weiblich|frau|women|mp_f)")) ped = "mp_f_freemode_01";
            else if (Regex.IsMatch(o, @"(^|/)(male|männlich|mann|men|mp_m)")) ped = "mp_m_freemode_01";
        }
        return ped == null ? lower : Prefix(ped, coll) + lower;
    }

    // ------------------------------------------------------------------ Analyse

    public class TexInfo { public char Letter { get; set; } public string Race { get; set; } public string File { get; set; } }

    public class Item
    {
        public string Id { get; set; }
        public string Ped { get; set; }
        public string Gender { get; set; }        // m/f/?
        public string Coll { get; set; }          // null = Replace
        public string Comp { get; set; }
        public string Label { get; set; }
        public bool Prop { get; set; }
        public int Index { get; set; }
        public string Suffix { get; set; }
        public string File { get; set; }
        public bool Encrypted { get; set; }
        public long Size { get; set; }
        public List<TexInfo> Textures { get; set; } = new();
    }

    static string Stream(string sessionDir) => Path.Combine(sessionDir, "stream");

    public static List<Item> Items(string sessionDir)
    {
        var dir = Stream(sessionDir);
        var map = new Dictionary<string, Item>();
        var texs = new List<(string key, Name n, string file)>();
        if (!Directory.Exists(dir)) return new List<Item>();
        foreach (var f in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(f);
            var n = ParseName(name);
            if (n == null) continue;
            var key = $"{n.Ped ?? "?"}|{n.Coll ?? ""}|{n.Comp}|{n.Index}";
            if (n.IsTexture) { texs.Add((key, n, "stream/" + name)); continue; }
            var rel = "stream/" + name;
            map[key] = new Item
            {
                Id = key, Ped = n.Ped, Gender = n.Ped == null ? "?" : (n.Ped.StartsWith("mp_f") ? "f" : "m"), Coll = n.Coll,
                Comp = n.Comp, Label = Labels.GetValueOrDefault(n.Comp, n.Comp), Prop = n.Prop, Index = n.Index, Suffix = n.Suffix ?? "",
                File = rel, Encrypted = Session.IsEncrypted(sessionDir, rel), Size = new FileInfo(f).Length,
            };
        }
        foreach (var (key, n, file) in texs)
            if (map.TryGetValue(key, out var it)) it.Textures.Add(new TexInfo { Letter = n.Letter, Race = n.Race, File = file });
        foreach (var it in map.Values) it.Textures = it.Textures.OrderBy(t => t.Letter).ToList();
        return map.Values.OrderBy(x => x.Ped).ThenBy(x => x.Coll).ThenBy(x => x.Prop).ThenBy(x => Array.IndexOf(x.Prop ? Props : Comps, x.Comp)).ThenBy(x => x.Index).ToList();
    }

    public static object Analyze(string sessionDir)
    {
        var items = Items(sessionDir);
        var warnings = new List<string>();
        foreach (var it in items)
        {
            if (it.Ped == null) warnings.Add($"{Path.GetFileName(it.File)}: Geschlecht unbekannt – bitte über „Geschlecht festlegen“ zuordnen.");
            if (it.Textures.Count == 0) warnings.Add($"{Path.GetFileName(it.File)}: keine Texturdatei gefunden.");
            if (it.Encrypted) warnings.Add($"{Path.GetFileName(it.File)} ist verschlüsselt (Escrow) und kann nicht bearbeitet werden.");
        }
        var collections = items.Where(i => i.Coll != null).GroupBy(i => (i.Ped, i.Coll))
            .Select(g => new { ped = g.Key.Ped, name = g.Key.Coll, items = g.Count() }).ToList();
        return new
        {
            sessionDir,
            items,
            collections,
            replaceCount = items.Count(i => i.Coll == null),
            addonCount = items.Count(i => i.Coll != null),
            warnings,
        };
    }

    static Item Find(string sessionDir, string id) =>
        Items(sessionDir).FirstOrDefault(i => i.Id == id) ?? throw new UserError("Kleidungsstück nicht gefunden.");

    // ------------------------------------------------------------------ Umbenennen (gemeinsam)

    static void Move(string sessionDir, string fromRel, string toName, string backupDir)
    {
        var src = Path.Combine(sessionDir, fromRel);
        var dst = Path.Combine(Stream(sessionDir), toName);
        if (src.Equals(dst, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(dst)) throw new UserError($"{toName} existiert bereits.");
        File.Move(src, dst);
    }

    static int NextIndex(string sessionDir, string ped, string coll, string comp)
    {
        var used = Items(sessionDir).Where(i => i.Ped == ped && i.Coll == coll && i.Comp == comp).Select(i => i.Index).ToHashSet();
        int n = 0;
        while (used.Contains(n)) n++;
        return n;
    }

    /// <summary>Kleidungsstück unter neuem Ped/Sammlung/Nummer ablegen (Modell + alle Texturen).</summary>
    static string Relocate(string sessionDir, Item it, string ped, string coll, int idx, string backupDir)
    {
        var model = ModelName(ped, coll, it.Comp, idx, it.Suffix, it.Prop);
        var moves = new List<(string from, string to)> { (it.File, model) };
        foreach (var t in it.Textures) moves.Add((t.File, TexName(ped, coll, it.Comp, idx, t.Letter, t.Race ?? "uni", it.Prop)));
        foreach (var (_, to) in moves)
            if (File.Exists(Path.Combine(Stream(sessionDir), to)) && !moves.Any(m => m.from.EndsWith("/" + to)))
                throw new UserError($"{to} existiert bereits. Bitte eine andere Nummer wählen.");
        // erst auf Zwischennamen, dann auf Zielnamen (falls sich Namen überkreuzen)
        var tmp = moves.Select((m, i) => (m.from, tmp: $"__tmp_{Guid.NewGuid():N}_{i}", m.to)).ToList();
        foreach (var t in tmp) File.Move(Path.Combine(sessionDir, t.from), Path.Combine(Stream(sessionDir), t.tmp));
        foreach (var t in tmp) File.Move(Path.Combine(Stream(sessionDir), t.tmp), Path.Combine(Stream(sessionDir), t.to));
        return $"{(ped ?? "?")}|{coll ?? ""}|{it.Comp}|{idx}";
    }

    // ------------------------------------------------------------------ Addon ⇄ Replace

    public static string CollectionName(string gender, string name)
    {
        var n = new string((name ?? "").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray()).Trim('_');
        if (n.Length == 0) throw new UserError("Bitte einen Namen für die Kleidungs-Sammlung eingeben.");
        if (n.StartsWith("mp_m_") || n.StartsWith("mp_f_")) n = n[5..];
        return (gender == "f" ? "mp_f_" : "mp_m_") + n;
    }

    public static object ToAddon(string sessionDir, List<string> ids, string collection, string backupDir)
    {
        var moved = new List<string>();
        foreach (var id in ids)
        {
            var it = Find(sessionDir, id);
            if (it.Ped == null) throw new UserError($"{Path.GetFileName(it.File)}: bitte zuerst das Geschlecht festlegen.");
            var coll = CollectionName(it.Gender, collection);
            var idx = NextIndex(sessionDir, it.Ped, coll, it.Comp);
            moved.Add(Relocate(sessionDir, it, it.Ped, coll, idx, backupDir));
        }
        return new { moved, model = Analyze(sessionDir) };
    }

    public static object ToReplace(string sessionDir, string id, int targetIndex, string backupDir)
    {
        var it = Find(sessionDir, id);
        if (it.Ped == null) throw new UserError("Bitte zuerst das Geschlecht festlegen.");
        if (targetIndex < 0 || targetIndex > 999) throw new UserError("Bitte eine Nummer zwischen 0 und 999 angeben.");
        var newId = Relocate(sessionDir, it, it.Ped, null, targetIndex, backupDir);
        return new { moved = new[] { newId }, model = Analyze(sessionDir) };
    }

    public static object SetGender(string sessionDir, string id, string gender, string backupDir)
    {
        var it = Find(sessionDir, id);
        var ped = gender == "f" ? "mp_f_freemode_01" : "mp_m_freemode_01";
        var coll = it.Coll == null ? null : CollectionName(gender, it.Coll);
        var newId = Relocate(sessionDir, it, ped, coll, it.Index, backupDir);
        return new { id = newId, model = Analyze(sessionDir) };
    }

    public static object Delete(string sessionDir, string id, string backupDir)
    {
        var it = Find(sessionDir, id);
        foreach (var f in it.Textures.Select(t => t.File).Append(it.File))
        {
            var p = Path.Combine(sessionDir, f);
            Session.Backup(p, backupDir);
            File.Delete(p);
        }
        return new { model = Analyze(sessionDir) };
    }

    // ------------------------------------------------------------------ Männlich ⇄ weiblich

    /// <summary>Arbeitskopie für Blender/Umwandlung: Modell unter dem kurzen Namen (z. B. jbib_000_u.ydd).</summary>
    public static object PrepareWork(string sessionDir, string id, string workDir, string ytd)
    {
        var it = Find(sessionDir, id);
        if (it.Encrypted) throw new UserError("Das Modell ist verschlüsselt (Escrow) und kann nicht bearbeitet werden.");
        var stem = Stem(it);
        Textures.TryDelete(workDir);
        Directory.CreateDirectory(workDir);
        var file = Path.Combine(workDir, stem + ".ydd");
        File.Copy(Path.Combine(sessionDir, it.File), file, true);
        // Texturen als DDS daneben (Sollumz sucht <modellordner>/<modellname>/)
        var texDir = Path.Combine(workDir, stem);
        Directory.CreateDirectory(texDir);
        var src = ytd ?? it.Textures.FirstOrDefault()?.File;
        int count = 0;
        if (src != null)
        {
            try
            {
                foreach (var t in Textures.TexList(Textures.Open(Path.Combine(sessionDir, src))))
                {
                    File.WriteAllBytes(Path.Combine(texDir, Textures.Safe(t.Name) + ".dds"), DDSIO.GetDDSFile(t));
                    count++;
                }
            }
            catch (Exception ex) { Program.Log($"Kleidung DDS: {ex.Message}"); }
        }
        return new { file, stem, ped = it.Ped, gender = it.Gender, prop = it.Prop, comp = it.Comp, textures = count };
    }

    static string Stem(Item it) => it.Prop ? $"{it.Comp}_{it.Index:000}" : $"{it.Comp}_{it.Index:000}_{it.Suffix}";

    /// <summary>Ergebnis aus Blender (gleiches Kleidungsstück) zurück übernehmen.</summary>
    public static object ApplyWork(string sessionDir, string id, string exportDir, string backupDir)
    {
        var it = Find(sessionDir, id);
        var stem = Stem(it);
        var f = Directory.Exists(exportDir) ? Directory.GetFiles(exportDir, "*.ydd", SearchOption.AllDirectories)
            .FirstOrDefault(x => Path.GetFileNameWithoutExtension(x).Equals(stem, StringComparison.OrdinalIgnoreCase)) : null;
        if (f == null) throw new UserError($"Blender hat keine {stem}.ydd exportiert.");
        var head = new byte[4];
        using (var fs = File.OpenRead(f)) fs.ReadExactly(head);
        if (head[0] != 'R' || head[1] != 'S' || head[2] != 'C' || head[3] != '7') throw new UserError("Die Datei aus Blender ist ungültig.");
        var target = Path.Combine(sessionDir, it.File);
        Session.Backup(target, backupDir);
        File.Copy(f, target, true);
        try { File.Delete(f); } catch { }
        return new { applied = Path.GetFileName(target) };
    }

    /// <summary>Angepasstes Modell als Kleidungsstück für das andere Geschlecht anlegen (Texturen werden kopiert).</summary>
    public static object GenderApply(string sessionDir, string id, string convertedYdd)
    {
        var it = Find(sessionDir, id);
        if (it.Ped == null) throw new UserError("Bitte zuerst das Geschlecht festlegen.");
        var otherGender = it.Gender == "f" ? "m" : "f";
        var ped = otherGender == "f" ? "mp_f_freemode_01" : "mp_m_freemode_01";
        string coll = it.Coll == null ? null : CollectionName(otherGender, it.Coll);
        int idx = it.Coll == null ? it.Index : NextIndex(sessionDir, ped, coll, it.Comp);
        var modelName = ModelName(ped, coll, it.Comp, idx, it.Suffix, it.Prop);
        if (File.Exists(Path.Combine(Stream(sessionDir), modelName)))
        {
            if (it.Coll != null) throw new UserError($"{modelName} existiert bereits.");
            idx = NextIndex(sessionDir, ped, null, it.Comp);
            modelName = ModelName(ped, null, it.Comp, idx, it.Suffix, it.Prop);
        }
        File.Copy(convertedYdd, Path.Combine(Stream(sessionDir), modelName));
        foreach (var t in it.Textures)
            File.Copy(Path.Combine(sessionDir, t.File), Path.Combine(Stream(sessionDir), TexName(ped, coll, it.Comp, idx, t.Letter, t.Race ?? "uni", it.Prop)), true);
        return new { id = $"{ped}|{coll ?? ""}|{it.Comp}|{idx}", model = Analyze(sessionDir) };
    }

    // ------------------------------------------------------------------ Texturvarianten

    public static object AddVariant(string sessionDir, string id, List<string> images, string backupDir)
    {
        var it = Find(sessionDir, id);
        if (it.Ped == null) throw new UserError("Bitte zuerst das Geschlecht festlegen.");
        var added = new List<string>();
        var race = it.Textures.FirstOrDefault()?.Race ?? (it.Suffix == "r" ? "whi" : "uni");
        TextureFormat fmt = TextureFormat.D3DFMT_DXT5;
        string templateYtd = it.Textures.FirstOrDefault()?.File;
        if (templateYtd != null)
        {
            try { fmt = Textures.TexList(Textures.Open(Path.Combine(sessionDir, templateYtd))).First().Format; } catch { }
        }
        char letter = it.Textures.Count == 0 ? 'a' : (char)(it.Textures.Max(t => t.Letter) + 1);
        foreach (var img in images)
        {
            if (letter > 'z') throw new UserError("Mehr als 26 Texturvarianten sind nicht möglich.");
            var file = TexName(it.Ped, it.Coll, it.Comp, it.Index, letter, race, it.Prop);
            var texName = Path.GetFileNameWithoutExtension(file);
            texName = texName.Contains('^') ? texName[(texName.IndexOf('^') + 1)..] : texName;
            var tex = Textures.BuildTexture(img, texName, fmt, keepFormatFromDds: true);
            var ytd = new YtdFile { TextureDict = new TextureDictionary() };
            ytd.TextureDict.BuildFromTextureList(new List<Texture> { tex });
            File.WriteAllBytes(Path.Combine(Stream(sessionDir), file), ytd.Save());
            added.Add(file);
            letter++;
        }
        var warn = it.Coll == null
            ? "Hinweis: Bei Replace-Kleidung kennt GTA nur so viele Texturen wie das Original hat. Für zusätzliche Varianten das Teil in ein Addon umwandeln."
            : null;
        return new { added, warning = warn, model = Analyze(sessionDir) };
    }

    public static object RemoveVariant(string sessionDir, string id, string file, string backupDir)
    {
        var it = Find(sessionDir, id);
        if (it.Textures.Count <= 1) throw new UserError("Ein Kleidungsstück braucht mindestens eine Textur.");
        var t = it.Textures.FirstOrDefault(x => x.File == file) ?? throw new UserError("Variante nicht gefunden.");
        var p = Path.Combine(sessionDir, t.File);
        Session.Backup(p, backupDir);
        File.Delete(p);
        // Buchstaben lückenlos halten
        var rest = Find(sessionDir, id).Textures;
        char letter = 'a';
        foreach (var r in rest)
        {
            var target = TexName(it.Ped, it.Coll, it.Comp, it.Index, letter, r.Race ?? "uni", it.Prop);
            if (!r.File.EndsWith("/" + target)) File.Move(Path.Combine(sessionDir, r.File), Path.Combine(Stream(sessionDir), target));
            letter++;
        }
        return new { model = Analyze(sessionDir) };
    }

    // ------------------------------------------------------------------ Position / Größe

    static YddFile LoadYdd(string file)
    {
        var data = File.ReadAllBytes(file);
        if (data.Length < 4 || data[0] != 'R') throw new UserError("Das Modell ist verschlüsselt (Escrow).");
        var ydd = new YddFile();
        ydd.Load(data);
        return ydd;
    }

    public static IEnumerable<DrawableGeometry> Geometries(YddFile ydd)
    {
        foreach (var d in ydd.Drawables ?? Array.Empty<Drawable>())
        {
            if (d == null) continue;
            foreach (var m in d.AllModels ?? Array.Empty<DrawableModel>())
                foreach (var g in m?.Geometries ?? Array.Empty<DrawableGeometry>())
                    if (g?.VertexData?.VertexBytes != null) yield return g;
        }
    }

    /// <summary>Verschieben (Meter, GTA-Achsen: X rechts, Y vorne, Z oben) und skalieren um die Modellmitte.</summary>
    public static object Transform(string sessionDir, string id, float dx, float dy, float dz, float scale, string backupDir)
    {
        var it = Find(sessionDir, id);
        var file = Path.Combine(sessionDir, it.File);
        var ydd = LoadYdd(file);
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        var geoms = Geometries(ydd).ToList();
        foreach (var g in geoms)
        {
            var vd = g.VertexData;
            for (int v = 0; v < vd.VertexCount; v++) { var p = vd.GetVector3(v, 0); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }
        var center = (min + max) / 2;
        if (scale <= 0) scale = 1;
        int changed = 0;
        foreach (var g in geoms)
        {
            var vd = g.VertexData;
            if (vd.Info.GetComponentType(0) != VertexComponentType.Float3) continue;
            int off = vd.Info.GetComponentOffset(0), stride = vd.Info.Stride;
            for (int v = 0; v < vd.VertexCount; v++)
            {
                int o = v * stride + off;
                var p = new Vector3(BitConverter.ToSingle(vd.VertexBytes, o), BitConverter.ToSingle(vd.VertexBytes, o + 4), BitConverter.ToSingle(vd.VertexBytes, o + 8));
                var n = center + (p - center) * scale + new Vector3(dx, dy, dz);
                BitConverter.TryWriteBytes(new Span<byte>(vd.VertexBytes, o, 4), n.X);
                BitConverter.TryWriteBytes(new Span<byte>(vd.VertexBytes, o + 4, 4), n.Y);
                BitConverter.TryWriteBytes(new Span<byte>(vd.VertexBytes, o + 8, 4), n.Z);
                changed++;
            }
            // Bounding-Box der Geometrie mitziehen
            var a = g.AABB;
            g.AABB = new AABB_s
            {
                Min = new Vector4(center + (new Vector3(a.Min.X, a.Min.Y, a.Min.Z) - center) * scale + new Vector3(dx, dy, dz), a.Min.W),
                Max = new Vector4(center + (new Vector3(a.Max.X, a.Max.Y, a.Max.Z) - center) * scale + new Vector3(dx, dy, dz), a.Max.W),
            };
        }
        var bytes = ydd.Save();
        Session.Backup(file, backupDir);
        File.WriteAllBytes(file, bytes);
        return new { vertices = changed };
    }

    // ------------------------------------------------------------------ UV-Vorlage

    public static object UvTemplate(string yddFile, int size, string outFile, string bgYtd)
    {
        var ydd = LoadYdd(yddFile);
        if (size < 256) size = 2048;
        var px = new byte[size * size * 4];
        if (!string.IsNullOrEmpty(bgYtd) && File.Exists(bgYtd))
        {
            try
            {
                var t = Textures.TexList(Textures.Open(bgYtd)).FirstOrDefault(x => !x.Name.EndsWith("_n") && !x.Name.EndsWith("_s"));
                var bg = t != null ? DDSIO.GetPixels(t, 0) : null;
                if (bg != null) Livery.BlitPublic(bg, t.Width, t.Height, px, size);
            }
            catch (Exception ex) { Program.Log($"UV-Hintergrund: {ex.Message}"); }
        }
        int tris = 0;
        foreach (var g in Geometries(ydd))
        {
            var vd = g.VertexData; var idx = g.IndexBuffer?.Indices;
            if (idx == null || (vd.Info.Flags & (1u << 6)) == 0) continue;
            var uv = new Vector2[vd.VertexCount];
            for (int v = 0; v < uv.Length; v++) uv[v] = Livery.ReadUvPublic(vd, v, 6);
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                if (idx[t] >= uv.Length || idx[t + 1] >= uv.Length || idx[t + 2] >= uv.Length) continue;
                Livery.FillTriPublic(px, size, uv[idx[t]], uv[idx[t + 1]], uv[idx[t + 2]]);
                tris++;
            }
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                if (idx[t] >= uv.Length || idx[t + 1] >= uv.Length || idx[t + 2] >= uv.Length) continue;
                Livery.LinePublic(px, size, uv[idx[t]], uv[idx[t + 1]]); Livery.LinePublic(px, size, uv[idx[t + 1]], uv[idx[t + 2]]); Livery.LinePublic(px, size, uv[idx[t + 2]], uv[idx[t]]);
            }
        }
        if (tris == 0) throw new UserError("Keine UV-Daten im Modell gefunden.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, Png.EncodeBgra(px, size, size));
        return new { file = outFile, triangles = tris };
    }

    // ------------------------------------------------------------------ Original-GTA-Kleidung übernehmen

    public static object AddFromGta(string sessionDir, string gtaPath, string cacheFile, string catalogId, string collection, bool keepTextures)
    {
        var tmp = Textures.TempDir();
        try
        {
            var (model, textures, vi) = Freemode.ExtractItem(gtaPath, cacheFile, catalogId, tmp);
            var gender = vi.Ped.StartsWith("mp_f") ? "f" : "m";
            var coll = CollectionName(gender, collection);
            var idx = NextIndex(sessionDir, vi.Ped, coll, vi.Comp);
            Directory.CreateDirectory(Stream(sessionDir));
            File.Copy(model, Path.Combine(Stream(sessionDir), ModelName(vi.Ped, coll, vi.Comp, idx, vi.Suffix ?? "u", vi.Prop)));
            var copied = 0;
            if (keepTextures)
            {
                foreach (var t in textures)
                {
                    var n = ParseName(Path.GetFileName(t));
                    if (n == null) continue;
                    File.Copy(t, Path.Combine(Stream(sessionDir), TexName(vi.Ped, coll, vi.Comp, idx, n.Letter, n.Race ?? "uni", vi.Prop)), true);
                    copied++;
                }
            }
            return new { id = $"{vi.Ped}|{coll}|{vi.Comp}|{idx}", textures = copied, model = Analyze(sessionDir) };
        }
        finally { Textures.TryDelete(tmp); }
    }

    // ------------------------------------------------------------------ YMT / Shop-Meta / FiveM-Export

    static int RaceId(string race) => Math.Max(0, Array.IndexOf(Races, race) - (race == "uni" ? 0 : 1));

    /// <summary>CPedVariationInfo-XML für eine Addon-Sammlung (Nummern müssen lückenlos sein).</summary>
    public static string BuildYmtXml(string coll, List<Item> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<CPedVariationInfo name=\"{coll}\">");
        sb.AppendLine(" <bHasTexVariations value=\"true\" />");
        sb.AppendLine(" <bHasDrawblVariations value=\"true\" />");
        sb.AppendLine(" <bHasLowLODs value=\"false\" />");
        sb.AppendLine(" <bIsSuperLOD value=\"false\" />");
        var comps = items.Where(i => !i.Prop).GroupBy(i => i.Comp).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Index).ToList());
        var avail = new List<string>();
        int slot = 0;
        foreach (var c in Comps) avail.Add(comps.ContainsKey(c) ? (slot++).ToString() : "255");
        sb.AppendLine($" <availComp>{string.Join(" ", avail)}</availComp>");
        sb.AppendLine(" <aComponentData3 itemType=\"CPVComponentData\">");
        foreach (var c in Comps.Where(comps.ContainsKey))
        {
            var list = comps[c];
            sb.AppendLine("  <Item>");
            sb.AppendLine($"   <numAvailTex value=\"{list.Sum(i => Math.Max(1, i.Textures.Count))}\" />");
            sb.AppendLine("   <aDrawblData3 itemType=\"CPVDrawblData\">");
            foreach (var it in list)
            {
                int mask = it.Suffix == "r" ? 17 : it.Suffix == "m" ? 33 : 1;
                sb.AppendLine("    <Item>");
                sb.AppendLine($"     <propMask value=\"{mask}\" />");
                sb.AppendLine("     <numAlternatives value=\"0\" />");
                sb.AppendLine("     <aTexData itemType=\"CPVTextureData\">");
                foreach (var t in it.Textures.DefaultIfEmpty(new TexInfo { Race = "uni" }))
                {
                    sb.AppendLine("      <Item>");
                    sb.AppendLine($"       <texId value=\"{(it.Suffix == "r" ? RaceId(t.Race) : 0)}\" />");
                    sb.AppendLine("       <distribution value=\"255\" />");
                    sb.AppendLine("      </Item>");
                }
                sb.AppendLine("     </aTexData>");
                sb.AppendLine("     <clothData>");
                sb.AppendLine("      <ownsCloth value=\"false\" />");
                sb.AppendLine("     </clothData>");
                sb.AppendLine("    </Item>");
            }
            sb.AppendLine("   </aDrawblData3>");
            sb.AppendLine("  </Item>");
        }
        sb.AppendLine(" </aComponentData3>");
        sb.AppendLine(" <aSelectionSets itemType=\"CPedSelectionSet\" />");
        sb.AppendLine(" <compInfos itemType=\"CComponentInfo\">");
        foreach (var c in Comps.Where(comps.ContainsKey))
            foreach (var it in comps[c])
            {
                sb.AppendLine("  <Item>");
                sb.AppendLine("   <pedXml_audioID>none</pedXml_audioID>");
                sb.AppendLine("   <pedXml_audioID2>none</pedXml_audioID2>");
                sb.AppendLine("   <pedXml_expressionMods>0 0 0 0 0</pedXml_expressionMods>");
                sb.AppendLine("   <flags value=\"0\" />");
                sb.AppendLine("   <inclusions>0</inclusions>");
                sb.AppendLine("   <exclusions>0</exclusions>");
                sb.AppendLine("   <pedXml_vfxComps>PV_COMP_HEAD</pedXml_vfxComps>");
                sb.AppendLine("   <pedXml_flags value=\"0\" />");
                sb.AppendLine($"   <pedXml_compIdx value=\"{Array.IndexOf(Comps, c)}\" />");
                sb.AppendLine($"   <pedXml_drawblIdx value=\"{it.Index}\" />");
                sb.AppendLine("  </Item>");
            }
        sb.AppendLine(" </compInfos>");
        var props = items.Where(i => i.Prop).OrderBy(i => Array.IndexOf(Props, i.Comp)).ThenBy(i => i.Index).ToList();
        sb.AppendLine(" <propInfo>");
        sb.AppendLine($"  <numAvailProps value=\"{props.Count}\" />");
        if (props.Count == 0) sb.AppendLine("  <aPropMetaData itemType=\"CPedPropMetaData\" />");
        else
        {
            sb.AppendLine("  <aPropMetaData itemType=\"CPedPropMetaData\">");
            foreach (var p in props)
            {
                sb.AppendLine("   <Item>");
                sb.AppendLine("    <audioId>none</audioId>");
                sb.AppendLine("    <expressionMods>0 0 0 0 0</expressionMods>");
                sb.AppendLine("    <texData itemType=\"CPedPropTexData\">");
                foreach (var _ in p.Textures.DefaultIfEmpty(new TexInfo()))
                {
                    sb.AppendLine("     <Item>");
                    sb.AppendLine("      <inclusions>0</inclusions>");
                    sb.AppendLine("      <exclusions>0</exclusions>");
                    sb.AppendLine("      <texId value=\"0\" />");
                    sb.AppendLine("      <inclusionId value=\"0\" />");
                    sb.AppendLine("      <exclusionId value=\"0\" />");
                    sb.AppendLine("      <distribution value=\"255\" />");
                    sb.AppendLine("     </Item>");
                }
                sb.AppendLine("    </texData>");
                sb.AppendLine("    <renderFlags />");
                sb.AppendLine("    <propFlags value=\"0\" />");
                sb.AppendLine("    <flags value=\"0\" />");
                sb.AppendLine($"    <anchorId value=\"{Array.IndexOf(Props, p.Comp)}\" />");
                sb.AppendLine($"    <propId value=\"{p.Index}\" />");
                sb.AppendLine("    <stickyness value=\"0\" />");
                sb.AppendLine("   </Item>");
            }
            sb.AppendLine("  </aPropMetaData>");
        }
        var anchors = props.GroupBy(p => p.Comp).ToList();
        if (anchors.Count == 0) sb.AppendLine("  <aAnchors itemType=\"CAnchorProps\" />");
        else
        {
            sb.AppendLine("  <aAnchors itemType=\"CAnchorProps\">");
            foreach (var a in anchors)
            {
                sb.AppendLine("   <Item>");
                sb.AppendLine($"    <props>{string.Join(" ", a.OrderBy(p => p.Index).Select(p => Math.Max(1, p.Textures.Count)))}</props>");
                sb.AppendLine($"    <anchor>{Anchors[Array.IndexOf(Props, a.Key)]}</anchor>");
                sb.AppendLine("   </Item>");
            }
            sb.AppendLine("  </aAnchors>");
        }
        sb.AppendLine(" </propInfo>");
        sb.AppendLine($" <dlcName>{coll}</dlcName>");
        sb.AppendLine("</CPedVariationInfo>");
        return sb.ToString();
    }

    static string ShopMeta(string ped, string coll) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ShopPedApparel>\n" +
        $"  <pedName>{ped}</pedName>\n  <dlcName>{coll}</dlcName>\n  <fullDlcName>{ped}_{coll}</fullDlcName>\n" +
        "  <eCharacter>SCR_CHAR_MULTIPLAYER</eCharacter>\n  <pedOutfits />\n  <pedComponents />\n  <pedProps />\n</ShopPedApparel>\n";

    /// <summary>Nummern je Sammlung und Kategorie lückenlos machen (000, 001 …), wie GTA es verlangt.</summary>
    static List<string> Compact(string sessionDir)
    {
        var changes = new List<string>();
        foreach (var g in Items(sessionDir).Where(i => i.Coll != null).GroupBy(i => (i.Ped, i.Coll, i.Comp)))
        {
            int n = 0;
            foreach (var it in g.OrderBy(i => i.Index).ToList())
            {
                if (it.Index != n)
                {
                    var fresh = Items(sessionDir).First(x => x.Id == it.Id);
                    Relocate(sessionDir, fresh, it.Ped, it.Coll, n, null);
                    changes.Add($"{it.Comp} {it.Index:000} → {n:000}");
                }
                n++;
            }
        }
        return changes;
    }

    public static object Export(string sessionDir, string outDir, string resourceName)
    {
        resourceName = ExportMerge.ResName(resourceName);
        var res = Path.Combine(outDir, resourceName);
        if (Directory.Exists(res) && Directory.EnumerateFileSystemEntries(res).Any())
        {
            if (!File.Exists(Path.Combine(res, "fxmanifest.lua")))
                throw new UserError($"Der Ordner {res} existiert schon und ist keine FiveM-Resource.");
            Directory.Delete(res, true);
        }
        var renumbered = Compact(sessionDir);
        var items = Items(sessionDir);
        if (items.Count == 0) throw new UserError("Keine Kleidung zum Exportieren.");
        var warnings = new List<string>();
        foreach (var it in items.Where(i => i.Ped == null))
            warnings.Add($"{Path.GetFileName(it.File)}: Geschlecht unbekannt – Datei wurde unverändert übernommen.");
        if (renumbered.Count > 0) warnings.Add("Nummern wurden lückenlos gemacht: " + string.Join(", ", renumbered.Take(8)));

        var stream = Path.Combine(res, "stream");
        Directory.CreateDirectory(stream);
        foreach (var it in items)
            foreach (var f in it.Textures.Select(t => t.File).Append(it.File))
                File.Copy(Path.Combine(sessionDir, f), Path.Combine(stream, Path.GetFileName(f)), true);

        var metas = new List<string>();
        foreach (var g in items.Where(i => i.Coll != null && i.Ped != null).GroupBy(i => (i.Ped, i.Coll)))
        {
            var (ped, coll) = g.Key;
            var xml = BuildYmtXml(coll, g.ToList());
            File.WriteAllBytes(Path.Combine(stream, $"{ped}_{coll}.ymt"), Ymt.FromXml(xml));
            var meta = $"{ped}_{coll}.meta";
            File.WriteAllText(Path.Combine(res, meta), ShopMeta(ped, coll), new UTF8Encoding(false));
            metas.Add(meta);
        }

        var fx = new StringBuilder();
        fx.AppendLine("fx_version 'cerulean'");
        fx.AppendLine("game 'gta5'");
        fx.AppendLine();
        fx.AppendLine("author 'GTA Tool'");
        fx.AppendLine($"description '{resourceName} – Kleidung, erstellt mit dem GTA Tool'");
        fx.AppendLine("version '1.0.0'");
        if (metas.Count > 0)
        {
            fx.AppendLine();
            fx.AppendLine("files {");
            foreach (var m in metas) fx.AppendLine($"    '{m}',");
            fx.AppendLine("}");
            fx.AppendLine();
            foreach (var m in metas) fx.AppendLine($"data_file 'SHOP_PED_APPAREL_META_FILE' '{m}'");
        }
        File.WriteAllText(Path.Combine(res, "fxmanifest.lua"), fx.ToString(), new UTF8Encoding(false));

        var readme = new StringBuilder();
        readme.AppendLine($"Resource: {resourceName}");
        readme.AppendLine();
        readme.AppendLine("So startest du die Resource:");
        readme.AppendLine($"  1. Den Ordner \"{resourceName}\" in den resources-Ordner deines Servers kopieren.");
        readme.AppendLine($"  2. In der server.cfg eintragen:  ensure {resourceName}");
        readme.AppendLine("  3. Server neu starten.");
        readme.AppendLine();
        readme.AppendLine("Hinweis: Addon-Kleidung wird hinter der vorhandenen Kleidung einsortiert. Die Nummer im Kleidungsmenü");
        readme.AppendLine("hängt davon ab, welche anderen Kleidungs-Resources vorher geladen werden.");
        if (warnings.Count > 0)
        {
            readme.AppendLine();
            readme.AppendLine("Hinweise:");
            foreach (var w in warnings) readme.AppendLine("  - " + w);
        }
        File.WriteAllText(Path.Combine(res, "LIESMICH.txt"), readme.ToString(), new UTF8Encoding(true));
        return new { folder = res, items = items.Count, collections = metas.Count, warnings, model = Analyze(sessionDir) };
    }
}
