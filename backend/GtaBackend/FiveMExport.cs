using System.Text;

namespace GtaBackend;

/// <summary>Baut aus dem Sitzungsordner eine fertige, startbare FiveM-Resource.</summary>
public static class FiveMExport
{
    // Reihenfolge ist wichtig: Layouts vor vehicles.meta
    static readonly (string kind, string dataFile)[] DataFiles =
    {
        ("vehiclelayouts", "VEHICLE_LAYOUTS_FILE"),
        ("handling", "HANDLING_FILE"),
        ("vehicles", "VEHICLE_METADATA_FILE"),
        ("carcols", "CARCOLS_FILE"),
        ("carvariations", "VEHICLE_VARIATION_FILE"),
        ("carmodcols", "CARMODCOLS_FILE"),
    };

    public static object Export(string sessionDir, string outDir, string resourceName)
    {
        resourceName = Session.SafeName(resourceName);
        if (string.IsNullOrEmpty(outDir)) throw new UserError("Bitte einen Zielordner wählen.");
        var res = Path.Combine(outDir, resourceName);
        if (Directory.Exists(res) && Directory.EnumerateFileSystemEntries(res).Any())
        {
            if (!File.Exists(Path.Combine(res, "fxmanifest.lua")))
                throw new UserError($"Der Ordner {res} existiert schon und ist keine FiveM-Resource. Bitte einen anderen Namen wählen.");
            Directory.Delete(res, true);
        }
        Directory.CreateDirectory(res);
        var warnings = new List<string>();

        // ---- stream
        var stream = Path.Combine(sessionDir, "stream");
        var streamOut = Path.Combine(res, "stream");
        Directory.CreateDirectory(streamOut);
        var streamFiles = Directory.Exists(stream) ? Directory.GetFiles(stream) : Array.Empty<string>();
        int i = 0;
        foreach (var f in streamFiles)
        {
            Program.Progress($"Kopiere {Path.GetFileName(f)} …", (int)(80.0 * i++ / Math.Max(1, streamFiles.Length)));
            File.Copy(f, Path.Combine(streamOut, Path.GetFileName(f).ToLowerInvariant()), true);
            if (new FileInfo(f).Length > 16 * 1024 * 1024)
                warnings.Add($"{Path.GetFileName(f)} ist größer als 16 MB – FiveM zeigt dafür eine Warnung.");
            if (Session.IsEncrypted(sessionDir, "stream/" + Path.GetFileName(f)))
                warnings.Add($"{Path.GetFileName(f)} ist verschlüsselt (Escrow) – läuft nur auf dem Server des Käufers.");
        }

        // ---- data
        var dataDir = Path.Combine(sessionDir, "data");
        var metas = new List<(string rel, string kind)>();
        if (Directory.Exists(dataDir))
        {
            foreach (var f in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories).OrderBy(f => f))
            {
                var kind = MetaData.MetaKind(f);
                if (kind is "other" or "contentunlocks" or "dlctext") continue;
                var rel = Path.GetRelativePath(dataDir, f).Replace('\\', '/').ToLowerInvariant();
                var target = Path.Combine(res, "data", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
                metas.Add(("data/" + rel, kind));
            }
        }

        // ---- Audio (eigene Motorsounds)
        var audioFiles = new List<string>();
        var audioData = new List<(string type, string path)>();
        var audioDir = Path.Combine(sessionDir, "audio");
        if (Directory.Exists(audioDir))
        {
            foreach (var srcDir in Directory.GetDirectories(audioDir))
            {
                var cfg = Path.Combine(srcDir, "audioconfig");
                if (Directory.Exists(cfg))
                {
                    foreach (var f in Directory.GetFiles(cfg))
                    {
                        var name = Path.GetFileName(f).ToLowerInvariant();
                        var target = Path.Combine(res, "audioconfig", name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(f, target, true);
                        audioFiles.Add("audioconfig/" + name);
                        // FiveM erwartet z. B. 'audioconfig/x_game.dat' für x_game.dat151.rel
                        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*\.dat)(\d+)\.rel$");
                        if (!m.Success) continue;
                        var type = m.Groups[2].Value switch
                        {
                            "151" => "AUDIO_GAMEDATA",
                            "54" => "AUDIO_SOUNDDATA",
                            "10" => "AUDIO_SYNTHDATA",
                            "15" => "AUDIO_DYNAMIXDATA",
                            "16" => "AUDIO_CURVEDATA",
                            "4" => "AUDIO_SPEECHDATA",
                            _ => null,
                        };
                        if (type != null) audioData.Add((type, "audioconfig/" + m.Groups[1].Value));
                    }
                }
                var sfx = Path.Combine(srcDir, "sfx");
                if (Directory.Exists(sfx))
                {
                    foreach (var pack in Directory.GetDirectories(sfx))
                    {
                        var packName = Path.GetFileName(pack).ToLowerInvariant();
                        foreach (var f in Directory.GetFiles(pack, "*.awc"))
                        {
                            var target = Path.Combine(res, "sfx", packName, Path.GetFileName(f).ToLowerInvariant());
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Copy(f, target, true);
                        }
                        audioFiles.Add($"sfx/{packName}/*.awc");
                        audioData.Add(("AUDIO_WAVEPACK", $"sfx/{packName}"));
                    }
                }
            }
        }

        // ---- Namen (AddTextEntry)
        var md = MetaData.Read(sessionDir, warnings);
        var names = new StringBuilder();
        names.AppendLine("-- Automatisch erzeugt vom GTA Tool: Anzeigenamen der Fahrzeuge");
        names.AppendLine("Citizen.CreateThread(function()");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in md.Vehicles)
        {
            if (!string.IsNullOrEmpty(v.GameName) && !v.GameName.Equals("null", StringComparison.OrdinalIgnoreCase) && seen.Add(v.GameName))
            {
                var label = MetaData.LabelText(md, v.GameName) ?? v.Model.ToUpperInvariant();
                names.AppendLine($"    AddTextEntry('{Lua(v.GameName)}', '{Lua(label)}')");
            }
            if (!string.IsNullOrEmpty(v.Make) && !v.Make.Equals("null", StringComparison.OrdinalIgnoreCase) && seen.Add(v.Make))
            {
                var label = MetaData.LabelText(md, v.Make);
                if (label != null) names.AppendLine($"    AddTextEntry('{Lua(v.Make)}', '{Lua(label)}')");
            }
        }
        names.AppendLine("end)");
        bool hasNames = seen.Count > 0;
        if (hasNames)
        {
            Directory.CreateDirectory(Path.Combine(res, "client"));
            File.WriteAllText(Path.Combine(res, "client", "names.lua"), names.ToString(), new UTF8Encoding(false));
        }

        // ---- fxmanifest.lua
        var fx = new StringBuilder();
        fx.AppendLine("fx_version 'cerulean'");
        fx.AppendLine("game 'gta5'");
        fx.AppendLine("lua54 'yes'");
        fx.AppendLine();
        fx.AppendLine("author 'GTA Tool'");
        fx.AppendLine($"description '{Lua(resourceName)} – erstellt mit dem GTA Tool'");
        fx.AppendLine("version '1.0.0'");
        fx.AppendLine();
        if (metas.Count > 0 || audioFiles.Count > 0)
        {
            fx.AppendLine("files {");
            foreach (var m in metas) fx.AppendLine($"    '{m.rel}',");
            foreach (var a in audioFiles.Distinct()) fx.AppendLine($"    '{a}',");
            fx.AppendLine("}");
            fx.AppendLine();
            foreach (var (kind, dataFile) in DataFiles)
                foreach (var m in metas.Where(m => m.kind == kind))
                    fx.AppendLine($"data_file '{dataFile}' '{m.rel}'");
            // Audio: erst Daten, dann Wavepacks
            foreach (var (type, path) in audioData.OrderBy(a => a.type == "AUDIO_WAVEPACK" ? 1 : 0))
                fx.AppendLine($"data_file '{type}' '{path}'");
            fx.AppendLine();
        }
        if (hasNames) fx.AppendLine("client_script 'client/names.lua'");
        File.WriteAllText(Path.Combine(res, "fxmanifest.lua"), fx.ToString(), new UTF8Encoding(false));

        // ---- Prüfungen
        var streamNames = new HashSet<string>(Directory.GetFiles(streamOut).Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
        foreach (var v in md.Vehicles)
        {
            if (!streamNames.Contains(v.Model + ".yft")) warnings.Add($"{v.Model}: Modell {v.Model}.yft fehlt im stream-Ordner.");
            if (!streamNames.Contains((v.TxdName ?? v.Model) + ".ytd")) warnings.Add($"{v.Model}: Texturdatei {(v.TxdName ?? v.Model)}.ytd fehlt.");
            if (!metas.Any(m => m.kind == "handling")) { }
        }
        if (md.Vehicles.Count > 0 && !metas.Any(m => m.kind == "handling"))
            warnings.Add("Keine handling.meta gefunden – das Fahrzeug nutzt dann ein vorhandenes Handling (handlingId).");
        if (audioData.Any(a => a.type == "AUDIO_WAVEPACK") && !audioData.Any(a => a.type == "AUDIO_GAMEDATA"))
            warnings.Add("Sound-Dateien (.awc) gefunden, aber keine Sound-Einstellungen (_game.dat151.rel) – der eigene Motorsound funktioniert so evtl. nicht.");

        // ---- LIESMICH
        var readme = new StringBuilder();
        readme.AppendLine($"Resource: {resourceName}");
        readme.AppendLine();
        readme.AppendLine("So startest du die Resource:");
        readme.AppendLine($"  1. Den Ordner \"{resourceName}\" in den resources-Ordner deines Servers kopieren.");
        readme.AppendLine($"  2. In der server.cfg eintragen:  ensure {resourceName}");
        readme.AppendLine("  3. Server neu starten.");
        readme.AppendLine();
        if (md.Vehicles.Count > 0)
        {
            readme.AppendLine("Spawn-Namen:");
            foreach (var v in md.Vehicles) readme.AppendLine($"  {v.Model}");
        }
        else
        {
            readme.AppendLine("Diese Resource ersetzt vorhandene GTA-Fahrzeuge (Replace):");
            foreach (var f in streamNames.Where(n => n.EndsWith(".yft") && !n.EndsWith("_hi.yft")).OrderBy(n => n)) readme.AppendLine($"  {f[..^4]}");
        }
        if (warnings.Count > 0)
        {
            readme.AppendLine();
            readme.AppendLine("Hinweise:");
            foreach (var w in warnings) readme.AppendLine("  - " + w);
        }
        File.WriteAllText(Path.Combine(res, "LIESMICH.txt"), readme.ToString(), new UTF8Encoding(true));

        Program.Progress("Fertig.", 100);
        return new
        {
            folder = res,
            streamFiles = streamNames.Count,
            metaFiles = metas.Count,
            vehicles = md.Vehicles.Select(v => v.Model).ToList(),
            warnings,
        };
    }

    static string Lua(string s) => (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
}
