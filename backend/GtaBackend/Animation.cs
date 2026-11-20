using System.Globalization;
using System.Text;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Animationen (.ycd = Clip-Dictionary):
///  – Erkennen (stream\*.ycd, auch .ycd.xml aus CodeWalker/Sollumz)
///  – Abtasten für die 3D-Vorschau (Bone-Tags, Position/Rotation, Laufweg)
///  – Bearbeiten über den CodeWalker-XML-Rundweg (Umbenennen, Duplizieren, Kopieren, Löschen, Tempo, Zuschneiden)
///  – Blender-Rundweg (Sollumz kann YCD nur als XML) und FiveM-Export
/// </summary>
public static class Animations
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Bekannte Ped-Bones (Tags) – zur Erkennung von Personen-Animationen
    static readonly HashSet<ushort> PedTags = new() { 11816, 23553, 24816, 24817, 24818, 31086, 58271, 51826, 18905, 57005, 39317 };

    // ------------------------------------------------------------------ Laden / Speichern

    public static YcdFile Load(string file)
    {
        if (!File.Exists(file)) throw new UserError($"Datei nicht gefunden: {Path.GetFileName(file)}");
        var data = File.ReadAllBytes(file);
        if (data.Length < 16 || data[0] != 'R' || data[1] != 'S' || data[2] != 'C' || data[3] != '7')
            throw new UserError($"{Path.GetFileName(file)} ist verschlüsselt (Escrow) oder beschädigt.");
        var ycd = new YcdFile();
        RpfFile.LoadResourceFile(ycd, data, 46);
        if (ycd.ClipDictionary == null) throw new UserError($"{Path.GetFileName(file)} konnte nicht gelesen werden.");
        ycd.Name = Path.GetFileName(file);
        return ycd;
    }

    static string ToXml(string file) => YcdXml.GetXml(Load(file));

    static byte[] FromXml(string xml)
    {
        var y = XmlYcd.GetYcd(xml);
        var data = y.Save();
        if (data == null || data.Length < 16) throw new UserError("Die Animation konnte nicht gespeichert werden.");
        return data;
    }

    static void SaveXml(string file, XmlDocument doc, string backupDir)
    {
        var data = FromXml(doc.OuterXml);
        Session.Backup(file, backupDir);
        File.WriteAllBytes(file, data);
    }

    static XmlDocument Doc(string file)
    {
        var doc = new XmlDocument();
        doc.LoadXml(ToXml(file));
        return doc;
    }

    /// <summary>Hash aus einem XML-Hashtext („name“ oder „hash_ABCDEF12“).</summary>
    static uint HashOf(string s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) && uint.TryParse(s[5..], NumberStyles.HexNumber, Inv, out var h)) return h;
        return JenkHash.GenHash(s.ToLowerInvariant());
    }

    static string ShortName(string name)
    {
        var n = (name ?? "").Replace('\\', '/');
        var i = n.LastIndexOf('/');
        if (i >= 0) n = n[(i + 1)..];
        if (n.EndsWith(".clip", StringComparison.OrdinalIgnoreCase)) n = n[..^5];
        return n;
    }

    static string CleanName(string s, string what)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) throw new UserError($"Bitte einen Namen für {what} eingeben.");
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '@' || c == '.'))
                throw new UserError($"Der Name „{s}“ enthält ungültige Zeichen. Erlaubt: a–z, 0–9, _ - @ .");
        return s;
    }

    static IEnumerable<XmlElement> Items(XmlDocument doc, string list) =>
        doc.DocumentElement.SelectSingleNode(list)?.SelectNodes("Item")?.Cast<XmlElement>() ?? Enumerable.Empty<XmlElement>();

    static XmlElement FindClip(XmlDocument doc, uint hash) =>
        Items(doc, "Clips").FirstOrDefault(x => HashOf(x["Hash"]?.InnerText) == hash)
        ?? throw new UserError("Der Clip wurde in der Datei nicht gefunden.");

    static List<uint> ClipAnimHashes(XmlElement clip)
    {
        var res = new List<uint>();
        if (clip["AnimationHash"] != null) res.Add(HashOf(clip["AnimationHash"].InnerText));
        foreach (XmlElement a in clip.SelectNodes("Animations/Item"))
            if (a["AnimationHash"] != null) res.Add(HashOf(a["AnimationHash"].InnerText));
        return res;
    }

    static void SetValue(XmlElement parent, string tag, string value)
    {
        var e = parent[tag];
        if (e == null) { e = parent.OwnerDocument.CreateElement(tag); parent.AppendChild(e); }
        e.SetAttribute("value", value);
    }

    static float GetValue(XmlElement parent, string tag, float def)
    {
        var v = parent[tag]?.GetAttribute("value");
        return float.TryParse(v, NumberStyles.Float, Inv, out var f) ? f : def;
    }

    static string F(float f) => f.ToString("0.######", Inv);

    // ------------------------------------------------------------------ Analyse

    public static object Analyze(string sessionDir)
    {
        var warnings = new List<string>();
        var stream = Path.Combine(sessionDir, "stream");
        Directory.CreateDirectory(stream);

        // CodeWalker-/Sollumz-XML → YCD
        var dataDir = Path.Combine(sessionDir, "data");
        if (Directory.Exists(dataDir))
        {
            foreach (var x in Directory.GetFiles(dataDir, "*.ycd.xml", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(x)[..^".ycd.xml".Length].ToLowerInvariant();
                try
                {
                    File.WriteAllBytes(Path.Combine(stream, name + ".ycd"), FromXml(File.ReadAllText(x)));
                    File.Delete(x);
                }
                catch (Exception ex) { warnings.Add($"{Path.GetFileName(x)}: XML konnte nicht umgewandelt werden ({ex.Message})"); }
            }
        }

        var dicts = new List<object>();
        int clipCount = 0;
        foreach (var f in Directory.GetFiles(stream, "*.ycd").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var rel = "stream/" + Path.GetFileName(f);
            var name = Path.GetFileNameWithoutExtension(f);
            var size = new FileInfo(f).Length;
            if (Session.IsEncrypted(sessionDir, rel))
            {
                dicts.Add(new { file = rel, name, size, encrypted = true, clips = new List<object>(), animations = 0 });
                continue;
            }
            try
            {
                var ycd = Load(f);
                var clips = new List<object>();
                foreach (var cme in ycd.ClipMapEntries.Where(c => c?.Clip != null).OrderBy(c => ShortName(c.Clip.Name), StringComparer.OrdinalIgnoreCase))
                {
                    clips.Add(ClipInfo(cme.Clip));
                    clipCount++;
                }
                dicts.Add(new { file = rel, name, size, encrypted = false, clips, animations = ycd.AnimMapEntries.Length });
                if (size > 16 * 1024 * 1024) warnings.Add($"{name}.ycd ist größer als 16 MB – FiveM lädt solche Dateien oft nicht zuverlässig.");
            }
            catch (Exception ex)
            {
                warnings.Add($"{name}.ycd: {ex.Message}");
                dicts.Add(new { file = rel, name, size, encrypted = false, broken = true, clips = new List<object>(), animations = 0 });
            }
        }
        return new { dicts, clipCount, warnings };
    }

    static List<(CodeWalker.GameFiles.Animation anim, float start, float end, float rate)> Parts(ClipBase clip)
    {
        var list = new List<(CodeWalker.GameFiles.Animation, float, float, float)>();
        if (clip is ClipAnimation ca && ca.Animation != null) list.Add((ca.Animation, ca.StartTime, ca.EndTime, ca.Rate <= 0 ? 1f : ca.Rate));
        if (clip is ClipAnimationList cl && cl.Animations?.Data != null)
            foreach (var e in cl.Animations.Data)
                if (e?.Animation != null) list.Add((e.Animation, e.StartTime, e.EndTime, e.Rate <= 0 ? 1f : e.Rate));
        return list;
    }

    static float ClipDuration(ClipBase clip)
    {
        if (clip is ClipAnimationList cl) return cl.Duration;
        if (clip is ClipAnimation ca) return Math.Max(0.001f, (ca.EndTime - ca.StartTime) / (ca.Rate <= 0 ? 1f : ca.Rate));
        return 0;
    }

    static object ClipInfo(ClipBase clip)
    {
        var parts = Parts(clip);
        var tracks = parts.SelectMany(p => p.anim.BoneIds?.data_items ?? Array.Empty<AnimationBoneId>()).ToList();
        var bones = tracks.Where(t => t.Track <= 2).Select(t => t.BoneId).Distinct().ToList();
        string kind;
        if (tracks.Any(t => t.Track == 17 || t.Track == 18)) kind = "uv";
        else if (tracks.Any(t => t.Track == 7 || t.Track == 8)) kind = "camera";
        else if (bones.Any(PedTags.Contains)) kind = "ped";
        else if (tracks.Any(t => t.Track >= 24 && t.Track <= 26)) kind = "face";
        else kind = "object";
        var ca = clip as ClipAnimation;
        var anim = parts.FirstOrDefault().anim;
        return new
        {
            id = clip.Hash.Hash.ToString(Inv),
            name = ShortName(clip.Name),
            fullName = clip.Name,
            type = clip is ClipAnimationList ? "list" : "single",
            duration = ClipDuration(clip),
            rate = ca?.Rate ?? 1f,
            start = ca?.StartTime ?? 0f,
            end = ca?.EndTime ?? 0f,
            animDuration = anim?.Duration ?? 0f,
            frames = anim?.Frames ?? 0,
            animations = parts.Count,
            bones = bones.Count,
            mover = tracks.Any(t => t.Track == 5 || t.Track == 6),
            kind,
            tags = (int)(clip.Tags?.TagCount1 ?? 0),
        };
    }

    // ------------------------------------------------------------------ Abtasten für die 3D-Vorschau

    static (int seq, int local) SeqFrame(CodeWalker.GameFiles.Animation a, int g)
    {
        int limit = Math.Max(1, (int)a.SequenceFrameLimit);
        int s = g / limit, l = g % limit;
        int count = a.Sequences?.data_items?.Length ?? 0;
        if (s >= count) { s = count - 1; l = g - s * limit; }
        return (Math.Max(0, s), l);
    }

    static Vector4 EvalVec(CodeWalker.GameFiles.Animation a, int bi, int g)
    {
        var (s, l) = SeqFrame(a, g);
        var seq = a.Sequences.data_items[s]?.Sequences;
        if (seq == null || bi >= seq.Length) return Vector4.Zero;
        return seq[bi].EvaluateVector(l);
    }

    static Quaternion EvalQuat(CodeWalker.GameFiles.Animation a, int bi, int g)
    {
        var (s, l) = SeqFrame(a, g);
        var seq = a.Sequences.data_items[s]?.Sequences;
        if (seq == null || bi >= seq.Length) return Quaternion.Identity;
        var q = seq[bi].EvaluateQuaternion(l);
        if (!float.IsFinite(q.X + q.Y + q.Z + q.W) || q.LengthSquared() < 1e-8f) return Quaternion.Identity;
        return Quaternion.Normalize(q);
    }

    /// <summary>Clip mit fester Bildrate abtasten. Ergebnis: Spuren je Bone-Tag (Float32 als Base64).</summary>
    public static object Sample(string file, string clipId, int fps)
    {
        var ycd = Load(file);
        if (!uint.TryParse(clipId, NumberStyles.Integer, Inv, out var hash)) throw new UserError("Ungültiger Clip.");
        var cme = ycd.ClipMapEntries.FirstOrDefault(c => c?.Clip?.Hash.Hash == hash) ?? throw new UserError("Der Clip wurde nicht gefunden.");
        var clip = cme.Clip;
        fps = Math.Clamp(fps, 10, 60);
        float duration = ClipDuration(clip);
        int n = Math.Max(2, (int)Math.Round(duration * fps) + 1);
        n = Math.Min(n, 30 * 60 * 10);

        var tracks = new Dictionary<(ushort bone, string type), float[]>();
        foreach (var (anim, start, end, rate) in Parts(clip))
        {
            var ids = anim.BoneIds?.data_items;
            if (ids == null || anim.Sequences?.data_items == null || anim.Frames == 0) continue;
            int frames = anim.Frames;
            float adur = anim.Duration > 0 ? anim.Duration : (frames - 1) / 30f;
            for (int i = 0; i < ids.Length; i++)
            {
                var id = ids[i];
                string type = id.Track switch { 0 => "t", 1 => "r", 5 => "mt", 6 => "mr", _ => null };
                if (type == null) continue;
                bool quat = type == "r" || type == "mr";
                int comps = quat ? 4 : 3;
                var data = new float[n * comps];
                for (int k = 0; k < n; k++)
                {
                    float t = duration * k / (n - 1);
                    float at = Math.Clamp(start + t * rate, 0, adur);
                    float fpos = frames > 1 ? at / adur * (frames - 1) : 0;
                    int f0 = Math.Clamp((int)Math.Floor(fpos), 0, frames - 1);
                    int f1 = Math.Min(f0 + 1, frames - 1);
                    float alpha = Math.Clamp(fpos - f0, 0, 1);
                    if (quat)
                    {
                        var q0 = EvalQuat(anim, i, f0);
                        var q1 = EvalQuat(anim, i, f1);
                        var q = alpha > 0 ? Quaternion.Slerp(q0, q1, alpha) : q0;
                        q = Quaternion.Normalize(q);
                        data[k * 4] = q.X; data[k * 4 + 1] = q.Y; data[k * 4 + 2] = q.Z; data[k * 4 + 3] = q.W;
                    }
                    else
                    {
                        var v0 = EvalVec(anim, i, f0);
                        var v1 = EvalVec(anim, i, f1);
                        var v = v0 * (1 - alpha) + v1 * alpha;
                        data[k * 3] = Fin(v.X); data[k * 3 + 1] = Fin(v.Y); data[k * 3 + 2] = Fin(v.Z);
                    }
                }
                // Quaternionen ohne Vorzeichensprünge (sonst dreht three.js beim Interpolieren „außen herum“)
                if (quat)
                    for (int k = 1; k < n; k++)
                    {
                        float dot = 0;
                        for (int c = 0; c < 4; c++) dot += data[k * 4 + c] * data[(k - 1) * 4 + c];
                        if (dot < 0) for (int c = 0; c < 4; c++) data[k * 4 + c] = -data[k * 4 + c];
                    }
                tracks[(id.BoneId, type)] = data;
            }
        }
        // Wie CodeWalker: Oberschenkel-Rollbones folgen dem Oberschenkel
        foreach (var (roll, thigh) in new (ushort, ushort)[] { (23639, 58271), (6442, 51826) })
            if (!tracks.ContainsKey((roll, "r")) && tracks.TryGetValue((thigh, "r"), out var td)) tracks[(roll, "r")] = (float[])td.Clone();

        return new
        {
            name = ShortName(clip.Name),
            duration,
            fps,
            frames = n,
            tracks = tracks.Select(kv => new { bone = kv.Key.bone, type = kv.Key.type, data = B64(kv.Value) }).ToList(),
        };
    }

    static float Fin(float f) => float.IsFinite(f) && Math.Abs(f) < 1e5f ? f : 0f;

    static string B64(float[] f)
    {
        var b = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return Convert.ToBase64String(b);
    }

    // ------------------------------------------------------------------ Bearbeiten

    static object Result(string sessionDir, object extra = null) => new { model = Analyze(sessionDir), extra };

    public static object RenameClip(string sessionDir, string rel, string clipId, string newName, string backupDir)
    {
        var file = Path.Combine(sessionDir, rel);
        newName = CleanName(newName, "den Clip");
        var doc = Doc(file);
        var clip = FindClip(doc, uint.Parse(clipId, Inv));
        var newHash = JenkHash.GenHash(newName);
        if (Items(doc, "Clips").Any(x => x != clip && HashOf(x["Hash"]?.InnerText) == newHash))
            throw new UserError($"Es gibt in dieser Datei schon einen Clip „{newName}“.");
        clip["Hash"].InnerText = newName;
        clip["Name"].InnerText = "pack:/" + newName + ".clip";
        SaveXml(file, doc, backupDir);
        return Result(sessionDir, new { id = newHash.ToString(Inv) });
    }

    public static object DuplicateClip(string sessionDir, string rel, string clipId, string newName, string backupDir)
    {
        var file = Path.Combine(sessionDir, rel);
        newName = CleanName(newName, "den neuen Clip");
        var doc = Doc(file);
        var clip = FindClip(doc, uint.Parse(clipId, Inv));
        var newHash = JenkHash.GenHash(newName);
        if (Items(doc, "Clips").Any(x => HashOf(x["Hash"]?.InnerText) == newHash))
            throw new UserError($"Es gibt in dieser Datei schon einen Clip „{newName}“.");
        var copy = (XmlElement)clip.CloneNode(true);
        copy["Hash"].InnerText = newName;
        copy["Name"].InnerText = "pack:/" + newName + ".clip";
        clip.ParentNode.AppendChild(copy);
        SaveXml(file, doc, backupDir);
        return Result(sessionDir, new { id = newHash.ToString(Inv) });
    }

    public static object DeleteClip(string sessionDir, string rel, string clipId, string backupDir)
    {
        var file = Path.Combine(sessionDir, rel);
        var doc = Doc(file);
        var clip = FindClip(doc, uint.Parse(clipId, Inv));
        var anims = ClipAnimHashes(clip);
        clip.ParentNode.RemoveChild(clip);
        // Nicht mehr benutzte Animationen mit entfernen
        var used = Items(doc, "Clips").SelectMany(ClipAnimHashes).ToHashSet();
        foreach (var a in Items(doc, "Animations").ToList())
            if (anims.Contains(HashOf(a["Hash"]?.InnerText)) && !used.Contains(HashOf(a["Hash"]?.InnerText))) a.ParentNode.RemoveChild(a);
        if (!Items(doc, "Clips").Any())
        {
            Session.Backup(file, backupDir);
            File.Delete(file);
            return Result(sessionDir, new { deletedFile = true });
        }
        SaveXml(file, doc, backupDir);
        return Result(sessionDir);
    }

    /// <summary>Tempo (Rate) und Ausschnitt (Start/Ende in Sekunden der Animation) eines Clips ändern.</summary>
    public static object SetTiming(string sessionDir, string rel, string clipId, float rate, float start, float end, string backupDir)
    {
        var file = Path.Combine(sessionDir, rel);
        var doc = Doc(file);
        var clip = FindClip(doc, uint.Parse(clipId, Inv));
        if (clip["AnimationHash"] == null) throw new UserError("Bei Clips aus mehreren Animationen kann das Tempo hier nicht geändert werden.");
        var ah = HashOf(clip["AnimationHash"].InnerText);
        var anim = Items(doc, "Animations").FirstOrDefault(a => HashOf(a["Hash"]?.InnerText) == ah) ?? throw new UserError("Die Animation zum Clip fehlt.");
        float adur = GetValue(anim, "Duration", 0);
        if (rate < 0.05f || rate > 20f) throw new UserError("Das Tempo muss zwischen 5 % und 2000 % liegen.");
        start = Math.Clamp(start, 0, adur);
        end = Math.Clamp(end, 0, adur);
        if (end - start < 0.02f) throw new UserError("Der Ausschnitt ist zu kurz.");
        SetValue(clip, "StartTime", F(start));
        SetValue(clip, "EndTime", F(end));
        SetValue(clip, "Rate", F(rate));
        SaveXml(file, doc, backupDir);
        return Result(sessionDir);
    }

    /// <summary>Clip (mit seinen Animationen) in eine andere YCD kopieren.</summary>
    public static object CopyClip(string sessionDir, string rel, string clipId, string targetRel, string newName, string backupDir)
    {
        var src = Path.Combine(sessionDir, rel);
        var dst = Path.Combine(sessionDir, targetRel);
        if (Path.GetFullPath(src).Equals(Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) throw new UserError("Bitte eine andere Datei als Ziel wählen (oder „Duplizieren“ benutzen).");
        var sdoc = Doc(src);
        var clip = FindClip(sdoc, uint.Parse(clipId, Inv));
        var ddoc = Doc(dst);
        var name = string.IsNullOrWhiteSpace(newName) ? ShortName(clip["Name"]?.InnerText) : CleanName(newName, "den Clip");
        var newHash = JenkHash.GenHash(name);
        if (Items(ddoc, "Clips").Any(x => HashOf(x["Hash"]?.InnerText) == newHash))
            throw new UserError($"Im Ziel gibt es schon einen Clip „{name}“. Bitte einen anderen Namen wählen.");
        var animHashes = ClipAnimHashes(clip);
        var dstAnims = ddoc.DocumentElement.SelectSingleNode("Animations") as XmlElement;
        if (dstAnims == null) { dstAnims = ddoc.CreateElement("Animations"); ddoc.DocumentElement.AppendChild(dstAnims); }
        var dstClips = ddoc.DocumentElement.SelectSingleNode("Clips") as XmlElement;
        if (dstClips == null) { dstClips = ddoc.CreateElement("Clips"); ddoc.DocumentElement.InsertBefore(dstClips, dstAnims); }
        var existing = Items(ddoc, "Animations").Select(a => HashOf(a["Hash"]?.InnerText)).ToHashSet();
        var renamed = new Dictionary<uint, string>();
        foreach (var a in Items(sdoc, "Animations").Where(a => animHashes.Contains(HashOf(a["Hash"]?.InnerText))))
        {
            var imported = (XmlElement)ddoc.ImportNode(a, true);
            var h = HashOf(a["Hash"]?.InnerText);
            if (existing.Contains(h))
            {
                // Gleicher Animationsname im Ziel → eindeutig umbenennen
                var nn = $"{name}_anim";
                int i = 1;
                while (existing.Contains(JenkHash.GenHash(nn))) nn = $"{name}_anim{i++}";
                imported["Hash"].InnerText = nn;
                renamed[h] = nn;
                existing.Add(JenkHash.GenHash(nn));
            }
            dstAnims.AppendChild(imported);
        }
        var c = (XmlElement)ddoc.ImportNode(clip, true);
        c["Hash"].InnerText = name;
        c["Name"].InnerText = "pack:/" + name + ".clip";
        foreach (XmlElement ah in c.SelectNodes(".//AnimationHash"))
            if (renamed.TryGetValue(HashOf(ah.InnerText), out var nn)) ah.InnerText = nn;
        dstClips.AppendChild(c);
        SaveXml(dst, ddoc, backupDir);
        return Result(sessionDir, new { id = newHash.ToString(Inv) });
    }

    public static object RenameDict(string sessionDir, string rel, string newName, string backupDir)
    {
        var file = Path.Combine(sessionDir, rel);
        newName = CleanName(newName, "die Animations-Datei");
        if (newName.EndsWith(".ycd")) newName = newName[..^4];
        var target = Path.Combine(Path.GetDirectoryName(file)!, newName + ".ycd");
        if (File.Exists(target) && !Path.GetFullPath(target).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            throw new UserError($"Es gibt schon eine Datei „{newName}.ycd“.");
        Session.Backup(file, backupDir);
        var tmp = target + ".tmp";
        File.Move(file, tmp, true);
        File.Move(tmp, target, true);
        return Result(sessionDir, new { file = "stream/" + newName + ".ycd" });
    }

    public static object DeleteDict(string sessionDir, string rel, string backupDir)
    {
        var file = Path.Combine(sessionDir, rel);
        Session.Backup(file, backupDir);
        File.Delete(file);
        return Result(sessionDir);
    }

    /// <summary>Neue, leere Animations-Datei aus einem Clip einer anderen Datei („Clip in neue Datei“).</summary>
    public static object NewDictFromClip(string sessionDir, string rel, string clipId, string dictName, string backupDir)
    {
        dictName = CleanName(dictName, "die neue Datei");
        if (dictName.EndsWith(".ycd")) dictName = dictName[..^4];
        var target = Path.Combine(sessionDir, "stream", dictName + ".ycd");
        if (File.Exists(target)) throw new UserError($"Es gibt schon eine Datei „{dictName}.ycd“.");
        var sdoc = Doc(Path.Combine(sessionDir, rel));
        var clip = FindClip(sdoc, uint.Parse(clipId, Inv));
        var animHashes = ClipAnimHashes(clip);
        var doc = new XmlDocument();
        doc.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ClipDictionary><Clips/><Animations/></ClipDictionary>");
        doc.DocumentElement["Clips"].AppendChild(doc.ImportNode(clip, true));
        foreach (var a in Items(sdoc, "Animations").Where(a => animHashes.Contains(HashOf(a["Hash"]?.InnerText))))
            doc.DocumentElement["Animations"].AppendChild(doc.ImportNode(a, true));
        File.WriteAllBytes(target, FromXml(doc.OuterXml));
        return Result(sessionDir, new { file = "stream/" + dictName + ".ycd" });
    }

    // ------------------------------------------------------------------ Einzelexport

    public static object ExportFile(string sessionDir, string rel, string outFile, string format)
    {
        var file = Path.Combine(sessionDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        if (format == "xml") File.WriteAllText(outFile, ToXml(file), new UTF8Encoding(false));
        else File.Copy(file, outFile, true);
        return new { file = outFile };
    }

    // ------------------------------------------------------------------ Blender

    /// <summary>YCD als Sollumz-XML in einen Arbeitsordner legen.</summary>
    public static object PrepareBlender(string sessionDir, string rel, string workDir)
    {
        var file = Path.Combine(sessionDir, rel);
        Textures.TryDelete(workDir);
        Directory.CreateDirectory(workDir);
        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        var xmlFile = Path.Combine(workDir, name + ".ycd.xml");
        File.WriteAllText(xmlFile, ToXml(file), new UTF8Encoding(false));
        return new { xml = xmlFile, name };
    }

    /// <summary>
    /// Ergebnis aus Blender übernehmen: nur der bearbeitete Clip und seine Animation(en) werden ersetzt –
    /// alle anderen Clips der Datei bleiben unverändert (Sollumz kodiert sonst alles neu).
    /// </summary>
    public static object ApplyBlender(string sessionDir, string rel, string exportDir, string clipId, string backupDir)
    {
        var file = Path.Combine(sessionDir, rel);
        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        var x = Directory.Exists(exportDir) ? Directory.GetFiles(exportDir, "*.ycd.xml", SearchOption.AllDirectories)
            .OrderByDescending(f => Path.GetFileName(f).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)).ThenByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        if (x == null) throw new UserError("Blender hat keine Animation exportiert.");
        var ndoc = new XmlDocument();
        ndoc.LoadXml(File.ReadAllText(x));
        try { File.Delete(x); } catch { }
        var doc = Doc(file);
        var changed = new List<string>();
        var targets = string.IsNullOrEmpty(clipId)
            ? Items(ndoc, "Clips").ToList()
            : Items(ndoc, "Clips").Where(c => HashOf(c["Hash"]?.InnerText) == uint.Parse(clipId, Inv)).ToList();
        if (targets.Count == 0) throw new UserError("Der bearbeitete Clip wurde im Blender-Export nicht gefunden (wurde er umbenannt?).");
        foreach (var nc in targets)
        {
            var h = HashOf(nc["Hash"]?.InnerText);
            var old = Items(doc, "Clips").FirstOrDefault(c => HashOf(c["Hash"]?.InnerText) == h);
            var imported = (XmlElement)doc.ImportNode(nc, true);
            // Clip-Name im Original-Stil behalten (pack:/…)
            if (old?["Name"] != null && imported["Name"] != null) imported["Name"].InnerText = old["Name"].InnerText;
            if (old != null) old.ParentNode.ReplaceChild(imported, old);
            else doc.DocumentElement["Clips"].AppendChild(imported);
            foreach (var ah in ClipAnimHashes(nc))
            {
                var na = Items(ndoc, "Animations").FirstOrDefault(a => HashOf(a["Hash"]?.InnerText) == ah);
                if (na == null) continue;
                var oa = Items(doc, "Animations").FirstOrDefault(a => HashOf(a["Hash"]?.InnerText) == ah);
                var ia = doc.ImportNode(na, true);
                if (oa != null) oa.ParentNode.ReplaceChild(ia, oa);
                else doc.DocumentElement["Animations"].AppendChild(ia);
            }
            changed.Add(ShortName(nc["Name"]?.InnerText ?? nc["Hash"]?.InnerText));
        }
        SaveXml(file, doc, backupDir);
        return new { applied = changed };
    }

    // ------------------------------------------------------------------ FiveM-Export

    public static object Export(string sessionDir, string outDir, string resourceName, bool testCommand)
    {
        var safe = Session.SafeName(string.IsNullOrWhiteSpace(resourceName) ? "animationen" : resourceName);
        var root = Path.Combine(outDir, safe);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var stream = Path.Combine(root, "stream");
        Directory.CreateDirectory(stream);
        var warnings = new List<string>();
        var list = new StringBuilder();
        int files = 0, clips = 0;
        foreach (var f in Directory.GetFiles(Path.Combine(sessionDir, "stream"), "*.ycd").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            File.Copy(f, Path.Combine(stream, name + ".ycd"), true);
            files++;
            try
            {
                var ycd = Load(f);
                list.AppendLine($"  {name}");
                foreach (var cme in ycd.ClipMapEntries.Where(c => c?.Clip != null).OrderBy(c => ShortName(c.Clip.Name)))
                {
                    list.AppendLine($"      {ShortName(cme.Clip.Name),-40} {ClipDuration(cme.Clip):0.00} s");
                    clips++;
                }
            }
            catch (Exception ex) { warnings.Add($"{name}.ycd: {ex.Message}"); list.AppendLine($"  {name}  (verschlüsselt/nicht lesbar)"); }
            if (new FileInfo(f).Length > 16 * 1024 * 1024) warnings.Add($"{name}.ycd ist größer als 16 MB.");
        }
        if (files == 0) throw new UserError("Es gibt keine Animationen zum Exportieren.");

        var fx = new StringBuilder();
        fx.AppendLine("fx_version 'cerulean'");
        fx.AppendLine("game 'gta5'");
        fx.AppendLine("lua54 'yes'");
        fx.AppendLine();
        fx.AppendLine("description 'Animationen – erstellt mit dem GTA Tool'");
        fx.AppendLine();
        fx.AppendLine("-- Die .ycd-Dateien im Ordner stream/ werden von FiveM automatisch gestreamt.");
        if (testCommand)
        {
            fx.AppendLine();
            fx.AppendLine("client_script 'client.lua'");
            File.WriteAllText(Path.Combine(root, "client.lua"), TestLua, new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(root, "fxmanifest.lua"), fx.ToString(), new UTF8Encoding(false));

        var readme = new StringBuilder();
        readme.AppendLine($"Resource „{safe}“ – erstellt mit dem GTA Tool am {DateTime.Now:dd.MM.yyyy HH:mm}");
        readme.AppendLine();
        readme.AppendLine("Installation:");
        readme.AppendLine($"  1. Den Ordner „{safe}“ in den resources-Ordner des Servers kopieren.");
        readme.AppendLine($"  2. In der server.cfg eintragen:  ensure {safe}");
        readme.AppendLine();
        readme.AppendLine("Verwendung in eigenen Scripts (Lua):");
        readme.AppendLine("  RequestAnimDict('<datei>')");
        readme.AppendLine("  while not HasAnimDictLoaded('<datei>') do Wait(0) end");
        readme.AppendLine("  TaskPlayAnim(PlayerPedId(), '<datei>', '<clip>', 8.0, -8.0, -1, 1, 0, false, false, false)");
        readme.AppendLine();
        if (testCommand)
        {
            readme.AppendLine("Test im Spiel (F8-Konsole oder Chat):");
            readme.AppendLine("  /animtest <datei> <clip>     spielt die Animation in Schleife ab");
            readme.AppendLine("  /animstop                    beendet sie");
            readme.AppendLine();
        }
        readme.AppendLine("Enthaltene Animationen (<datei> und darunter <clip> mit Länge):");
        readme.Append(list);
        File.WriteAllText(Path.Combine(root, "LIESMICH.txt"), readme.ToString(), new UTF8Encoding(true));
        return new { folder = root, files, clips, warnings };
    }

    const string TestLua = """
-- Test-Befehle (erstellt mit dem GTA Tool)
--   /animtest <datei> <clip>   spielt die Animation in Schleife ab
--   /animstop                  beendet sie

RegisterCommand('animtest', function(_, args)
    local dict, clip = args[1], args[2]
    if not dict or not clip then
        print('Benutzung: /animtest <datei> <clip>')
        return
    end
    RequestAnimDict(dict)
    local timeout = GetGameTimer() + 5000
    while not HasAnimDictLoaded(dict) do
        if GetGameTimer() > timeout then
            print(('Animations-Datei „%s“ wurde nicht gefunden.'):format(dict))
            return
        end
        Wait(10)
    end
    TaskPlayAnim(PlayerPedId(), dict, clip, 8.0, -8.0, -1, 1, 0, false, false, false)
    RemoveAnimDict(dict)
end, false)

RegisterCommand('animstop', function()
    ClearPedTasks(PlayerPedId())
end, false)
""";
}
