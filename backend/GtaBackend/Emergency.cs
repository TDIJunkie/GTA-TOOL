using System.Text.RegularExpressions;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Einsatzfahrzeuge: Blaulicht (Sirenen) und Extras eines Fahrzeugs.
///  - Blaulicht-Punkte sind Bones „siren1“ … „siren20“ im Modell (dazu optional „siren_glass1“ … als leuchtendes Glas).
///  - Die Lichter selbst (Farbe, Blinkmuster) stehen in carcols.meta unter &lt;Sirens&gt;, verknüpft über
///    &lt;sirenSettings value="ID"/&gt; in carvariations.meta.
///  - Einsatzfahrzeug-Verhalten (Sirene, Polizei-KI) über vehicles.meta: vehicleClass VC_EMERGENCY + Flags.
///  - Extras sind Bones „extra_1“ … „extra_14“ mit eigenen Teilen (Fragment-Kinder) – im Spiel an-/abschaltbar.
/// </summary>
public static class Emergency
{
    static readonly Regex SirenRx = new(@"^siren(\d+)$", RegexOptions.IgnoreCase);
    static readonly Regex GlassRx = new(@"^siren_glass(\d+)$", RegexOptions.IgnoreCase);
    static readonly Regex ExtraRx = new(@"^extra_(\d+|ten)$", RegexOptions.IgnoreCase);   // Extra 10 heißt bei GTA „extra_ten“
    static int ExtraNo(string name) { var g = ExtraRx.Match(name).Groups[1].Value; return g.Equals("ten", StringComparison.OrdinalIgnoreCase) ? 10 : int.Parse(g); }

    public class BoneInfo { public int Index; public string Name; public ushort Tag; public int Parent; public Vector3 Pos; }

    public static List<BoneInfo> Bones(YftFile yft)
    {
        var sk = yft.Fragment?.Drawable?.Skeleton;
        var bones = sk?.Bones?.Items ?? Array.Empty<Bone>();
        var abs = new Matrix[bones.Length];
        var list = new List<BoneInfo>();
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            var q = b.Rotation; q.Normalize();
            var m = Matrix.Scaling(b.Scale) * Matrix.RotationQuaternion(q) * Matrix.Translation(b.Translation);
            abs[i] = b.ParentIndex >= 0 && b.ParentIndex < i ? m * abs[b.ParentIndex] : m;
            list.Add(new BoneInfo { Index = i, Name = b.Name, Tag = b.Tag, Parent = b.ParentIndex, Pos = abs[i].TranslationVector });
        }
        return list;
    }

    static YftFile LoadYft(string file)
    {
        var y = new YftFile();
        y.Load(File.ReadAllBytes(file));
        return y;
    }

    /// <summary>Was hat das Modell: Blaulicht-Punkte, leuchtende Gläser, Extras (mit/ohne eigenes Teil).</summary>
    public static object ModelInfo(string yftFile)
    {
        var yft = LoadYft(yftFile);
        var bones = Bones(yft);
        var children = yft.Fragment?.PhysicsLODGroup?.PhysicsLOD1?.Children?.data_items ?? Array.Empty<FragPhysTypeChild>();
        int ModelsAt(ushort tag) => children.Where(c => c.BoneTag == tag).Sum(c => c.Drawable1?.AllModels?.Length ?? 0);
        int MainModelsAt(int boneIndex) => (yft.Fragment?.Drawable?.DrawableModels?.High ?? Array.Empty<DrawableModel>())
            .Count(m => (int)((m.SkeletonBinding >> 24) & 0xFF) == boneIndex && ((m.SkeletonBinding >> 8) & 0xFF) == 0);
        float[] P(Vector3 v) => new[] { (float)Math.Round(v.X, 3), (float)Math.Round(v.Y, 3), (float)Math.Round(v.Z, 3) };

        var sirens = bones.Where(b => SirenRx.IsMatch(b.Name ?? "")).OrderBy(b => int.Parse(SirenRx.Match(b.Name).Groups[1].Value))
            .Select(b => new { name = b.Name, number = int.Parse(SirenRx.Match(b.Name).Groups[1].Value), tag = b.Tag, pos = P(b.Pos) }).ToList();
        var glass = bones.Where(b => GlassRx.IsMatch(b.Name ?? "")).Select(b => new { name = b.Name, tag = b.Tag, models = ModelsAt(b.Tag) + MainModelsAt(b.Index) }).ToList();
        var extras = bones.Where(b => ExtraRx.IsMatch(b.Name ?? "")).OrderBy(b => ExtraNo(b.Name))
            .Select(b => new { name = b.Name, number = ExtraNo(b.Name), tag = b.Tag, pos = P(b.Pos), switchable = children.Any(c => c.BoneTag == b.Tag) }).ToList();
        return new
        {
            sirens, glass, extras,
            bones = bones.Select(b => new { b.Index, b.Name, b.Tag, b.Parent, pos = P(b.Pos) }).ToList(),
        };
    }

    // ================================================================== Metas (vehicles / carvariations / carcols)

    public class Settings
    {
        public string Kind { get; set; } = "keep";          // keep | civil | police | sheriff | ambulance | fire | utility
        public bool? Lights { get; set; }                   // null = Blaulicht nicht anfassen
        public string Colors { get; set; } = "blue";        // blue | blue_red | red | red_white | blue_white | amber | amber_white
        public string Pattern { get; set; } = "alternate";  // alternate | double | together | rotate
        public bool Wigwag { get; set; }                    // Scheinwerfer/Rückleuchten im Wechsel
        public string SirenSound { get; set; } = "keep";    // keep | POLICE | POLICE3 | SHERIFF | FBI | AMBULANCE | FIRETRUK
        public int SirenId { get; set; }                    // 0 = automatisch
        public string ExtrasMode { get; set; } = "keep";    // keep | random | all | fixed
        public List<int> ExtrasOn { get; set; } = new();
    }

    const string TagPrefix = "gtatool";
    static readonly string[] ManagedFlags = { "FLAG_LAW_ENFORCEMENT", "FLAG_EMERGENCY_SERVICE", "FLAG_REPORT_CRIME_IF_STANDING_ON" };

    class Hit { public string File; public System.Xml.Linq.XDocument Doc; public System.Xml.Linq.XElement Item; }

    static IEnumerable<string> MetaFiles(string sessionDir)
    {
        var d = Path.Combine(sessionDir, "data");
        if (!Directory.Exists(d)) return Enumerable.Empty<string>();
        return Directory.GetFiles(d, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f);
    }

    static List<(string file, System.Xml.Linq.XDocument doc)> Docs(string sessionDir, string root)
    {
        var list = new List<(string, System.Xml.Linq.XDocument)>();
        foreach (var f in MetaFiles(sessionDir))
        {
            try
            {
                var doc = MetaData.LoadXml(f);
                if (doc.Root?.Name.LocalName == root) list.Add((f, doc));
            }
            catch { /* fehlerhafte Datei – meldet die Analyse */ }
        }
        return list;
    }

    static System.Xml.Linq.XElement El(System.Xml.Linq.XElement e, string name) =>
        e?.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    static IEnumerable<System.Xml.Linq.XElement> Items(System.Xml.Linq.XElement root, string list) =>
        El(root, list)?.Elements().Where(x => x.Name.LocalName == "Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>();

    static Hit FindModel(List<(string file, System.Xml.Linq.XDocument doc)> docs, string list, string model)
    {
        foreach (var (f, doc) in docs)
        {
            var it = Items(doc.Root, list).FirstOrDefault(i => VehicleConvert.GetVal(i, "modelName")?.Equals(model, StringComparison.OrdinalIgnoreCase) == true);
            if (it != null) return new Hit { File = f, Doc = doc, Item = it };
        }
        return null;
    }

    static int AttrInt(System.Xml.Linq.XElement item, string name)
    {
        var v = El(item, name)?.Attribute("value")?.Value ?? El(item, name)?.Value;
        return int.TryParse(v?.Trim(), out var n) ? n : 0;
    }

    static void SetAttr(System.Xml.Linq.XElement item, string name, string value, string after = null)
    {
        var el = El(item, name);
        if (el == null)
        {
            el = new System.Xml.Linq.XElement(name);
            var anchor = after != null ? El(item, after) : null;
            if (anchor != null) anchor.AddAfterSelf(el); else item.Add(el);
        }
        el.RemoveNodes();
        el.SetAttributeValue("value", value);
    }

    static List<string> Flags(System.Xml.Linq.XElement veh) =>
        (VehicleConvert.GetVal(veh, "flags") ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    static string StateFile(string sessionDir) => Path.Combine(sessionDir, "einsatz.json");

    static Dictionary<string, Dictionary<string, string>> LoadState(string sessionDir)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(StateFile(sessionDir))) ?? new(); }
        catch { return new(); }
    }

    static string ModelFile(string sessionDir, string model)
    {
        foreach (var n in new[] { model + "_hi.yft", model + ".yft" })
        {
            var p = Path.Combine(sessionDir, "stream", n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Alles zu Blaulicht/Extras eines Fahrzeugs für die Oberfläche.</summary>
    public static object Info(string sessionDir, string model)
    {
        model = model.ToLowerInvariant();
        var vehDocs = Docs(sessionDir, "CVehicleModelInfo__InitDataList");
        var veh = FindModel(vehDocs, "InitDatas", model);
        var varDocs = Docs(sessionDir, "CVehicleModelInfoVariation");
        var vari = FindModel(varDocs, "variationData", model);
        var colDocs = Docs(sessionDir, "CVehicleModelInfoVarGlobal");
        int sirenId = vari != null ? AttrInt(vari.Item, "sirenSettings") : 0;
        System.Xml.Linq.XElement siren = null;
        if (sirenId > 0)
            foreach (var (_, doc) in colDocs)
                siren ??= Items(doc.Root, "Sirens").FirstOrDefault(s => AttrInt(s, "id") == sirenId);
        var sirenName = siren != null ? VehicleConvert.GetVal(siren, "name") : null;
        var ours = sirenName != null && sirenName.StartsWith(TagPrefix + "|");
        var parts = ours ? sirenName.Split('|') : Array.Empty<string>();

        var flags = veh != null ? Flags(veh.Item) : new List<string>();
        var cls = veh != null ? VehicleConvert.GetVal(veh.Item, "vehicleClass") : null;
        var state = LoadState(sessionDir);
        state.TryGetValue(model, out var st);
        string kind = st != null && st.TryGetValue("kind", out var k) ? k
            : flags.Contains("FLAG_LAW_ENFORCEMENT") ? "police"
            : flags.Contains("FLAG_EMERGENCY_SERVICE") ? "ambulance"
            : "civil";
        var req = (veh != null ? VehicleConvert.GetVal(veh.Item, "requiredExtras") : null) ?? "";
        var reqList = Regex.Matches(req, @"EXTRA_(\d+)", RegexOptions.IgnoreCase).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        string extrasMode = flags.Contains("FLAG_EXTRAS_ALL") ? "all" : reqList.Count > 0 ? "fixed" : "random";

        object modelInfo = null;
        string modelError = null;
        var yft = ModelFile(sessionDir, model);
        if (yft != null)
        {
            try { modelInfo = ModelInfo(yft); }
            catch (Exception ex) { modelError = ex.Message; }
        }
        else modelError = "Modell nicht gefunden.";
        return new
        {
            model,
            hasMeta = veh != null,
            hasVariation = vari != null,
            vehicleClass = cls,
            flags,
            audio = veh != null ? VehicleConvert.GetVal(veh.Item, "audioNameHash") : null,
            origAudio = st != null && st.TryGetValue("origAudio", out var oa) ? oa : null,
            sirenId,
            sirenDefined = siren != null,
            sirenOurs = ours,
            sirenName,
            kind,
            lights = sirenId > 0,
            colors = parts.Length > 1 ? parts[1] : null,
            pattern = parts.Length > 2 ? parts[2] : null,
            wigwag = parts.Length > 3 && parts[3] == "wigwag",
            extrasMode,
            extrasOn = reqList,
            modelInfo,
            modelError,
        };
    }

    /// <summary>3D-Marker (kleine Rauten) an den Blaulicht-Punkten – je Seite in der gewählten Farbe. Gibt 0–2 GLB-Dateien zurück.</summary>
    public static object Markers(string sessionDir, string model, string colors, string outDir)
    {
        var yft = ModelFile(sessionDir, model.ToLowerInvariant());
        if (yft == null) return new { files = new List<string>() };
        var (ca, cb) = ColorPair(colors ?? "blue");
        var sides = new[] { (pts: new List<Vector3>(), tris: new List<int>(), col: ca), (pts: new List<Vector3>(), tris: new List<int>(), col: cb) };
        foreach (var b in Bones(LoadYft(yft)))
        {
            var m = SirenRx.Match(b.Name ?? "");
            if (!m.Success) continue;
            int n = int.Parse(m.Groups[1].Value);
            bool sideA = Math.Abs(b.Pos.X) > 0.02f ? b.Pos.X < 0 : n % 2 == 1;
            var s = sides[sideA ? 0 : 1];
            const float r = 0.06f;
            int i0 = s.pts.Count;
            s.pts.AddRange(new[] { b.Pos + new Vector3(r, 0, 0), b.Pos - new Vector3(r, 0, 0), b.Pos + new Vector3(0, r, 0), b.Pos - new Vector3(0, r, 0), b.Pos + new Vector3(0, 0, r), b.Pos - new Vector3(0, 0, r) });
            foreach (var (a, c, d) in new[] { (0, 2, 4), (2, 1, 4), (1, 3, 4), (3, 0, 4), (2, 0, 5), (1, 2, 5), (3, 1, 5), (0, 3, 5) })
            { s.tris.Add(i0 + a); s.tris.Add(i0 + c); s.tris.Add(i0 + d); }
        }
        Directory.CreateDirectory(outDir);
        var files = new List<string>();
        int k = 0;
        foreach (var s in sides)
        {
            if (s.pts.Count == 0) continue;
            var col = new[] { ((s.col >> 16) & 0xFF) / 255f, ((s.col >> 8) & 0xFF) / 255f, (s.col & 0xFF) / 255f, 1f };
            var f = Path.Combine(outDir, $"{model}_blaulicht_{k++}_{Guid.NewGuid():N}.glb");
            File.WriteAllBytes(f, Model3D.SimpleGlb(s.pts, s.tris, col));
            files.Add(f);
        }
        return new { files };
    }

    // ---- Sirenen-Eintrag (carcols.meta)

    const uint Red = 0xFFFF0A0A, Blue = 0xFF0A0AFF, White = 0xFFFFFFFF, Amber = 0xFFFF8C00;

    static (uint a, uint b) ColorPair(string c) => c switch
    {
        "blue_red" => (Red, Blue),
        "red" => (Red, Red),
        "red_white" => (Red, White),
        "blue_white" => (Blue, White),
        "amber" => (Amber, Amber),
        "amber_white" => (Amber, White),
        _ => (Blue, Blue),
    };

    static (uint a, uint b, int bpm) PatternSeq(string p) => p switch
    {
        "double" => (0xA0A0A0A0, 0x0A0A0A0A, 600),
        "together" => (0xAAAAAAAA, 0xAAAAAAAA, 200),
        "rotate" => (0xFFFFFFFF, 0xFFFFFFFF, 200),
        _ => (0xAAAAAAAA, 0x55555555, 200),
    };

    static System.Xml.Linq.XElement V(string name, object value) =>
        new(name, new System.Xml.Linq.XAttribute("value", value is bool b ? (b ? "true" : "false") : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));

    static System.Xml.Linq.XElement SirenItem(int id, string model, Settings s, Dictionary<int, float> xByNumber)
    {
        var (ca, cb) = ColorPair(s.Colors);
        var (sa, sb, bpm) = PatternSeq(s.Pattern);
        bool rotate = s.Pattern == "rotate";
        var sirens = new System.Xml.Linq.XElement("sirens", new System.Xml.Linq.XAttribute("itemType", "sirenLight"));
        for (int n = 1; n <= 20; n++)
        {
            // Seite A = links (x < 0), B = rechts; Mitte abwechselnd
            bool sideA = xByNumber.TryGetValue(n, out var x) && Math.Abs(x) > 0.02f ? x < 0 : n % 2 == 1;
            sirens.Add(new System.Xml.Linq.XElement("Item",
                new System.Xml.Linq.XElement("rotation",
                    V("delta", 0), V("start", 0), V("speed", rotate ? 3 : 0), V("sequencer", 0xFFFFFFFF), V("multiples", 1), V("direction", sideA), V("syncToBpm", true)),
                new System.Xml.Linq.XElement("flashiness",
                    V("delta", 0), V("start", 0), V("speed", 0), V("sequencer", sideA ? sa : sb), V("multiples", 1), V("direction", true), V("syncToBpm", true)),
                new System.Xml.Linq.XElement("corona",
                    V("intensity", 50), V("size", 0.8), V("pull", 0.15), V("faceCamera", false)),
                V("color", $"0x{(sideA ? ca : cb):X8}"),
                V("intensity", 1),
                V("lightGroup", 0),
                V("rotate", rotate),
                V("scale", true),
                V("scaleFactor", 2),
                V("flash", !rotate),
                V("light", n % 2 == 1),
                V("spotLight", true),
                V("castShadows", false)));
        }
        // Scheinwerfer/Rückleuchten im Wechsel – bei schnellem Takt längere Phasen
        uint head = !s.Wigwag ? 0u : bpm >= 400 ? 0xF0F0F0F0u : 0xAAAAAAAAu, head2 = !s.Wigwag ? 0u : bpm >= 400 ? 0x0F0F0F0Fu : 0x55555555u;
        return new System.Xml.Linq.XElement("Item",
            V("id", id),
            new System.Xml.Linq.XElement("name", $"{TagPrefix}|{s.Colors}|{s.Pattern}|{(s.Wigwag ? "wigwag" : "-")}|{model}"),
            V("timeMultiplier", 1), V("lightFalloffMax", 10), V("lightFalloffExponent", 10),
            V("lightInnerConeAngle", 2.29061), V("lightOuterConeAngle", 70), V("lightOffset", 0),
            new System.Xml.Linq.XElement("textureName", "VehicleLight_sirenlight"),
            V("sequencerBpm", bpm),
            new System.Xml.Linq.XElement("leftHeadLight", V("sequencer", head)),
            new System.Xml.Linq.XElement("rightHeadLight", V("sequencer", head2)),
            new System.Xml.Linq.XElement("leftTailLight", V("sequencer", head2)),
            new System.Xml.Linq.XElement("rightTailLight", V("sequencer", head)),
            V("leftHeadLightMultiples", 1), V("rightHeadLightMultiples", 1),
            V("leftTailLightMultiples", 1), V("rightTailLightMultiples", 1),
            V("useRealLights", true),
            sirens);
    }

    static System.Xml.Linq.XElement NewVariation(string model)
    {
        var liveries = new System.Xml.Linq.XElement("liveries");
        for (int i = 0; i < 32; i++) liveries.Add(new System.Xml.Linq.XElement("Item", new System.Xml.Linq.XAttribute("value", "false")));
        return new System.Xml.Linq.XElement("Item",
            new System.Xml.Linq.XElement("modelName", model),
            new System.Xml.Linq.XElement("colors", new System.Xml.Linq.XElement("Item",
                new System.Xml.Linq.XElement("indices", new System.Xml.Linq.XAttribute("content", "char_array"), "0 0 0 0 0 0"),
                liveries)),
            new System.Xml.Linq.XElement("kits", new System.Xml.Linq.XElement("Item", "0_default_modkit")),
            new System.Xml.Linq.XElement("windowsWithExposedEdges"),
            new System.Xml.Linq.XElement("plateProbabilities", new System.Xml.Linq.XElement("Probabilities")),
            V("lightSettings", 1),
            V("sirenSettings", 0));
    }

    /// <summary>Einsatzfahrzeug einstellen: Typ (Flags/Klasse), Blaulicht (carcols + carvariations), Sirenen-Ton, Extras.</summary>
    public static object Apply(string sessionDir, string model, Settings s, string backupDir)
    {
        model = model.ToLowerInvariant();
        s ??= new Settings();
        var vehDocs = Docs(sessionDir, "CVehicleModelInfo__InitDataList");
        var veh = FindModel(vehDocs, "InitDatas", model)
            ?? throw new UserError($"Für „{model}“ gibt es keine vehicles.meta. Replace-Fahrzeuge bitte zuerst „In Addon umwandeln“.");
        var dir = Path.GetDirectoryName(veh.File)!;
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var state = LoadState(sessionDir);
        if (!state.TryGetValue(model, out var st)) state[model] = st = new Dictionary<string, string>();

        // ---- Fahrzeugtyp: Klasse + Flags
        var cls = VehicleConvert.GetVal(veh.Item, "vehicleClass");
        if (!st.ContainsKey("origClass") && cls != null && cls != "VC_EMERGENCY") st["origClass"] = cls;
        var flags = Flags(veh.Item);
        var before = string.Join(" ", flags) + "|" + cls;
        if (s.Kind != "keep")
        {
            flags.RemoveAll(f => ManagedFlags.Contains(f));
            switch (s.Kind)
            {
                case "police":
                case "sheriff":
                    flags.AddRange(ManagedFlags);
                    cls = "VC_EMERGENCY";
                    break;
                case "ambulance":
                case "fire":
                    flags.Add("FLAG_EMERGENCY_SERVICE");
                    cls = "VC_EMERGENCY";
                    break;
                default: // civil, utility
                    if (cls == "VC_EMERGENCY") cls = st.TryGetValue("origClass", out var oc) ? oc : "VC_SEDAN";
                    break;
            }
            st["kind"] = s.Kind;
        }
        // ---- Extras beim Spawnen
        var req = new List<int>();
        if (s.ExtrasMode != "keep")
        {
            flags.Remove("FLAG_EXTRAS_ALL");
            flags.Remove("FLAG_EXTRAS_REQUIRE");
            if (s.ExtrasMode == "all") flags.Add("FLAG_EXTRAS_ALL");
            if (s.ExtrasMode == "fixed") req = (s.ExtrasOn ?? new()).Where(n => n is >= 1 and <= 14).Distinct().OrderBy(n => n).ToList();
            if (s.ExtrasMode is "all" or "fixed" && !flags.Contains("FLAG_EXTRAS_STRONG")) flags.Add("FLAG_EXTRAS_STRONG"); // Lichtbalken fällt nicht ab
            VehicleConvert.SetVal(veh.Item, "requiredExtras", string.Join(" ", req.Select(n => $"EXTRA_{n}")), "flags");
            changed.Add(veh.File);
        }
        flags = flags.Distinct().ToList();
        if (string.Join(" ", flags) + "|" + cls != before)
        {
            VehicleConvert.SetVal(veh.Item, "flags", string.Join(" ", flags));
            if (cls != null) VehicleConvert.SetVal(veh.Item, "vehicleClass", cls);
            changed.Add(veh.File);
        }
        // ---- Sirenen-Ton (gehört in GTA zum Fahrzeug-Sound: ändert auch den Motorsound)
        if (!string.IsNullOrEmpty(s.SirenSound) && s.SirenSound != "keep")
        {
            var cur = VehicleConvert.GetVal(veh.Item, "audioNameHash") ?? "";
            if (!st.ContainsKey("origAudio")) st["origAudio"] = cur;
            var target = s.SirenSound == "orig" ? st["origAudio"] : s.SirenSound.ToUpperInvariant();
            if (!target.Equals(cur, StringComparison.OrdinalIgnoreCase))
            {
                VehicleConvert.SetVal(veh.Item, "audioNameHash", target, "handlingId");
                changed.Add(veh.File);
            }
        }

        // ---- Blaulicht: carvariations (sirenSettings) + carcols (Sirens)
        var varDocs = Docs(sessionDir, "CVehicleModelInfoVariation");
        var vari = FindModel(varDocs, "variationData", model);
        var colDocs = Docs(sessionDir, "CVehicleModelInfoVarGlobal");
        int current = vari != null ? AttrInt(vari.Item, "sirenSettings") : 0;
        System.Xml.Linq.XElement Own(int id) => id <= 0 ? null : colDocs.SelectMany(d => Items(d.doc.Root, "Sirens")).FirstOrDefault(x => AttrInt(x, "id") == id);
        int Users(int id) => varDocs.SelectMany(d => Items(d.doc.Root, "variationData")).Count(v => AttrInt(v, "sirenSettings") == id);

        bool lightsKnown = s.Lights.HasValue;
        bool lights = s.Lights == true;
        int newId = 0;
        if (lights)
        {
            var used = new HashSet<int>(colDocs.SelectMany(d => Items(d.doc.Root, "Sirens")).Select(x => AttrInt(x, "id")));
            if (s.SirenId is > 0 and < 256) newId = s.SirenId;
            else if (current > 0 && Own(current) != null && Users(current) <= 1) newId = current;
            else
            {
                newId = 100 + (int)(JenkHash.GenHash(model) % 155);
                while (used.Contains(newId)) newId = newId >= 254 ? 100 : newId + 1;
            }
            if (newId <= 13) warnings.Add($"Sirenen-ID {newId} ist bei GTA schon vergeben – besser ab 100 wählen.");
        }

        // alten eigenen Eintrag entfernen (nur wenn ihn kein anderes Fahrzeug benutzt)
        string targetCarcols = null;
        foreach (var id in (lightsKnown ? new[] { current, newId } : Array.Empty<int>()).Where(i => i > 0).Distinct())
        {
            if (id == current && Users(current) > 1 && id != newId) continue;
            foreach (var (f, doc) in colDocs)
                foreach (var it in Items(doc.Root, "Sirens").Where(x => AttrInt(x, "id") == id).ToList())
                {
                    targetCarcols ??= f;
                    it.Remove();
                    changed.Add(f);
                }
        }

        if (lights)
        {
            var mi = ModelFile(sessionDir, model);
            var xs = new Dictionary<int, float>();
            if (mi != null)
            {
                try
                {
                    foreach (var b in Bones(LoadYft(mi)))
                    {
                        var m = SirenRx.Match(b.Name ?? "");
                        if (m.Success) xs[int.Parse(m.Groups[1].Value)] = b.Pos.X;
                    }
                }
                catch (Exception ex) { warnings.Add($"Modell nicht lesbar ({ex.Message}) – Farben werden abwechselnd verteilt."); }
            }
            if (xs.Count == 0)
                warnings.Add("Das Modell hat keine Blaulicht-Punkte (Bones „siren1“ …). Die Einstellungen sind gespeichert, leuchten aber erst, wenn du in Blender Blaulicht-Punkte setzt.");

            // Ziel-Datei: bisherige, sonst eine carcols im Ordner des Fahrzeugs, sonst neue carcols.meta
            targetCarcols ??= colDocs.Select(d => d.file).FirstOrDefault(f => Path.GetDirectoryName(f)!.Equals(dir, StringComparison.OrdinalIgnoreCase));
            System.Xml.Linq.XDocument cdoc;
            if (targetCarcols != null) cdoc = colDocs.First(d => d.file == targetCarcols).doc;
            else
            {
                targetCarcols = Path.Combine(dir, File.Exists(Path.Combine(dir, "carcols.meta")) ? $"carcols_{model}.meta" : "carcols.meta");
                cdoc = new System.Xml.Linq.XDocument(new System.Xml.Linq.XDeclaration("1.0", "UTF-8", null),
                    new System.Xml.Linq.XElement("CVehicleModelInfoVarGlobal"));
                colDocs.Add((targetCarcols, cdoc));
            }
            var list = El(cdoc.Root, "Sirens");
            if (list == null)
            {
                list = new System.Xml.Linq.XElement("Sirens", new System.Xml.Linq.XAttribute("itemType", "CSirenSettings"));
                cdoc.Root.Add(list);
            }
            list.Add(SirenItem(newId, model, s, xs));
            changed.Add(targetCarcols);
        }

        int finalId = !lightsKnown ? current : lights ? newId : 0;
        if (vari == null && finalId > 0)
        {
            // Fahrzeug ohne carvariations → Eintrag anlegen
            var vfile = varDocs.Select(d => d.file).FirstOrDefault(f => Path.GetDirectoryName(f)!.Equals(dir, StringComparison.OrdinalIgnoreCase));
            System.Xml.Linq.XDocument vdoc;
            if (vfile != null) vdoc = varDocs.First(d => d.file == vfile).doc;
            else
            {
                vfile = Path.Combine(dir, "carvariations.meta");
                vdoc = new System.Xml.Linq.XDocument(new System.Xml.Linq.XDeclaration("1.0", "UTF-8", null),
                    new System.Xml.Linq.XElement("CVehicleModelInfoVariation", new System.Xml.Linq.XElement("variationData")));
                varDocs.Add((vfile, vdoc));
            }
            var vd = El(vdoc.Root, "variationData") ?? new System.Xml.Linq.XElement("variationData");
            if (vd.Parent == null) vdoc.Root.Add(vd);
            var item = NewVariation(model);
            vd.Add(item);
            vari = new Hit { File = vfile, Doc = vdoc, Item = item };
        }
        if (vari != null && AttrInt(vari.Item, "sirenSettings") != finalId)
        {
            SetAttr(vari.Item, "sirenSettings", finalId.ToString(), "lightSettings");
            changed.Add(vari.File);
        }

        // ---- speichern
        foreach (var f in changed)
        {
            var doc = f == veh.File ? veh.Doc
                : vari != null && f == vari.File ? vari.Doc
                : colDocs.Where(d => d.file == f).Select(d => d.doc).FirstOrDefault()
                  ?? varDocs.Where(d => d.file == f).Select(d => d.doc).First();
            if (File.Exists(f)) Session.Backup(f, backupDir);
            VehicleConvert.SaveXml(doc, f);
        }
        File.WriteAllText(StateFile(sessionDir), System.Text.Json.JsonSerializer.Serialize(state));
        return new { changed = changed.Select(f => Path.GetRelativePath(sessionDir, f).Replace('\\', '/')).ToList(), warnings, sirenId = finalId, info = Info(sessionDir, model) };
    }
}
