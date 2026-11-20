using System.Text;
using System.Text.Json;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Wandelt eine .yft (Fahrzeug oder Tuningteil) in eine GLB-Datei für die 3D-Vorschau um.
/// – höchste LOD-Stufe, Teile an ihren Bone-Positionen, Räder an allen Radpositionen
/// – Texturen aus eingebetteten Texturen und den angegebenen .ytd-Dateien (max. 1024 px)
/// – GTA (Z oben) → glTF (Y oben)
/// </summary>
public static class Model3D
{
    static readonly HashSet<ushort> WheelFront = new() { 27922, 26418 };
    static readonly HashSet<ushort> WheelRear = new() { 29921, 29922, 29923, 27902, 5857, 5858, 5859, 26398 };
    static readonly HashSet<ushort> WheelRight = new() { 26418, 5857, 5858, 5859, 26398 };

    static uint H(string s) => JenkHash.GenHash(s.ToLowerInvariant());
    static readonly uint DiffuseSampler = H("DiffuseSampler");
    static readonly uint PlateBgSampler = H("platebgsampler");
    static readonly HashSet<uint> PaintShaders = new(new[] {
        "vehicle_paint1", "vehicle_paint1_enveff", "vehicle_paint2", "vehicle_paint2_enveff", "vehicle_paint3", "vehicle_paint3_enveff",
        "vehicle_paint3_lvr", "vehicle_paint4", "vehicle_paint4_emissive", "vehicle_paint4_enveff", "vehicle_paint5_enveff",
        "vehicle_paint6", "vehicle_paint6_enveff", "vehicle_paint7", "vehicle_paint7_enveff", "vehicle_paint8", "vehicle_paint9" }.Select(H));
    static readonly HashSet<uint> GlassShaders = new(new[] { "vehicle_vehglass", "vehicle_vehglass_inner", "glass", "glass_pv", "glass_env" }.Select(H));
    static readonly HashSet<uint> AlphaShaders = new(new[] {
        "vehicle_decal", "vehicle_decal2", "vehicle_badges", "vehicle_cutout", "vehicle_emissive_alpha", "vehicle_lightsemissive",
        "vehicle_mesh2_enveff", "vehicle_blurredrotor", "vehicle_blurredrotor_emissive", "decal", "cutout" }.Select(H));
    static readonly HashSet<uint> TireShaders = new(new[] { "vehicle_tire", "vehicle_tire_emissive" }.Select(H));

    // ------------------------------------------------------------------ Aufbau

    class Prim
    {
        public List<float> Pos = new(), Nrm = new(), Uv = new(), Uv2 = new();
        public List<uint> Idx = new();
        public int Material;
        public List<ushort> Joints;     // nur Rig (Skin): 4 Bone-Indizes je Vertex
        public List<float> Weights;     // nur Rig: 4 Gewichte je Vertex
    }

    class Mat
    {
        public string Name;
        public int Texture = -1;   // Index in Images
        public int TexCoord = 0;   // UV-Kanal der Textur (Folie: 1)
        public float[] Color = { 0.8f, 0.8f, 0.8f, 1f };
        public string Alpha = "OPAQUE";
        public float Metallic = 0f, Roughness = 0.6f;
        public bool DoubleSided;
    }

    class Builder
    {
        public List<Prim> Prims = new();
        public List<Mat> Mats = new();
        public List<byte[]> Images = new();
        public Dictionary<string, int> MatIndex = new();
        public Dictionary<string, int> ImgIndex = new();
        public Dictionary<string, Texture> TexByName = new(StringComparer.OrdinalIgnoreCase);
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
        public int Triangles;
        public int Missing;
        public string Livery;      // null = Standard (Folie aus dem Modell), "-" = keine, sonst Texturname
        public Texture FallbackDiffuse; // Kleidung: Textur der gewählten Variante
        public string LiveryShown;
        public Bone[] RigBones;         // Rig: Skelett (GTA-Raum), Wurzel wird nach Y-oben gedreht
        public Matrix[] RigAbs;         // Rig: absolute Grundpose je Bone
    }

    // ------------------------------------------------------------------ Rig (bewegliches Modell für Animationen)

    /// <summary>
    /// Freemode-Körper mit Skelett als Skin-GLB: Bones als Knoten (Name, extras.tag), Meshes mit JOINTS_0/WEIGHTS_0.
    /// Alles bleibt im GTA-Raum; ein Wurzelknoten „GTA“ dreht Z-oben → Y-oben. So passen die YCD-Werte direkt.
    /// </summary>
    public static object ExportRig(string skeletonYft, List<string> models, List<string> ytds, string outFile)
    {
        if (!File.Exists(skeletonYft)) throw new UserError("Das Skelett (.yft) wurde nicht gefunden.");
        var yft = new YftFile();
        yft.Load(File.ReadAllBytes(skeletonYft));
        var bones = yft.Fragment?.Drawable?.Skeleton?.Bones?.Items ?? throw new UserError("Die YFT enthält kein Skelett.");
        int n = bones.Length;
        var local = new Matrix[n];
        for (int i = 0; i < n; i++)
        {
            var bn = bones[i];
            var q = bn.Rotation; if (q.LengthSquared() < 1e-8f) q = Quaternion.Identity; q.Normalize();
            var s = bn.Scale; if (s.LengthSquared() < 1e-8f) s = Vector3.One;
            local[i] = Matrix.Scaling(s) * Matrix.RotationQuaternion(q) * Matrix.Translation(bn.Translation);
        }
        var abs = new Matrix[n];
        var done = new bool[n];
        Matrix Abs(int i, int depth)
        {
            if (done[i]) return abs[i];
            int p = bones[i].ParentIndex;
            abs[i] = (p >= 0 && p < n && p != i && depth < 256) ? local[i] * Abs(p, depth + 1) : local[i];
            done[i] = true;
            return abs[i];
        }
        for (int i = 0; i < n; i++) Abs(i, 0);

        var b = new Builder { RigBones = bones, RigAbs = abs };
        for (int k = 0; k < models.Count; k++)
        {
            var mf = models[k];
            if (!File.Exists(mf)) continue;
            var yt = k < ytds.Count ? ytds[k] : null;
            if (!string.IsNullOrEmpty(yt) && File.Exists(yt))
            {
                try
                {
                    var ytd = new YtdFile();
                    ytd.Load(File.ReadAllBytes(yt));
                    foreach (var t in ytd.TextureDict?.Textures?.data_items ?? Array.Empty<Texture>())
                        if (t != null && !string.IsNullOrEmpty(t.Name)) b.TexByName.TryAdd(t.Name, t);
                }
                catch (Exception ex) { Program.Log($"Rig-Textur {yt}: {ex.Message}"); }
            }
            try
            {
                var ydd = new YddFile();
                ydd.Load(File.ReadAllBytes(mf));
                foreach (var d in ydd.Drawables ?? Array.Empty<Drawable>())
                {
                    if (d == null) continue;
                    foreach (var t in d.ShaderGroup?.TextureDictionary?.Textures?.data_items ?? Array.Empty<Texture>())
                        if (t != null && !string.IsNullOrEmpty(t.Name)) b.TexByName.TryAdd(t.Name, t);
                    var ms = d.DrawableModels?.High;
                    if (ms == null || ms.Length == 0) ms = d.AllModels;
                    foreach (var model in ms ?? Array.Empty<DrawableModel>())
                    {
                        if (model?.Geometries == null) continue;
                        bool skin = ((model.SkeletonBinding >> 8) & 0xFF) > 0;
                        int bone = (int)((model.SkeletonBinding >> 24) & 0xFF);
                        foreach (var g in model.Geometries)
                        {
                            try { AddGeometryRig(b, g, d, skin, bone); }
                            catch (Exception ex) { Program.Log($"Rig-Geometrie übersprungen: {ex.Message}"); }
                        }
                    }
                }
            }
            catch (Exception ex) { Program.Log($"Rig {mf}: {ex.Message}"); }
        }
        if (b.Prims.Count == 0) throw new UserError("Der Körper enthält keine darstellbare Geometrie.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, WriteGlb(b));
        return new { file = outFile, triangles = b.Triangles, bones = n };
    }

    static void AddGeometryRig(Builder b, DrawableGeometry g, DrawableBase d, bool skin, int rigidBone)
    {
        var vd = g.VertexData;
        var idx = g.IndexBuffer?.Indices;
        if (vd?.Info == null || vd.VertexBytes == null || idx == null || idx.Length < 3) return;
        var flags = vd.Info.Flags;
        bool hasN = (flags & (1u << 3)) != 0, hasUv = (flags & (1u << 6)) != 0;
        bool hasW = (flags & (1u << 1)) != 0, hasI = (flags & (1u << 2)) != 0;
        int nb = b.RigBones.Length;
        if (rigidBone < 0 || rigidBone >= nb) rigidBone = 0;
        var m = skin ? Matrix.Identity : b.RigAbs[rigidBone];

        var shader = g.Shader;
        if (shader == null && d.ShaderGroup?.Shaders?.data_items is { } sh && g.ShaderID < sh.Length) shader = sh[g.ShaderID];
        var p = new Prim { Material = MaterialFor(b, shader), Uv2 = null, Joints = new List<ushort>(), Weights = new List<float>() };
        int n = vd.VertexCount;
        for (int v = 0; v < n; v++)
        {
            var pos = Read(vd, v, 0);
            var wp = Vector3.TransformCoordinate(new Vector3(Fin(pos.X), Fin(pos.Y), Fin(pos.Z)), m);
            p.Pos.Add(Fin(wp.X)); p.Pos.Add(Fin(wp.Y)); p.Pos.Add(Fin(wp.Z));
            if (hasN)
            {
                var nr = Read(vd, v, 3);
                var wn = Vector3.TransformNormal(new Vector3(Fin(nr.X), Fin(nr.Y), Fin(nr.Z)), m);
                if (wn.LengthSquared() > 1e-8f && float.IsFinite(wn.LengthSquared())) wn.Normalize(); else wn = Vector3.UnitZ;
                p.Nrm.Add(wn.X); p.Nrm.Add(wn.Y); p.Nrm.Add(wn.Z);
            }
            if (hasUv)
            {
                var uv = Read(vd, v, 6);
                p.Uv.Add(Fin(uv.X)); p.Uv.Add(Fin(uv.Y));
            }
            if (skin && hasI)
            {
                var bi = RawBytes4(vd, v, 2);
                var bw = hasW ? RawBytes4(vd, v, 1) : new byte[] { 255, 0, 0, 0 };
                float sum = bw[0] + bw[1] + bw[2] + bw[3];
                for (int k = 0; k < 4; k++)
                {
                    int local = bi[k];
                    int boneIdx = g.BoneIds != null && local < g.BoneIds.Length ? g.BoneIds[local] : local;
                    if (boneIdx < 0 || boneIdx >= nb) boneIdx = 0;
                    float w = sum > 0 ? bw[k] / sum : (k == 0 ? 1f : 0f);
                    p.Joints.Add((ushort)(w > 0 ? boneIdx : 0));
                    p.Weights.Add(w);
                }
            }
            else
            {
                p.Joints.Add((ushort)rigidBone); p.Joints.Add(0); p.Joints.Add(0); p.Joints.Add(0);
                p.Weights.Add(1f); p.Weights.Add(0f); p.Weights.Add(0f); p.Weights.Add(0f);
            }
        }
        int tris = idx.Length / 3;
        for (int t = 0; t < tris; t++)
        {
            uint a = idx[t * 3], c1 = idx[t * 3 + 1], c2 = idx[t * 3 + 2];
            if (a >= n || c1 >= n || c2 >= n) continue;
            p.Idx.Add(a); p.Idx.Add(c1); p.Idx.Add(c2);
        }
        if (p.Idx.Count == 0) return;
        if (!hasN) p.Nrm = null;
        if (!hasUv) p.Uv = null;
        b.Triangles += p.Idx.Count / 3;
        b.Prims.Add(p);
    }

    public static object Export(string file, List<string> ytds, string outFile, string attachYft = null, string attachBone = null, string livery = null, List<string> hideBones = null, bool variantTexture = false)
    {
        if (!File.Exists(file)) throw new UserError($"Modell nicht gefunden: {Path.GetFileName(file)}");
        var data = File.ReadAllBytes(file);
        if (data.Length < 4 || data[0] != 'R' || data[1] != 'S' || data[2] != 'C' || data[3] != '7')
            throw new UserError("Verschlüsselte Modelle (Escrow) können nicht angezeigt werden.");
        if (file.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase))
            return ExportYdd(data, ytds, outFile, attachYft, attachBone, variantTexture);
        if (file.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase))
            return ExportYdr(data, ytds, outFile);
        var yft = new YftFile();
        yft.Load(data);
        var frag = yft.Fragment ?? throw new UserError("Die YFT konnte nicht gelesen werden.");

        var b = new Builder { Livery = livery };
        // Texturen: eingebettet zuerst, dann die YTDs
        void AddDict(TextureDictionary td)
        {
            foreach (var t in td?.Textures?.data_items ?? Array.Empty<Texture>())
                if (t != null && !string.IsNullOrEmpty(t.Name)) b.TexByName.TryAdd(t.Name, t);
        }
        AddDict(frag.Drawable?.ShaderGroup?.TextureDictionary);
        foreach (var y in ytds)
        {
            if (!File.Exists(y)) continue;
            try
            {
                var yd = File.ReadAllBytes(y);
                if (yd.Length < 4 || yd[0] != 'R') continue;
                var ytd = new YtdFile();
                ytd.Load(yd);
                AddDict(ytd.TextureDict);
            }
            catch (Exception ex) { Program.Log($"3D: {y}: {ex.Message}"); }
        }

        // Tuningteil: an den Bone des Fahrzeugs hängen (carcols.meta <bone>)
        Matrix? attach = attachYft != null ? AttachMatrix(attachYft, attachBone) : null;

        // Hauptmodell mit Grundpose
        var main = frag.Drawable;
        if (main != null)
        {
            var pose = PoseMatrices(main, frag);
            HashSet<int> hidden = null;
            var boneItems = main.Skeleton?.Bones?.Items;
            if (hideBones != null && hideBones.Count > 0 && boneItems != null)
            {
                var names = new HashSet<string>(hideBones, StringComparer.OrdinalIgnoreCase);
                hidden = new HashSet<int>();
                for (int i = 0; i < boneItems.Length; i++) if (names.Contains(boneItems[i].Name ?? "")) hidden.Add(i);
                // Kind-Bones mit ausblenden (z. B. Teile, die an einer Stoßstange hängen)
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int i = 0; i < boneItems.Length; i++)
                        if (!hidden.Contains(i) && boneItems[i].ParentIndex >= 0 && hidden.Contains(boneItems[i].ParentIndex)) { hidden.Add(i); grew = true; }
                }
            }
            AddDrawable(b, main, pose, attach, hidden);
        }

        // Räder und weitere Kind-Modelle (wie CodeWalker)
        var pl1 = frag.PhysicsLODGroup?.PhysicsLOD1;
        var children = pl1?.Children?.data_items;
        var mats = pl1?.FragTransforms?.Matrices;
        if (children != null)
        {
            FragDrawable wf = null, wr = null;
            foreach (var ch in children)
            {
                if (ch?.Drawable1 == null || !HasModels(ch.Drawable1)) continue;
                if (WheelFront.Contains(ch.BoneTag)) wf ??= ch.Drawable1;
                else if (WheelRear.Contains(ch.BoneTag)) wr ??= ch.Drawable1;
            }
            for (int i = 0; i < children.Length; i++)
            {
                var ch = children[i];
                if (ch == null) continue;
                Matrix m = Matrix.Identity;
                if (mats != null && i < mats.Length)
                {
                    m = mats[i];
                    m.Row4 += new Vector4(pl1.PositionOffset, 0f);
                    // Frag-Matrizen sind 3×4 gespeichert – 4. Spalte auf (0,0,0,1) setzen
                    m.M14 = 0; m.M24 = 0; m.M34 = 0; m.M44 = 1;
                }
                bool isWheel = WheelFront.Contains(ch.BoneTag) || WheelRear.Contains(ch.BoneTag);
                if (isWheel)
                {
                    var d = (ch.Drawable1 != null && HasModels(ch.Drawable1)) ? ch.Drawable1
                        : (WheelFront.Contains(ch.BoneTag) ? (wf ?? wr) : (wr ?? wf));
                    if (d == null) continue;
                    if (WheelRight.Contains(ch.BoneTag))
                    {
                        m.M11 = -1; m.M12 = 0; m.M13 = 0;
                        m.M21 = 0; m.M22 = 1; m.M23 = 0;
                        m.M31 = 0; m.M32 = 0; m.M33 = -1;
                    }
                    AddDrawable(b, d, null, m);
                }
                else if (ch.Drawable1 != null && HasModels(ch.Drawable1) && ch.Drawable1 != main)
                {
                    AddDrawable(b, ch.Drawable1, null, m);
                }
            }
        }

        if (b.Prims.Count == 0) throw new UserError("Das Modell enthält keine darstellbare Geometrie.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, WriteGlb(b));
        return new
        {
            file = outFile,
            triangles = b.Triangles,
            materials = b.Mats.Count,
            textures = b.Images.Count,
            missingTextures = b.Missing,
            livery = b.LiveryShown,
            size = new[] { b.Max.X - b.Min.X, b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z },
        };
    }

    /// <summary>Position eines Fahrzeug-Bones (z. B. „boot“, „bonnet“) in der Grundpose.</summary>
    static Matrix? AttachMatrix(string vehicleYft, string boneName)
    {
        try
        {
            if (!File.Exists(vehicleYft)) return null;
            var yft = new YftFile();
            yft.Load(File.ReadAllBytes(vehicleYft));
            var d = yft.Fragment?.Drawable;
            var bones = d?.Skeleton?.Bones?.Items;
            if (bones == null) return null;
            var name = string.IsNullOrWhiteSpace(boneName) ? "chassis" : boneName.Trim();
            int idx = Array.FindIndex(bones, x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = Array.FindIndex(bones, x => string.Equals(x.Name, "chassis", StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return null;
            var pose = PoseMatrices(d, yft.Fragment);
            return pose != null && idx < pose.Length ? pose[idx] : null;
        }
        catch (Exception ex) { Program.Log($"3D-Anhängen: {ex.Message}"); return null; }
    }

    /// <summary>Kleidung / Körperteil (.ydd). Skin-Modelle liegen bereits in der Grundhaltung.</summary>
    static object ExportYdd(byte[] data, List<string> ytds, string outFile, string attachYft, string attachBone, bool variantTexture)
    {
        var ydd = new YddFile();
        ydd.Load(data);
        var b = new Builder();
        void AddDict(TextureDictionary td, bool first)
        {
            foreach (var t in td?.Textures?.data_items ?? Array.Empty<Texture>())
            {
                if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                b.TexByName.TryAdd(t.Name, t);
                var n = t.Name.ToLowerInvariant();
                if (first && variantTexture && b.FallbackDiffuse == null && !n.EndsWith("_n") && !n.EndsWith("_s") && !n.Contains("_normal") && !n.Contains("_spec"))
                    b.FallbackDiffuse = t;
            }
        }
        bool firstYtd = true;
        foreach (var y in ytds)
        {
            if (!File.Exists(y)) continue;
            try
            {
                var yd = File.ReadAllBytes(y);
                if (yd.Length < 4 || yd[0] != 'R') continue;
                var ytd = new YtdFile();
                ytd.Load(yd);
                AddDict(ytd.TextureDict, firstYtd);
                firstYtd = false;
            }
            catch (Exception ex) { Program.Log($"3D: {y}: {ex.Message}"); }
        }
        foreach (var d in ydd.Drawables ?? Array.Empty<Drawable>()) AddDict(d?.ShaderGroup?.TextureDictionary, false);

        // Requisiten (Hut, Brille …) hängen an einem Bone des Ped-Skeletts
        Matrix? attach = attachYft != null ? AttachMatrix(attachYft, attachBone) : null;
        foreach (var d in ydd.Drawables ?? Array.Empty<Drawable>())
        {
            if (d == null) continue;
            if (attach.HasValue) AddDrawable(b, d, null, attach);
            else AddDrawable(b, d, PoseMatrices(d, null), null);
        }
        if (b.Prims.Count == 0) throw new UserError("Das Modell enthält keine darstellbare Geometrie.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, WriteGlb(b));
        return new
        {
            file = outFile,
            triangles = b.Triangles,
            materials = b.Mats.Count,
            textures = b.Images.Count,
            missingTextures = b.Missing,
            size = new[] { b.Max.X - b.Min.X, b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z },
        };
    }

    /// <summary>Statisches Modell (.ydr), z. B. MLO-Objekte.</summary>
    static object ExportYdr(byte[] data, List<string> ytds, string outFile)
    {
        var ydr = new YdrFile();
        ydr.Load(data);
        var d = ydr.Drawable ?? throw new UserError("Die YDR konnte nicht gelesen werden.");
        var b = new Builder();
        void AddDict(TextureDictionary td)
        {
            foreach (var t in td?.Textures?.data_items ?? Array.Empty<Texture>())
                if (t != null && !string.IsNullOrEmpty(t.Name)) b.TexByName.TryAdd(t.Name, t);
        }
        AddDict(d.ShaderGroup?.TextureDictionary);
        foreach (var y in ytds)
        {
            if (!File.Exists(y)) continue;
            try
            {
                var yd = File.ReadAllBytes(y);
                if (yd.Length < 4 || yd[0] != 'R') continue;
                var ytd = new YtdFile();
                ytd.Load(yd);
                AddDict(ytd.TextureDict);
            }
            catch (Exception ex) { Program.Log($"3D: {y}: {ex.Message}"); }
        }
        AddDrawable(b, d, PoseMatrices(d, null), null);
        if (b.Prims.Count == 0) throw new UserError("Das Modell enthält keine darstellbare Geometrie.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, WriteGlb(b));
        return new { file = outFile, triangles = b.Triangles, materials = b.Mats.Count, textures = b.Images.Count, missingTextures = b.Missing,
            size = new[] { b.Max.X - b.Min.X, b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z } };
    }

    /// <summary>Einfaches einfarbiges Netz (GTA-Koordinaten) als GLB, z. B. für Kollisionen.</summary>
    public static byte[] SimpleGlb(List<Vector3> points, List<int> tris, float[] color)
    {
        var b = new Builder();
        var mat = new Mat { Name = "collision", Color = color, Alpha = color[3] < 1 ? "BLEND" : "OPAQUE", DoubleSided = true, Roughness = 0.9f };
        b.Mats.Add(mat);
        var p = new Prim { Material = 0, Nrm = null, Uv = null, Uv2 = null };
        foreach (var v in points) { p.Pos.Add(v.X); p.Pos.Add(v.Z); p.Pos.Add(-v.Y); }
        foreach (var i in tris) p.Idx.Add((uint)i);
        b.Prims.Add(p);
        b.Triangles = tris.Count / 3;
        return WriteGlb(b);
    }

    static bool HasModels(DrawableBase d) =>
        (d.DrawableModels?.High?.Length ?? 0) > 0 || (d.AllModels?.Length ?? 0) > 0;

    /// <summary>Absolute Bone-Matrizen (Modellraum) der Grundpose.</summary>
    static Matrix[] PoseMatrices(DrawableBase d, FragType frag)
    {
        var skel = d.Skeleton;
        var pose = frag?.BoneTransforms?.Items;
        // Manche Fahrzeuge haben eine leere Grundpose (alle Positionen 0) – dann das Skelett verwenden
        bool poseUsable = pose != null && pose.Length > 1 &&
            pose.Any(p => Math.Abs(p.Row1.W) + Math.Abs(p.Row2.W) + Math.Abs(p.Row3.W) > 1e-4f);
        if (poseUsable)
        {
            var r = new Matrix[pose.Length];
            for (int i = 0; i < pose.Length; i++)
            {
                var p = pose[i];
                Vector4 r1 = p.Row1, r2 = p.Row2, r3 = p.Row3;
                r[i] = new Matrix(r1.X, r2.X, r3.X, 0f, r1.Y, r2.Y, r3.Y, 0f, r1.Z, r2.Z, r3.Z, 0f, r1.W, r2.W, r3.W, 1f);
            }
            return r;
        }
        var t = skel?.Transformations;
        if (t == null) return null;
        var res = new Matrix[t.Length];
        var parents = skel.ParentIndices;
        for (int i = 0; i < t.Length; i++)
        {
            var m = t[i];
            m.Column4 = Vector4.UnitW;
            int pi = parents != null && i < parents.Length ? parents[i] : -1;
            int guard = 0;
            while (pi >= 0 && pi < t.Length && guard++ < 256)
            {
                var pm = t[pi];
                pm.Column4 = Vector4.UnitW;
                m = Matrix.Multiply(m, pm);
                pi = parents[pi];
            }
            res[i] = m;
        }
        return res;
    }

    static void AddDrawable(Builder b, DrawableBase d, Matrix[] pose, Matrix? fixedTransform, HashSet<int> hidden = null)
    {
        var models = d.DrawableModels?.High;
        if (models == null || models.Length == 0) models = d.AllModels;
        if (models == null) return;
        foreach (var model in models)
        {
            if (model?.Geometries == null) continue;
            bool skin = ((model.SkeletonBinding >> 8) & 0xFF) > 0;
            int bone = (int)((model.SkeletonBinding >> 24) & 0xFF);
            if (hidden != null && !skin && hidden.Contains(bone)) continue;
            Matrix m = Matrix.Identity;
            if (fixedTransform.HasValue) m = fixedTransform.Value;
            else if (!skin && pose != null && bone < pose.Length) m = pose[bone];
            foreach (var g in model.Geometries)
            {
                try { AddGeometry(b, g, m, d, skin ? hidden : null); }
                catch (Exception ex) { Program.Log($"3D-Geometrie übersprungen: {ex.Message}"); }
            }
        }
    }

    static void AddGeometry(Builder b, DrawableGeometry g, Matrix m, DrawableBase d, HashSet<int> hidden = null)
    {
        var vd = g.VertexData;
        var idx = g.IndexBuffer?.Indices;
        if (vd?.Info == null || vd.VertexBytes == null || idx == null || idx.Length < 3) return;
        var flags = vd.Info.Flags;
        bool hasN = (flags & (1u << 3)) != 0, hasUv = (flags & (1u << 6)) != 0, hasUv2 = (flags & (1u << 7)) != 0;

        var shader = g.Shader;
        if (shader == null && d.ShaderGroup?.Shaders?.data_items is { } sh && g.ShaderID < sh.Length) shader = sh[g.ShaderID];
        var p = new Prim { Material = MaterialFor(b, shader) };
        int n = vd.VertexCount;
        for (int v = 0; v < n; v++)
        {
            var pos = Read(vd, v, 0);
            var wp = Vector3.TransformCoordinate(new Vector3(Fin(pos.X), Fin(pos.Y), Fin(pos.Z)), m);
            wp = new Vector3(Fin(wp.X), Fin(wp.Y), Fin(wp.Z));
            // GTA (x, y, z) → glTF (x, z, -y)
            p.Pos.Add(wp.X); p.Pos.Add(wp.Z); p.Pos.Add(-wp.Y);
            b.Min = Vector3.Min(b.Min, new Vector3(wp.X, wp.Z, -wp.Y));
            b.Max = Vector3.Max(b.Max, new Vector3(wp.X, wp.Z, -wp.Y));
            if (hasN)
            {
                var nr = Read(vd, v, 3);
                var wn = Vector3.TransformNormal(new Vector3(Fin(nr.X), Fin(nr.Y), Fin(nr.Z)), m);
                if (wn.LengthSquared() > 1e-8f && float.IsFinite(wn.LengthSquared())) wn.Normalize(); else wn = Vector3.UnitZ;
                p.Nrm.Add(wn.X); p.Nrm.Add(wn.Z); p.Nrm.Add(-wn.Y);
            }
            if (hasUv)
            {
                var uv = Read(vd, v, 6);
                p.Uv.Add(Fin(uv.X)); p.Uv.Add(Fin(uv.Y));
            }
            var uv2 = hasUv2 ? Read(vd, v, 7) : (hasUv ? Read(vd, v, 6) : Vector4.Zero);
            p.Uv2.Add(Fin(uv2.X)); p.Uv2.Add(Fin(uv2.Y));
        }
        // Ausgeblendete Bones (turnOffBones): Vertices, deren Haupt-Bone versteckt ist
        bool[] hide = null;
        if (hidden != null && hidden.Count > 0 && (flags & (1u << 2)) != 0)
        {
            hide = new bool[n];
            bool hasW = (flags & (1u << 1)) != 0;
            for (int v = 0; v < n; v++)
            {
                var bi = RawBytes4(vd, v, 2);
                int best = 0;
                if (hasW)
                {
                    var bw = RawBytes4(vd, v, 1);
                    for (int k = 1; k < 4; k++) if (bw[k] > bw[best]) best = k;
                }
                int local = bi[best];
                int boneIdx = g.BoneIds != null && local < g.BoneIds.Length ? g.BoneIds[local] : local;
                hide[v] = hidden.Contains(boneIdx);
            }
        }
        // Spiegelung (rechte Räder) dreht die Dreiecksrichtung um
        bool flip = m.Determinant() < 0;
        int tris = idx.Length / 3;
        for (int t = 0; t < tris; t++)
        {
            uint a = idx[t * 3], c1 = idx[t * 3 + 1], c2 = idx[t * 3 + 2];
            if (a >= n || c1 >= n || c2 >= n) continue;
            if (hide != null && hide[a] && hide[c1] && hide[c2]) continue;
            if (flip) { p.Idx.Add(a); p.Idx.Add(c2); p.Idx.Add(c1); }
            else { p.Idx.Add(a); p.Idx.Add(c1); p.Idx.Add(c2); }
        }
        if (p.Idx.Count == 0) return;
        if (!hasN) p.Nrm = null;
        if (!hasUv) p.Uv = null;
        if (!hasUv && !hasUv2) p.Uv2 = null;
        b.Triangles += p.Idx.Count / 3;
        b.Prims.Add(p);
    }

    static byte[] RawBytes4(VertexData vd, int v, int c)
    {
        int o = v * vd.Info.Stride + vd.Info.GetComponentOffset(c);
        var b = vd.VertexBytes;
        return o + 4 <= b.Length ? new[] { b[o], b[o + 1], b[o + 2], b[o + 3] } : new byte[4];
    }

    static float Fin(float f) => float.IsFinite(f) && Math.Abs(f) < 1e6f ? f : 0f;

    /// <summary>Liest eine Vertex-Komponente unabhängig vom Speicherformat.</summary>
    static Vector4 Read(VertexData vd, int v, int c)
    {
        var info = vd.Info;
        var type = info.GetComponentType(c);
        int o = v * info.Stride + info.GetComponentOffset(c);
        var bytes = vd.VertexBytes;
        float F(int off) => o + off + 4 <= bytes.Length ? BitConverter.ToSingle(bytes, o + off) : 0f;
        float Hf(int off) => o + off + 2 <= bytes.Length ? (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, o + off)) : 0f;
        switch (type)
        {
            case VertexComponentType.Float: return new Vector4(F(0), 0, 0, 0);
            case VertexComponentType.Float2: return new Vector4(F(0), F(4), 0, 0);
            case VertexComponentType.Float3: return new Vector4(F(0), F(4), F(8), 0);
            case VertexComponentType.Float4: return new Vector4(F(0), F(4), F(8), F(12));
            case VertexComponentType.Half2: return new Vector4(Hf(0), Hf(2), 0, 0);
            case VertexComponentType.Half4: return new Vector4(Hf(0), Hf(2), Hf(4), Hf(6));
            case VertexComponentType.RGBA8SNorm: return vd.GetRGBA8SNorm(v, c);
            case VertexComponentType.Colour:
            case VertexComponentType.UByte4:
                if (o + 4 > bytes.Length) return Vector4.Zero;
                return new Vector4(bytes[o] / 255f, bytes[o + 1] / 255f, bytes[o + 2] / 255f, bytes[o + 3] / 255f);
            default: return Vector4.Zero;
        }
    }

    static int MaterialFor(Builder b, ShaderFX shader)
    {
        uint sname = shader?.Name.Hash ?? 0;
        string texName = null;
        var ps = shader?.ParametersList?.Parameters;
        var hs = shader?.ParametersList?.Hashes;
        if (ps != null && hs != null)
        {
            for (int i = 0; i < ps.Length && i < hs.Length; i++)
            {
                uint ph = (uint)hs[i];
                if ((ph == DiffuseSampler || ph == PlateBgSampler) && ps[i].Data is TextureBase tb && !string.IsNullOrEmpty(tb.Name))
                {
                    texName = tb.Name;
                    if (ph == DiffuseSampler) break;
                }
            }
        }
        string kind = PaintShaders.Contains(sname) ? "paint" : GlassShaders.Contains(sname) ? "glass"
            : TireShaders.Contains(sname) ? "tire" : AlphaShaders.Contains(sname) ? "alpha" : "basic";
        string liveryTex = null;
        if (kind == "paint" && ps != null && hs != null && b.Livery != "-")
        {
            for (int i = 0; i < ps.Length && i < hs.Length; i++)
                if ((uint)hs[i] == Livery.LiverySampler && ps[i].Data is TextureBase lt && !string.IsNullOrEmpty(lt.Name)) { liveryTex = lt.Name; break; }
            if (liveryTex != null && !string.IsNullOrEmpty(b.Livery)) liveryTex = b.Livery;
        }
        string key = kind + "|" + (texName ?? "") + "|" + (liveryTex ?? "");
        if (b.MatIndex.TryGetValue(key, out var mi)) return mi;

        var mat = new Mat { Name = kind == "paint" ? "paint" : (texName ?? kind) };
        switch (kind)
        {
            case "paint":
                // Lackfarbe kommt im Spiel zur Laufzeit – Vorschau in Silber; Decals im Lack werden ignoriert
                mat.Color = new[] { 0.62f, 0.64f, 0.67f, 1f };
                mat.Metallic = 0.6f; mat.Roughness = 0.3f;
                if (liveryTex != null)
                {
                    int li = ImageFor(b, liveryTex);
                    if (li >= 0) { mat.Texture = li; mat.TexCoord = 1; b.LiveryShown = liveryTex; }
                }
                break;
            case "glass":
                mat.Color = new[] { 0.08f, 0.1f, 0.12f, 0.45f };
                mat.Alpha = "BLEND"; mat.Metallic = 0.2f; mat.Roughness = 0.05f; mat.DoubleSided = true;
                break;
            case "tire":
                mat.Color = new[] { 0.12f, 0.12f, 0.12f, 1f }; mat.Roughness = 0.9f;
                break;
        }
        if (kind != "paint" && kind != "glass" && texName != null)
        {
            if (b.FallbackDiffuse != null && !b.TexByName.ContainsKey(texName) && !texName.ToLowerInvariant().EndsWith("_n"))
                texName = b.FallbackDiffuse.Name;
            if (b.FallbackDiffuse != null && kind == "basic" && texName != b.FallbackDiffuse.Name && texName.Contains("diff", StringComparison.OrdinalIgnoreCase))
                texName = b.FallbackDiffuse.Name;
            int img = ImageFor(b, texName);
            if (img >= 0)
            {
                mat.Texture = img;
                mat.Color = new[] { 1f, 1f, 1f, 1f };
                if (kind == "alpha") { mat.Alpha = "MASK"; mat.DoubleSided = true; }
            }
            else
            {
                b.Missing++;
                mat.Color = GuessColor(texName, mat.Color[3]);
                if (kind == "alpha") { mat.Alpha = "OPAQUE"; }
            }
        }
        else if (kind != "paint" && kind != "glass" && texName == null && kind != "tire")
        {
            mat.Color = new[] { 0.3f, 0.3f, 0.32f, mat.Color[3] };
        }
        mi = b.Mats.Count;
        b.Mats.Add(mat);
        b.MatIndex[key] = mi;
        return mi;
    }

    /// <summary>Ersatzfarbe für GTA-Standardtexturen, die nicht im Mod enthalten sind (vehshare usw.).</summary>
    static float[] GuessColor(string name, float alpha)
    {
        var n = name.ToLowerInvariant();
        float g;
        if (n.Contains("black") || n.Contains("tyre") || n.Contains("tire") || n.Contains("rubber") || n.Contains("carbon") || n.Contains("dark")) g = 0.04f;
        else if (n.Contains("plastic") || n.Contains("grill") || n.Contains("mesh") || n.Contains("dinghy")) g = 0.07f;
        else if (n.Contains("chrome") || n.Contains("metal") || n.Contains("silver")) g = 0.65f;
        else if (n.Contains("plate")) g = 0.85f;
        else if (n.Contains("white")) g = 0.9f;
        else if (n.Contains("red")) return new[] { 0.6f, 0.05f, 0.05f, alpha };
        else if (n.Contains("orange") || n.Contains("amber")) return new[] { 0.8f, 0.4f, 0.05f, alpha };
        else g = 0.18f;
        return new[] { g, g, g, alpha };
    }

    static int ImageFor(Builder b, string name)
    {
        if (b.ImgIndex.TryGetValue(name, out var i)) return i;
        int idx = -1;
        if (b.TexByName.TryGetValue(name, out var t))
        {
            try
            {
                var png = TexturePng(t, 1024);
                if (png != null) { idx = b.Images.Count; b.Images.Add(png); }
            }
            catch (Exception ex) { Program.Log($"3D-Textur {name}: {ex.Message}"); }
        }
        b.ImgIndex[name] = idx;
        return idx;
    }

    static byte[] TexturePng(Texture t, int maxSize)
    {
        var uri = Textures.ThumbPublic(t, maxSize);
        if (uri == null) return null;
        var comma = uri.IndexOf(',');
        return Convert.FromBase64String(uri[(comma + 1)..]);
    }

    // ------------------------------------------------------------------ GLB schreiben

    static byte[] WriteGlb(Builder b)
    {
        var bin = new MemoryStream();
        var views = new List<object>();
        var accessors = new List<object>();
        var meshes = new List<object>();
        var nodes = new List<object>();

        int View(byte[] data, int? target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            long off = bin.Length;
            bin.Write(data);
            var v = new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = data.Length };
            if (target.HasValue) v["target"] = target.Value;
            views.Add(v);
            return views.Count - 1;
        }
        int FloatAcc(List<float> f, int comps, string type, bool minmax)
        {
            var bytes = new byte[f.Count * 4];
            Buffer.BlockCopy(f.ToArray(), 0, bytes, 0, bytes.Length);
            var acc = new Dictionary<string, object>
            {
                ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = f.Count / comps, ["type"] = type,
            };
            if (minmax)
            {
                var mn = new float[comps]; var mx = new float[comps];
                for (int c = 0; c < comps; c++) { mn[c] = float.MaxValue; mx[c] = float.MinValue; }
                for (int i = 0; i < f.Count; i++) { int c = i % comps; mn[c] = Math.Min(mn[c], f[i]); mx[c] = Math.Max(mx[c], f[i]); }
                acc["min"] = mn; acc["max"] = mx;
            }
            accessors.Add(acc);
            return accessors.Count - 1;
        }

        var images = new List<object>();
        var textures = new List<object>();
        foreach (var img in b.Images)
        {
            images.Add(new Dictionary<string, object> { ["bufferView"] = View(img, null), ["mimeType"] = "image/png" });
            textures.Add(new Dictionary<string, object> { ["source"] = images.Count - 1, ["sampler"] = 0 });
        }

        var materials = b.Mats.Select(m =>
        {
            var pbr = new Dictionary<string, object>
            {
                ["baseColorFactor"] = m.Color, ["metallicFactor"] = m.Metallic, ["roughnessFactor"] = m.Roughness,
            };
            if (m.Texture >= 0) pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = m.Texture, ["texCoord"] = m.TexCoord };
            var md = new Dictionary<string, object> { ["name"] = m.Name, ["pbrMetallicRoughness"] = pbr, ["alphaMode"] = m.Alpha, ["doubleSided"] = m.DoubleSided };
            if (m.Alpha == "MASK") md["alphaCutoff"] = 0.3f;
            return (object)md;
        }).ToList();

        foreach (var p in b.Prims)
        {
            var attrs = new Dictionary<string, object> { ["POSITION"] = FloatAcc(p.Pos, 3, "VEC3", true) };
            if (p.Nrm != null) attrs["NORMAL"] = FloatAcc(p.Nrm, 3, "VEC3", false);
            if (p.Uv != null) attrs["TEXCOORD_0"] = FloatAcc(p.Uv, 2, "VEC2", false);
            if (p.Uv2 != null && b.Mats[p.Material].TexCoord == 1) attrs["TEXCOORD_1"] = FloatAcc(p.Uv2, 2, "VEC2", false);
            if (b.RigBones != null && p.Joints != null)
            {
                var jb = new byte[p.Joints.Count * 2];
                Buffer.BlockCopy(p.Joints.ToArray(), 0, jb, 0, jb.Length);
                accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(jb, 34962), ["componentType"] = 5123, ["count"] = p.Joints.Count / 4, ["type"] = "VEC4" });
                attrs["JOINTS_0"] = accessors.Count - 1;
                attrs["WEIGHTS_0"] = FloatAcc(p.Weights, 4, "VEC4", false);
            }
            var ib = new byte[p.Idx.Count * 4];
            Buffer.BlockCopy(p.Idx.ToArray(), 0, ib, 0, ib.Length);
            accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(ib, 34963), ["componentType"] = 5125, ["count"] = p.Idx.Count, ["type"] = "SCALAR" });
            int ia = accessors.Count - 1;
            meshes.Add(new Dictionary<string, object>
            {
                ["primitives"] = new[] { new Dictionary<string, object> { ["attributes"] = attrs, ["indices"] = ia, ["material"] = p.Material } },
            });
            var node = new Dictionary<string, object> { ["mesh"] = meshes.Count - 1 };
            if (b.RigBones != null) node["skin"] = 0;
            nodes.Add(node);
        }
        var sceneNodes = Enumerable.Range(0, nodes.Count).ToList();
        List<object> skins = null;
        if (b.RigBones != null)
        {
            // Wurzel „GTA“ (Z oben → Y oben), darunter die Bones
            var bones = b.RigBones;
            int axis = nodes.Count;
            int first = axis + 1;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var children = new List<int>[bones.Length];
            for (int i = 0; i < bones.Length; i++) children[i] = new List<int>();
            var roots = new List<int>();
            for (int i = 0; i < bones.Length; i++)
            {
                int pi = bones[i].ParentIndex;
                if (pi >= 0 && pi < bones.Length && pi != i) children[pi].Add(first + i); else roots.Add(first + i);
            }
            nodes.Add(new Dictionary<string, object> { ["name"] = "GTA", ["rotation"] = new[] { -0.70710678f, 0f, 0f, 0.70710678f }, ["children"] = roots });
            for (int i = 0; i < bones.Length; i++)
            {
                var bn = bones[i];
                var name = string.IsNullOrEmpty(bn.Name) ? $"bone_{i}" : bn.Name;
                if (!used.Add(name)) { name = $"{name}_{i}"; used.Add(name); }
                var q = bn.Rotation; if (q.LengthSquared() < 1e-8f) q = Quaternion.Identity; q.Normalize();
                var s = bn.Scale; if (s.LengthSquared() < 1e-8f) s = Vector3.One;
                var nd = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["translation"] = new[] { bn.Translation.X, bn.Translation.Y, bn.Translation.Z },
                    ["rotation"] = new[] { q.X, q.Y, q.Z, q.W },
                    ["scale"] = new[] { s.X, s.Y, s.Z },
                    ["extras"] = new Dictionary<string, object> { ["tag"] = (int)bn.Tag },
                };
                if (children[i].Count > 0) nd["children"] = children[i];
                nodes.Add(nd);
            }
            var ibm = new List<float>(bones.Length * 16);
            for (int i = 0; i < bones.Length; i++)
            {
                var inv = Matrix.Invert(b.RigAbs[i]);
                ibm.AddRange(new[] { inv.M11, inv.M12, inv.M13, inv.M14, inv.M21, inv.M22, inv.M23, inv.M24, inv.M31, inv.M32, inv.M33, inv.M34, inv.M41, inv.M42, inv.M43, inv.M44 });
            }
            var ib2 = new byte[ibm.Count * 4];
            Buffer.BlockCopy(ibm.ToArray(), 0, ib2, 0, ib2.Length);
            accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(ib2, null), ["componentType"] = 5126, ["count"] = bones.Length, ["type"] = "MAT4" });
            skins = new List<object> { new Dictionary<string, object> {
                ["joints"] = Enumerable.Range(first, bones.Length).ToArray(), ["inverseBindMatrices"] = accessors.Count - 1, ["skeleton"] = axis } };
            sceneNodes.Add(axis);
        }
        while (bin.Length % 4 != 0) bin.WriteByte(0);

        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "GTA Tool" },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = sceneNodes.ToArray() } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["materials"] = materials, ["accessors"] = accessors,
            ["bufferViews"] = views, ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = bin.Length } },
        };
        if (skins != null) gltf["skins"] = skins;
        if (images.Count > 0)
        {
            gltf["images"] = images; gltf["textures"] = textures;
            gltf["samplers"] = new[] { new Dictionary<string, object> { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 } };
        }
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gltf));
        int jsonPad = (4 - json.Length % 4) % 4;
        var binBytes = bin.ToArray();

        using var o = new MemoryStream();
        var w = new BinaryWriter(o);
        int total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length;
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
        w.Write((uint)(json.Length + jsonPad)); w.Write(0x4E4F534Au); w.Write(json);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)binBytes.Length); w.Write(0x004E4942u); w.Write(binBytes);
        w.Flush();
        return o.ToArray();
    }
}
