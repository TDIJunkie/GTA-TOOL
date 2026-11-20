using System.Diagnostics;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace GtaBackend;

/// <summary>Texturen in .ytd sowie eingebettete Texturen in .yft/.ydr lesen, exportieren, ersetzen, hinzufÃ¼gen.</summary>
public static class Textures
{
    // ------------------------------------------------------------- Container laden/speichern

    /// <summary>HÃ¤lt eine geladene Datei mit TexturwÃ¶rterbuch.</summary>
    internal class Container
    {
        public string File;
        public YtdFile Ytd;
        public YftFile Yft;
        public YdrFile Ydr;
        public TextureDictionary Dict;
        public List<ShaderGroup> ShaderGroups = new();

        public byte[] Save()
        {
            if (Ytd != null) return Ytd.Save();
            if (Yft != null) return Yft.Save();
            if (Ydr != null) return Ydr.Save();
            throw new InvalidOperationException();
        }
    }

    internal static Container Open(string file, bool createEmbedded = false)
    {
        if (!File.Exists(file)) throw new UserError($"Datei nicht gefunden: {Path.GetFileName(file)}");
        var data = File.ReadAllBytes(file);
        if (data.Length < 4 || data[0] != 'R' || data[1] != 'S' || data[2] != 'C' || data[3] != '7')
            throw new UserError($"{Path.GetFileName(file)} ist verschlÃ¼sselt oder keine GTA-Ressource.");
        var c = new Container { File = file };
        var ext = Path.GetExtension(file).ToLowerInvariant();
        switch (ext)
        {
            case ".ytd":
                c.Ytd = new YtdFile();
                c.Ytd.Load(data);
                c.Dict = c.Ytd.TextureDict ?? throw new UserError("Die YTD enthÃ¤lt kein TexturwÃ¶rterbuch.");
                break;
            case ".yft":
                c.Yft = new YftFile();
                c.Yft.Load(data);
                var frag = c.Yft.Fragment ?? throw new UserError("Die YFT konnte nicht gelesen werden.");
                var drawables = new List<DrawableBase>();
                if (frag.Drawable != null) drawables.Add(frag.Drawable);
                if (frag.DrawableCloth != null) drawables.Add(frag.DrawableCloth);
                if (frag.DrawableArray?.data_items != null) drawables.AddRange(frag.DrawableArray.data_items.Where(d => d != null));
                foreach (var d in drawables) if (d.ShaderGroup != null) c.ShaderGroups.Add(d.ShaderGroup);
                c.Dict = c.ShaderGroups.Select(s => s.TextureDictionary).FirstOrDefault(t => t != null);
                break;
            case ".ydr":
                c.Ydr = new YdrFile();
                c.Ydr.Load(data);
                if (c.Ydr.Drawable?.ShaderGroup != null) c.ShaderGroups.Add(c.Ydr.Drawable.ShaderGroup);
                c.Dict = c.ShaderGroups.Select(s => s.TextureDictionary).FirstOrDefault(t => t != null);
                break;
            default:
                throw new UserError("Texturen gibt es nur in .ytd, .yft und .ydr.");
        }
        if (c.Dict == null && createEmbedded)
        {
            throw new UserError("Dieses Modell hat keine eingebetteten Texturen. Neue Texturen bitte in die zugehÃ¶rige .ytd importieren.");
        }
        return c;
    }

    internal static List<Texture> TexList(Container c) =>
        c.Dict?.Textures?.data_items?.Where(t => t != null).ToList() ?? new List<Texture>();

    internal static void Write(Container c, List<Texture> list, string backupDir)
    {
        c.Dict.BuildFromTextureList(list);
        var bytes = c.Save();
        Session.Backup(c.File, backupDir);
        File.WriteAllBytes(c.File, bytes);
    }

    static Texture Find(Container c, string name)
    {
        var t = TexList(c).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return t ?? throw new UserError($"Textur {name} nicht gefunden.");
    }

    // ------------------------------------------------------------- Liste / Vorschau

    public static object List(string file)
    {
        var c = Open(file);
        var result = new List<object>();
        foreach (var t in TexList(c))
        {
            string thumb = null;
            try { thumb = Thumb(t, 160); } catch { }
            result.Add(new
            {
                name = t.Name,
                width = (int)t.Width,
                height = (int)t.Height,
                format = FormatLabel(t.Format),
                formatRaw = t.Format.ToString(),
                mips = (int)t.Levels,
                sizeKb = (t.Data?.FullData?.Length ?? 0) / 1024,
                thumb,
                isNormal = t.Name.EndsWith("_n", StringComparison.OrdinalIgnoreCase),
            });
        }
        return new { file, embedded = c.Ytd == null, textures = result };
    }

    public static object Preview(string file, string name, int maxSize)
    {
        var c = Open(file);
        var t = Find(c, name);
        return new { name = t.Name, image = Thumb(t, maxSize) };
    }

    public static string ThumbPublic(Texture t, int maxSize) => Thumb(t, maxSize);

    static string Thumb(Texture t, int maxSize)
    {
        int mip = 0;
        while (mip < t.Levels - 1 && Math.Max(t.Width >> mip, t.Height >> mip) > maxSize) mip++;
        int w = Math.Max(1, t.Width >> mip), h = Math.Max(1, t.Height >> mip);
        byte[] px = null;
        try { px = DDSIO.GetPixels(t, mip); } catch { }
        if (px != null && px.Length >= w * h * 4)
        {
            if (Math.Max(w, h) > maxSize) (px, w, h) = Downscale(px, w, h, maxSize);
            return "data:image/png;base64," + Convert.ToBase64String(Png.EncodeBgra(px, w, h));
        }

        // Fallback (z. B. BC7): Ã¼ber texconv
        var tmp = TempDir();
        try
        {
            var dds = Path.Combine(tmp, Safe(t.Name) + ".dds");
            File.WriteAllBytes(dds, DDSIO.GetDDSFile(t));
            double scale = Math.Min(1.0, maxSize / (double)Math.Max(t.Width, t.Height));
            int tw = Math.Max(4, (int)(t.Width * scale)), th = Math.Max(4, (int)(t.Height * scale));
            Texconv(new[] { "-ft", "png", "-f", "R8G8B8A8_UNORM", "-m", "1", "-w", tw.ToString(), "-h", th.ToString(), "-y", "-o", tmp, "--", dds });
            var png = Path.ChangeExtension(dds, ".png");
            return File.Exists(png) ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png)) : null;
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>Box-Filter-Verkleinerung (BGRA).</summary>
    static (byte[], int, int) Downscale(byte[] px, int w, int h, int maxSize)
    {
        int f = 1;
        while (Math.Max(w / f, h / f) > maxSize) f++;
        int nw = Math.Max(1, w / f), nh = Math.Max(1, h / f);
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (int dy = 0; dy < f; dy++)
                    for (int dx = 0; dx < f; dx++)
                    {
                        int i = ((y * f + dy) * w + (x * f + dx)) * 4;
                        b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3]; n++;
                    }
                int j = (y * nw + x) * 4;
                o[j] = (byte)(b / n); o[j + 1] = (byte)(g / n); o[j + 2] = (byte)(r / n); o[j + 3] = (byte)(a / n);
            }
        return (o, nw, nh);
    }

    // ------------------------------------------------------------- Export

    public static object Export(string file, List<string> names, string outDir, string format)
    {
        var c = Open(file);
        Directory.CreateDirectory(outDir);
        var list = TexList(c);
        if (names.Count > 0) list = list.Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var written = new List<string>();
        var warnings = new List<string>();
        int i = 0;
        foreach (var t in list)
        {
            Program.Progress($"Exportiere {t.Name} â€¦", (int)(100.0 * i++ / Math.Max(1, list.Count)));
            var dds = DDSIO.GetDDSFile(t);
            var baseName = Safe(t.Name);
            if (format == "dds")
            {
                var target = Path.Combine(outDir, baseName + ".dds");
                File.WriteAllBytes(target, dds);
                written.Add(target);
                continue;
            }
            var tmp = TempDir();
            try
            {
                var ddsPath = Path.Combine(tmp, baseName + ".dds");
                File.WriteAllBytes(ddsPath, dds);
                Texconv(new[] { "-ft", "png", "-f", "R8G8B8A8_UNORM", "-m", "1", "-y", "-o", outDir, "--", ddsPath });
                var png = Path.Combine(outDir, baseName + ".png");
                if (File.Exists(png)) written.Add(png); else warnings.Add($"{t.Name}: PNG-Export fehlgeschlagen");
            }
            catch (Exception ex) { warnings.Add($"{t.Name}: {ex.Message}"); }
            finally { TryDelete(tmp); }
        }
        return new { files = written, warnings };
    }

    // ------------------------------------------------------------- Ersetzen / HinzufÃ¼gen / Entfernen

    public static object Replace(string file, string name, string image, string backupDir)
    {
        var c = Open(file);
        var list = TexList(c);
        var old = Find(c, name);
        var neu = BuildTexture(image, old.Name, old.Format, keepFormatFromDds: false);
        list[list.IndexOf(old)] = neu;
        RelinkShaders(c, old, neu);
        Write(c, list, backupDir);
        return new { name = neu.Name, width = (int)neu.Width, height = (int)neu.Height, format = FormatLabel(neu.Format) };
    }

    public static object Add(string file, List<string> images, string backupDir)
    {
        var c = Open(file, createEmbedded: true);
        var list = TexList(c);
        var added = new List<string>();
        foreach (var img in images)
        {
            var name = Safe(Path.GetFileNameWithoutExtension(img));
            var existing = list.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            var fmt = existing?.Format ?? (name.EndsWith("_n") ? TextureFormat.D3DFMT_ATI2 : TextureFormat.D3DFMT_DXT5);
            var tex = BuildTexture(img, name, fmt, keepFormatFromDds: true);
            if (existing != null) { list[list.IndexOf(existing)] = tex; RelinkShaders(c, existing, tex); }
            else list.Add(tex);
            added.Add(name);
        }
        Write(c, list, backupDir);
        return new { added };
    }

    public static object Remove(string file, List<string> names, string backupDir)
    {
        var c = Open(file);
        var list = TexList(c);
        var removed = list.RemoveAll(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        if (list.Count == 0) throw new UserError("Eine Texturdatei braucht mindestens eine Textur.");
        Write(c, list, backupDir);
        return new { removed };
    }

    internal static void RelinkShaders(Container c, Texture oldTex, Texture newTex)
    {
        foreach (var sg in c.ShaderGroups)
        {
            if (sg.Shaders?.data_items == null) continue;
            foreach (var sh in sg.Shaders.data_items)
            {
                var ps = sh?.ParametersList?.Parameters;
                if (ps == null) continue;
                foreach (var p in ps)
                    if (ReferenceEquals(p.Data, oldTex)) p.Data = newTex;
            }
        }
    }

    /// <summary>Erzeugt aus einem Bild (PNG/JPG/TGA/BMP/DDS) eine GTA-Textur mit Mipmaps.</summary>
    internal static Texture BuildTexture(string image, string name, TextureFormat format, bool keepFormatFromDds)
    {
        if (!File.Exists(image)) throw new UserError($"Bild nicht gefunden: {image}");
        byte[] dds;
        if (image.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && keepFormatFromDds)
        {
            dds = File.ReadAllBytes(image);
        }
        else
        {
            var fmt = TexconvFormat(format);
            var tmp = TempDir();
            try
            {
                var src = Path.Combine(tmp, "src" + Path.GetExtension(image).ToLowerInvariant());
                File.Copy(image, src, true);
                var outDir = Path.Combine(tmp, "out");
                Directory.CreateDirectory(outDir);
                bool bc = fmt.StartsWith("BC");
                // 1. Durchgang: GrÃ¶ÃŸe bestimmen
                var args1 = new List<string> { "-f", fmt, "-m", "1", "-y", "-o", outDir };
                if (bc) args1.Add("-pow2");
                args1.AddRange(new[] { "--", src });
                Texconv(args1.ToArray());
                var first = Path.Combine(outDir, "src.dds");
                if (!File.Exists(first)) throw new UserError("Das Bild konnte nicht umgewandelt werden.");
                var hdr = File.ReadAllBytes(first);
                int h = BitConverter.ToInt32(hdr, 12), w = BitConverter.ToInt32(hdr, 16);
                // Mipmaps bis 4Ã—4 (wie GTA)
                int mips = 1;
                while (Math.Min(w >> mips, h >> mips) >= 4) mips++;
                if (mips > 1)
                {
                    var args2 = new List<string> { "-f", fmt, "-m", mips.ToString(), "-y", "-o", outDir };
                    if (bc) args2.Add("-pow2");
                    args2.AddRange(new[] { "--", src });
                    Texconv(args2.ToArray());
                }
                dds = File.ReadAllBytes(first);
            }
            finally { TryDelete(tmp); }
        }
        var tex = DDSIO.GetTexture(dds);
        tex.Name = name;
        tex.NameHash = JenkHash.GenHash(name.ToLowerInvariant());
        return tex;
    }

    // ------------------------------------------------------------- Hilfen

    static string TexconvFormat(TextureFormat f) => f switch
    {
        TextureFormat.D3DFMT_DXT1 => "BC1_UNORM",
        TextureFormat.D3DFMT_DXT3 => "BC2_UNORM",
        TextureFormat.D3DFMT_DXT5 => "BC3_UNORM",
        TextureFormat.D3DFMT_ATI1 => "BC4_UNORM",
        TextureFormat.D3DFMT_ATI2 => "BC5_UNORM",
        TextureFormat.D3DFMT_BC7 => "BC7_UNORM",
        TextureFormat.D3DFMT_A8R8G8B8 => "B8G8R8A8_UNORM",
        TextureFormat.D3DFMT_X8R8G8B8 => "B8G8R8X8_UNORM",
        TextureFormat.D3DFMT_A8B8G8R8 => "R8G8B8A8_UNORM",
        TextureFormat.D3DFMT_L8 => "R8_UNORM",
        TextureFormat.D3DFMT_A8 => "A8_UNORM",
        TextureFormat.D3DFMT_A1R5G5B5 => "B5G5R5A1_UNORM",
        _ => "BC3_UNORM",
    };

    public static string FormatLabel(TextureFormat f) => f switch
    {
        TextureFormat.D3DFMT_DXT1 => "DXT1",
        TextureFormat.D3DFMT_DXT3 => "DXT3",
        TextureFormat.D3DFMT_DXT5 => "DXT5",
        TextureFormat.D3DFMT_ATI1 => "BC4",
        TextureFormat.D3DFMT_ATI2 => "BC5 (Normal)",
        TextureFormat.D3DFMT_BC7 => "BC7",
        TextureFormat.D3DFMT_A8R8G8B8 => "ARGB8",
        TextureFormat.D3DFMT_A8B8G8R8 => "ABGR8",
        _ => f.ToString().Replace("D3DFMT_", ""),
    };

    public static string Safe(string name)
    {
        var s = new string((name ?? "textur").ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
        return s.Length == 0 ? "textur" : s;
    }

    public static string TexconvPath()
    {
        var env = Environment.GetEnvironmentVariable("GTA_TOOL_TEXCONV");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var local = Path.Combine(AppContext.BaseDirectory, "texconv.exe");
        if (File.Exists(local)) return local;
        throw new UserError("texconv.exe wurde nicht gefunden.");
    }

    public static void Texconv(string[] args)
    {
        var psi = new ProcessStartInfo(TexconvPath())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-nologo");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new UserError("texconv hat zu lange gebraucht."); }
        if (p.ExitCode != 0)
        {
            var msg = (stdout.Result + " " + stderr.Result).Trim();
            if (msg.Length > 300) msg = msg[..300];
            throw new UserError($"Bildumwandlung fehlgeschlagen: {msg}");
        }
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "GTATool", "tmp", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
