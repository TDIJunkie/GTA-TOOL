using System.Text.RegularExpressions;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Folierungen (Liveries) über den Lack-Shader (vehicle_paint3 …, Slot DiffuseSampler2).
/// GTA erkennt die Folien an den Texturnamen  &lt;basis&gt;_1, &lt;basis&gt;_2 … (z. B. r34_sign_1).
/// Die Folie liegt auf dem zweiten UV-Kanal (TexCoord1), falls vorhanden.
/// </summary>
public static class Livery
{
    static uint H(string s) => JenkHash.GenHash(s.ToLowerInvariant());
    public static readonly uint LiverySampler = H("DiffuseSampler2");
    static readonly Regex Numbered = new(@"^(.*)_(\d+)$", RegexOptions.Compiled);

    class Slot
    {
        public string Texture;
        public string Base;
        public HashSet<ushort> ShaderIds = new();
    }

    static List<Slot> FindSlots(YftFile yft, out DrawableBase drawable)
    {
        drawable = yft.Fragment?.Drawable;
        var slots = new List<Slot>();
        var shaders = drawable?.ShaderGroup?.Shaders?.data_items;
        if (shaders == null) return slots;
        for (int i = 0; i < shaders.Length; i++)
        {
            var s = shaders[i];
            var ps = s?.ParametersList?.Parameters;
            var hs = s?.ParametersList?.Hashes;
            if (ps == null || hs == null) continue;
            for (int k = 0; k < ps.Length && k < hs.Length; k++)
            {
                if ((uint)hs[k] != LiverySampler || ps[k].Data is not TextureBase tb || string.IsNullOrEmpty(tb.Name)) continue;
                var m = Numbered.Match(tb.Name);
                var bas = m.Success ? m.Groups[1].Value : tb.Name;
                var slot = slots.FirstOrDefault(x => x.Base.Equals(bas, StringComparison.OrdinalIgnoreCase));
                if (slot == null) { slot = new Slot { Texture = tb.Name, Base = bas }; slots.Add(slot); }
                slot.ShaderIds.Add((ushort)i);
            }
        }
        return slots;
    }

    static YftFile LoadYft(string file)
    {
        if (!File.Exists(file)) throw new UserError($"Modell nicht gefunden: {Path.GetFileName(file)}");
        var data = File.ReadAllBytes(file);
        if (data.Length < 4 || data[0] != 'R') throw new UserError("Das Modell ist verschlüsselt (Escrow).");
        var yft = new YftFile();
        yft.Load(data);
        return yft;
    }

    /// <summary>Alle Folien des Fahrzeugs.</summary>
    public static object Info(string yftFile, List<string> ytds)
    {
        var yft = LoadYft(yftFile);
        var slots = FindSlots(yft, out _);
        var list = new List<object>();
        string targetYtd = null;
        foreach (var slot in slots)
        {
            foreach (var y in ytds)
            {
                if (!File.Exists(y) || Session.IsEncrypted(Path.GetDirectoryName(y)!, Path.GetFileName(y))) continue;
                Textures.Container c;
                try { c = Textures.Open(y); } catch { continue; }
                foreach (var t in Textures.TexList(c))
                {
                    var m = Numbered.Match(t.Name ?? "");
                    bool match = m.Success ? m.Groups[1].Value.Equals(slot.Base, StringComparison.OrdinalIgnoreCase)
                                           : string.Equals(t.Name, slot.Base, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    targetYtd ??= y;
                    string thumb = null;
                    try { thumb = Textures.ThumbPublic(t, 256); } catch { }
                    bool? empty = null;
                    try { empty = IsEmpty(t); } catch { }
                    list.Add(new
                    {
                        name = t.Name,
                        number = m.Success ? int.Parse(m.Groups[2].Value) : 1,
                        @base = slot.Base,
                        ytd = y,
                        width = (int)t.Width,
                        height = (int)t.Height,
                        format = Textures.FormatLabel(t.Format),
                        thumb,
                        empty,
                    });
                }
            }
        }
        return new
        {
            supported = slots.Count > 0,
            bases = slots.Select(s => s.Base).ToList(),
            targetYtd = targetYtd ?? ytds.FirstOrDefault(),
            liveries = list.OrderBy(x => ((dynamic)x).number).ToList(),
        };
    }

    /// <summary>true, wenn die Folie (fast) komplett durchsichtig ist – typischer Platzhalter für „keine Folie“.</summary>
    static bool? IsEmpty(Texture t)
    {
        int mip = 0;
        while (mip < t.Levels - 1 && Math.Max(t.Width >> mip, t.Height >> mip) > 256) mip++;
        var px = DDSIO.GetPixels(t, mip);
        if (px == null) return null;
        int n = px.Length / 4, visible = 0;
        for (int i = 0; i < n; i += 3) if (px[i * 4 + 3] > 20) visible++;
        return visible < (n / 3) * 0.002;
    }

    // ------------------------------------------------------------------ UV-Vorlage

    /// <summary>Zeichnet die UV-Aufteilung aller Folien-Flächen als PNG (optional über einer vorhandenen Folie).</summary>
    public static object Template(string yftFile, int size, string outFile, string bgYtd, string bgName)
    {
        var yft = LoadYft(yftFile);
        var slots = FindSlots(yft, out var drawable);
        if (slots.Count == 0) throw new UserError("Dieses Fahrzeug hat keinen Folien-Slot (Lack-Shader ohne Livery-Textur).");
        var ids = slots.SelectMany(s => s.ShaderIds).ToHashSet();
        if (size < 256) size = 2048;

        var px = new byte[size * size * 4]; // BGRA
        if (!string.IsNullOrEmpty(bgYtd) && !string.IsNullOrEmpty(bgName))
        {
            try
            {
                var c = Textures.Open(bgYtd);
                var t = Textures.TexList(c).FirstOrDefault(x => string.Equals(x.Name, bgName, StringComparison.OrdinalIgnoreCase));
                var bg = t != null ? DDSIO.GetPixels(t, 0) : null;
                if (bg != null) Blit(bg, t.Width, t.Height, px, size);
            }
            catch (Exception ex) { Program.Log($"Vorlage-Hintergrund: {ex.Message}"); }
        }

        var models = drawable.DrawableModels?.High ?? drawable.AllModels ?? Array.Empty<DrawableModel>();
        int tris = 0;
        foreach (var model in models)
        {
            foreach (var g in model?.Geometries ?? Array.Empty<DrawableGeometry>())
            {
                if (!ids.Contains(g.ShaderID)) continue;
                var vd = g.VertexData; var idx = g.IndexBuffer?.Indices;
                if (vd?.Info == null || idx == null) continue;
                int ch = (vd.Info.Flags & (1u << 7)) != 0 ? 7 : 6;
                if ((vd.Info.Flags & (1u << ch)) == 0) continue;
                var uv = new Vector2[vd.VertexCount];
                for (int v = 0; v < uv.Length; v++) uv[v] = ReadUv(vd, v, ch);
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
                    FillTri(px, size, uv[a], uv[b], uv[c]);
                    tris++;
                }
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
                    Line(px, size, uv[a], uv[b]); Line(px, size, uv[b], uv[c]); Line(px, size, uv[c], uv[a]);
                }
            }
        }
        if (tris == 0) throw new UserError("Keine Folien-Flächen im Modell gefunden.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, Png.EncodeBgra(px, size, size));
        return new { file = outFile, size, triangles = tris };
    }

    public static Vector2 ReadUvPublic(VertexData vd, int v, int c) => ReadUv(vd, v, c);
    public static void BlitPublic(byte[] src, int sw, int sh, byte[] dst, int size) => Blit(src, sw, sh, dst, size);
    public static void FillTriPublic(byte[] px, int size, Vector2 a, Vector2 b, Vector2 c) => FillTri(px, size, a, b, c);
    public static void LinePublic(byte[] px, int size, Vector2 a, Vector2 b) => Line(px, size, a, b);

    static Vector2 ReadUv(VertexData vd, int v, int c)
    {
        var type = vd.Info.GetComponentType(c);
        int o = v * vd.Info.Stride + vd.Info.GetComponentOffset(c);
        var b = vd.VertexBytes;
        if (type == VertexComponentType.Float2 && o + 8 <= b.Length) return new Vector2(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4));
        if (type == VertexComponentType.Half2 && o + 4 <= b.Length)
            return new Vector2((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, o)), (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, o + 2)));
        return Vector2.Zero;
    }

    static void Blit(byte[] src, int sw, int sh, byte[] dst, int size)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx = x * sw / size, sy = y * sh / size;
                int si = (sy * sw + sx) * 4, di = (y * size + x) * 4;
                if (si + 3 < src.Length) { dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2]; dst[di + 3] = src[si + 3]; }
            }
    }

    static float Wrap(float f) { f -= MathF.Floor(f); return f; }

    // Fläche hellgrau, halbtransparent
    static void FillTri(byte[] px, int size, Vector2 a, Vector2 b, Vector2 c)
    {
        // Dreiecke, die über die Kachelgrenze laufen, an der gemeinsamen Kachel ausrichten
        float ox = MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), oy = MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)));
        var p0 = new Vector2((a.X - ox) * size, (a.Y - oy) * size);
        var p1 = new Vector2((b.X - ox) * size, (b.Y - oy) * size);
        var p2 = new Vector2((c.X - ox) * size, (c.Y - oy) * size);
        int minX = (int)Math.Max(0, MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        int maxX = (int)Math.Min(size - 1, MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        int minY = (int)Math.Max(0, MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        int maxY = (int)Math.Min(size - 1, MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        if (maxX - minX > size / 2 && maxY - minY > size / 2) return; // entartete Riesendreiecke
        float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(area) < 1e-6f) return;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float w0 = (p1.X - fx) * (p2.Y - fy) - (p2.X - fx) * (p1.Y - fy);
                float w1 = (p2.X - fx) * (p0.Y - fy) - (p0.X - fx) * (p2.Y - fy);
                float w2 = (p0.X - fx) * (p1.Y - fy) - (p1.X - fx) * (p0.Y - fy);
                bool inside = area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                if (!inside) continue;
                Blend(px, size, x, y, 200, 200, 200, 60);
            }
    }

    static void Line(byte[] px, int size, Vector2 a, Vector2 b)
    {
        float ox = MathF.Floor(Math.Min(a.X, b.X)), oy = MathF.Floor(Math.Min(a.Y, b.Y));
        float x0 = (a.X - ox) * size, y0 = (a.Y - oy) * size, x1 = (b.X - ox) * size, y1 = (b.Y - oy) * size;
        float len = MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
        if (len > size / 2f) return;
        int steps = Math.Max(1, (int)MathF.Ceiling(len));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = (int)(x0 + (x1 - x0) * t), y = (int)(y0 + (y1 - y0) * t);
            Blend(px, size, x, y, 20, 20, 20, 230);
        }
    }

    static void Blend(byte[] px, int size, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        int i = (y * size + x) * 4;
        float fa = a / 255f, da = px[i + 3] / 255f;
        float oa = fa + da * (1 - fa);
        if (oa <= 0) return;
        px[i] = (byte)((b * fa + px[i] * da * (1 - fa)) / oa);
        px[i + 1] = (byte)((g * fa + px[i + 1] * da * (1 - fa)) / oa);
        px[i + 2] = (byte)((r * fa + px[i + 2] * da * (1 - fa)) / oa);
        px[i + 3] = (byte)(oa * 255);
    }

    // ------------------------------------------------------------------ Hinzufügen / Neu / Löschen

    static int NextNumber(List<Texture> list, string bas)
    {
        int max = 0;
        foreach (var t in list)
        {
            var m = Numbered.Match(t.Name ?? "");
            if (m.Success && m.Groups[1].Value.Equals(bas, StringComparison.OrdinalIgnoreCase)) max = Math.Max(max, int.Parse(m.Groups[2].Value));
        }
        return max + 1;
    }

    static TextureFormat FormatOf(List<Texture> list, string bas) =>
        list.FirstOrDefault(t => Numbered.Match(t.Name ?? "") is { Success: true } m && m.Groups[1].Value.Equals(bas, StringComparison.OrdinalIgnoreCase))?.Format
        ?? TextureFormat.D3DFMT_DXT5;

    public static object Add(string ytd, string bas, List<string> images, string backupDir)
    {
        var c = Textures.Open(ytd);
        var list = Textures.TexList(c);
        var fmt = FormatOf(list, bas);
        var names = new List<string>();
        foreach (var img in images)
        {
            var name = $"{bas}_{NextNumber(list, bas)}";
            list.Add(Textures.BuildTexture(img, name, fmt, keepFormatFromDds: true));
            names.Add(name);
        }
        Textures.Write(c, list, backupDir);
        return new { added = names };
    }

    /// <summary>Neue, leere (durchsichtige) Folie anlegen.</summary>
    public static object Create(string ytd, string bas, int size, string backupDir)
    {
        if (size < 256) size = 2048;
        var tmp = Textures.TempDir();
        try
        {
            var png = Path.Combine(tmp, "leer.png");
            File.WriteAllBytes(png, Png.EncodeBgra(new byte[size * size * 4], size, size));
            var c = Textures.Open(ytd);
            var list = Textures.TexList(c);
            var name = $"{bas}_{NextNumber(list, bas)}";
            list.Add(Textures.BuildTexture(png, name, FormatOf(list, bas), keepFormatFromDds: false));
            Textures.Write(c, list, backupDir);
            return new { name };
        }
        finally { Textures.TryDelete(tmp); }
    }

    /// <summary>Folie löschen und die folgenden nachrücken lassen (GTA braucht eine lückenlose Reihe).</summary>
    public static object Remove(string ytd, string bas, string name, string backupDir)
    {
        var c = Textures.Open(ytd);
        var list = Textures.TexList(c);
        var target = list.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new UserError($"Folie {name} nicht gefunden.");
        if (list.Count == 1) throw new UserError("Die Texturdatei braucht mindestens eine Textur.");
        list.Remove(target);
        var rest = list.Select(t => (t, m: Numbered.Match(t.Name ?? "")))
            .Where(x => x.m.Success && x.m.Groups[1].Value.Equals(bas, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => int.Parse(x.m.Groups[2].Value)).ToList();
        int n = 1;
        var renamed = new List<string>();
        foreach (var (t, _) in rest)
        {
            var nn = $"{bas}_{n++}";
            if (!string.Equals(t.Name, nn, StringComparison.OrdinalIgnoreCase))
            {
                renamed.Add($"{t.Name} → {nn}");
                t.Name = nn;
                t.NameHash = JenkHash.GenHash(nn.ToLowerInvariant());
            }
        }
        Textures.Write(c, list, backupDir);
        return new { removed = name, renamed };
    }
}
