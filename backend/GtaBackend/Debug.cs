using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>Diagnose: Aufbau einer YFT (Kinder, Bones, Modelle, Shader) – nur für Entwicklung.</summary>
public static class DebugInfo
{
    /// <summary>Eine Datei aus einem RPF-Archiv speichern (Ressourcen mit RSC7-Kopf).</summary>
    public static object ExtractFromRpf(string rpfPath, string entryPath, string outFile)
    {
        var rpf = new RpfFile(rpfPath, Path.GetFileName(rpfPath));
        rpf.ScanStructure(null, null);
        RpfFileEntry found = null;
        RpfFile owner = null;
        void Walk(RpfFile f)
        {
            foreach (var e in f.AllEntries ?? new List<RpfEntry>())
                if (e is RpfFileEntry fe && fe.Path.Replace('\\', '/').EndsWith(entryPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) { found = fe; owner = f; }
            foreach (var c in f.Children ?? new List<RpfFile>()) Walk(c);
        }
        Walk(rpf);
        if (found == null) throw new UserError("Nicht gefunden: " + entryPath);
        var data = owner.ExtractFile(found);
        if (found is RpfResourceFileEntry rr) data = ResourceBuilder.AddResourceHeader(rr, ResourceBuilder.Compress(data));
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, data);
        return new { file = outFile, size = data.Length, path = found.Path };
    }

    /// <summary>Alle Einträge eines RPF-Archivs, deren Name einen Suchbegriff enthält.</summary>
    public static object FindInRpf(string rpfPath, string term)
    {
        var rpf = new RpfFile(rpfPath, Path.GetFileName(rpfPath));
        rpf.ScanStructure(null, null);
        var hits = new List<string>();
        void Walk(RpfFile f)
        {
            foreach (var e in f.AllEntries ?? new List<RpfEntry>())
                if (e is RpfFileEntry fe && fe.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) hits.Add($"{fe.Path} ({fe.GetFileSize()})");
            foreach (var c in f.Children ?? new List<RpfFile>()) Walk(c);
        }
        Walk(rpf);
        return hits;
    }

    public static object Yft(string file)
    {
        var yft = new YftFile();
        yft.Load(File.ReadAllBytes(file));
        var f = yft.Fragment;
        var bones = f.Drawable?.Skeleton?.Bones?.Items;
        var pl1 = f.PhysicsLODGroup?.PhysicsLOD1;

        object Shaders(DrawableBase d) => d?.ShaderGroup?.Shaders?.data_items?.Select((s, i) => new
        {
            i,
            name = s.Name.ToString(),
            tex = s.ParametersList?.Parameters?.Select((p, k) => (p, k)).Where(x => x.p.Data is TextureBase)
                .Select(x => $"{(uint)s.ParametersList.Hashes[x.k]}={((TextureBase)x.p.Data).Name}").ToArray(),
            geoms = (d.DrawableModels?.High ?? Array.Empty<DrawableModel>()).SelectMany(m => m.Geometries ?? Array.Empty<DrawableGeometry>()).Count(g => g.ShaderID == i),
        }).ToList();

        var childShaders = pl1?.Children?.data_items?.Select((c, i) => new
        {
            i, tag = c.BoneTag, bone = bones?.FirstOrDefault(b => b.Tag == c.BoneTag)?.Name,
            same = ReferenceEquals(c.Drawable1?.ShaderGroup, f.Drawable?.ShaderGroup),
            models = c.Drawable1?.AllModels?.Length ?? 0,
        }).Where(x => x.models > 0).ToList();

        var models = (f.Drawable?.DrawableModels?.High ?? Array.Empty<DrawableModel>()).Select(m => new
        {
            bone = (int)((m.SkeletonBinding >> 24) & 0xFF),
            boneName = bones != null && ((m.SkeletonBinding >> 24) & 0xFF) < bones.Length ? bones[(m.SkeletonBinding >> 24) & 0xFF].Name : null,
            skin = ((m.SkeletonBinding >> 8) & 0xFF) > 0,
            geoms = m.Geometries?.Select(g => g.ShaderID).ToArray(),
        }).ToList();
        return new { shaders = Shaders(f.Drawable), childShaders, models, boneCount = bones?.Length ?? 0 };
    }

    /// <summary>Sucht Hashes in allen GTA-Archiven: Dateinamen und Archetypen in YTYPs; zählt YTYPs, die nicht lesbar sind.</summary>
    public static object FindHashes(string gtaPath, List<string> hashes)
    {
        var want = hashes.Select(h => h.StartsWith("0x") ? Convert.ToUInt32(h[2..], 16) : JenkHash.GenHash(h.ToLowerInvariant())).ToHashSet();
        var archives = Directory.GetFiles(gtaPath, "x64*.rpf").ToList();
        var dlc = Path.Combine(gtaPath, "update", "x64", "dlcpacks");
        if (Directory.Exists(dlc)) archives.AddRange(Directory.GetDirectories(dlc).Select(d => Path.Combine(d, "dlc.rpf")).Where(File.Exists));
        archives.AddRange(new[] { "update.rpf", "update2.rpf" }.Select(u => Path.Combine(gtaPath, "update", u)).Where(File.Exists));
        var hits = new List<string>(); int ytyps = 0, failed = 0; var failedNames = new List<string>();
        foreach (var a in archives)
        {
            RpfFile rpf; try { rpf = new RpfFile(a, Path.GetFileName(a)); rpf.ScanStructure(null, null); } catch (Exception ex) { hits.Add("ARCHIV " + a + ": " + ex.Message); continue; }
            void Walk(RpfFile f)
            {
                foreach (var e in f.AllEntries ?? new List<RpfEntry>())
                {
                    if (e is not RpfFileEntry fe) continue;
                    var bn = Path.GetFileNameWithoutExtension(fe.Name).ToLowerInvariant();
                    if (want.Contains(JenkHash.GenHash(bn))) hits.Add("DATEI " + fe.Path);
                    if (fe.Name.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase))
                    {
                        ytyps++;
                        try
                        {
                            var y = new YtypFile(); y.Load(f.ExtractFile(fe), fe);
                            foreach (var ar in y.AllArchetypes ?? Array.Empty<Archetype>())
                                if (want.Contains(ar._BaseArchetypeDef.name.Hash)) hits.Add($"ARCHETYP {ar.Name} in {fe.Path} (dd={ar._BaseArchetypeDef.drawableDictionary}, txd={ar._BaseArchetypeDef.textureDictionary})");
                        }
                        catch (Exception ex) { failed++; if (failedNames.Count < 15) failedNames.Add(fe.Path + ": " + ex.Message); }
                    }
                }
                foreach (var c in f.Children ?? new List<RpfFile>()) Walk(c);
            }
            Walk(rpf);
        }
        return new { hits, ytyps, failed, failedNames };
    }

    /// <summary>Alle Einträge einer YMAP (normale Objekte + MLO-Instanzen).</summary>
    public static object Ymap(string file)
    {
        var y = new YmapFile(); y.Load(File.ReadAllBytes(file));
        return new
        {
            mlo = y.CMloInstanceDefs?.Length ?? 0,
            entities = (y.AllEntities ?? Array.Empty<YmapEntityDef>()).Select(e => new { name = e._CEntityDef.archetypeName.ToString(), hash = $"0x{e._CEntityDef.archetypeName.Hash:X8}", mlo = e.IsMlo, pos = new[] { e._CEntityDef.position.X, e._CEntityDef.position.Y, e._CEntityDef.position.Z }, flags = e._CEntityDef.flags, lod = e._CEntityDef.lodLevel.ToString(), parent = e._CEntityDef.parentIndex }).ToList(),
        };
    }

    public static object Ydr(string file)
    {
        var y = new YdrFile(); y.Load(File.ReadAllBytes(file));
        var d = y.Drawable;
        object Lod(DrawableModel[] ms) => ms?.Select(m => new { bind = $"0x{m.SkeletonBinding:X8}", geos = m.Geometries?.Select(g => new { verts = g.VertexData?.VertexCount, idx = g.IndexBuffer?.Indices?.Length, shader = g.Shader?.Name.ToString(), flags = g.VertexData?.Info?.Flags }).ToList() }).ToList();
        return new
        {
            high = Lod(d.DrawableModels?.High), med = Lod(d.DrawableModels?.Med), low = Lod(d.DrawableModels?.Low), vlow = Lod(d.DrawableModels?.VLow),
            all = d.AllModels?.Length,
            bones = d.Skeleton?.Bones?.Items?.Select(b => b.Name).ToList(),
            lodDist = new[] { d.LodDistHigh, d.LodDistMed, d.LodDistLow, d.LodDistVlow },
            shaders = d.ShaderGroup?.Shaders?.data_items?.Select(sh => new { name = sh.Name.ToString(), file = sh.FileName.ToString(), tex = sh.ParametersList?.Parameters?.Select((p, i) => p.Data is TextureBase tb ? $"{(MetaHash)(uint)sh.ParametersList.Hashes[i]}={tb.Name}" : null).Where(x => x != null).ToList() }).ToList(),
            embedded = d.ShaderGroup?.TextureDictionary?.Textures?.data_items?.Length ?? 0,
        };
    }
}

public static class DebugSkel
{
    /// <summary>Vergleicht berechnete Grundpose mit TransformationsInverted der Datei.</summary>
    public static object Compare(string yftFile)
    {
        var yft = new CodeWalker.GameFiles.YftFile();
        yft.Load(File.ReadAllBytes(yftFile));
        var sk = yft.Fragment.Drawable.Skeleton;
        var bones = sk.Bones.Items;
        var inv = sk.TransformationsInverted;
        var res = new List<object>();
        int n = bones.Length;
        var abs = new SharpDX.Matrix[n];
        for (int i = 0; i < n; i++)
        {
            var b = bones[i];
            var q = b.Rotation; q.Normalize();
            var m = SharpDX.Matrix.Scaling(b.Scale) * SharpDX.Matrix.RotationQuaternion(q) * SharpDX.Matrix.Translation(b.Translation);
            int p = b.ParentIndex;
            abs[i] = p >= 0 ? m * abs[p] : m;
        }
        for (int i = 0; i < n; i++)
        {
            if (inv == null || i >= inv.Length) break;
            var fi = inv[i]; fi.M44 = 1; fi.M14 = 0; fi.M24 = 0; fi.M34 = 0;
            var prod = abs[i] * fi; // sollte Einheit sein
            float dev = Math.Abs(prod.M41) + Math.Abs(prod.M42) + Math.Abs(prod.M43) + Math.Abs(prod.M11 - 1) + Math.Abs(prod.M22 - 1) + Math.Abs(prod.M33 - 1);
            if (dev > 0.01f) res.Add(new { i, name = bones[i].Name, parent = bones[i].ParentIndex, dev, t = new[] { prod.M41, prod.M42, prod.M43 }, raw = new[] { inv[i].M14, inv[i].M24, inv[i].M34, inv[i].M44 } });
        }
        return new { bones = n, invCount = inv?.Length ?? 0, parentsOrdered = bones.Select((b, i) => b.ParentIndex < i).All(x => x), deviating = res.Count, list = res.Take(40) };
    }
}

public static class DebugXml
{
    /// <summary>Beliebige Meta-Datei (.ytyp/.ymap/.ymt …) als CodeWalker-XML.</summary>
    public static object ToXml(string file, string outFile)
    {
        var data = File.ReadAllBytes(file);
        var entry = CodeWalker.GameFiles.RpfFile.CreateResourceFileEntry(ref data, 0);
        entry.Name = Path.GetFileName(file);
        entry.NameLower = entry.Name.ToLowerInvariant();
        data = CodeWalker.GameFiles.ResourceBuilder.Decompress(data);
        var xml = CodeWalker.GameFiles.MetaXml.GetXml(entry, data, out _);
        File.WriteAllText(outFile, xml);
        return new { outFile, length = xml?.Length ?? 0 };
    }
}

public static class DebugYdd
{
    public static object Info(string file)
    {
        var y = new CodeWalker.GameFiles.YddFile(); y.Load(File.ReadAllBytes(file));
        return y.Drawables.Select(d => new
        {
            name = d.Name,
            models = d.AllModels?.Length,
            geos = d.AllModels?.SelectMany(m => m.Geometries ?? Array.Empty<CodeWalker.GameFiles.DrawableGeometry>()).Take(6).Select(g => new
            {
                flags = g.VertexData?.Info?.Flags,
                verts = g.VertexData?.VertexCount,
                shader = g.Shader?.Name.ToString(),
                c0 = g.VertexData != null && (g.VertexData.Info.Flags & 16) != 0 ? g.VertexData.GetColour(0, 4).ToString() : null,
                c1 = g.VertexData != null && (g.VertexData.Info.Flags & 16) != 0 && g.VertexData.VertexCount > 50 ? g.VertexData.GetColour(50, 4).ToString() : null,
            }).ToList(),
        }).Take(5).ToList();
    }
}
