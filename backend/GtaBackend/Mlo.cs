using System.Text;
using CodeWalker.GameFiles;
using SharpDX;
using CodeWalker;

namespace GtaBackend;

/// <summary>
/// MLO (Interiors): ytyp mit CMloArchetypeDef (Räume, Portale, Objekte) + ymap mit CMloInstanceDef (Platz in der Welt).
/// Drehungen: Objekte im MLO speichern die Drehung INVERTIERT, die MLO-Platzierung in der ymap DIREKT.
/// </summary>
public static class Mlo
{
    static string Stream(string sessionDir) => Path.Combine(sessionDir, "stream");

    static YtypFile LoadYtyp(string file)
    {
        var data = File.ReadAllBytes(file);
        var y = new YtypFile();
        if (data.Length > 4 && data[0] == 'R' && data[1] == 'S' && data[2] == 'C' && data[3] == '7') y.Load(data);
        else
        {
            var entry = new RpfResourceFileEntry { Name = Path.GetFileName(file), NameLower = Path.GetFileName(file).ToLowerInvariant() };
            y.Load(data, entry);
        }
        return y;
    }

    static YmapFile LoadYmap(string file)
    {
        var y = new YmapFile();
        y.Load(File.ReadAllBytes(file));
        return y;
    }

    /// <summary>Namen aller Dateien im Paket registrieren, damit Archetyp-Hashes lesbar werden.</summary>
    static void RegisterNames(string sessionDir)
    {
        var s = Stream(sessionDir);
        if (!Directory.Exists(s)) return;
        foreach (var f in Directory.GetFiles(s)) JenkIndex.Ensure(Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
    }

    static float[] V3(Vector3 v) => new[] { v.X, v.Y, v.Z };
    static float[] Q(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };

    static string ArchName(MetaHash h)
    {
        var s = h.ToString();
        bool unknown = s.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0x") || s.All(char.IsDigit);
        if (!unknown) return s;
        return Props.NameOf(h.Hash) ?? $"0x{h.Hash:X8}";
    }

    // ------------------------------------------------------------------ Übersicht

    class PackArch { public string Name; public string Ytyp; public string Model; public string Txd; public bool IsMlo; }

    static Dictionary<string, PackArch> PackArchetypes(string sessionDir)
    {
        RegisterNames(sessionDir);
        var map = new Dictionary<string, PackArch>(StringComparer.OrdinalIgnoreCase);
        var s = Stream(sessionDir);
        if (!Directory.Exists(s)) return map;
        foreach (var f in Directory.GetFiles(s, "*.ytyp"))
        {
            YtypFile y;
            try { y = LoadYtyp(f); } catch { continue; }
            foreach (var a in y.AllArchetypes ?? Array.Empty<Archetype>())
            {
                var name = ArchName(a._BaseArchetypeDef.name);
                var txd = a._BaseArchetypeDef.textureDictionary.Hash != 0 ? ArchName(a._BaseArchetypeDef.textureDictionary) : null;
                string model = null;
                foreach (var ext in new[] { ".ydr", ".yft", ".ydd" })
                    if (File.Exists(Path.Combine(s, name + ext))) { model = "stream/" + name + ext; break; }
                if (model == null && a._BaseArchetypeDef.drawableDictionary.Hash != 0)
                {
                    var dd = ArchName(a._BaseArchetypeDef.drawableDictionary);
                    if (File.Exists(Path.Combine(s, dd + ".ydd"))) model = "stream/" + dd + ".ydd";
                }
                map[name] = new PackArch { Name = name, Ytyp = "stream/" + Path.GetFileName(f), Model = model, Txd = txd, IsMlo = a is MloArchetype };
            }
        }
        return map;
    }

    public static object Analyze(string sessionDir)
    {
        RegisterNames(sessionDir);
        var s = Stream(sessionDir);
        var mlos = new List<object>();
        var warnings = new List<string>();
        var instances = new List<(string ymap, int index, CMloInstanceDef def)>();
        if (Directory.Exists(s))
            foreach (var f in Directory.GetFiles(s, "*.ymap"))
            {
                try
                {
                    var ym = LoadYmap(f);
                    if (ym.CMloInstanceDefs != null)
                        for (int i = 0; i < ym.CMloInstanceDefs.Length; i++) instances.Add(("stream/" + Path.GetFileName(f), i, ym.CMloInstanceDefs[i]));
                }
                catch (Exception ex) { warnings.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            }

        if (Directory.Exists(s))
            foreach (var f in Directory.GetFiles(s, "*.ytyp"))
            {
                YtypFile y;
                try { y = LoadYtyp(f); } catch (Exception ex) { warnings.Add($"{Path.GetFileName(f)}: {ex.Message}"); continue; }
                foreach (var m in (y.AllArchetypes ?? Array.Empty<Archetype>()).OfType<MloArchetype>())
                {
                    var name = ArchName(m._BaseArchetypeDef.name);
                    var inst = instances.FirstOrDefault(x => x.def.CEntityDef.archetypeName.Hash == m._BaseArchetypeDef.name.Hash);
                    var ents = m.entities?.Length ?? 0;
                    var setEnts = m.entitySets?.Sum(es => es.Entities?.Length ?? 0) ?? 0;
                    mlos.Add(new
                    {
                        key = $"stream/{Path.GetFileName(f)}|{name}",
                        ytyp = "stream/" + Path.GetFileName(f),
                        name,
                        entities = ents + setEnts,
                        rooms = m.rooms?.Select((r, i) => new { index = i, name = r.RoomName }).ToList(),
                        portals = m.portals?.Length ?? 0,
                        entitySets = m.entitySets?.Select(es => es.Name).ToList(),
                        instance = inst.ymap == null ? null : new
                        {
                            ymap = inst.ymap,
                            index = inst.index,
                            position = V3(inst.def.CEntityDef.position),
                            rotation = Q(new Quaternion(inst.def.CEntityDef.rotation)),
                        },
                    });
                    if (inst.ymap == null) warnings.Add($"Für das Interior „{name}“ wurde keine ymap mit Platzierung gefunden – es erscheint so nicht im Spiel.");
                }
            }

        var arch = PackArchetypes(sessionDir);
        var files = Directory.Exists(s) ? Directory.GetFiles(s).Select(Path.GetFileName).ToList() : new List<string>();
        var models = files.Where(f => f.EndsWith(".ydr") || f.EndsWith(".ydd") || f.EndsWith(".yft"))
            .Select(f => new
            {
                file = "stream/" + f,
                name = Path.GetFileNameWithoutExtension(f),
                archetype = arch.ContainsKey(Path.GetFileNameWithoutExtension(f)),
                encrypted = Session.IsEncrypted(sessionDir, "stream/" + f),
                size = new FileInfo(Path.Combine(s, f)).Length,
            }).OrderBy(x => x.name).ToList();
        var ytds = files.Where(f => f.EndsWith(".ytd")).Select(f => "stream/" + f).OrderBy(x => x).ToList();
        var collisions = files.Where(f => f.EndsWith(".ybn")).Select(f => "stream/" + f).OrderBy(x => x).ToList();
        var ymaps = files.Where(f => f.EndsWith(".ymap")).Select(f => "stream/" + f).OrderBy(x => x).ToList();
        var ytypsList = files.Where(f => f.EndsWith(".ytyp")).Select(f => "stream/" + f).OrderBy(x => x).ToList();
        foreach (var m in models.Where(m => m.encrypted)) warnings.Add($"{Path.GetFileName(m.file)} ist verschlüsselt (Escrow).");
        return new { sessionDir, mlos, models, ytds, collisions, ymaps, ytyps = ytypsList, warnings };
    }

    // ------------------------------------------------------------------ Szene eines MLO

    static (YtypFile ytyp, MloArchetype mlo, string file) Find(string sessionDir, string key)
    {
        var parts = key.Split('|');
        var file = Path.Combine(sessionDir, parts[0]);
        RegisterNames(sessionDir);
        var y = LoadYtyp(file);
        var m = (y.AllArchetypes ?? Array.Empty<Archetype>()).OfType<MloArchetype>()
            .FirstOrDefault(a => ArchName(a._BaseArchetypeDef.name).Equals(parts[1], StringComparison.OrdinalIgnoreCase))
            ?? throw new UserError("Interior nicht gefunden.");
        return (y, m, file);
    }

    /// <summary>Alle Objekte: Index "e:3" (Hauptliste) oder "s:1:4" (Objekt-Set 1, Objekt 4).</summary>
    static IEnumerable<(string id, MCEntityDef ent, string set)> AllEntities(MloArchetype m)
    {
        for (int i = 0; i < (m.entities?.Length ?? 0); i++) yield return ($"e:{i}", m.entities[i], null);
        for (int s = 0; s < (m.entitySets?.Length ?? 0); s++)
        {
            var es = m.entitySets[s];
            for (int i = 0; i < (es.Entities?.Length ?? 0); i++) yield return ($"s:{s}:{i}", es.Entities[i], es.Name);
        }
    }

    static int RoomOf(MloArchetype m, int entityIndex)
    {
        if (m.rooms == null) return -1;
        for (int r = 0; r < m.rooms.Length; r++)
            if (m.rooms[r].AttachedObjects != null && m.rooms[r].AttachedObjects.Contains((uint)entityIndex)) return r;
        return -1;
    }

    public static object Scene(string sessionDir, string key, string propsCache)
    {
        var gta = Props.Load(propsCache);
        var (_, m, _) = Find(sessionDir, key);
        var pack = PackArchetypes(sessionDir);
        var entities = new List<object>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, e, set) in AllEntities(m))
        {
            var d = e._Data;
            var name = ArchName(d.archetypeName);
            names.Add(name);
            var stored = new Quaternion(d.rotation);
            var ori = stored == Quaternion.Identity ? stored : Quaternion.Invert(stored);
            int idx = id.StartsWith("e:") ? int.Parse(id[2..]) : -1;
            entities.Add(new
            {
                id,
                name,
                set,
                room = idx >= 0 ? RoomOf(m, idx) : -1,
                position = V3(d.position),
                rotation = Q(Quaternion.Normalize(ori)),
                scale = new[] { d.scaleXY, d.scaleZ },
                lodDist = d.lodDist,
                flags = d.flags,
                source = pack.TryGetValue(name, out var pa) && pa.Model != null ? "pack" : gta != null && gta.Archetypes.ContainsKey(name) ? "gta" : "missing",
            });
        }
        var archetypes = names.Select(n =>
        {
            pack.TryGetValue(n, out var pa);
            return new
            {
                name = n,
                source = pa?.Model != null ? "pack" : gta != null && gta.Archetypes.ContainsKey(n) ? "gta" : "missing",
                model = pa?.Model,
                ytds = pa?.Txd != null && File.Exists(Path.Combine(Stream(sessionDir), pa.Txd + ".ytd")) ? new[] { "stream/" + pa.Txd + ".ytd" } : Array.Empty<string>(),
            };
        }).ToList();
        return new
        {
            entities,
            archetypes,
            rooms = m.rooms?.Select((r, i) => new { index = i, name = r.RoomName, bbMin = V3(r.BBMin), bbMax = V3(r.BBMax) }).ToList(),
            gtaCatalog = gta != null,
        };
    }

    /// <summary>GLB eines Objekts: aus dem Paket oder aus GTA (wird dazu einmalig in den Cache geholt).</summary>
    public static object ArchetypeGlb(string sessionDir, string name, string gtaPath, string propsCache, string propsDir, string outFile)
    {
        var pack = PackArchetypes(sessionDir);
        if (pack.TryGetValue(name, out var pa) && pa.Model != null)
        {
            var ytds = new List<string>();
            if (pa.Txd != null && File.Exists(Path.Combine(Stream(sessionDir), pa.Txd + ".ytd"))) ytds.Add(Path.Combine(Stream(sessionDir), pa.Txd + ".ytd"));
            return Model3D.Export(Path.Combine(sessionDir, pa.Model), ytds, outFile);
        }
        var (model, gtaYtds) = Props.Extract(gtaPath, propsCache, name, propsDir);
        if (model == null) throw new UserError($"Objekt „{name}“ nicht gefunden.");
        return Model3D.Export(model, gtaYtds, outFile);
    }

    // ------------------------------------------------------------------ Kollision

    /// <summary>Kollision (.ybn) als halbtransparentes GLB, optional in den MLO-Raum umgerechnet.</summary>
    /// <summary>Kollision aus .ybn oder eingebettet in .ydr (Drawable.Bound) / .yft (Physik-LOD).</summary>
    public static Bounds LoadBounds(string file)
    {
        var data = File.ReadAllBytes(file);
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".ydr":
            {
                var y = new YdrFile(); y.Load(data);
                return y.Drawable?.Bound ?? throw new UserError("Dieses Modell hat keine eingebettete Kollision.");
            }
            case ".yft":
            {
                var y = new YftFile(); y.Load(data);
                var f = y.Fragment;
                return f?.PhysicsLODGroup?.PhysicsLOD1?.Bound ?? f?.Drawable?.Bound ?? throw new UserError("Dieses Modell hat keine eingebettete Kollision.");
            }
            default:
            {
                var y = new YbnFile(); y.Load(data);
                return y.Bounds;
            }
        }
    }

    public static object CollisionGlb(string ybnFile, string outFile, float[] mloPos, float[] mloRot, float[] clipMin = null, float[] clipMax = null)
    {
        Vector3? cMin = clipMin != null ? new Vector3(clipMin[0], clipMin[1], clipMin[2]) - new Vector3(1f) : null;
        Vector3? cMax = clipMax != null ? new Vector3(clipMax[0], clipMax[1], clipMax[2]) + new Vector3(1f) : null;
        bool Inside(Vector3 v) => cMin == null || (v.X >= cMin.Value.X && v.Y >= cMin.Value.Y && v.Z >= cMin.Value.Z && v.X <= cMax.Value.X && v.Y <= cMax.Value.Y && v.Z <= cMax.Value.Z);
        var rootBounds = LoadBounds(ybnFile);
        var pts = new List<Vector3>();
        var tris = new List<int>();
        Matrix toLocal = Matrix.Identity;
        if (mloPos != null && mloRot != null)
        {
            var world = Matrix.RotationQuaternion(new Quaternion(mloRot[0], mloRot[1], mloRot[2], mloRot[3])) * Matrix.Translation(mloPos[0], mloPos[1], mloPos[2]);
            toLocal = Matrix.Invert(world);
        }
        void Tri(Vector3 a, Vector3 b, Vector3 c) { if (!Inside(a) && !Inside(b) && !Inside(c)) return; int i = pts.Count; pts.Add(a); pts.Add(b); pts.Add(c); tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); }
        void Box(Vector3 mn, Vector3 mx, Matrix tf)
        {
            var c = new Vector3[8];
            for (int i = 0; i < 8; i++) c[i] = Vector3.TransformCoordinate(new Vector3((i & 1) == 0 ? mn.X : mx.X, (i & 2) == 0 ? mn.Y : mx.Y, (i & 4) == 0 ? mn.Z : mx.Z), tf);
            int[][] f = { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 } };
            foreach (var q in f) { Tri(c[q[0]], c[q[1]], c[q[2]]); Tri(c[q[0]], c[q[2]], c[q[3]]); }
        }
        void Walk(Bounds b, Matrix parent)
        {
            if (b == null) return;
            if (b is BoundComposite comp)
            {
                var kids = comp.Children?.data_items;
                if (kids == null) return;
                for (int i = 0; i < kids.Length; i++) Walk(kids[i], parent);
                return;
            }
            if (b is BoundGeometry g && g.Polygons != null)
            {
                foreach (var p in g.Polygons)
                {
                    switch (p)
                    {
                        case BoundPolygonTriangle t:
                            Tri(Vector3.TransformCoordinate(g.GetVertexPos(t.vertIndex1), parent),
                                Vector3.TransformCoordinate(g.GetVertexPos(t.vertIndex2), parent),
                                Vector3.TransformCoordinate(g.GetVertexPos(t.vertIndex3), parent));
                            break;
                        case BoundPolygonBox bx:
                            var vs = new[] { bx.boxIndex1, bx.boxIndex2, bx.boxIndex3, bx.boxIndex4 }.Select(ix => g.GetVertexPos(ix)).ToArray();
                            var mn = Vector3.Min(Vector3.Min(vs[0], vs[1]), Vector3.Min(vs[2], vs[3]));
                            var mx = Vector3.Max(Vector3.Max(vs[0], vs[1]), Vector3.Max(vs[2], vs[3]));
                            Box(mn, mx, parent);
                            break;
                    }
                }
                return;
            }
            if (b is BoundBox || b is BoundSphere || b is BoundCapsule || b is BoundCylinder)
                Box(b.BoxMin, b.BoxMax, b.Transform * parent);
        }
        Walk(rootBounds, toLocal);
        if (tris.Count == 0)
            throw new UserError(cMin != null ? "Diese Kollision liegt nicht im Bereich des Interiors." : "Die Kollision enthält keine darstellbaren Flächen.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, Model3D.SimpleGlb(pts, tris, new[] { 1f, 0.35f, 0.1f, 0.35f }));
        return new { file = outFile, triangles = tris.Count / 3 };
    }

    // ------------------------------------------------------------------ Bearbeiten

    static void SaveYtyp(YtypFile y, string file, string backupDir)
    {
        var bytes = y.Save();
        Session.Backup(file, backupDir);
        File.WriteAllBytes(file, bytes);
    }

    static MCEntityDef EntityById(MloArchetype m, string id) =>
        AllEntities(m).FirstOrDefault(x => x.id == id).ent ?? throw new UserError("Objekt nicht gefunden.");

    /// <summary>Position/Drehung (Orientierung, nicht invertiert)/Skalierung eines Objekts setzen.</summary>
    public static object SetEntity(string sessionDir, string key, string id, float[] pos, float[] rot, float[] scale, string backupDir)
    {
        var (y, m, file) = Find(sessionDir, key);
        var e = EntityById(m, id);
        var d = e._Data;
        if (pos != null) d.position = new Vector3(pos[0], pos[1], pos[2]);
        if (rot != null)
        {
            var q = Quaternion.Normalize(new Quaternion(rot[0], rot[1], rot[2], rot[3]));
            var inv = Quaternion.Normalize(Quaternion.Invert(q));
            d.rotation = inv.ToVector4();
        }
        if (scale != null) { d.scaleXY = scale[0]; d.scaleZ = scale[1]; }
        e._Data = d;
        SaveYtyp(y, file, backupDir);
        return new { ok = true };
    }

    public static object DeleteEntity(string sessionDir, string key, string id, string backupDir)
    {
        var (y, m, file) = Find(sessionDir, key);
        if (id.StartsWith("e:"))
        {
            int idx = int.Parse(id[2..]);
            var list = m.entities.ToList();
            list.RemoveAt(idx);
            m.entities = list.ToArray();
            // Raum-Zuordnungen nachziehen (Indizes verschieben sich)
            foreach (var r in m.rooms ?? Array.Empty<MCMloRoomDef>())
            {
                if (r.AttachedObjects == null) continue;
                r.AttachedObjects = r.AttachedObjects.Where(o => o != idx).Select(o => o > idx ? o - 1 : o).ToArray();
            }
            foreach (var p in m.portals ?? Array.Empty<MCMloPortalDef>())
            {
                if (p.AttachedObjects == null) continue;
                p.AttachedObjects = p.AttachedObjects.Where(o => o != idx).Select(o => o > idx ? o - 1 : o).ToArray();
            }
            for (int i = 0; i < m.entities.Length; i++) m.entities[i].Index = i;
        }
        else
        {
            var parts = id.Split(':');
            var es = m.entitySets[int.Parse(parts[1])];
            int idx = int.Parse(parts[2]);
            es.Entities = es.Entities.Where((_, i) => i != idx).ToArray();
            if (es.Locations != null && es.Locations.Length > idx) es.Locations = es.Locations.Where((_, i) => i != idx).ToArray();
        }
        SaveYtyp(y, file, backupDir);
        return new { ok = true };
    }

    /// <summary>Modell eines Objekts tauschen (z. B. fehlende Tür durch GTA-Tür ersetzen).
    /// id leer → alle Objekte mit dem Modell „from“ (auch in Objekt-Sets).</summary>
    public static object SwapArchetype(string sessionDir, string key, string id, string from, string to, string backupDir)
    {
        if (string.IsNullOrWhiteSpace(to)) throw new UserError("Bitte ein neues Objekt wählen.");
        var (y, m, file) = Find(sessionDir, key);
        to = to.Trim().ToLowerInvariant();
        JenkIndex.Ensure(to);
        uint hash = to.StartsWith("0x") ? Convert.ToUInt32(to[2..], 16) : JenkHash.GenHash(to);
        int n = 0;
        foreach (var (eid, e, _) in AllEntities(m).ToList())
        {
            var d = e._Data;
            bool hit = !string.IsNullOrEmpty(id) ? eid == id : ArchName(d.archetypeName).Equals(from ?? "", StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;
            d.archetypeName = hash;
            e._Data = d;
            n++;
        }
        if (n == 0) throw new UserError("Kein passendes Objekt gefunden.");
        SaveYtyp(y, file, backupDir);
        return new { changed = n, name = ArchName(hash) };
    }

    /// <summary>Neues Objekt einfügen (Archetyp-Name aus Paket oder GTA) in einen Raum.</summary>
    public static object AddEntity(string sessionDir, string key, string name, float[] pos, float[] rot, int room, string copyFrom, string backupDir)
    {
        var (y, m, file) = Find(sessionDir, key);
        CEntityDef d;
        if (!string.IsNullOrEmpty(copyFrom))
        {
            d = EntityById(m, copyFrom)._Data;
            if (room < 0 && copyFrom.StartsWith("e:")) room = RoomOf(m, int.Parse(copyFrom[2..]));
        }
        else
        {
            d = new CEntityDef
            {
                scaleXY = 1, scaleZ = 1, lodDist = 100, childLodDist = 0, flags = 1572865,
                lodLevel = rage__eLodType.LODTYPES_DEPTH_ORPHANHD, priorityLevel = rage__ePriorityLevel.PRI_REQUIRED,
                parentIndex = -1, numChildren = 0, ambientOcclusionMultiplier = 255, artificialAmbientOcclusion = 255,
                tintValue = 0, rotation = Quaternion.Identity.ToVector4(),
            };
        }
        if (!string.IsNullOrEmpty(name)) d.archetypeName = JenkHash.GenHash(name.ToLowerInvariant());
        JenkIndex.Ensure(name ?? "");
        d.guid = (uint)Random.Shared.Next(1, int.MaxValue);
        if (pos != null) d.position = new Vector3(pos[0], pos[1], pos[2]);
        if (rot != null) d.rotation = Quaternion.Normalize(Quaternion.Invert(new Quaternion(rot[0], rot[1], rot[2], rot[3]))).ToVector4();
        var ent = new YmapEntityDef(null, 0, ref d);
        if (room < 0) room = 0;
        if (m.rooms == null || room >= m.rooms.Length) room = Math.Max(0, (m.rooms?.Length ?? 1) - 1);
        if (!m.AddEntity(ent, room)) throw new UserError("Das Objekt konnte nicht eingefügt werden.");
        SaveYtyp(y, file, backupDir);
        return new { id = $"e:{m.entities.Length - 1}" };
    }

    /// <summary>Platzierung des Interiors in der Welt (ymap) ändern.</summary>
    public static object SetPlacement(string sessionDir, string ymapRel, int index, float[] pos, float[] rot, string backupDir)
    {
        var file = Path.Combine(sessionDir, ymapRel);
        var ym = LoadYmap(file);
        var ent = ym.AllEntities?.Where(e => e.IsMlo).Skip(index).FirstOrDefault() ?? throw new UserError("Platzierung nicht gefunden.");
        if (pos != null) ent.SetPosition(new Vector3(pos[0], pos[1], pos[2]));
        if (rot != null) ent.SetOrientation(Quaternion.Normalize(new Quaternion(rot[0], rot[1], rot[2], rot[3])));
        ym.CalcExtents();
        var bytes = ym.Save();
        Session.Backup(file, backupDir);
        File.WriteAllBytes(file, bytes);
        return new { ok = true, note = "Hinweis: Kollisionen (.ybn) liegen in Weltkoordinaten und werden beim Verschieben nicht mitbewegt." };
    }

    // ------------------------------------------------------------------ Eigenes Modell einfügen

    /// <summary>Neues Modell (.ydr + optional .ytd) ins Paket aufnehmen und als Archetyp anlegen.</summary>
    public static object ImportModel(string sessionDir, string key, string ydr, string ytd, string backupDir)
    {
        if (!File.Exists(ydr)) throw new UserError("Modell nicht gefunden.");
        var name = Session.SafeName(Path.GetFileNameWithoutExtension(ydr));
        var s = Stream(sessionDir);
        var target = Path.Combine(s, name + Path.GetExtension(ydr).ToLowerInvariant());
        if (File.Exists(target)) throw new UserError($"{Path.GetFileName(target)} gibt es im Paket schon.");
        File.Copy(ydr, target);
        string txd = null;
        if (!string.IsNullOrEmpty(ytd) && File.Exists(ytd))
        {
            txd = Session.SafeName(Path.GetFileNameWithoutExtension(ytd));
            File.Copy(ytd, Path.Combine(s, txd + ".ytd"), true);
        }
        // Bounding-Box aus dem Modell
        var bmin = new Vector3(-0.5f); var bmax = new Vector3(0.5f);
        try
        {
            var yd = new YdrFile();
            yd.Load(File.ReadAllBytes(target));
            bmin = yd.Drawable.BoundingBoxMin; bmax = yd.Drawable.BoundingBoxMax;
        }
        catch { }
        var (y, _, file) = Find(sessionDir, key);
        var a = y.AddArchetype();
        var bd = a._BaseArchetypeDef;
        bd.name = JenkHash.GenHash(name);
        bd.assetName = bd.name;
        bd.textureDictionary = txd != null ? JenkHash.GenHash(txd) : (MetaHash)0;
        bd.assetType = rage__fwArchetypeDef__eAssetType.ASSET_TYPE_DRAWABLE;
        bd.lodDist = 100; bd.flags = 0; bd.specialAttribute = 0;
        bd.bbMin = bmin; bd.bbMax = bmax;
        bd.bsCentre = (bmin + bmax) * 0.5f; bd.bsRadius = (bmax - bmin).Length() * 0.5f;
        bd.hdTextureDist = 100;
        a._BaseArchetypeDef = bd;
        JenkIndex.Ensure(name);
        if (txd != null) JenkIndex.Ensure(txd);
        SaveYtyp(y, file, backupDir);
        return new { name };
    }

    // ------------------------------------------------------------------ FiveM-Export

    public static object Export(string sessionDir, string outDir, string resourceName)
    {
        resourceName = Session.SafeName(resourceName);
        var res = Path.Combine(outDir, resourceName);
        if (Directory.Exists(res) && Directory.EnumerateFileSystemEntries(res).Any())
        {
            if (!File.Exists(Path.Combine(res, "fxmanifest.lua"))) throw new UserError($"{res} existiert schon und ist keine FiveM-Resource.");
            Directory.Delete(res, true);
        }
        var s = Stream(sessionDir);
        var outStream = Path.Combine(res, "stream");
        Directory.CreateDirectory(outStream);
        int n = 0;
        var warnings = new List<string>();
        foreach (var f in Directory.GetFiles(s))
        {
            File.Copy(f, Path.Combine(outStream, Path.GetFileName(f)), true);
            n++;
            if (new FileInfo(f).Length > 16 * 1024 * 1024) warnings.Add($"{Path.GetFileName(f)} ist größer als 16 MB.");
        }
        var fx = new StringBuilder();
        fx.AppendLine("fx_version 'cerulean'");
        fx.AppendLine("game 'gta5'");
        fx.AppendLine();
        fx.AppendLine("author 'GTA Tool'");
        fx.AppendLine($"description '{resourceName} – Interior (MLO), erstellt mit dem GTA Tool'");
        fx.AppendLine("version '1.0.0'");
        fx.AppendLine();
        fx.AppendLine("this_is_a_map 'yes'");
        File.WriteAllText(Path.Combine(res, "fxmanifest.lua"), fx.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(res, "LIESMICH.txt"),
            $"Resource: {resourceName}\n\nOrdner nach „resources“ kopieren, in der server.cfg „ensure {resourceName}“ eintragen, Server neu starten.\n",
            new UTF8Encoding(true));
        return new { folder = res, files = n, warnings };
    }
}
