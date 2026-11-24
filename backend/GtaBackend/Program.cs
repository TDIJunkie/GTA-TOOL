using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GtaBackend;

/// <summary>
/// JSON-Zeilen-Protokoll über stdin/stdout.
/// Anfrage:  {"id":1,"method":"…","params":{…}}
/// Antwort:  {"id":1,"result":…}  oder  {"id":1,"error":"…"}
/// Ereignis: {"event":"progress","data":{"text":"…","percent":42}}
/// </summary>
public static class Program
{
    static readonly object WriteLock = new();
    static TextWriter Out;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static void Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        var stdout = Console.OpenStandardOutput();
        Out = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
        // CodeWalker schreibt gelegentlich auf Console – das darf das Protokoll nicht stören.
        Console.SetOut(TextWriter.Null);

        var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        Send(new JsonObject { ["event"] = "ready" });

        string line;
        while ((line = input.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode id = null;
            try
            {
                var req = JsonNode.Parse(line)!.AsObject();
                id = req["id"]?.DeepClone();
                var method = req["method"]?.GetValue<string>() ?? "";
                var p = req["params"] as JsonObject ?? new JsonObject();
                object result = Dispatch(method, p);
                var resp = new JsonObject { ["id"] = id };
                resp["result"] = result == null ? null : JsonSerializer.SerializeToNode(result, result.GetType(), JsonOpts);
                Send(resp);
            }
            catch (Exception ex)
            {
                var msg = ex is UserError ? ex.Message : $"{ex.Message}";
                Log($"FEHLER: {ex}");
                Send(new JsonObject { ["id"] = id, ["error"] = msg });
            }
        }
    }

    static object Dispatch(string method, JsonObject p) => method switch
    {
        "ping" => new { ok = true, version = "0.1.0" },
        "keys.load" => Keys.Load(p.Str("gtaPath"), p.Str("aesKey")),
        "keys.status" => Keys.Status(),
        "session.open" => Session.Open(p.StrList("paths"), p.Str("sessionDir")),
        "session.analyze" => Session.Analyze(p.Str("sessionDir")),
        "textures.list" => Textures.List(p.Str("file")),
        "textures.preview" => Textures.Preview(p.Str("file"), p.Str("name"), p.Int("maxSize", 1024)),
        "textures.export" => Textures.Export(p.Str("file"), p.StrList("names"), p.Str("outDir"), p.Str("format")),
        "textures.replace" => Textures.Replace(p.Str("file"), p.Str("name"), p.Str("image"), p.Str("backupDir")),
        "textures.add" => Textures.Add(p.Str("file"), p.StrList("images"), p.Str("backupDir")),
        "textures.remove" => Textures.Remove(p.Str("file"), p.StrList("names"), p.Str("backupDir")),
        "blender.prepare" => BlenderBridge.Prepare(p.Str("sessionDir"), p.Str("model"), p.StrList("ytds")),
        "blender.apply" => BlenderBridge.Apply(p.Str("sessionDir"), p.Str("exportDir"), p.Str("model"), p.Str("backupDir")),
        "debug.yft" => DebugInfo.Yft(p.Str("file")),
        "debug.ymt" => Ymt.Roundtrip(p.Str("file"), p.Str("outXml")),
        "debug.extract" => DebugInfo.ExtractFromRpf(p.Str("rpf"), p.Str("entry"), p.Str("out")),
        "debug.hashes" => DebugInfo.FindHashes(p.Str("gtaPath"), p.StrList("hashes")),
        "debug.ymap" => DebugInfo.Ymap(p.Str("file")),
        "debug.ydr" => DebugInfo.Ydr(p.Str("file")),
        "debug.find" => DebugInfo.FindInRpf(p.Str("rpf"), p.Str("term")),
        "debug.skel" => DebugSkel.Compare(p.Str("file")),
        "debug.xml" => DebugXml.ToXml(p.Str("file"), p.Str("outFile")),
        "debug.ydd" => DebugYdd.Info(p.Str("file")),
        "map.build" => MapView.Build(p.Str("gtaPath"), p.Str("outFile")),
        "mlo.doorList" => MloCreate.DoorList(p.Str("propsCache")),
        "world.status" => World.Status(p.Str("cacheFile")),
        "world.build" => World.BuildIndex(p.Str("gtaPath"), p.Str("cacheFile")),
        "world.area" => World.Area(p.Str("gtaPath"), p.Str("cacheFile"), p.Str("propsCache"), p.Float("x"), p.Float("y"), p.Float("radius", 120f), p.Int("max", 1500)),
        "world.archGlb" => World.ArchGlb(p.Str("gtaPath"), p.Str("propsCache"), p.Str("propsDir"), p.Str("name"), p.Str("outFile")),
        "mlo.applyNew" => MloCreate.ApplyNew(p.Str("sessionDir"), p.Str("exportDir"), p.Str("name"), p.Str("backupDir")),
        "map.height" => MapView.Height(p.Str("gtaPath"), p.Float("x"), p.Float("y"), p.Str("flipY") == "true"),
        "model.glb" => Model3D.Export(p.Str("file"), p.StrList("ytds"), p.Str("outFile"), p.Str("attachYft"), p.Str("attachBone"), p.Str("livery"), p.StrList("hideBones"), p.Str("variantTexture") == "true"),
        "livery.info" => Livery.Info(p.Str("yft"), p.StrList("ytds")),
        "livery.template" => Livery.Template(p.Str("yft"), p.Int("size", 2048), p.Str("outFile"), p.Str("bgYtd"), p.Str("bgName")),
        "livery.add" => Livery.Add(p.Str("ytd"), p.Str("base"), p.StrList("images"), p.Str("backupDir")),
        "livery.create" => Livery.Create(p.Str("ytd"), p.Str("base"), p.Int("size", 2048), p.Str("backupDir")),
        "livery.remove" => Livery.Remove(p.Str("ytd"), p.Str("base"), p.Str("name"), p.Str("backupDir")),
        "audio.info" => Audio.Info(p.Str("sessionDir")),
        "audio.setName" => Audio.SetAudioName(p.Str("sessionDir"), p.Str("model"), p.Str("audioName"), p.Str("backupDir")),
        "audio.wav" => Audio.StreamWav(p.Str("awc"), p.Int("index", 0), p.Str("outFile")),
        "audio.exportAll" => Audio.ExportAll(p.Str("awc"), p.Str("outDir")),
        "audio.replace" => Audio.ReplaceStream(p.Str("awc"), p.Int("index", 0), p.Str("wav"), p.Str("backupDir")),
        "audio.relGet" => Audio.RelXmlGet(p.Str("file")),
        "audio.relSet" => Audio.RelXmlSet(p.Str("file"), p.Str("xml"), p.Str("backupDir")),
        "vanilla.status" => Vanilla.Status(p.Str("cacheFile"), p.Str("gtaPath")),
        "vanilla.build" => Vanilla.Build(p.Str("gtaPath"), p.Str("cacheFile")),
        "vanilla.lookup" => Vanilla.Lookup(p.Str("cacheFile"), p.StrList("models")),
        "vehicle.replaceToAddon" => VehicleConvert.ReplaceToAddon(p.Str("sessionDir"), p.Str("cacheFile"), p.Str("oldModel"), p.Str("newModel"),
            p.Str("displayName"), p.Str("makeName"), p.Str("ownHandling") != "false", p.Str("backupDir")),
        "vehicle.rename" => VehicleConvert.RenameAddon(p.Str("sessionDir"), p.Str("oldModel"), p.Str("newModel"), p.Str("displayName"), p.Str("makeName"), p.Str("backupDir")),
        "freemode.base" => Freemode.EnsureBase(p.Str("gtaPath"), p.Str("cacheDir")),
        "freemode.catalogStatus" => Freemode.CatalogStatus(p.Str("cacheFile")),
        "freemode.catalogBuild" => Freemode.BuildCatalog(p.Str("gtaPath"), p.Str("cacheFile")),
        "freemode.browse" => Freemode.Browse(p.Str("cacheFile"), p.Str("ped"), p.Str("comp"), p.Str("coll")),
        "freemode.preview" => Freemode.PreviewItem(p.Str("gtaPath"), p.Str("cacheFile"), p.Str("id"), p.Str("outDir")),
        "clothing.analyze" => Clothing.Analyze(p.Str("sessionDir")),
        "clothing.toAddon" => Clothing.ToAddon(p.Str("sessionDir"), p.StrList("ids"), p.Str("collection"), p.Str("backupDir")),
        "clothing.toReplace" => Clothing.ToReplace(p.Str("sessionDir"), p.Str("id"), p.Int("index", 0), p.Str("backupDir")),
        "clothing.setGender" => Clothing.SetGender(p.Str("sessionDir"), p.Str("id"), p.Str("gender"), p.Str("backupDir")),
        "clothing.delete" => Clothing.Delete(p.Str("sessionDir"), p.Str("id"), p.Str("backupDir")),
        "clothing.addVariant" => Clothing.AddVariant(p.Str("sessionDir"), p.Str("id"), p.StrList("images"), p.Str("backupDir")),
        "clothing.removeVariant" => Clothing.RemoveVariant(p.Str("sessionDir"), p.Str("id"), p.Str("file"), p.Str("backupDir")),
        "clothing.transform" => Clothing.Transform(p.Str("sessionDir"), p.Str("id"), p.Float("dx"), p.Float("dy"), p.Float("dz"), p.Float("scale", 1f), p.Str("backupDir")),
        "clothing.uv" => Clothing.UvTemplate(p.Str("ydd"), p.Int("size", 2048), p.Str("outFile"), p.Str("bgYtd")),
        "clothing.addFromGta" => Clothing.AddFromGta(p.Str("sessionDir"), p.Str("gtaPath"), p.Str("cacheFile"), p.Str("catalogId"), p.Str("collection"), p.Str("keepTextures") != "false"),
        "clothing.prepareWork" => Clothing.PrepareWork(p.Str("sessionDir"), p.Str("id"), p.Str("workDir"), p.Str("ytd")),
        "clothing.applyWork" => Clothing.ApplyWork(p.Str("sessionDir"), p.Str("id"), p.Str("exportDir"), p.Str("backupDir")),
        "clothing.genderApply" => Clothing.GenderApply(p.Str("sessionDir"), p.Str("id"), p.Str("file")),
        "clothing.export" => ExportMerge.Run(p.Str("sessionDir"), p.Str("outDir"), p.Str("resourceName"), (o, n) => Clothing.Export(p.Str("sessionDir"), o, n)),
        "props.status" => Props.Status(p.Str("cacheFile")),
        "props.build" => Props.Build(p.Str("gtaPath"), p.Str("cacheFile")),
        "props.search" => Props.Search(p.Str("cacheFile"), p.Str("term"), p.Int("max", 200)),
        "mlo.analyze" => Mlo.Analyze(p.Str("sessionDir")),
        "mlo.scene" => Mlo.Scene(p.Str("sessionDir"), p.Str("key"), p.Str("propsCache")),
        "mlo.archGlb" => Mlo.ArchetypeGlb(p.Str("sessionDir"), p.Str("name"), p.Str("gtaPath"), p.Str("propsCache"), p.Str("propsDir"), p.Str("outFile")),
        "mlo.collisionGlb" => Mlo.CollisionGlb(p.Str("ybn"), p.Str("outFile"), p.Floats("mloPos"), p.Floats("mloRot"), p.Floats("clipMin"), p.Floats("clipMax")),
        "mlo.setEntity" => Mlo.SetEntity(p.Str("sessionDir"), p.Str("key"), p.Str("id"), p.Floats("pos"), p.Floats("rot"), p.Floats("scale"), p.Str("backupDir")),
        "mlo.deleteEntity" => Mlo.DeleteEntity(p.Str("sessionDir"), p.Str("key"), p.Str("id"), p.Str("backupDir")),
        "mlo.swapArchetype" => Mlo.SwapArchetype(p.Str("sessionDir"), p.Str("key"), p.Str("id"), p.Str("from"), p.Str("to"), p.Str("backupDir")),
        "mlo.addEntity" => Mlo.AddEntity(p.Str("sessionDir"), p.Str("key"), p.Str("name"), p.Floats("pos"), p.Floats("rot"), p.Int("room", -1), p.Str("copyFrom"), p.Str("backupDir")),
        "mlo.setPlacement" => Mlo.SetPlacement(p.Str("sessionDir"), p.Str("ymap"), p.Int("index", 0), p.Floats("pos"), p.Floats("rot"), p.Str("backupDir")),
        "mlo.importModel" => Mlo.ImportModel(p.Str("sessionDir"), p.Str("key"), p.Str("ydr"), p.Str("ytd"), p.Str("backupDir")),
        "mlo.export" => ExportMerge.Run(p.Str("sessionDir"), p.Str("outDir"), p.Str("resourceName"), (o, n) => Mlo.Export(p.Str("sessionDir"), o, n)),
        "blender.prepareFile" => BlenderBridge.PrepareFile(p.Str("sessionDir"), p.Str("file"), p.Str("workDir"), p.StrList("ytds")),
        "blender.applyFile" => BlenderBridge.ApplyFile(p.Str("sessionDir"), p.Str("file"), p.Str("exportDir"), p.Str("backupDir")),
        "anim.analyze" => Animations.Analyze(p.Str("sessionDir")),
        "anim.sample" => Animations.Sample(p.Str("file"), p.Str("clip"), p.Int("fps", 30)),
        "anim.rig" => Model3D.ExportRig(p.Str("skeleton"), p.StrList("models"), p.StrList("ytds"), p.Str("outFile")),
        "anim.renameClip" => Animations.RenameClip(p.Str("sessionDir"), p.Str("file"), p.Str("clip"), p.Str("name"), p.Str("backupDir")),
        "anim.duplicateClip" => Animations.DuplicateClip(p.Str("sessionDir"), p.Str("file"), p.Str("clip"), p.Str("name"), p.Str("backupDir")),
        "anim.deleteClip" => Animations.DeleteClip(p.Str("sessionDir"), p.Str("file"), p.Str("clip"), p.Str("backupDir")),
        "anim.setTiming" => Animations.SetTiming(p.Str("sessionDir"), p.Str("file"), p.Str("clip"), p.Float("rate", 1f), p.Float("start"), p.Float("end"), p.Str("backupDir")),
        "anim.copyClip" => Animations.CopyClip(p.Str("sessionDir"), p.Str("file"), p.Str("clip"), p.Str("target"), p.Str("name"), p.Str("backupDir")),
        "anim.newDictFromClip" => Animations.NewDictFromClip(p.Str("sessionDir"), p.Str("file"), p.Str("clip"), p.Str("name"), p.Str("backupDir")),
        "anim.renameDict" => Animations.RenameDict(p.Str("sessionDir"), p.Str("file"), p.Str("name"), p.Str("backupDir")),
        "anim.deleteDict" => Animations.DeleteDict(p.Str("sessionDir"), p.Str("file"), p.Str("backupDir")),
        "anim.exportFile" => Animations.ExportFile(p.Str("sessionDir"), p.Str("file"), p.Str("outFile"), p.Str("format")),
        "anim.prepareBlender" => Animations.PrepareBlender(p.Str("sessionDir"), p.Str("file"), p.Str("workDir")),
        "anim.applyBlender" => Animations.ApplyBlender(p.Str("sessionDir"), p.Str("file"), p.Str("exportDir"), p.Str("clip"), p.Str("backupDir")),
        "anim.export" => ExportMerge.Run(p.Str("sessionDir"), p.Str("outDir"), p.Str("resourceName"), (o, n) => Animations.Export(p.Str("sessionDir"), o, n, p.Str("testCommand") != "false")),
        "prop.analyze" => PropPack.Analyze(p.Str("sessionDir")),
        "prop.details" => PropPack.Details(p.Str("sessionDir"), p.Str("file")),
        "prop.ensureArchetypes" => PropPack.EnsureArchetypes(p.Str("sessionDir"), p.Str("ytypName"), p.Str("backupDir")),
        "prop.setLod" => PropPack.SetLod(p.Str("sessionDir"), p.Str("name"), p.Float("lodDist", 150f), p.Str("backupDir")),
        "prop.rename" => PropPack.Rename(p.Str("sessionDir"), p.Str("name"), p.Str("newName"), p.Str("backupDir")),
        "prop.delete" => PropPack.Delete(p.Str("sessionDir"), p.Str("name"), p.Str("backupDir")),
        "prop.addFromGta" => PropPack.AddFromGta(p.Str("sessionDir"), p.Str("gtaPath"), p.Str("propsCache"), p.Str("propsDir"), p.Str("gtaName"), p.Str("newName"), p.Str("ytypName"), p.Str("backupDir")),
        "prop.applyNew" => PropPack.ApplyNew(p.Str("sessionDir"), p.Str("exportDir"), p.Str("name"), p.Str("backupDir")),
        "weapon.analyze" => Weapons.Analyze(p.Str("sessionDir")),
        "weapon.setLabel" => Weapons.SetLabel(p.Str("sessionDir"), p.Str("weapon"), p.Str("text"), p.Str("backupDir")),
        "weapon.gtaStatus" => Weapons.GtaStatus(p.Str("cacheFile"), p.Str("propsCache")),
        "weapon.gtaBuild" => Weapons.BuildGta(p.Str("gtaPath"), p.Str("cacheFile"), p.Str("propsCache")),
        "weapon.gtaSearch" => Weapons.GtaSearch(p.Str("cacheFile"), p.Str("term"), p.Str("parts") == "true"),
        "weapon.gtaExtract" => Weapons.GtaExtract(p.Str("gtaPath"), p.Str("propsCache"), p.Str("model"), p.Str("cacheDir")),
        "weapon.addFromGta" => Weapons.AddFromGta(p.Str("sessionDir"), p.Str("gtaPath"), p.Str("propsCache"), p.Str("model"), p.Str("cacheDir")),
        "weapon.export" => ExportMerge.Run(p.Str("sessionDir"), p.Str("outDir"), p.Str("resourceName"), (o, n) => Weapons.Export(p.Str("sessionDir"), o, n, p.Str("testCommand") != "false")),
        "prop.export" => ExportMerge.Run(p.Str("sessionDir"), p.Str("outDir"), p.Str("resourceName"), (o, n) => PropPack.Export(p.Str("sessionDir"), o, n, p.Str("testCommand") != "false", p.Str("backupDir"))),
        "fivem.export" => ExportMerge.Run(p.Str("sessionDir"), p.Str("outDir"), p.Str("resourceName"), (o, n) => FiveMExport.Export(p.Str("sessionDir"), o, n)),
        "session.original" => new { name = Session.FindOriginal(p.Str("sessionDir"))?.Name },
        "file.export" => Session.ExportFile(p.Str("file"), p.Str("outDir")),
        "file.replace" => Session.ReplaceFile(p.Str("file"), p.Str("source"), p.Str("backupDir")),
        _ => throw new UserError($"Unbekannter Befehl: {method}")
    };

    public static void Send(JsonNode node)
    {
        var s = node.ToJsonString();
        lock (WriteLock) { Out.WriteLine(s); }
    }

    public static void Progress(string text, int percent = -1)
    {
        Send(new JsonObject { ["event"] = "progress", ["data"] = new JsonObject { ["text"] = text, ["percent"] = percent } });
    }

    public static void Log(string text)
    {
        try { Console.Error.WriteLine(text); } catch { }
    }

    // ---- kleine Helfer für Parameter ----
    public static string Str(this JsonObject o, string key)
    {
        var n = o[key];
        return n == null ? null : n.GetValue<string>();
    }
    public static int Int(this JsonObject o, string key, int def)
    {
        var n = o[key];
        return n == null ? def : n.GetValue<int>();
    }
    public static float Float(this JsonObject o, string key, float def = 0f)
    {
        var n = o[key];
        if (n == null) return def;
        try { return n.GetValue<float>(); } catch { }
        try { return (float)n.GetValue<double>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        return float.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
    }

    public static float[] Floats(this JsonObject o, string key)
    {
        if (o[key] is not JsonArray a) return null;
        return a.Select(x => { try { return x!.GetValue<float>(); } catch { return (float)x!.GetValue<double>(); } }).ToArray();
    }

    public static List<string> StrList(this JsonObject o, string key)
    {
        var n = o[key] as JsonArray;
        return n == null ? new List<string>() : n.Select(x => x!.GetValue<string>()).ToList();
    }
}

/// <summary>Fehler mit verständlicher Meldung für den Nutzer.</summary>
public class UserError : Exception
{
    public UserError(string msg) : base(msg) { }
}
