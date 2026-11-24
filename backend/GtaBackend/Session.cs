using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>
/// Sitzungsordner (kein Projekt): alles wird beim Öffnen in einen Arbeitsordner entpackt.
///   stream\   Modelle und Texturen (.yft .ytd .ydr …)
///   data\&lt;quelle&gt;\  Meta-Dateien (vehicles.meta, carcols.meta …)
///   lang\&lt;quelle&gt;\  GXT2-Texte (Fahrzeugnamen)
///   audio\&lt;quelle&gt;\ Sounddateien (für spätere Ausbaustufe)
///   index.json  Herkunft jeder Datei
/// </summary>
public static class Session
{
    static readonly HashSet<string> StreamExt = new(StringComparer.OrdinalIgnoreCase)
        { ".yft", ".ytd", ".ydr", ".ydd", ".ybn", ".ytyp", ".ymap", ".ycd", ".ypt", ".ynv", ".yld" };
    static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".awc", ".rel", ".dat151", ".dat54", ".dat10", ".dat15", ".dat16", ".dat22", ".dat4", ".nametable" };
    static readonly HashSet<string> IgnoreNames = new(StringComparer.OrdinalIgnoreCase)
        { "content.xml", "setup2.xml", "fxmanifest.lua", "__resource.lua" };

    public class IndexEntry
    {
        public string File { get; set; }     // relativ zum Sitzungsordner
        public string Origin { get; set; }   // Herkunft (z. B. mycar/dlc.rpf/x64/vehicles.rpf/mycar.yft)
        public string Source { get; set; }   // Quellenname
    }

    // ------------------------------------------------------------------ Öffnen

    public static object Open(List<string> paths, string sessionDir)
    {
        if (paths.Count == 0) throw new UserError("Keine Datei ausgewählt.");
        Directory.CreateDirectory(sessionDir);
        var index = LoadIndex(sessionDir);
        var warnings = new List<string>();
        int added = 0;

        foreach (var raw in paths)
        {
            var path = raw.TrimEnd('\\', '/');
            if (Directory.Exists(path))
            {
                var src = SafeName(Path.GetFileName(path));
                Program.Progress($"Ordner {Path.GetFileName(path)} wird gelesen …");
                SaveOriginal(path, src, sessionDir);
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(path, f);
                    if (f.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsGtaRpf(f)) added += OpenRpf(f, sessionDir, SafeName(RpfSourceName(f, path)), index, warnings);
                        else warnings.Add(ForeignRpfText(f));
                    }
                    else
                        added += Take(Path.GetFileName(f), () => File.ReadAllBytes(f), $"{Path.GetFileName(path)}/{rel.Replace('\\', '/')}", src, sessionDir, index, warnings);
                }
            }
            else if (File.Exists(path))
            {
                if (path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsGtaRpf(path)) added += OpenRpf(path, sessionDir, SafeName(RpfSourceName(path, null)), index, warnings);
                    else warnings.Add(ForeignRpfText(path));
                }
                else
                    added += Take(Path.GetFileName(path), () => File.ReadAllBytes(path), Path.GetFileName(path), "dateien", sessionDir, index, warnings);
            }
            else warnings.Add($"Nicht gefunden: {path}");
        }

        SaveIndex(sessionDir, index);
        if (added == 0) warnings.Add("Es wurden keine verwendbaren GTA-Dateien gefunden.");
        var model = Analyze(sessionDir);
        return new { added, warnings, model };
    }

    // ------------------------------------------------------------------ Original-Resource erhalten

    /// <summary>
    /// Merkt sich alle Dateien eines geöffneten Ordners, die nicht in stream\ landen
    /// (fxmanifest.lua, Lua-Skripte, .fxap, Metas, Audio …), unter original\&lt;quelle&gt;\.
    /// Beim Export wird daraus die Resource 1:1 wiederhergestellt – nur geänderte Dateien werden ersetzt.
    /// </summary>
    static void SaveOriginal(string path, string src, string sessionDir)
    {
        var root = Path.Combine(sessionDir, "original", src);
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            // Resources mit .rpf (z. B. dlc.rpf) werden beim Export in lose Dateien umgewandelt – dort gibt es kein 1:1-Original.
            if (Directory.EnumerateFiles(path, "*.rpf", SearchOption.AllDirectories).Any(IsGtaRpf)) return;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (StreamExt.Contains(Path.GetExtension(f)) || (f.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) && IsGtaRpf(f))) continue;
                var rel = Path.GetRelativePath(path, f);
                if (rel.Split('\\', '/').Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.Equals("node_modules", StringComparison.OrdinalIgnoreCase))) continue;
                var target = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
            }
            var listFile = Path.Combine(sessionDir, "originals.json");
            var list = File.Exists(listFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(listFile)) ?? new() : new();
            list[src] = Path.GetFileName(path);
            File.WriteAllText(listFile, JsonSerializer.Serialize(list));
        }
        catch { /* ohne Original geht der Export wie bisher */ }
    }

    public class OriginalResource
    {
        public string Name;     // ursprünglicher Ordnername der Resource
        public string Dir;      // Kopie der Nicht-stream-Dateien (Wurzel der Resource)
        public string Prefix;   // Herkunfts-Präfix in index.json („ordner/unterordner“)
    }

    /// <summary>Genau eine geöffnete Original-Resource (mit fxmanifest.lua) – sonst null.</summary>
    public static OriginalResource FindOriginal(string sessionDir)
    {
        var listFile = Path.Combine(sessionDir, "originals.json");
        if (!File.Exists(listFile)) return null;
        Dictionary<string, string> list;
        try { list = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(listFile)) ?? new(); } catch { return null; }
        var found = new List<OriginalResource>();
        foreach (var (src, folder) in list)
        {
            var root = Path.Combine(sessionDir, "original", src);
            if (!Directory.Exists(root)) continue;
            foreach (var m in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(f => Path.GetFileName(f).Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("__resource.lua", StringComparison.OrdinalIgnoreCase)))
            {
                var dir = Path.GetDirectoryName(m)!;
                var relDir = Path.GetRelativePath(root, dir).Replace('\\', '/');
                found.Add(new OriginalResource
                {
                    Name = relDir == "." ? folder : Path.GetFileName(dir),
                    Dir = dir,
                    Prefix = relDir == "." ? folder : folder + "/" + relDir,
                });
            }
        }
        return found.Count == 1 ? found[0] : null;
    }

    static string RpfSourceName(string rpfPath, string root)
    {
        // dlc.rpf heißt immer gleich – der Ordnername ist aussagekräftiger.
        var name = Path.GetFileNameWithoutExtension(rpfPath);
        if (name.Equals("dlc", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetFileName(Path.GetDirectoryName(rpfPath));
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return name;
    }

    /// <summary>Echtes GTA-V-Archiv (Kopf „RPF7“)? FiveM legt z. B. leere „resource.rpf“ im alten RPF2-Format an.</summary>
    public static bool IsGtaRpf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b = new byte[4];
            return fs.Read(b, 0, 4) == 4 && b[0] == (byte)'7' && b[1] == (byte)'F' && b[2] == (byte)'P' && b[3] == (byte)'R';
        }
        catch { return false; }
    }

    static string ForeignRpfText(string path) =>
        $"{Path.GetFileName(path)} ist kein GTA-V-Archiv (alte FiveM-Datei) und wurde übersprungen – sie bleibt beim Export unverändert erhalten.";

    static int OpenRpf(string path, string sessionDir, string src, List<IndexEntry> index, List<string> warnings)
    {
        Program.Progress($"{Path.GetFileName(path)} wird gelesen …");
        var rpf = new RpfFile(path, Path.GetFileName(path));
        var errors = new List<string>();
        try
        {
            rpf.ScanStructure(null, e => errors.Add(e));
        }
        catch (Exception ex)
        {
            throw new UserError(RpfErrorText(path, ex.Message));
        }
        if (rpf.LastException != null || rpf.AllEntries == null)
            throw new UserError(RpfErrorText(path, rpf.LastError ?? rpf.LastException?.Message ?? "unbekannt"));
        foreach (var e in errors.Take(5)) warnings.Add($"{Path.GetFileName(path)}: {e}");

        int added = 0;
        var label = src + "/" + Path.GetFileName(path);
        added += ExtractRpf(rpf, label, src, sessionDir, index, warnings);
        return added;
    }

    static string RpfErrorText(string path, string detail)
    {
        var keys = Keys.Loaded ? "" : " Tipp: In den Einstellungen den GTA-V-Ordner angeben – verschlüsselte RPFs brauchen die GTA-Schlüssel.";
        return $"{Path.GetFileName(path)} konnte nicht gelesen werden ({detail}).{keys}";
    }

    static int ExtractRpf(RpfFile rpf, string label, string src, string sessionDir, List<IndexEntry> index, List<string> warnings)
    {
        int added = 0;
        foreach (var entry in rpf.AllEntries)
        {
            if (entry is not RpfFileEntry fe) continue;
            if (fe.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) continue; // Kind-Archive unten
            var inner = fe.Path.Replace('\\', '/');
            var origin = label + "/" + StripRootName(inner, rpf.Name);
            try
            {
                added += Take(fe.Name, () =>
                {
                    var data = rpf.ExtractFile(fe);
                    if (data != null && fe is RpfResourceFileEntry rrfe)
                    {
                        // ExtractFile liefert entpackte Rohdaten – als normale RSC7-Datei speichern.
                        data = ResourceBuilder.Compress(data);
                        data = ResourceBuilder.AddResourceHeader(rrfe, data);
                    }
                    return data;
                }, origin, src, sessionDir, index, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add($"{fe.Name}: konnte nicht entpackt werden ({ex.Message})");
            }
        }
        if (rpf.Children != null)
        {
            foreach (var child in rpf.Children)
                added += ExtractRpf(child, label, src, sessionDir, index, warnings);
        }
        return added;
    }

    static string StripRootName(string inner, string rpfName)
    {
        // fe.Path enthält den vollen Pfad ab dem Wurzel-Archiv
        var i = inner.IndexOf('/');
        return i >= 0 ? inner[(i + 1)..] : inner;
    }

    static int Take(string name, Func<byte[]> read, string origin, string src, string sessionDir, List<IndexEntry> index, List<string> warnings)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var lower = name.ToLowerInvariant();
        if (IgnoreNames.Contains(lower)) return 0;
        string relTarget;
        if (StreamExt.Contains(ext))
        {
            // Kleidung: Ped/Sammlung aus dem Ordnernamen in den Dateinamen übernehmen (verhindert Überschreiben m/w)
            if (ext == ".ydd" || ext == ".ytd") lower = Clothing.CanonicalName(lower, origin);
            relTarget = Path.Combine("stream", lower);
        }
        else if (ext == ".meta" || (ext == ".xml" && !lower.StartsWith("content") && !lower.StartsWith("setup2")) || ext == ".ymt")
            relTarget = Path.Combine("data", src, lower);
        else if (ext == ".gxt2")
        {
            var lang = LanguageOf(origin);
            if (lang == null) return 0;
            relTarget = Path.Combine("lang", src, lang + "_" + lower);
        }
        else if (AudioExt.Contains(ext) || lower.Contains(".dat151") || lower.Contains(".dat54") || lower.Contains(".dat10") || lower.Contains(".dat15") || lower.Contains(".dat16") || lower.Contains(".dat22"))
        {
            if (ext == ".awc")
            {
                // Pack-Ordner = übergeordneter Ordner (z. B. sfx/dlc_meinauto/…awc)
                var parts = origin.Replace('\\', '/').Split('/');
                var pack = parts.Length >= 2 ? parts[^2] : "dlc_" + src;
                if (pack.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) pack = pack[..^4];
                pack = Session.SafeName(pack);
                relTarget = Path.Combine("audio", src, "sfx", pack, lower);
            }
            else relTarget = Path.Combine("audio", src, "audioconfig", lower);
        }
        else return 0;

        var data = read();
        if (data == null) { warnings.Add($"{name}: leer oder nicht lesbar"); return 0; }

        if (ext == ".meta" && data.Length > 4 && data[0] == 'R' && data[1] == 'B' && data[2] == 'F' && data[3] == '0')
        {
            try
            {
                var rbf = new RbfFile();
                rbf.Load(data);
                data = Encoding.UTF8.GetBytes(RbfXml.GetXml(rbf));
            }
            catch (Exception ex) { warnings.Add($"{name}: Binär-Meta konnte nicht umgewandelt werden ({ex.Message})"); }
        }

        var full = Path.Combine(sessionDir, relTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var relNorm = relTarget.Replace('\\', '/');
        var existing = index.FirstOrDefault(x => x.File.Equals(relNorm, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // Nur warnen, wenn sich die Dateien wirklich unterscheiden
            if (StreamExt.Contains(ext) && File.Exists(full) && new FileInfo(full).Length != data.Length)
                warnings.Add($"{lower} kommt doppelt (in unterschiedlichen Versionen) vor – die zuletzt geladene wird verwendet.");
            index.Remove(existing);
        }
        File.WriteAllBytes(full, data);
        index.Add(new IndexEntry { File = relNorm, Origin = origin, Source = src });
        return 1;
    }

    static string LanguageOf(string origin)
    {
        var o = origin.ToLowerInvariant();
        foreach (var l in new[] { "american", "german", "french", "italian", "spanish", "polish", "russian", "japanese", "korean", "chinese", "portuguese", "mexican" })
            if (o.Contains("/" + l)) return l;
        return "american";
    }

    public static string SafeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "quelle";
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString().Trim('_') is { Length: > 0 } r ? r : "quelle";
    }

    public static List<IndexEntry> LoadIndex(string sessionDir)
    {
        var f = Path.Combine(sessionDir, "index.json");
        if (!File.Exists(f)) return new List<IndexEntry>();
        try { return JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllText(f), Program.JsonOpts) ?? new(); }
        catch { return new List<IndexEntry>(); }
    }

    public static void SaveIndex(string sessionDir, List<IndexEntry> index)
    {
        File.WriteAllText(Path.Combine(sessionDir, "index.json"), JsonSerializer.Serialize(index, Program.JsonOpts));
    }

    // ------------------------------------------------------------------ Analyse

    public class PartInfo
    {
        public string Name { get; set; }
        public string File { get; set; }
        public string HiFile { get; set; }
        public string Ytd { get; set; }
        public string Category { get; set; }
        public string Label { get; set; }
        public string Bone { get; set; }
        public string ModType { get; set; }
        public List<string> TurnOffBones { get; set; } = new();
        public long Size { get; set; }
        public bool Encrypted { get; set; }
    }

    public class VehicleInfo
    {
        public string Model { get; set; }
        public string DisplayName { get; set; }
        public string GameName { get; set; }
        public string Make { get; set; }
        public string HandlingId { get; set; }
        public string AudioName { get; set; }
        public string Type { get; set; }
        public string TxdName { get; set; }
        public bool HasMeta { get; set; }
        public string File { get; set; }
        public string HiFile { get; set; }
        public long Size { get; set; }
        public bool Encrypted { get; set; }
        public List<string> Ytds { get; set; } = new();
        public List<PartInfo> Parts { get; set; } = new();
        public List<string> Kits { get; set; } = new();
    }

    public static object Analyze(string sessionDir)
    {
        var stream = Path.Combine(sessionDir, "stream");
        var files = Directory.Exists(stream) ? Directory.GetFiles(stream).Select(Path.GetFileName).ToList() : new List<string>();
        var yfts = files.Where(f => f.EndsWith(".yft")).Select(f => f[..^4]).ToHashSet();
        var ytds = files.Where(f => f.EndsWith(".ytd")).Select(f => f[..^4]).ToHashSet();
        var index = LoadIndex(sessionDir);
        var warnings = new List<string>();

        var meta = MetaData.Read(sessionDir, warnings);

        // Fahrzeuge: aus vehicles.meta, sonst (Replace) alle YFT mit gleichnamiger YTD bzw. ohne Teil-Muster
        var vehicles = new List<VehicleInfo>();
        foreach (var v in meta.Vehicles)
        {
            if (vehicles.Any(x => x.Model == v.Model)) continue;
            vehicles.Add(v);
        }
        var allPartNames = meta.KitParts.Keys.ToHashSet();
        if (vehicles.Count == 0)
        {
            foreach (var y in yfts.Where(y => !y.EndsWith("_hi")).OrderBy(y => y))
            {
                if (allPartNames.Contains(y)) continue;
                bool looksLikePart = yfts.Any(o => o != y && !o.EndsWith("_hi") && y.StartsWith(o + "_"));
                if (looksLikePart) continue;
                if (ytds.Contains(y) || ytds.Contains(y + "+hi") || !y.Contains('_') || yfts.Contains(y + "_hi"))
                    vehicles.Add(new VehicleInfo { Model = y, TxdName = y, HasMeta = false });
            }
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vehicles)
        {
            if (yfts.Contains(v.Model)) { v.File = $"stream/{v.Model}.yft"; used.Add(v.File); }
            if (yfts.Contains(v.Model + "_hi")) { v.HiFile = $"stream/{v.Model}_hi.yft"; used.Add(v.HiFile); }
            v.Size = Size(sessionDir, v.File) + Size(sessionDir, v.HiFile);
            v.Encrypted = IsEncrypted(sessionDir, v.File);
            foreach (var t in new[] { v.Model, v.TxdName, v.Model + "+hi", v.TxdName + "+hi" }.Where(t => !string.IsNullOrEmpty(t)).Distinct())
                if (ytds.Contains(t)) AddYtd(v, $"stream/{t}.ytd", used);
            foreach (var parent in meta.TxdParents(v.TxdName ?? v.Model))
                if (ytds.Contains(parent)) AddYtd(v, $"stream/{parent}.ytd", used);
            if (string.IsNullOrEmpty(v.DisplayName)) v.DisplayName = v.Model;
        }

        // Teile zuordnen: 1) über Modkits (carvariations → carcols), 2) über Namensschema modell_*
        var parts = yfts.Where(y => !y.EndsWith("_hi") || !yfts.Contains(y[..^3]))
                        .Select(y => y.EndsWith("_hi") ? y : y)
                        .Where(y => !used.Contains($"stream/{y}.yft") && !y.EndsWith("_hi"))
                        .OrderBy(y => y).ToList();
        var unassignedModels = new List<PartInfo>();
        foreach (var p in parts)
        {
            VehicleInfo owner = null;
            string modType = null, label = null, bone = null;
            if (meta.KitParts.TryGetValue(p, out var kp))
            {
                modType = kp.ModType; label = kp.Label; bone = kp.Bone;
                owner = vehicles.FirstOrDefault(v => v.Kits.Contains(kp.Kit, StringComparer.OrdinalIgnoreCase));
            }
            owner ??= vehicles.Where(v => p.StartsWith(v.Model + "_", StringComparison.OrdinalIgnoreCase))
                              .OrderByDescending(v => v.Model.Length).FirstOrDefault();
            var info = new PartInfo
            {
                Name = p,
                File = $"stream/{p}.yft",
                HiFile = yfts.Contains(p + "_hi") ? $"stream/{p}_hi.yft" : null,
                Category = Categorize(p, modType, index),
                Label = label,
                Bone = bone,
                ModType = modType,
                TurnOffBones = kp?.TurnOffBones ?? new List<string>(),
            };
            info.Size = Size(sessionDir, info.File) + Size(sessionDir, info.HiFile);
            info.Encrypted = IsEncrypted(sessionDir, info.File);
            used.Add(info.File); if (info.HiFile != null) used.Add(info.HiFile);
            if (ytds.Contains(p)) { info.Ytd = $"stream/{p}.ytd"; used.Add(info.Ytd); }
            if (owner != null) owner.Parts.Add(info); else unassignedModels.Add(info);
        }

        var otherFiles = files.Select(f => $"stream/{f}").Where(f => !used.Contains(f))
            .Select(f => new { file = f, size = Size(sessionDir, f), encrypted = IsEncrypted(sessionDir, f) })
            .Where(x => !x.file.EndsWith(".yft") || !unassignedModels.Any(u => u.File == x.file || u.HiFile == x.file))
            .ToList();

        foreach (var v in vehicles)
        {
            if (v.File == null) warnings.Add($"Für {v.Model} fehlt die Modelldatei {v.Model}.yft.");
            if (v.Encrypted) warnings.Add($"{v.Model}.yft ist verschlüsselt (FiveM-Escrow) und kann nicht bearbeitet werden.");
        }
        foreach (var f in files)
        {
            var len = new FileInfo(Path.Combine(stream, f)).Length;
            if (len > 16 * 1024 * 1024)
                warnings.Add($"{f} ist {len / 1024 / 1024} MB groß – FiveM warnt ab 16 MB (Texturen verkleinern).");
        }

        var dataFiles = Directory.Exists(Path.Combine(sessionDir, "data"))
            ? Directory.GetFiles(Path.Combine(sessionDir, "data"), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(sessionDir, f).Replace('\\', '/')).OrderBy(f => f).ToList()
            : new List<string>();
        var audioFiles = Directory.Exists(Path.Combine(sessionDir, "audio"))
            ? Directory.GetFiles(Path.Combine(sessionDir, "audio"), "*", SearchOption.AllDirectories).Length : 0;

        return new
        {
            sessionDir,
            sources = index.Select(i => i.Source).Distinct().ToList(),
            vehicles,
            unassignedModels,
            otherFiles,
            dataFiles,
            audioFiles,
            warnings,
            isReplace = meta.Vehicles.Count == 0 && yfts.Count > 0,
            soundOnly = yfts.Count == 0 && audioFiles > 0,
        };
    }

    static void AddYtd(VehicleInfo v, string f, HashSet<string> used)
    {
        if (!v.Ytds.Contains(f)) v.Ytds.Add(f);
        used.Add(f);
    }

    static readonly Dictionary<string, string> ModTypeCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VMT_SPOILER"] = "Spoiler", ["VMT_BUMPER_F"] = "Stoßstange vorne", ["VMT_BUMPER_R"] = "Stoßstange hinten",
        ["VMT_SKIRT"] = "Seitenschweller", ["VMT_EXHAUST"] = "Auspuff", ["VMT_CHASSIS"] = "Überrollkäfig / Chassis",
        ["VMT_GRILL"] = "Kühlergrill", ["VMT_BONNET"] = "Motorhaube", ["VMT_WING_L"] = "Kotflügel links",
        ["VMT_WING_R"] = "Kotflügel rechts", ["VMT_ROOF"] = "Dach", ["VMT_PLTHOLDER"] = "Kennzeichenhalter",
        ["VMT_PLTVANITY"] = "Zierkennzeichen", ["VMT_INTERIOR1"] = "Innenraum", ["VMT_INTERIOR2"] = "Innenraum",
        ["VMT_INTERIOR3"] = "Innenraum", ["VMT_INTERIOR4"] = "Innenraum", ["VMT_INTERIOR5"] = "Innenraum",
        ["VMT_SEATS"] = "Sitze", ["VMT_STEERING"] = "Lenkrad", ["VMT_KNOB"] = "Schaltknauf", ["VMT_PLAQUE"] = "Plakette",
        ["VMT_ICE"] = "Sound / ICE", ["VMT_TRUNK"] = "Kofferraum", ["VMT_HYDRO"] = "Hydraulik",
        ["VMT_ENGINEBAY1"] = "Motorraum", ["VMT_ENGINEBAY2"] = "Motorraum", ["VMT_ENGINEBAY3"] = "Motorraum",
        ["VMT_CHASSIS2"] = "Chassis", ["VMT_CHASSIS3"] = "Chassis", ["VMT_CHASSIS4"] = "Chassis", ["VMT_CHASSIS5"] = "Chassis",
        ["VMT_DOOR_L"] = "Türen", ["VMT_DOOR_R"] = "Türen", ["VMT_LIVERY_MOD"] = "Lackierung (Livery)",
        ["VMT_LIGHTBAR"] = "Lichtleiste", ["VMT_WHEELS"] = "Felgen", ["VMT_WHEELS_REAR_OR_HYDRAULICS"] = "Felgen",
    };

    static string Categorize(string name, string modType, List<IndexEntry> index)
    {
        if (modType != null && ModTypeCategory.TryGetValue(modType, out var c)) return c;
        var n = name.ToLowerInvariant();
        var origin = index.FirstOrDefault(i => i.File.Equals($"stream/{n}.yft", StringComparison.OrdinalIgnoreCase))?.Origin?.ToLowerInvariant() ?? "";
        if (n.Contains("wheel") || n.Contains("rim") || n.Contains("_whl") || origin.Contains("/wheels") || origin.Contains("rim")) return "Felgen";
        (string key, string cat)[] map =
        {
            ("spoiler", "Spoiler"), ("wing", "Spoiler"), ("bumf", "Stoßstange vorne"), ("bumr", "Stoßstange hinten"),
            ("fbumper", "Stoßstange vorne"), ("rbumper", "Stoßstange hinten"), ("skirt", "Seitenschweller"),
            ("exh", "Auspuff"), ("bonnet", "Motorhaube"), ("hood", "Motorhaube"), ("grill", "Kühlergrill"),
            ("roof", "Dach"), ("cage", "Überrollkäfig / Chassis"), ("chassis", "Überrollkäfig / Chassis"),
            ("seat", "Sitze"), ("steer", "Lenkrad"), ("mirror", "Spiegel"), ("fender", "Kotflügel"),
            ("splitter", "Stoßstange vorne"), ("diffuser", "Stoßstange hinten"), ("livery", "Lackierung (Livery)"),
        };
        foreach (var (key, cat) in map) if (n.Contains(key)) return cat;
        return "Sonstige Teile";
    }

    public static long Size(string sessionDir, string rel)
    {
        if (rel == null) return 0;
        var f = Path.Combine(sessionDir, rel);
        return File.Exists(f) ? new FileInfo(f).Length : 0;
    }

    public static bool IsEncrypted(string sessionDir, string rel)
    {
        if (rel == null) return false;
        var f = Path.Combine(sessionDir, rel);
        if (!File.Exists(f)) return false;
        using var fs = File.OpenRead(f);
        var b = new byte[4];
        if (fs.Read(b, 0, 4) < 4) return true;
        return !(b[0] == 'R' && b[1] == 'S' && b[2] == 'C' && b[3] == '7');
    }

    // ------------------------------------------------------------------ Einzeldateien

    public static object ExportFile(string file, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, Path.GetFileName(file));
        File.Copy(file, target, true);
        return new { file = target };
    }

    public static object ReplaceFile(string file, string source, string backupDir)
    {
        if (!File.Exists(source)) throw new UserError("Die gewählte Datei existiert nicht.");
        if (!Path.GetExtension(source).Equals(Path.GetExtension(file), StringComparison.OrdinalIgnoreCase))
            throw new UserError($"Bitte eine {Path.GetExtension(file)}-Datei wählen.");
        var head = new byte[4];
        using (var fs = File.OpenRead(source)) fs.Read(head, 0, 4);
        if (Encoding.ASCII.GetString(head) != "RSC7")
            throw new UserError("Die gewählte Datei ist keine gültige GTA-Ressource (verschlüsselt oder beschädigt).");
        Backup(file, backupDir);
        File.Copy(source, file, true);
        return new { ok = true };
    }

    public static void Backup(string file, string backupDir)
    {
        if (string.IsNullOrEmpty(backupDir) || !File.Exists(file)) return;
        Directory.CreateDirectory(backupDir);
        var target = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(file)}");
        File.Copy(file, target, true);
    }
}
