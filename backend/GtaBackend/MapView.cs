using System.Text;
using System.Text.Json;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Weltkarte zum Auswählen von Positionen:
///  – Radar-Geometrie des Spiels (x64e.rpf\levels\gta5\minimap.rpf, *.ydd) liegt in echten Weltkoordinaten
///    → wird zu einer flachen GLB mit Vertexfarben zusammengefasst (einmalig, Cache)
///  – Bodenhöhe aus heightmap.dat (Max-Höhe je Zelle)
/// </summary>
public static class MapView
{
    static RpfFile OpenRpf(string path)
    {
        var r = new RpfFile(path, Path.GetFileName(path));
        r.ScanStructure(null, null);
        return r;
    }

    static IEnumerable<(RpfFile owner, RpfFileEntry fe)> Walk(RpfFile f)
    {
        foreach (var e in f.AllEntries ?? new List<RpfEntry>()) if (e is RpfFileEntry fe) yield return (f, fe);
        foreach (var c in f.Children ?? new List<RpfFile>()) foreach (var x in Walk(c)) yield return x;
    }

    static void RequireGta(string gtaPath)
    {
        if (string.IsNullOrEmpty(gtaPath) || !File.Exists(Path.Combine(gtaPath, "GTA5.exe")))
            throw new UserError("Für die Karte wird der GTA-V-Ordner gebraucht (Einstellungen).");
        if (!Keys.Loaded) throw new UserError("Die GTA-Schlüssel sind nicht geladen.");
    }

    /// <summary>Karte als GLB (Y oben, flach) in outFile – nur wenn noch nicht vorhanden.</summary>
    public static object Build(string gtaPath, string outFile)
    {
        if (File.Exists(outFile)) return new { file = outFile, cached = true };
        RequireGta(gtaPath);
        Program.Progress("Karte wird aus GTA gelesen …", 5);
        var rpf = OpenRpf(Path.Combine(gtaPath, "x64e.rpf"));
        var entries = Walk(rpf).Where(x => x.fe.Path.Replace('\\', '/').Contains("levels/gta5/minimap.rpf/", StringComparison.OrdinalIgnoreCase)
            && x.fe.Name.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0) throw new UserError("Die Kartendaten wurden in x64e.rpf nicht gefunden.");
        var pos = new List<float>(); var col = new List<byte>(); var idx = new List<uint>();
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        int n = 0;
        foreach (var (owner, fe) in entries)
        {
            n++;
            if (n % 10 == 0) Program.Progress($"Karte wird aufgebaut ({n}/{entries.Count}) …", 5 + 90 * n / entries.Count);
            try
            {
                var data = owner.ExtractFile(fe);
                if (data == null) continue;
                var ydd = new YddFile();
                ydd.Load(data, fe);
                foreach (var d in ydd.Drawables ?? Array.Empty<Drawable>())
                foreach (var m in d?.AllModels ?? Array.Empty<DrawableModel>())
                foreach (var g in m?.Geometries ?? Array.Empty<DrawableGeometry>())
                {
                    var vd = g.VertexData; var ib = g.IndexBuffer?.Indices;
                    if (vd?.Info == null || ib == null) continue;
                    bool hasCol = (vd.Info.Flags & 16) != 0;
                    uint baseIdx = (uint)(pos.Count / 3);
                    for (int v = 0; v < vd.VertexCount; v++)
                    {
                        var p = vd.GetVector3(v, 0);
                        // GTA (x, y) → glTF (x, -y); Höhe flach (0), damit die Karte eine Ebene ist
                        pos.Add(p.X); pos.Add(p.Z); pos.Add(-p.Y);   // Höhe = Ebenen-Reihenfolge der Radarkarte
                        min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                        var c = hasCol ? vd.GetColour(v, 4) : new Color(120, 120, 120, 255);
                        col.Add(c.R); col.Add(c.G); col.Add(c.B); col.Add(255);
                    }
                    for (int t = 0; t + 2 < ib.Length; t += 3)
                    {
                        if (ib[t] >= vd.VertexCount || ib[t + 1] >= vd.VertexCount || ib[t + 2] >= vd.VertexCount) continue;
                        idx.Add(baseIdx + ib[t]); idx.Add(baseIdx + ib[t + 2]); idx.Add(baseIdx + ib[t + 1]);
                    }
                }
            }
            catch (Exception ex) { Program.Log($"Karte {fe.Name}: {ex.Message}"); }
        }
        if (idx.Count == 0) throw new UserError("Die Karte konnte nicht aufgebaut werden.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, ColoredGlb(pos, col, idx));
        Program.Progress("Fertig.", 100);
        return new { file = outFile, cached = false, tiles = entries.Count, triangles = idx.Count / 3, min = new[] { min.X, min.Y }, max = new[] { max.X, max.Y } };
    }

    /// <summary>Ein Netz mit Vertexfarben (unbeleuchtet) als GLB.</summary>
    static byte[] ColoredGlb(List<float> pos, List<byte> col, List<uint> idx)
    {
        var bin = new MemoryStream();
        var views = new List<object>(); var accessors = new List<object>();
        int View(byte[] d, int target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            long off = bin.Length; bin.Write(d);
            views.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = d.Length, ["target"] = target });
            return views.Count - 1;
        }
        var pb = new byte[pos.Count * 4]; Buffer.BlockCopy(pos.ToArray(), 0, pb, 0, pb.Length);
        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < pos.Count; i++) { mn[i % 3] = Math.Min(mn[i % 3], pos[i]); mx[i % 3] = Math.Max(mx[i % 3], pos[i]); }
        accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(pb, 34962), ["componentType"] = 5126, ["count"] = pos.Count / 3, ["type"] = "VEC3", ["min"] = mn, ["max"] = mx });
        accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(col.ToArray(), 34962), ["componentType"] = 5121, ["normalized"] = true, ["count"] = col.Count / 4, ["type"] = "VEC4" });
        var ib = new byte[idx.Count * 4]; Buffer.BlockCopy(idx.ToArray(), 0, ib, 0, ib.Length);
        accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(ib, 34963), ["componentType"] = 5125, ["count"] = idx.Count, ["type"] = "SCALAR" });
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "GTA Tool" },
            ["extensionsUsed"] = new[] { "KHR_materials_unlit" },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = new[] { 0 } } },
            ["nodes"] = new[] { new Dictionary<string, object> { ["mesh"] = 0, ["name"] = "karte" } },
            ["meshes"] = new[] { new Dictionary<string, object> { ["primitives"] = new[] { new Dictionary<string, object> {
                ["attributes"] = new Dictionary<string, object> { ["POSITION"] = 0, ["COLOR_0"] = 1 }, ["indices"] = 2, ["material"] = 0 } } } },
            ["materials"] = new[] { new Dictionary<string, object> {
                ["name"] = "karte", ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = new Dictionary<string, object> { ["baseColorFactor"] = new[] { 1f, 1f, 1f, 1f } },
                ["extensions"] = new Dictionary<string, object> { ["KHR_materials_unlit"] = new Dictionary<string, object>() } } },
            ["accessors"] = accessors, ["bufferViews"] = views,
            ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = bin.Length } },
        };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gltf));
        int pad = (4 - json.Length % 4) % 4;
        var binBytes = bin.ToArray();
        using var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + json.Length + pad + 8 + binBytes.Length));
        w.Write((uint)(json.Length + pad)); w.Write(0x4E4F534Au); w.Write(json);
        for (int i = 0; i < pad; i++) w.Write((byte)0x20);
        w.Write((uint)binBytes.Length); w.Write(0x004E4942u); w.Write(binBytes);
        w.Flush();
        return o.ToArray();
    }

    // ------------------------------------------------------------------ Bodenhöhe

    static HeightmapFile heightmap;

    static HeightmapFile LoadHeightmap(string gtaPath)
    {
        if (heightmap != null) return heightmap;
        RequireGta(gtaPath);
        foreach (var (arch, inner) in new[] { ("update/update.rpf", "common/data/levels/gta5/heightmap.dat"), ("common.rpf", "data/levels/gta5/heightmap.dat") })
        {
            var p = Path.Combine(gtaPath, arch);
            if (!File.Exists(p)) continue;
            var rpf = OpenRpf(p);
            var hit = Walk(rpf).FirstOrDefault(x => x.fe.Path.Replace('\\', '/').EndsWith(inner, StringComparison.OrdinalIgnoreCase));
            if (hit.fe == null) continue;
            var data = hit.owner.ExtractFile(hit.fe);
            var hm = new HeightmapFile();
            hm.Load(data, hit.fe);
            heightmap = hm;
            return hm;
        }
        throw new UserError("heightmap.dat wurde nicht gefunden.");
    }

    /// <summary>Ungefähre Bodenhöhe (Gelände/Gebäude-Oberkante der Zelle) an x/y.</summary>
    public static object Height(string gtaPath, float x, float y, bool flipY)
    {
        var hm = LoadHeightmap(gtaPath);
        float fx = (x - hm.BBMin.X) / (hm.BBMax.X - hm.BBMin.X);
        float fy = (y - hm.BBMin.Y) / (hm.BBMax.Y - hm.BBMin.Y);
        if (fx < 0 || fx > 1 || fy < 0 || fy > 1) return new { z = 0f, inside = false, min = 0f };
        int cx = Math.Clamp((int)(fx * hm.Width), 0, hm.Width - 1);
        int cy = Math.Clamp((int)(fy * hm.Height), 0, hm.Height - 1);
        if (flipY) cy = hm.Height - 1 - cy;
        float range = hm.BBMax.Z - hm.BBMin.Z;
        float zMax = hm.BBMin.Z + hm.MaxHeights[cy * hm.Width + cx] / 255f * range;
        float zMin = hm.BBMin.Z + hm.MinHeights[cy * hm.Width + cx] / 255f * range;
        return new { z = zMax, min = zMin, inside = true, cell = new[] { cx, cy }, size = new[] { (int)hm.Width, (int)hm.Height } };
    }
}
