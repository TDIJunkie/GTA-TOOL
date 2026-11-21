using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GtaBackend;

/// <summary>
/// Gemeinsamer Export für alle Studios.
/// Wurde eine fertige Resource geöffnet (Ordner mit fxmanifest.lua – vom PC oder per SFTP), bleibt sie erhalten:
/// gleicher Name, gleiche Unterordner und Dateinamen, fxmanifest.lua, Skripte, .fxap usw.
/// Das Studio exportiert wie gewohnt in einen Zwischenordner; dessen Dateien werden an ihren Originalplatz einsortiert.
/// Im Tool gelöschte Dateien fehlen, neue kommen an den Platz, den das Studio vorsieht.
/// </summary>
public static class ExportMerge
{
    /// <summary>Resource-Name so genau wie möglich übernehmen – nur für Ordner unzulässige Zeichen werden ersetzt.</summary>
    public static string ResName(string n)
    {
        var sb = new StringBuilder();
        foreach (var c in (n ?? "").Trim())
            sb.Append(c < 0x21 || c > 0x7E || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        var r = sb.ToString().TrimEnd('.');
        return r.Length > 0 ? r : "resource";
    }

    public static object Run(string sessionDir, string outDir, string requested, Func<string, string, object> export)
    {
        if (string.IsNullOrEmpty(outDir)) throw new UserError("Bitte einen Zielordner wählen.");
        var orig = Session.FindOriginal(sessionDir);
        if (orig == null) return export(outDir, requested);

        var name = ResName(requested);
        var tmp = Path.Combine(Path.GetTempPath(), "GTATool", "export-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tmp);
        try
        {
            var r = export(tmp, requested);
            var node = JsonSerializer.SerializeToNode(r, r.GetType(), Program.JsonOpts) as JsonObject ?? new JsonObject();
            var gen = node["folder"]?.GetValue<string>() ?? Path.Combine(tmp, name);
            var res = Path.Combine(outDir, name);
            if (Directory.Exists(res) && Directory.EnumerateFileSystemEntries(res).Any())
            {
                if (!File.Exists(Path.Combine(res, "fxmanifest.lua")) && !File.Exists(Path.Combine(res, "__resource.lua")))
                    throw new UserError($"Der Ordner {res} existiert schon und ist keine FiveM-Resource. Bitte einen anderen Namen wählen.");
                Directory.Delete(res, true);
            }
            var warnings = new List<string>();
            var (files, added) = Merge(sessionDir, orig, gen, res, warnings);
            node["folder"] = res;
            node["files"] = files;
            node["preserved"] = true;
            node["original"] = orig.Name;
            node["manifestAdded"] = added;
            var w = node["warnings"] as JsonArray ?? new JsonArray();
            foreach (var x in warnings) w.Add(x);
            node["warnings"] = w;
            return node;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* egal */ }
        }
    }

    static string Rel(string root, string f) => Path.GetRelativePath(root, f).Replace('\\', '/');

    /// <summary>Nur vom Studio erzeugte Hilfsdateien – bei einer erhaltenen Resource nicht übernehmen.</summary>
    static bool GeneratedExtra(string rel)
    {
        var n = Path.GetFileName(rel).ToLowerInvariant();
        return n is "fxmanifest.lua" or "__resource.lua" or "liesmich.txt" || n.EndsWith(".lua");
    }

    static (int files, int added) Merge(string sessionDir, Session.OriginalResource orig, string gen, string res, List<string> warnings)
    {
        var index = Session.LoadIndex(sessionDir);
        var prefix = orig.Prefix + "/";
        var ci = StringComparer.OrdinalIgnoreCase;

        // Original-Dateien (relativer Pfad, exakte Schreibweise) → Kopie auf der Platte (null = nur über die Sitzung bekannt)
        var origFiles = new Dictionary<string, string>(ci);
        foreach (var f in Directory.EnumerateFiles(orig.Dir, "*", SearchOption.AllDirectories)) origFiles[Rel(orig.Dir, f)] = f;
        var entryByRel = new Dictionary<string, Session.IndexEntry>(ci);
        var relBySession = new Dictionary<string, string>(ci);
        foreach (var e in index)
        {
            if (e.Origin == null || !e.Origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = e.Origin[prefix.Length..];
            entryByRel[rel] = e;
            relBySession[e.File] = rel;
            if (!origFiles.ContainsKey(rel)) origFiles[rel] = null;
        }
        var byName = origFiles.Keys.GroupBy(k => Path.GetFileName(k), ci).ToDictionary(g => g.Key, g => g.ToList(), ci);
        var nameCount = index.GroupBy(e => Path.GetFileName(e.File), ci).ToDictionary(g => g.Key, g => g.ToList(), ci);

        var output = new Dictionary<string, string>(ci);   // Ziel (relativ) → Quelldatei
        var claimed = new HashSet<string>(ci);              // ersetzte Original-Pfade
        var pathMap = new Dictionary<string, string>(ci);   // Pfad im Studio-Export → Ziel

        foreach (var g in Directory.EnumerateFiles(gen, "*", SearchOption.AllDirectories))
        {
            var grel = Rel(gen, g);
            if (GeneratedExtra(grel)) continue;
            var gname = Path.GetFileName(grel);
            string target = null;

            // 1) Gehört zu einer Sitzungsdatei, die aus der Original-Resource stammt → an deren Platz
            string sess = null;
            if (relBySession.ContainsKey(grel)) sess = grel;
            else if (nameCount.TryGetValue(gname, out var same) && same.Count == 1) sess = same[0].File;
            if (sess != null && relBySession.TryGetValue(sess, out var orel) && !claimed.Contains(orel))
            {
                var on = Path.GetFileName(orel);
                var dir = orel.Contains('/') ? orel[..orel.LastIndexOf('/')] + "/" : "";
                target = dir + (on.Equals(gname, StringComparison.OrdinalIgnoreCase) ? on : gname);
                claimed.Add(orel);
            }
            // 2) Gleicher Dateiname in der Original-Resource (z. B. neu erzeugte .ymt/.ytyp mit Originalnamen)
            else if (byName.TryGetValue(gname, out var cands) && cands.FirstOrDefault(c => !claimed.Contains(c)) is string c2)
            {
                target = c2;
                claimed.Add(c2);
            }
            // 3) Neu
            else target = grel;

            output[target] = g;
            pathMap[grel] = target;
        }

        // Unveränderte Original-Dateien übernehmen
        var importTime = File.Exists(Path.Combine(sessionDir, "index.json"))
            ? File.GetLastWriteTimeUtc(Path.Combine(sessionDir, "index.json")).AddSeconds(2) : DateTime.MaxValue;
        foreach (var (rel, src) in origFiles)
        {
            if (claimed.Contains(rel) || output.ContainsKey(rel)) continue;
            if (entryByRel.TryGetValue(rel, out var e))
            {
                var sf = Path.Combine(sessionDir, e.File);
                if (!File.Exists(sf)) continue; // im Tool gelöscht oder umbenannt
                // im Tool geändert, aber vom Studio nicht exportiert → geänderte Fassung, sonst das Original
                output[rel] = src != null && File.GetLastWriteTimeUtc(sf) <= importTime ? src : sf;
            }
            else if (src != null) output[rel] = src; // Skripte, fxmanifest, .fxap, sonstige Dateien
        }

        int n = 0;
        foreach (var (rel, src) in output)
        {
            var target = Path.Combine(res, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(src, target, true);
            n++;
        }

        // fxmanifest: Original behalten, nur fehlende data_file-/files-Einträge des Studios ergänzen
        int added = 0;
        var genFx = Path.Combine(gen, "fxmanifest.lua");
        var manRel = output.Keys.FirstOrDefault(k => k.Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase))
                     ?? output.Keys.FirstOrDefault(k => k.Equals("__resource.lua", StringComparison.OrdinalIgnoreCase));
        // Dateien/Ordner, die es schon in der Original-Resource gab, liefen auch ohne Eintrag – nicht anfassen
        bool IsOriginal(string p)
        {
            var s = p.Replace('\\', '/');
            var star = s.IndexOf('*');
            if (star >= 0) s = s[..Math.Max(0, s.LastIndexOf('/', star))];
            return origFiles.ContainsKey(s) || origFiles.Keys.Any(k => k.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        }
        if (manRel != null && File.Exists(genFx))
        {
            var manPath = Path.Combine(res, manRel);
            var text = File.ReadAllText(manPath);
            var gtext = File.ReadAllText(genFx);
            var addFiles = new List<string>();
            var addData = new List<string>();
            var fm = Regex.Match(gtext, @"files\s*\{(.*?)\}", RegexOptions.Singleline);
            if (fm.Success)
                foreach (Match m in Regex.Matches(fm.Groups[1].Value, @"'([^']+)'"))
                {
                    var p = MapPath(m.Groups[1].Value, pathMap);
                    if (!Mentions(text, p) && !IsOriginal(p)) addFiles.Add(p);
                }
            foreach (Match m in Regex.Matches(gtext, @"data_file\s+'([^']+)'\s+'([^']+)'"))
            {
                var p = MapPath(m.Groups[2].Value, pathMap);
                if (!Mentions(text, p) && !IsOriginal(p)) addData.Add($"data_file '{m.Groups[1].Value}' '{p}'");
            }
            if (addFiles.Count + addData.Count > 0)
            {
                var sb = new StringBuilder(text.TrimEnd());
                sb.AppendLine().AppendLine().AppendLine("-- ergänzt vom GTA Tool");
                if (addFiles.Count > 0)
                {
                    sb.AppendLine("files {");
                    foreach (var p in addFiles) sb.AppendLine($"    '{p}',");
                    sb.AppendLine("}");
                }
                foreach (var d in addData) sb.AppendLine(d);
                File.WriteAllText(manPath, sb.ToString(), new UTF8Encoding(false));
                added = addFiles.Count + addData.Count;
                warnings.Add($"Im fxmanifest wurden {added} Einträge für neue Dateien ergänzt.");
                if (output.Keys.Any(k => k.EndsWith(".fxap", StringComparison.OrdinalIgnoreCase)))
                    warnings.Add("Achtung: Die Resource ist mit Escrow geschützt – ein geändertes fxmanifest kann dort Probleme machen.");
            }
        }
        return (n, added);
    }

    /// <summary>Pfad aus dem Studio-Manifest auf den neuen Platz umrechnen (auch Ordner und Platzhalter wie sfx/pack/*.awc).</summary>
    static string MapPath(string p, Dictionary<string, string> pathMap)
    {
        if (pathMap.TryGetValue(p, out var t)) return t;
        var star = p.IndexOf('*');
        var basePart = star >= 0 ? p[..p.LastIndexOf('/', star)] : p;
        var rest = star >= 0 ? p[basePart.Length..] : "";
        foreach (var (k, v) in pathMap)
        {
            if (!k.StartsWith(basePart, StringComparison.OrdinalIgnoreCase)) continue;
            var tail = k[basePart.Length..];
            if (!v.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;
            return v[..^tail.Length] + rest;
        }
        return p;
    }

    static bool Mentions(string manifest, string p)
    {
        var s = p.Replace('\\', '/').TrimEnd('/');
        var star = s.IndexOf('*');
        if (star >= 0) s = s[..Math.Max(0, s.LastIndexOf('/', star))];
        var token = s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] : s;
        return token.Length > 0 && manifest.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
