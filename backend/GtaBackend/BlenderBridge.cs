using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace GtaBackend;

/// <summary>
/// Bereitet Modelle fÃ¼r Blender/Sollumz vor und Ã¼bernimmt das Ergebnis zurÃ¼ck in die Sitzung.
/// Sollumz sucht externe Texturen als DDS im Ordner &lt;modellordner&gt;/&lt;modellname&gt;/.
/// </summary>
public static class BlenderBridge
{
    public static object Prepare(string sessionDir, string model, List<string> ytds)
    {
        model = model.ToLowerInvariant();
        var stream = Path.Combine(sessionDir, "stream");
        var src = Path.Combine(stream, model + ".yft");
        if (!File.Exists(src)) throw new UserError($"{model}.yft wurde nicht gefunden.");
        if (Session.IsEncrypted(sessionDir, $"stream/{model}.yft"))
            throw new UserError($"{model}.yft ist verschlÃ¼sselt (FiveM-Escrow) und kann nicht bearbeitet werden.");

        var work = Path.Combine(sessionDir, "blender", model);
        Textures.TryDelete(work);
        Directory.CreateDirectory(work);
        var exportDir = Path.Combine(sessionDir, "blender", model + "_export");
        Textures.TryDelete(exportDir);
        Directory.CreateDirectory(exportDir);

        File.Copy(src, Path.Combine(work, model + ".yft"), true);
        var hi = Path.Combine(stream, model + "_hi.yft");
        if (File.Exists(hi)) File.Copy(hi, Path.Combine(work, model + "_hi.yft"), true);

        // Texturen als DDS daneben legen
        var texDir = Path.Combine(work, model);
        Directory.CreateDirectory(texDir);
        int count = 0;
        foreach (var rel in ytds)
        {
            var f = Path.Combine(sessionDir, rel);
            if (!File.Exists(f) || Session.IsEncrypted(sessionDir, rel)) continue;
            Program.Progress($"Texturen aus {Path.GetFileName(f)} werden fÃ¼r Blender bereitgestellt â€¦");
            try
            {
                var ytd = new YtdFile();
                ytd.Load(File.ReadAllBytes(f));
                foreach (var t in ytd.TextureDict?.Textures?.data_items ?? Array.Empty<Texture>())
                {
                    if (t == null) continue;
                    var target = Path.Combine(texDir, Textures.Safe(t.Name) + ".dds");
                    if (File.Exists(target)) continue; // erste YTD (Fahrzeug) gewinnt
                    File.WriteAllBytes(target, DDSIO.GetDDSFile(t));
                    count++;
                }
            }
            catch (Exception ex) { Program.Log($"{rel}: {ex.Message}"); }
        }

        return new
        {
            workDir = work,
            yft = Path.Combine(work, model + ".yft"),
            exportDir,
            textures = count,
        };
    }

    /// <summary>Beliebige Einzeldatei (.ydr/.ybn …) für Blender vorbereiten; Texturen aus YTDs als DDS daneben.</summary>
    public static object PrepareFile(string sessionDir, string rel, string workDir, List<string> ytds)
    {
        var src = Path.Combine(sessionDir, rel);
        if (!File.Exists(src)) throw new UserError("Datei nicht gefunden.");
        if (Session.IsEncrypted(sessionDir, rel)) throw new UserError("Die Datei ist verschlüsselt (Escrow) und kann nicht bearbeitet werden.");
        Textures.TryDelete(workDir);
        Directory.CreateDirectory(workDir);
        var name = Path.GetFileNameWithoutExtension(src).ToLowerInvariant();
        var file = Path.Combine(workDir, name + Path.GetExtension(src).ToLowerInvariant());
        File.Copy(src, file, true);
        var texDir = Path.Combine(workDir, name);
        Directory.CreateDirectory(texDir);
        int count = 0;
        foreach (var y in ytds ?? new List<string>())
        {
            var p = Path.Combine(sessionDir, y);
            if (!File.Exists(p)) continue;
            try
            {
                var ytd = new YtdFile();
                ytd.Load(File.ReadAllBytes(p));
                foreach (var t in ytd.TextureDict?.Textures?.data_items ?? Array.Empty<Texture>())
                {
                    if (t == null) continue;
                    File.WriteAllBytes(Path.Combine(texDir, Textures.Safe(t.Name) + ".dds"), DDSIO.GetDDSFile(t));
                    count++;
                }
            }
            catch (Exception ex) { Program.Log($"{y}: {ex.Message}"); }
        }
        return new { file, name, textures = count };
    }

    public static object ApplyFile(string sessionDir, string rel, string exportDir, string backupDir)
    {
        var target = Path.Combine(sessionDir, rel);
        var name = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        var ext = Path.GetExtension(target).ToLowerInvariant();
        var f = Directory.Exists(exportDir) ? Directory.GetFiles(exportDir, "*" + ext, SearchOption.AllDirectories)
            .FirstOrDefault(x => Path.GetFileNameWithoutExtension(x).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        if (f == null) throw new UserError($"Blender hat keine {name}{ext} exportiert.");
        var head = new byte[4];
        using (var fs = File.OpenRead(f)) fs.ReadExactly(head);
        if (head[0] != 'R' || head[1] != 'S' || head[2] != 'C' || head[3] != '7') throw new UserError("Die Datei aus Blender ist ungültig.");
        Session.Backup(target, backupDir);
        File.Copy(f, target, true);
        try { File.Delete(f); } catch { }
        return new { applied = Path.GetFileName(target) };
    }

    public static object Apply(string sessionDir, string exportDir, string model, string backupDir)
    {
        model = model.ToLowerInvariant();
        if (!Directory.Exists(exportDir)) throw new UserError("Es wurde noch nichts aus Blender exportiert.");
        var files = Directory.GetFiles(exportDir, "*.yft", SearchOption.AllDirectories);
        var main = files.FirstOrDefault(f => Path.GetFileName(f).Equals(model + ".yft", StringComparison.OrdinalIgnoreCase));
        if (main == null)
        {
            var names = string.Join(", ", files.Select(Path.GetFileName));
            throw new UserError(files.Length == 0
                ? "Blender hat keine .yft exportiert."
                : $"Blender hat {names} exportiert, erwartet wurde {model}.yft. Bitte das Fragment in Blender wieder â€ž{model}â€œ nennen.");
        }
        var wanted = new[] { model + ".yft", model + "_hi.yft" };
        var applied = new List<string>();
        foreach (var f in files.Where(f => wanted.Contains(Path.GetFileName(f).ToLowerInvariant())))
        {
            var head = new byte[4];
            using (var fs = File.OpenRead(f)) fs.Read(head, 0, 4);
            if (head[0] != 'R' || head[1] != 'S' || head[2] != 'C' || head[3] != '7')
                throw new UserError($"{Path.GetFileName(f)} aus Blender ist ungÃ¼ltig (keine RSC7-Signatur).");
            var target = Path.Combine(sessionDir, "stream", Path.GetFileName(f).ToLowerInvariant());
            Session.Backup(target, backupDir);
            File.Copy(f, target, true);
            applied.Add(Path.GetFileName(target));
        }
        // Export-Ordner leeren, damit ein zweiter Durchlauf eindeutig ist
        foreach (var f in files) { try { File.Delete(f); } catch { } }
        return new { applied };
    }
}
