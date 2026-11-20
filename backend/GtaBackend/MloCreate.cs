using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaBackend;

/// <summary>
/// Neues Interior (MLO) aus Blender:
///   &lt;name&gt;_shell.ydr (+ _shell.ytd)  Gebäudehülle
///   &lt;name&gt;_col.ybn                     Kollision (bereits in Weltkoordinaten)
///   &lt;name&gt;.ytyp                        Hülle-Archetyp + MLO-Archetyp (Räume, Portale, Türen)
///   &lt;name&gt;_milo.ymap                   Platzierung (Position + Drehung)
/// Danach lässt sich alles im MLO Studio weiterbearbeiten.
/// </summary>
public static class MloCreate
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(float f) => f.ToString("0.######", Inv);
    static string V(float[] v) => $"x=\"{F(v[0])}\" y=\"{F(v[1])}\" z=\"{F(v[2])}\"";
    static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "");

    // Vom Tool erzeugte Objekte (Hülle, Türen) tragen diese GUIDs – alles andere bleibt beim Neuaufbau erhalten
    const uint GuidBase = 0x6A7A0000;
    static bool IsToolGuid(uint g) => (g & 0xFFFF0000) == GuidBase;

    class Kept { public string Arch; public float[] Pos; public float[] Rot; public float Sxy, Sz, Lod; public uint Flags, Guid; }

    /// <summary>Im MLO Studio eingefügte Objekte aus der bisherigen YTYP übernehmen.</summary>
    static List<Kept> KeptEntities(string ytypFile, string mloName)
    {
        var res = new List<Kept>();
        if (!File.Exists(ytypFile)) return res;
        try
        {
            var y = new YtypFile();
            y.Load(File.ReadAllBytes(ytypFile));
            var h = JenkHash.GenHash(mloName);
            var mlo = y.AllArchetypes?.OfType<MloArchetype>().FirstOrDefault(a => a._BaseArchetypeDef.name.Hash == h);
            foreach (var e in mlo?.entities ?? Array.Empty<MCEntityDef>())
            {
                var d = e._Data;
                if (IsToolGuid(d.guid)) continue;
                var nm = JenkIndex.TryGetString(d.archetypeName.Hash);
                if (string.IsNullOrEmpty(nm)) nm = Props.NameOf(d.archetypeName.Hash) ?? $"hash_{d.archetypeName.Hash:X8}";
                res.Add(new Kept
                {
                    Arch = nm, Pos = new[] { d.position.X, d.position.Y, d.position.Z }, Rot = new[] { d.rotation.X, d.rotation.Y, d.rotation.Z, d.rotation.W },
                    Sxy = d.scaleXY, Sz = d.scaleZ, Lod = d.lodDist, Flags = d.flags, Guid = d.guid,
                });
            }
        }
        catch (Exception ex) { Program.Log($"MLO-Neuaufbau: alte Objekte nicht lesbar: {ex.Message}"); }
        return res;
    }

    // Zeitzyklus (Licht-Stimmung) wie in einem funktionierenden Custom-MLO
    const string TimecycleRoom = "hash_42225664";
    const string TimecycleLimbo = "hash_56DC69DB";

    static readonly string[] Preferred = { "v_ilev_ra_door1_l", "v_ilev_fh_frntdoor", "v_ilev_mm_door", "v_ilev_gc_door04", "prop_door_01", "v_ilev_fib_door1", "v_ilev_cs_door01", "v_ilev_247door", "v_ilev_bl_door_l", "v_ilev_ct_door01" };

    /// <summary>Typische GTA-Türen (Name + Größe) aus dem Objektkatalog für die Auswahl in Blender.</summary>
    public static object DoorList(string propsCache)
    {
        var c = Props.Load(propsCache);
        var res = new List<object>();
        if (c == null) return res;
        string[] skip = { "garage", "gate", "shutter", "roller", "frame", "lock", "handle", "glass", "_lod", "slod", "barrier", "car", "boot", "bonnet", "cell", "vault", "lift", "elev" };
        var list = c.Archetypes.Where(kv => kv.Key.Contains("door", StringComparison.OrdinalIgnoreCase)
                && !skip.Any(s => kv.Key.Contains(s, StringComparison.OrdinalIgnoreCase))
                && c.Files.ContainsKey(kv.Key + ".ydr") && kv.Value.BbMin != null && kv.Value.BbMax != null)
            .Select(kv => (name: kv.Key.ToLowerInvariant(), a: kv.Value))
            .Where(x =>
            {
                float w = x.a.BbMax[0] - x.a.BbMin[0], d = x.a.BbMax[1] - x.a.BbMin[1], h = x.a.BbMax[2] - x.a.BbMin[2];
                return w >= 0.7f && w <= 1.6f && d <= 0.4f && h >= 1.9f && h <= 2.9f;
            })
            .OrderBy(x => Array.IndexOf(Preferred, x.name) is var i && i >= 0 ? i : 100 + (x.name.StartsWith("v_ilev_") ? 0 : x.name.StartsWith("prop_") ? 1 : 2)).ThenBy(x => x.name)
            .Take(80);
        foreach (var (name, a) in list) res.Add(new { name, bbMin = a.BbMin, bbMax = a.BbMax });
        return res;
    }

    class Room { public string name { get; set; } public float[] min { get; set; } public float[] max { get; set; } }
    class Portal { public string name { get; set; } public float[][] corners { get; set; } public int from { get; set; } public int to { get; set; } }
    class Door { public string archetype { get; set; } public float[] position { get; set; } public float[] rotation { get; set; } public int room { get; set; } public int portal { get; set; } }
    class MloData
    {
        public List<Room> rooms { get; set; } = new();
        public List<Portal> portals { get; set; } = new();
        public List<Door> doors { get; set; } = new();
        public List<string> warnings { get; set; } = new();
        public float[] position { get; set; }
        public float heading { get; set; }
    }

    public static object ApplyNew(string sessionDir, string exportDir, string name, string backupDir)
    {
        name = Session.SafeName(name);
        var s = Path.Combine(sessionDir, "stream");
        Directory.CreateDirectory(s);
        var jsonFile = Path.Combine(exportDir, "mlo.json");
        if (!File.Exists(jsonFile)) throw new UserError("Blender hat keine Interior-Daten (mlo.json) geliefert.");
        var data = JsonSerializer.Deserialize<MloData>(File.ReadAllText(jsonFile));
        var shellName = name + "_shell";
        var colName = name + "_col";

        // Hülle + Texturen + Kollision
        var ydrSrc = Path.Combine(exportDir, shellName + ".ydr");
        if (!File.Exists(ydrSrc)) throw new UserError($"Blender hat keine {shellName}.ydr exportiert.");
        var ydr = Path.Combine(s, shellName + ".ydr");
        Session.Backup(ydr, backupDir);
        File.Copy(ydrSrc, ydr, true);
        string txd = null;
        var texDir = Path.Combine(exportDir, "texturen");
        var images = Directory.Exists(texDir) ? Directory.GetFiles(texDir).Where(f => new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds" }.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList() : new List<string>();
        if (images.Count > 0)
        {
            var list = images.Select(img =>
            {
                var tn = Path.GetFileNameWithoutExtension(img);
                Program.Progress($"Textur {tn} wird umgewandelt …");
                return Textures.BuildTexture(img, tn, tn.EndsWith("_n") ? TextureFormat.D3DFMT_ATI2 : TextureFormat.D3DFMT_DXT5, keepFormatFromDds: true);
            }).ToList();
            var ytd = new YtdFile { TextureDict = new TextureDictionary() };
            ytd.TextureDict.BuildFromTextureList(list);
            var ytdFile = Path.Combine(s, shellName + ".ytd");
            Session.Backup(ytdFile, backupDir);
            File.WriteAllBytes(ytdFile, ytd.Save());
            txd = shellName;
        }
        var ybnSrc = Path.Combine(exportDir, colName + ".ybn");
        if (File.Exists(ybnSrc)) { var t = Path.Combine(s, colName + ".ybn"); Session.Backup(t, backupDir); File.Copy(ybnSrc, t, true); }

        // Hülle: Größe
        var yd = new YdrFile(); yd.Load(File.ReadAllBytes(ydr));
        var bmin = yd.Drawable.BoundingBoxMin; var bmax = yd.Drawable.BoundingBoxMax;

        // Bisher im MLO Studio eingefügte Objekte behalten
        var kept = KeptEntities(Path.Combine(s, name + ".ytyp"), name);

        // ---------------- YTYP
        var x = new StringBuilder();
        x.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        x.AppendLine("<CMapTypes>");
        x.AppendLine(" <extensions />");
        x.AppendLine(" <archetypes>");
        // Hülle
        x.AppendLine("  <Item type=\"CBaseArchetypeDef\">");
        x.AppendLine("   <lodDist value=\"200\" />");
        x.AppendLine("   <flags value=\"0\" />");
        x.AppendLine("   <specialAttribute value=\"0\" />");
        x.AppendLine($"   <bbMin {V(new[] { bmin.X, bmin.Y, bmin.Z })} />");
        x.AppendLine($"   <bbMax {V(new[] { bmax.X, bmax.Y, bmax.Z })} />");
        var c = (bmin + bmax) * 0.5f;
        x.AppendLine($"   <bsCentre {V(new[] { c.X, c.Y, c.Z })} />");
        x.AppendLine($"   <bsRadius value=\"{F((bmax - bmin).Length() * 0.5f)}\" />");
        x.AppendLine("   <hdTextureDist value=\"100\" />");
        x.AppendLine($"   <name>{shellName}</name>");
        x.AppendLine(txd != null ? $"   <textureDictionary>{txd}</textureDictionary>" : "   <textureDictionary />");
        x.AppendLine("   <clipDictionary />");
        x.AppendLine("   <drawableDictionary />");
        x.AppendLine("   <physicsDictionary />");
        x.AppendLine("   <assetType>ASSET_TYPE_DRAWABLE</assetType>");
        x.AppendLine($"   <assetName>{shellName}</assetName>");
        x.AppendLine("   <extensions />");
        x.AppendLine("  </Item>");
        // MLO
        x.AppendLine("  <Item type=\"CMloArchetypeDef\">");
        x.AppendLine("   <lodDist value=\"100\" />");
        x.AppendLine("   <flags value=\"0\" />");
        x.AppendLine("   <specialAttribute value=\"0\" />");
        x.AppendLine("   <bbMin x=\"0\" y=\"0\" z=\"0\" />");
        x.AppendLine("   <bbMax x=\"0\" y=\"0\" z=\"0\" />");
        x.AppendLine("   <bsCentre x=\"0\" y=\"0\" z=\"0\" />");
        x.AppendLine("   <bsRadius value=\"0\" />");
        x.AppendLine("   <hdTextureDist value=\"40\" />");
        x.AppendLine($"   <name>{name}</name>");
        x.AppendLine("   <textureDictionary />");
        x.AppendLine("   <clipDictionary />");
        x.AppendLine("   <drawableDictionary />");
        x.AppendLine("   <physicsDictionary />");
        x.AppendLine("   <assetType>ASSET_TYPE_ASSETLESS</assetType>");
        x.AppendLine($"   <assetName>{name}</assetName>");
        x.AppendLine("   <extensions />");
        x.AppendLine("   <mloFlags value=\"0\" />");
        // Objekte: 0 = Hülle, danach Türen
        x.AppendLine("   <entities>");
        void Entity(string arch, float[] pos, float[] rotStored, float lod, uint guid = 0, float sxy = 1, float sz = 1, uint flags = 0)
        {
            x.AppendLine("    <Item type=\"CEntityDef\">");
            x.AppendLine($"     <archetypeName>{Esc(arch)}</archetypeName>");
            x.AppendLine($"     <flags value=\"{flags}\" />");
            x.AppendLine($"     <guid value=\"{guid}\" />");
            x.AppendLine($"     <position {V(pos)} />");
            x.AppendLine($"     <rotation x=\"{F(rotStored[0])}\" y=\"{F(rotStored[1])}\" z=\"{F(rotStored[2])}\" w=\"{F(rotStored[3])}\" />");
            x.AppendLine($"     <scaleXY value=\"{F(sxy)}\" />");
            x.AppendLine($"     <scaleZ value=\"{F(sz)}\" />");
            x.AppendLine("     <parentIndex value=\"-1\" />");
            x.AppendLine($"     <lodDist value=\"{F(lod)}\" />");
            x.AppendLine("     <childLodDist value=\"0\" />");
            x.AppendLine("     <lodLevel>LODTYPES_DEPTH_ORPHANHD</lodLevel>");
            x.AppendLine("     <numChildren value=\"0\" />");
            x.AppendLine("     <priorityLevel>PRI_REQUIRED</priorityLevel>");
            x.AppendLine("     <extensions />");
            x.AppendLine("     <ambientOcclusionMultiplier value=\"255\" />");
            x.AppendLine("     <artificialAmbientOcclusion value=\"255\" />");
            x.AppendLine("     <tintValue value=\"0\" />");
            x.AppendLine("    </Item>");
        }
        Entity(shellName, new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 0f, 1f }, 200, GuidBase);
        // MLO-Objekte speichern die Drehung invertiert
        for (int i = 0; i < data.doors.Count; i++)
        {
            var d = data.doors[i];
            Entity(d.archetype, d.position, new[] { -d.rotation[0], -d.rotation[1], -d.rotation[2], d.rotation[3] }, 100, GuidBase + 1 + (uint)i);
        }
        foreach (var k in kept) Entity(k.Arch, k.Pos, k.Rot, k.Lod <= 0 ? 100 : k.Lod, k.Guid, k.Sxy, k.Sz, k.Flags);
        x.AppendLine("   </entities>");

        // Räume: 0 = limbo (draußen), dann die Räume aus Blender
        int nRooms = data.rooms.Count + 1;
        var attached = Enumerable.Range(0, nRooms).Select(_ => new List<int>()).ToList();
        attached[Math.Min(1, nRooms - 1)].Add(0); // Hülle im ersten Raum
        for (int i = 0; i < data.doors.Count; i++)
        {
            int r = Math.Clamp(data.doors[i].room, 0, nRooms - 1);
            attached[r == 0 ? 1 : r].Add(i + 1);
        }
        for (int i = 0; i < kept.Count; i++)
        {
            var p = kept[i].Pos;
            int r = 1;
            for (int ri = 0; ri < data.rooms.Count; ri++)
            {
                var rm = data.rooms[ri];
                bool inside = true;
                for (int k = 0; k < 3; k++) inside &= p[k] >= rm.min[k] - 0.05f && p[k] <= rm.max[k] + 0.05f;
                if (inside) { r = ri + 1; break; }
            }
            attached[Math.Min(r, nRooms - 1)].Add(1 + data.doors.Count + i);
        }
        var portalCount = new int[nRooms];
        foreach (var p in data.portals)
        {
            portalCount[Math.Clamp(p.from, 0, nRooms - 1)]++;
            if (p.to != p.from) portalCount[Math.Clamp(p.to, 0, nRooms - 1)]++;
        }
        x.AppendLine("   <rooms itemType=\"CMloRoomDef\">");
        void RoomXml(string rn, float[] mn, float[] mx, string tc, float blend, int idx)
        {
            x.AppendLine("    <Item>");
            x.AppendLine($"     <name>{Esc(rn)}</name>");
            x.AppendLine($"     <bbMin {V(mn)} />");
            x.AppendLine($"     <bbMax {V(mx)} />");
            x.AppendLine($"     <blend value=\"{F(blend)}\" />");
            x.AppendLine($"     <timecycleName>{tc}</timecycleName>");
            x.AppendLine("     <secondaryTimecycleName />");
            x.AppendLine("     <flags value=\"0\" />");
            x.AppendLine($"     <portalCount value=\"{portalCount[idx]}\" />");
            x.AppendLine($"     <floorId value=\"{(idx == 0 ? 0 : 1)}\" />");
            x.AppendLine("     <exteriorVisibiltyDepth value=\"-1\" />");
            x.AppendLine(attached[idx].Count > 0 ? $"     <attachedObjects>{string.Join(" ", attached[idx])}</attachedObjects>" : "     <attachedObjects />");
            x.AppendLine("    </Item>");
        }
        RoomXml("limbo", new[] { bmin.X, bmin.Y, bmin.Z }, new[] { bmax.X, bmax.Y, bmax.Z }, TimecycleLimbo, 1f, 0);
        for (int i = 0; i < data.rooms.Count; i++)
        {
            var rn = Session.SafeName(data.rooms[i].name);
            RoomXml(rn, data.rooms[i].min, data.rooms[i].max, TimecycleRoom, 1f, i + 1);
        }
        x.AppendLine("   </rooms>");

        // Portale mit angehängten Türen
        x.AppendLine("   <portals itemType=\"CMloPortalDef\">");
        for (int pi = 0; pi < data.portals.Count; pi++)
        {
            var p = data.portals[pi];
            var doors = data.doors.Select((d, i) => (d, i)).Where(t => t.d.portal == pi).Select(t => t.i + 1).ToList();
            x.AppendLine("    <Item>");
            x.AppendLine($"     <roomFrom value=\"{p.from}\" />");
            x.AppendLine($"     <roomTo value=\"{p.to}\" />");
            x.AppendLine("     <flags value=\"0\" />");
            x.AppendLine("     <mirrorPriority value=\"0\" />");
            x.AppendLine("     <opacity value=\"0\" />");
            x.AppendLine("     <audioOcclusion value=\"0\" />");
            x.AppendLine("     <corners>");
            foreach (var k in p.corners) x.AppendLine($"      <Item>{F(k[0])}, {F(k[1])}, {F(k[2])}, 0</Item>");
            x.AppendLine("     </corners>");
            x.AppendLine(doors.Count > 0 ? $"     <attachedObjects>{string.Join(" ", doors)}</attachedObjects>" : "     <attachedObjects />");
            x.AppendLine("    </Item>");
        }
        x.AppendLine("   </portals>");
        x.AppendLine("   <entitySets itemType=\"CMloEntitySet\" />");
        x.AppendLine("   <timeCycleModifiers itemType=\"CMloTimeCycleModifier\" />");
        x.AppendLine("  </Item>");
        x.AppendLine(" </archetypes>");
        x.AppendLine($" <name>{name}</name>");
        x.AppendLine(" <dependencies />");
        x.AppendLine(" <compositeEntityTypes itemType=\"CCompositeEntityType\" />");
        x.AppendLine("</CMapTypes>");
        WriteMeta(x.ToString(), Path.Combine(s, name + ".ytyp"), backupDir);

        // ---------------- YMAP (Platzierung)
        var pos = data.position ?? new[] { 0f, 0f, 0f };
        var ymapName = name + "_milo";
        var m = new StringBuilder();
        m.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        m.AppendLine("<CMapData>");
        m.AppendLine($" <name>{ymapName}</name>");
        m.AppendLine(" <parent />");
        m.AppendLine(" <flags value=\"0\" />");
        m.AppendLine(" <contentFlags value=\"9\" />");
        m.AppendLine($" <streamingExtentsMin {V(pos)} />");
        m.AppendLine($" <streamingExtentsMax {V(pos)} />");
        m.AppendLine($" <entitiesExtentsMin {V(pos)} />");
        m.AppendLine($" <entitiesExtentsMax {V(pos)} />");
        m.AppendLine(" <entities>");
        m.AppendLine("  <Item type=\"CMloInstanceDef\">");
        m.AppendLine($"   <archetypeName>{name}</archetypeName>");
        m.AppendLine("   <flags value=\"32\" />");
        m.AppendLine("   <guid value=\"0\" />");
        m.AppendLine($"   <position {V(pos)} />");
        m.AppendLine("   <rotation x=\"0\" y=\"0\" z=\"0\" w=\"1\" />");
        m.AppendLine("   <scaleXY value=\"1\" />");
        m.AppendLine("   <scaleZ value=\"1\" />");
        m.AppendLine("   <parentIndex value=\"-1\" />");
        m.AppendLine("   <lodDist value=\"200\" />");
        m.AppendLine("   <childLodDist value=\"0\" />");
        m.AppendLine("   <lodLevel>LODTYPES_DEPTH_ORPHANHD</lodLevel>");
        m.AppendLine("   <numChildren value=\"0\" />");
        m.AppendLine("   <priorityLevel>PRI_REQUIRED</priorityLevel>");
        m.AppendLine("   <extensions />");
        m.AppendLine("   <ambientOcclusionMultiplier value=\"255\" />");
        m.AppendLine("   <artificialAmbientOcclusion value=\"255\" />");
        m.AppendLine("   <tintValue value=\"0\" />");
        m.AppendLine("   <groupId value=\"0\" />");
        m.AppendLine("   <floorId value=\"0\" />");
        m.AppendLine("   <defaultEntitySets />");
        m.AppendLine($"   <numExitPortals value=\"{data.portals.Count(p => p.to == 0 || p.from == 0)}\" />");
        m.AppendLine("   <MLOInstflags value=\"0\" />");
        m.AppendLine("  </Item>");
        m.AppendLine(" </entities>");
        m.AppendLine(" <containerLods itemType=\"rage__fwContainerLodDef\" />");
        m.AppendLine(" <boxOccluders itemType=\"BoxOccluder\" />");
        m.AppendLine(" <occludeModels itemType=\"OccludeModel\" />");
        m.AppendLine(" <physicsDictionaries />");
        m.AppendLine(" <instancedData>");
        m.AppendLine("  <ImapLink />");
        m.AppendLine("  <PropInstanceList itemType=\"rage__fwPropInstanceListDef\" />");
        m.AppendLine("  <GrassInstanceList itemType=\"rage__fwGrassInstanceListDef\" />");
        m.AppendLine(" </instancedData>");
        m.AppendLine(" <timeCycleModifiers itemType=\"CTimeCycleModifier\" />");
        m.AppendLine(" <carGenerators itemType=\"CCarGen\" />");
        m.AppendLine(" <LODLightsSOA>");
        m.AppendLine("  <direction itemType=\"FloatXYZ\" />");
        m.AppendLine("  <falloff />");
        m.AppendLine("  <falloffExponent />");
        m.AppendLine("  <timeAndStateFlags />");
        m.AppendLine("  <hash />");
        m.AppendLine("  <coneInnerAngle />");
        m.AppendLine("  <coneOuterAngleOrCapExt />");
        m.AppendLine("  <coronaIntensity />");
        m.AppendLine(" </LODLightsSOA>");
        m.AppendLine(" <DistantLODLightsSOA>");
        m.AppendLine("  <position itemType=\"FloatXYZ\" />");
        m.AppendLine("  <RGBI />");
        m.AppendLine("  <numStreetLights value=\"0\" />");
        m.AppendLine("  <category value=\"0\" />");
        m.AppendLine(" </DistantLODLightsSOA>");
        m.AppendLine(" <block>");
        m.AppendLine("  <version value=\"0\" />");
        m.AppendLine("  <flags value=\"0\" />");
        m.AppendLine($"  <name>{ymapName}</name>");
        m.AppendLine("  <exportedBy>GTA Tool</exportedBy>");
        m.AppendLine("  <owner></owner>");
        m.AppendLine($"  <time>{DateTime.Now:dd MMMM yyyy HH:mm}</time>");
        m.AppendLine(" </block>");
        m.AppendLine("</CMapData>");
        var ymapRel = "stream/" + ymapName + ".ymap";
        WriteMeta(m.ToString(), Path.Combine(sessionDir, ymapRel), backupDir);
        // Drehung + Extents über CodeWalker (richtige Konvention für MLO-Platzierungen)
        float h = data.heading * MathF.PI / 180f;
        Mlo.SetPlacement(sessionDir, ymapRel, 0, pos, new[] { 0f, 0f, MathF.Sin(h / 2), MathF.Cos(h / 2) }, null);

        foreach (var n in new[] { name, shellName, colName, ymapName }) JenkIndex.Ensure(n);
        foreach (var d in data.doors) JenkIndex.Ensure(d.archetype);
        return new
        {
            name,
            rooms = data.rooms.Count, portals = data.portals.Count, doors = data.doors.Count, textures = images.Count, keptObjects = kept.Count,
            warnings = data.warnings,
        };
    }

    static void WriteMeta(string xml, string file, string backupDir)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var bytes = XmlMeta.GetData(doc, MetaFormat.RSC, "");
        if (bytes == null || bytes.Length < 16) throw new UserError($"{Path.GetFileName(file)} konnte nicht erzeugt werden.");
        Session.Backup(file, backupDir);
        File.WriteAllBytes(file, bytes);
    }
}
