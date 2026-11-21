using System.Text;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Prop Studio: eigene Objekte (Props) – Modelle (.ydr/.yft), Texturen (.ytd), Kollision, Archetypen (.ytyp).
///  – Erkennen, welche Modelle einen Archetyp haben (ohne Archetyp sind Props im Spiel nicht spawnbar)
///  – Fehlende Archetypen automatisch anlegen, umbenennen, löschen, Sichtweite ändern
///  – GTA-Objekt als Vorlage übernehmen
///  – FiveM-Export mit DLC_ITYP_REQUEST und optionalem Test-Befehl /spawnprop
/// </summary>
public static class PropPack
{
    static string Stream(string sessionDir) => Path.Combine(sessionDir, "stream");
    static readonly string[] ModelExt = { ".ydr", ".yft", ".ydd" };

    class ArchInfo
    {
        public string Name, AssetName, Txd, Type, Ytyp;
        public float LodDist;
        public bool IsMlo;
    }

    static string HashName(MetaHash h)
    {
        if (h.Hash == 0) return "";
        var s = JenkIndex.TryGetString(h.Hash);
        if (!string.IsNullOrEmpty(s)) return s.ToLowerInvariant();
        return Props.NameOf(h.Hash)?.ToLowerInvariant() ?? $"hash_{h.Hash:X8}";
    }

    static YtypFile LoadYtyp(string file)
    {
        var data = File.ReadAllBytes(file);
        var y = new YtypFile();
        if (data.Length > 4 && data[0] == 'R' && data[1] == 'S' && data[2] == 'C' && data[3] == '7') y.Load(data);
        else y.Load(data, new RpfResourceFileEntry { Name = Path.GetFileName(file), NameLower = Path.GetFileName(file).ToLowerInvariant() });
        y.AllArchetypes ??= Array.Empty<Archetype>();
        return y;
    }

    static void SaveYtyp(YtypFile y, string file, string backupDir)
    {
        var data = y.Save();
        if (data == null || data.Length < 16) throw new UserError("Die YTYP konnte nicht gespeichert werden.");
        Session.Backup(file, backupDir);
        File.WriteAllBytes(file, data);
    }

    /// <summary>Alle Namen der Paket-Dateien in den Hash-Index (damit Archetyp-Namen lesbar sind).</summary>
    static void RegisterNames(string sessionDir)
    {
        var s = Stream(sessionDir);
        if (!Directory.Exists(s)) return;
        foreach (var f in Directory.GetFiles(s)) JenkIndex.Ensure(Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
    }

    static List<ArchInfo> Archetypes(string sessionDir, List<string> warnings)
    {
        var res = new List<ArchInfo>();
        foreach (var f in Directory.GetFiles(Stream(sessionDir), "*.ytyp"))
        {
            try
            {
                var y = LoadYtyp(f);
                foreach (var a in y.AllArchetypes)
                {
                    var bd = a._BaseArchetypeDef;
                    res.Add(new ArchInfo
                    {
                        Name = HashName(bd.name), AssetName = HashName(bd.assetName), Txd = HashName(bd.textureDictionary),
                        Type = bd.assetType.ToString().Replace("ASSET_TYPE_", "").ToLowerInvariant(), LodDist = bd.lodDist,
                        Ytyp = "stream/" + Path.GetFileName(f), IsMlo = a is MloArchetype,
                    });
                }
            }
            catch (Exception ex) { warnings?.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        return res;
    }

    // ------------------------------------------------------------------ Analyse

    public static object Analyze(string sessionDir)
    {
        var warnings = new List<string>();
        var s = Stream(sessionDir);
        Directory.CreateDirectory(s);
        RegisterNames(sessionDir);
        var archs = Archetypes(sessionDir, warnings);
        var files = Directory.GetFiles(s).Select(Path.GetFileName).ToList();
        var lower = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var props = new List<object>();
        var usedYtd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int noArch = 0;
        foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (!ModelExt.Contains(ext)) continue;
            var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            if (ext == ".yft" && name.EndsWith("_hi") && lower.Contains(name[..^3] + ".yft")) continue;
            modelNames.Add(name);
            var rel = "stream/" + f;
            var arch = archs.FirstOrDefault(a => !a.IsMlo && (a.AssetName == name || a.Name == name));
            var ytds = new List<string>();
            if (arch != null && !string.IsNullOrEmpty(arch.Txd) && lower.Contains(arch.Txd + ".ytd")) ytds.Add("stream/" + arch.Txd + ".ytd");
            if (lower.Contains(name + ".ytd") && !ytds.Contains("stream/" + name + ".ytd")) ytds.Add("stream/" + name + ".ytd");
            foreach (var y in ytds) usedYtd.Add(y);
            var ybn = lower.Contains(name + ".ybn") ? "stream/" + name + ".ybn" : null;
            if (arch == null) noArch++;
            props.Add(new
            {
                name, file = rel, type = ext.TrimStart('.'), size = new FileInfo(Path.Combine(s, f)).Length,
                encrypted = Session.IsEncrypted(sessionDir, rel),
                hi = ext == ".yft" && lower.Contains(name + "_hi.yft") ? $"stream/{name}_hi.yft" : null,
                ytds, ybn,
                archetype = arch == null ? null : new { arch.Name, arch.Txd, arch.Ytyp, arch.LodDist, arch.Type },
                txdMissing = arch != null && !string.IsNullOrEmpty(arch.Txd) && !lower.Contains(arch.Txd + ".ytd") && arch.Txd != name,
            });
            if (new FileInfo(Path.Combine(s, f)).Length > 16 * 1024 * 1024) warnings.Add($"{f} ist größer als 16 MB – FiveM lädt solche Dateien oft nicht zuverlässig.");
        }
        var orphan = archs.Where(a => !a.IsMlo && !modelNames.Contains(a.AssetName) && !modelNames.Contains(a.Name)).Select(a => a.Name).Distinct().ToList();
        if (orphan.Count > 0) warnings.Add($"{orphan.Count} Archetyp(en) ohne Modell im Paket (verweisen evtl. auf GTA-Modelle): {string.Join(", ", orphan.Take(8))}{(orphan.Count > 8 ? " …" : "")}");
        if (archs.Any(a => a.IsMlo)) warnings.Add("Das Paket enthält ein Interior (MLO) – dafür besser das MLO Studio benutzen.");
        return new
        {
            props,
            ytds = files.Where(f => f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)).Select(f => new { file = "stream/" + f, used = usedYtd.Contains("stream/" + f) }).ToList(),
            ybns = files.Where(f => f.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase)).Select(f => "stream/" + f).ToList(),
            ytyps = files.Where(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)).Select(f => "stream/" + f).ToList(),
            missingArchetypes = noArch,
            warnings,
            original = Session.FindOriginal(sessionDir)?.Name,
        };
    }

    /// <summary>Details zu einem Modell (Größe, eingebettete Kollision, Texturen).</summary>
    public static object Details(string sessionDir, string rel)
    {
        var f = Path.Combine(sessionDir, rel);
        var (mn, mx, hasBound, embTex) = ModelInfo(f);
        var size = mx - mn;
        return new { size = new[] { size.X, size.Y, size.Z }, collision = hasBound, embeddedTextures = embTex };
    }

    static (Vector3 min, Vector3 max, bool bound, int tex) ModelInfo(string file)
    {
        var data = File.ReadAllBytes(file);
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".ydr":
            {
                var y = new YdrFile(); y.Load(data);
                var d = y.Drawable ?? throw new UserError("Das Modell konnte nicht gelesen werden.");
                return (d.BoundingBoxMin, d.BoundingBoxMax, d.Bound != null, d.ShaderGroup?.TextureDictionary?.Textures?.data_items?.Length ?? 0);
            }
            case ".yft":
            {
                var y = new YftFile(); y.Load(data);
                var d = y.Fragment?.Drawable ?? throw new UserError("Das Modell konnte nicht gelesen werden.");
                bool b = y.Fragment.PhysicsLODGroup?.PhysicsLOD1?.Bound != null || d.Bound != null;
                return (d.BoundingBoxMin, d.BoundingBoxMax, b, d.ShaderGroup?.TextureDictionary?.Textures?.data_items?.Length ?? 0);
            }
            case ".ydd":
            {
                var y = new YddFile(); y.Load(data);
                var d = y.Drawables?.FirstOrDefault() ?? throw new UserError("Das Modell konnte nicht gelesen werden.");
                return (d.BoundingBoxMin, d.BoundingBoxMax, d.Bound != null, 0);
            }
        }
        throw new UserError("Unbekannter Modelltyp.");
    }

    // ------------------------------------------------------------------ Archetypen

    /// <summary>Ziel-YTYP: vorhandene (ohne MLO) oder neue „&lt;name&gt;.ytyp“.</summary>
    static (YtypFile y, string file) TargetYtyp(string sessionDir, string ytypName)
    {
        var s = Stream(sessionDir);
        foreach (var f in Directory.GetFiles(s, "*.ytyp").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try { var y = LoadYtyp(f); if (!y.AllArchetypes.Any(a => a is MloArchetype)) return (y, f); } catch { }
        }
        var name = Session.SafeName(string.IsNullOrWhiteSpace(ytypName) ? "props" : ytypName);
        var file = Path.Combine(s, name + ".ytyp");
        var ny = new YtypFile { NameHash = JenkHash.GenHash(name), AllArchetypes = Array.Empty<Archetype>() };
        ny._CMapTypes = new CMapTypes { name = JenkHash.GenHash(name) };
        JenkIndex.Ensure(name);
        return (ny, file);
    }

    static Archetype NewArchetype(YtypFile y, string name, string modelFile, string txd, float lodDist)
    {
        var (mn, mx, _, _) = ModelInfo(modelFile);
        var a = y.AddArchetype();
        var bd = a._BaseArchetypeDef;
        bd.name = JenkHash.GenHash(name);
        bd.assetName = bd.name;
        bd.textureDictionary = string.IsNullOrEmpty(txd) ? (MetaHash)0 : JenkHash.GenHash(txd);
        bd.assetType = modelFile.EndsWith(".yft", StringComparison.OrdinalIgnoreCase) ? rage__fwArchetypeDef__eAssetType.ASSET_TYPE_FRAGMENT : rage__fwArchetypeDef__eAssetType.ASSET_TYPE_DRAWABLE;
        bd.lodDist = lodDist;
        bd.hdTextureDist = Math.Min(lodDist, 100f);
        bd.flags = 0; bd.specialAttribute = 0;
        bd.bbMin = mn; bd.bbMax = mx;
        bd.bsCentre = (mn + mx) * 0.5f; bd.bsRadius = (mx - mn).Length() * 0.5f;
        a._BaseArchetypeDef = bd;
        JenkIndex.Ensure(name);
        if (!string.IsNullOrEmpty(txd)) JenkIndex.Ensure(txd);
        return a;
    }

    /// <summary>Fehlende Archetypen für alle Modelle anlegen.</summary>
    public static object EnsureArchetypes(string sessionDir, string ytypName, string backupDir)
    {
        RegisterNames(sessionDir);
        var archs = Archetypes(sessionDir, null);
        var s = Stream(sessionDir);
        var added = new List<string>();
        YtypFile y = null; string file = null;
        foreach (var f in Directory.GetFiles(s).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".ydr" && ext != ".yft") continue;
            var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            if (ext == ".yft" && name.EndsWith("_hi") && File.Exists(Path.Combine(s, name[..^3] + ".yft"))) continue;
            if (archs.Any(a => a.AssetName == name || a.Name == name)) continue;
            if (Session.IsEncrypted(sessionDir, "stream/" + Path.GetFileName(f))) continue;
            if (y == null) (y, file) = TargetYtyp(sessionDir, ytypName);
            var txd = File.Exists(Path.Combine(s, name + ".ytd")) ? name : null;
            NewArchetype(y, name, f, txd, 150f);
            added.Add(name);
        }
        if (y != null && added.Count > 0) SaveYtyp(y, file, backupDir);
        return new { added, ytyp = file == null ? null : "stream/" + Path.GetFileName(file) };
    }

    static IEnumerable<(YtypFile y, string file)> AllYtyps(string sessionDir)
    {
        foreach (var f in Directory.GetFiles(Stream(sessionDir), "*.ytyp"))
        {
            YtypFile y = null;
            try { y = LoadYtyp(f); } catch { }
            if (y != null) yield return (y, f);
        }
    }

    /// <summary>Sichtweite (LOD-Distanz) eines Props ändern.</summary>
    public static object SetLod(string sessionDir, string name, float lodDist, string backupDir)
    {
        if (lodDist < 5 || lodDist > 5000) throw new UserError("Die Sichtweite muss zwischen 5 und 5000 m liegen.");
        RegisterNames(sessionDir);
        var h = JenkHash.GenHash(name.ToLowerInvariant());
        foreach (var (y, f) in AllYtyps(sessionDir))
        {
            var a = y.AllArchetypes.FirstOrDefault(x => x._BaseArchetypeDef.name.Hash == h || x._BaseArchetypeDef.assetName.Hash == h);
            if (a == null) continue;
            var bd = a._BaseArchetypeDef;
            bd.lodDist = lodDist;
            bd.hdTextureDist = Math.Min(bd.hdTextureDist <= 0 ? 100 : bd.hdTextureDist, lodDist);
            a._BaseArchetypeDef = bd;
            SaveYtyp(y, f, backupDir);
            return new { ok = true };
        }
        throw new UserError("Für dieses Prop gibt es noch keinen Archetyp – zuerst „Archetyp anlegen“.");
    }

    /// <summary>Prop umbenennen: Modell-, Textur- und Kollisionsdatei sowie Archetyp.</summary>
    public static object Rename(string sessionDir, string oldName, string newName, string backupDir)
    {
        oldName = oldName.ToLowerInvariant();
        newName = CleanName(newName);
        if (newName == oldName) return new { name = newName };
        var s = Stream(sessionDir);
        if (ModelExt.Any(e => File.Exists(Path.Combine(s, newName + e)))) throw new UserError($"Es gibt schon ein Modell „{newName}“.");
        RegisterNames(sessionDir);
        var archs = Archetypes(sessionDir, null);
        var arch = archs.FirstOrDefault(a => a.AssetName == oldName || a.Name == oldName);
        // Textur nur mit umbenennen, wenn sie zum Prop gehört (gleicher Name und von keinem anderen Prop benutzt)
        bool renameTxd = File.Exists(Path.Combine(s, oldName + ".ytd")) && !archs.Any(a => a != arch && a.Txd == oldName);
        void Move(string from, string to)
        {
            var a = Path.Combine(s, from); var b = Path.Combine(s, to);
            if (!File.Exists(a)) return;
            Session.Backup(a, backupDir);
            File.Move(a, b);
        }
        foreach (var e in ModelExt) Move(oldName + e, newName + e);
        Move(oldName + "_hi.yft", newName + "_hi.yft");
        Move(oldName + ".ybn", newName + ".ybn");
        if (renameTxd) Move(oldName + ".ytd", newName + ".ytd");
        JenkIndex.Ensure(newName);
        var ho = JenkHash.GenHash(oldName);
        foreach (var (y, f) in AllYtyps(sessionDir))
        {
            bool changed = false;
            foreach (var a in y.AllArchetypes)
            {
                var bd = a._BaseArchetypeDef;
                if (bd.name.Hash == ho) { bd.name = JenkHash.GenHash(newName); changed = true; }
                if (bd.assetName.Hash == ho) { bd.assetName = JenkHash.GenHash(newName); changed = true; }
                if (renameTxd && bd.textureDictionary.Hash == ho) { bd.textureDictionary = JenkHash.GenHash(newName); changed = true; }
                a._BaseArchetypeDef = bd;
            }
            if (changed) SaveYtyp(y, f, backupDir);
        }
        return new { name = newName };
    }

    /// <summary>Prop löschen (Modell, eigene Kollision, Archetyp; Textur nur wenn nicht mehr benutzt).</summary>
    public static object Delete(string sessionDir, string name, string backupDir)
    {
        name = name.ToLowerInvariant();
        var s = Stream(sessionDir);
        RegisterNames(sessionDir);
        var archs = Archetypes(sessionDir, null);
        var arch = archs.FirstOrDefault(a => a.AssetName == name || a.Name == name);
        void Del(string fn) { var p = Path.Combine(s, fn); if (File.Exists(p)) { Session.Backup(p, backupDir); File.Delete(p); } }
        foreach (var e in ModelExt) Del(name + e);
        Del(name + "_hi.yft");
        Del(name + ".ybn");
        var txd = arch?.Txd ?? name;
        if (!string.IsNullOrEmpty(txd) && !archs.Any(a => a != arch && a.Txd == txd)) Del(txd + ".ytd");
        var h = JenkHash.GenHash(name);
        foreach (var (y, f) in AllYtyps(sessionDir))
        {
            var rm = y.AllArchetypes.Where(a => a._BaseArchetypeDef.name.Hash == h || a._BaseArchetypeDef.assetName.Hash == h).ToList();
            if (rm.Count == 0) continue;
            foreach (var a in rm) y.RemoveArchetype(a);
            if (y.AllArchetypes.Length == 0) { Session.Backup(f, backupDir); File.Delete(f); }
            else SaveYtyp(y, f, backupDir);
        }
        return new { ok = true };
    }

    static string CleanName(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) throw new UserError("Bitte einen Namen eingeben.");
        if (s.Length > 60) throw new UserError("Der Name ist zu lang (max. 60 Zeichen).");
        foreach (var c in s) if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) throw new UserError($"Der Name „{s}“ enthält ungültige Zeichen. Erlaubt: a–z, 0–9, _ und -.");
        return s;
    }

    /// <summary>GTA-Objekt als Vorlage ins Paket kopieren (eigener Name, eigene Texturdatei, Archetyp).</summary>
    public static object AddFromGta(string sessionDir, string gtaPath, string propsCache, string propsDir, string gtaName, string newName, string ytypName, string backupDir)
    {
        newName = CleanName(newName);
        var s = Stream(sessionDir);
        Directory.CreateDirectory(s);
        if (ModelExt.Any(e => File.Exists(Path.Combine(s, newName + e)))) throw new UserError($"Es gibt schon ein Modell „{newName}“.");
        if (Props.Load(propsCache) == null) throw new UserError("Der Katalog der GTA-Objekte wurde noch nicht eingelesen.");
        var (model, ytds) = Props.Extract(gtaPath, propsCache, gtaName, Path.Combine(propsDir, Session.SafeName(gtaName)));
        if (model == null) throw new UserError($"Das GTA-Objekt „{gtaName}“ konnte nicht gelesen werden.");
        var ext = Path.GetExtension(model).ToLowerInvariant();
        if (ext == ".ydd") throw new UserError("Dieses GTA-Objekt liegt in einem Modell-Paket (.ydd) und kann nicht einzeln übernommen werden.");
        var target = Path.Combine(s, newName + ext);
        File.Copy(model, target);
        string txd = null;
        if (ytds.Count > 0)
        {
            txd = newName;
            File.Copy(ytds[0], Path.Combine(s, txd + ".ytd"), true);
        }
        var (y, file) = TargetYtyp(sessionDir, ytypName);
        NewArchetype(y, newName, target, txd, 150f);
        SaveYtyp(y, file, backupDir);
        string note = null;
        if (txd == null) note = "Das Objekt benutzt Texturen aus gemeinsamen GTA-Texturdateien – sie sind im Spiel vorhanden, aber nicht im Paket.";
        return new { name = newName, file = "stream/" + Path.GetFileName(target), note };
    }

    // ------------------------------------------------------------------ Neues Prop aus Blender

    /// <summary>
    /// Ergebnis von „Neues Prop in Blender“ übernehmen: &lt;name&gt;.ydr ins Paket, Bilder aus „texturen\“ als &lt;name&gt;.ytd,
    /// Archetyp anlegen bzw. aktualisieren (Größe, Texturdatei). Mehrfaches Zurückschicken ersetzt den alten Stand.
    /// </summary>
    public static object ApplyNew(string sessionDir, string exportDir, string name, string backupDir)
    {
        name = CleanName(name);
        var s = Stream(sessionDir);
        Directory.CreateDirectory(s);
        var ydr = Directory.Exists(exportDir) ? Directory.GetFiles(exportDir, "*.ydr", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        if (ydr == null) throw new UserError($"Blender hat keine {name}.ydr exportiert.");
        var head = new byte[4];
        using (var fs = File.OpenRead(ydr)) fs.ReadExactly(head);
        if (head[0] != 'R' || head[1] != 'S' || head[2] != 'C' || head[3] != '7') throw new UserError("Die Datei aus Blender ist ungültig.");
        var target = Path.Combine(s, name + ".ydr");
        Session.Backup(target, backupDir);
        File.Copy(ydr, target, true);

        // Texturen → <name>.ytd
        var texDir = Path.Combine(exportDir, "texturen");
        var images = Directory.Exists(texDir)
            ? Directory.GetFiles(texDir).Where(f => new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".tif", ".tiff" }.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList()
            : new List<string>();
        string txd = null;
        var ytdFile = Path.Combine(s, name + ".ytd");
        if (images.Count > 0)
        {
            var list = new List<Texture>();
            foreach (var img in images)
            {
                var tn = Path.GetFileNameWithoutExtension(img);
                Program.Progress($"Textur {tn} wird umgewandelt …");
                var fmt = tn.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ? TextureFormat.D3DFMT_ATI2 : TextureFormat.D3DFMT_DXT5;
                list.Add(Textures.BuildTexture(img, tn, fmt, keepFormatFromDds: true));
            }
            var ytd = new YtdFile { TextureDict = new TextureDictionary() };
            ytd.TextureDict.BuildFromTextureList(list);
            Session.Backup(ytdFile, backupDir);
            File.WriteAllBytes(ytdFile, ytd.Save());
            txd = name;
        }
        else if (File.Exists(ytdFile)) txd = name;

        // Archetyp anlegen oder aktualisieren
        RegisterNames(sessionDir);
        var h = JenkHash.GenHash(name);
        bool updated = false;
        foreach (var (y, f) in AllYtyps(sessionDir))
        {
            var a = y.AllArchetypes.FirstOrDefault(x => x._BaseArchetypeDef.name.Hash == h || x._BaseArchetypeDef.assetName.Hash == h);
            if (a == null) continue;
            var (mn, mx, _, _) = ModelInfo(target);
            var bd = a._BaseArchetypeDef;
            bd.bbMin = mn; bd.bbMax = mx; bd.bsCentre = (mn + mx) * 0.5f; bd.bsRadius = (mx - mn).Length() * 0.5f;
            bd.textureDictionary = txd != null ? JenkHash.GenHash(txd) : (MetaHash)0;
            bd.assetType = rage__fwArchetypeDef__eAssetType.ASSET_TYPE_DRAWABLE;
            a._BaseArchetypeDef = bd;
            SaveYtyp(y, f, backupDir);
            updated = true;
            break;
        }
        if (!updated)
        {
            var (y, f) = TargetYtyp(sessionDir, "props");
            NewArchetype(y, name, target, txd, 150f);
            SaveYtyp(y, f, backupDir);
        }
        try { File.Delete(ydr); } catch { }
        return new { name, file = "stream/" + name + ".ydr", textures = images.Count };
    }

    // ------------------------------------------------------------------ FiveM-Export

    public static object Export(string sessionDir, string outDir, string resourceName, bool testCommand, string backupDir)
    {
        resourceName = ExportMerge.ResName(string.IsNullOrWhiteSpace(resourceName) ? "props" : resourceName);
        var ensured = EnsureArchetypes(sessionDir, Session.SafeName(resourceName), backupDir);
        var res = Path.Combine(outDir, resourceName);
        if (Directory.Exists(res) && Directory.EnumerateFileSystemEntries(res).Any())
        {
            if (!File.Exists(Path.Combine(res, "fxmanifest.lua"))) throw new UserError($"{res} existiert schon und ist keine FiveM-Resource.");
            Directory.Delete(res, true);
        }
        var s = Stream(sessionDir);
        var warnings = new List<string>();
        var outStream = Path.Combine(res, "stream");
        Directory.CreateDirectory(outStream);
        int n = 0;
        foreach (var f in Directory.GetFiles(s))
        {
            File.Copy(f, Path.Combine(outStream, Path.GetFileName(f).ToLowerInvariant()), true);
            n++;
            if (new FileInfo(f).Length > 16 * 1024 * 1024) warnings.Add($"{Path.GetFileName(f)} ist größer als 16 MB.");
        }
        var ytyps = Directory.GetFiles(s, "*.ytyp").Select(f => Path.GetFileName(f).ToLowerInvariant()).OrderBy(x => x).ToList();
        var analyzed = Archetypes(sessionDir, null).Where(a => !a.IsMlo).Select(a => a.Name).Distinct().OrderBy(x => x).ToList();

        var fx = new StringBuilder();
        fx.AppendLine("fx_version 'cerulean'");
        fx.AppendLine("game 'gta5'");
        fx.AppendLine("lua54 'yes'");
        fx.AppendLine();
        fx.AppendLine($"description '{resourceName} – Props, erstellt mit dem GTA Tool'");
        fx.AppendLine("version '1.0.0'");
        fx.AppendLine();
        fx.AppendLine("-- Modelle und Texturen im Ordner stream/ werden automatisch gestreamt.");
        foreach (var y in ytyps) fx.AppendLine($"data_file 'DLC_ITYP_REQUEST' 'stream/{y}'");
        if (testCommand)
        {
            fx.AppendLine();
            fx.AppendLine("client_script 'client.lua'");
            File.WriteAllText(Path.Combine(res, "client.lua"), TestLua, new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(res, "fxmanifest.lua"), fx.ToString(), new UTF8Encoding(false));

        var rd = new StringBuilder();
        rd.AppendLine($"Resource „{resourceName}“ – erstellt mit dem GTA Tool am {DateTime.Now:dd.MM.yyyy HH:mm}");
        rd.AppendLine();
        rd.AppendLine("Installation:");
        rd.AppendLine($"  1. Den Ordner „{resourceName}“ in den resources-Ordner des Servers kopieren.");
        rd.AppendLine($"  2. In der server.cfg eintragen:  ensure {resourceName}");
        rd.AppendLine();
        if (testCommand)
        {
            rd.AppendLine("Test im Spiel (F8-Konsole oder Chat):");
            rd.AppendLine("  /spawnprop <name>   setzt das Prop vor dich");
            rd.AppendLine("  /delprops           entfernt alle so gesetzten Props");
            rd.AppendLine();
        }
        rd.AppendLine("Verwendung in eigenen Scripts (Lua):");
        rd.AppendLine("  local model = joaat('<name>')");
        rd.AppendLine("  RequestModel(model) while not HasModelLoaded(model) do Wait(0) end");
        rd.AppendLine("  local obj = CreateObject(model, x, y, z, true, true, false)");
        rd.AppendLine();
        rd.AppendLine("Enthaltene Props:");
        foreach (var a in analyzed) rd.AppendLine($"  {a}");
        File.WriteAllText(Path.Combine(res, "LIESMICH.txt"), rd.ToString(), new UTF8Encoding(true));
        return new { folder = res, files = n, props = analyzed.Count, addedArchetypes = ensured, warnings, model = Analyze(sessionDir) };
    }

    const string TestLua = """
-- Test-Befehle (erstellt mit dem GTA Tool)
--   /spawnprop <name>   setzt das Prop vor dich
--   /delprops           entfernt alle so gesetzten Props

local spawned = {}

RegisterCommand('spawnprop', function(_, args)
    local name = args[1]
    if not name then
        print('Benutzung: /spawnprop <name>')
        return
    end
    local model = joaat(name)
    if not IsModelInCdimage(model) then
        print(('Prop „%s“ wurde nicht gefunden.'):format(name))
        return
    end
    RequestModel(model)
    local timeout = GetGameTimer() + 5000
    while not HasModelLoaded(model) do
        if GetGameTimer() > timeout then
            print(('Prop „%s“ konnte nicht geladen werden.'):format(name))
            return
        end
        Wait(10)
    end
    local ped = PlayerPedId()
    local pos = GetOffsetFromEntityInWorldCoords(ped, 0.0, 2.0, 0.0)
    local obj = CreateObject(model, pos.x, pos.y, pos.z, false, false, false)
    SetEntityHeading(obj, GetEntityHeading(ped))
    PlaceObjectOnGroundProperly(obj)
    FreezeEntityPosition(obj, true)
    SetModelAsNoLongerNeeded(model)
    spawned[#spawned + 1] = obj
end, false)

RegisterCommand('delprops', function()
    for _, obj in ipairs(spawned) do
        if DoesEntityExist(obj) then DeleteObject(obj) end
    end
    spawned = {}
end, false)
""";
}
