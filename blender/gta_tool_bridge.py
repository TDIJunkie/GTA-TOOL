"""GTA Tool – Blender-Brücke.

Start (vom GTA Tool):
    blender --python gta_tool_bridge.py -- --yft <pfad.yft> --export <ordner> --status <status.json> [--auto-export]

- importiert das Modell mit Sollumz (Texturen liegen als DDS in <modellordner>/<modellname>/)
- zeigt in der Seitenleiste (Taste N) den Reiter „GTA Tool“ mit dem Knopf „Speichern & zurück zum GTA Tool“
- der Knopf exportiert mit Sollumz (nativ, GEN8) in den Export-Ordner und meldet das per Statusdatei
- --auto-export: nur für Tests (importieren, sofort exportieren, beenden)
"""

import json
import shutil
import os
import sys
import time
import traceback
from pathlib import Path

import bpy

ARGS = {}
if "--" in sys.argv:
    rest = sys.argv[sys.argv.index("--") + 1:]
    i = 0
    while i < len(rest):
        key = rest[i]
        if key.startswith("--"):
            if i + 1 < len(rest) and not rest[i + 1].startswith("--"):
                ARGS[key[2:]] = rest[i + 1]
                i += 2
                continue
            ARGS[key[2:]] = True
        i += 1

YCD = ARGS.get("ycd")                    # Animation (.ycd.xml) – Körper kommt über --body/--skeleton
IS_ANIM = bool(YCD)
CLIP = str(ARGS.get("clip") or "")
YFT = Path(ARGS.get("yft") or ARGS.get("ydd") or ARGS.get("file") or YCD or "")
EXT = YFT.suffix.lower()
IS_YDD = EXT == ".ydd"
IS_GENERIC = EXT in (".ydd", ".ydr", ".ybn")   # Export über das Wurzelobjekt (nicht Fragment)
ROOT_TYPES = {".ydd": "sollumz_drawable_dictionary", ".ydr": "sollumz_drawable", ".ybn": "sollumz_bound_composite"}
SKELETON = ARGS.get("skeleton")          # Freemode-Skelett (.yft) für Kleidung
BODY = [Path(b) for b in str(ARGS.get("body") or "").split(";") if b]   # Körper-Referenz (nicht exportiert)
EXPORT_DIR = Path(ARGS.get("export", ""))
STATUS = ARGS.get("status")
NEW_PROP = str(ARGS.get("new-prop") or "").strip().lower()   # Neues Prop von Grund auf modellieren
NEW_MLO = str(ARGS.get("new-mlo") or "").strip().lower()     # Neues Interior von Grund auf
IS_MLO = bool(NEW_MLO)
IS_NEW = bool(NEW_PROP)
MODEL = NEW_MLO if IS_MLO else NEW_PROP if IS_NEW else (YFT.name[:-len(".ycd.xml")].lower() if IS_ANIM else YFT.stem.lower())
ANIM = {"clip": None, "full": False, "action_end": None}
STATE = {"exports": 0, "last": None, "error": None}


def write_status(event: str, **extra):
    if not STATUS:
        return
    data = {"event": event, "model": MODEL, "time": time.time(), "pid": os.getpid(), **STATE, **extra}
    tmp = STATUS + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(data, fh)
        os.replace(tmp, STATUS)
    except OSError:
        pass


def ensure_sollumz():
    import addon_utils
    for name in ("bl_ext.user_default.sollumz", "bl_ext.blender_org.sollumz", "sollumz"):
        try:
            if addon_utils.check(name)[1]:
                return True
            mod = addon_utils.enable(name, default_set=True)
            if mod:
                return True
        except Exception:
            pass
    return hasattr(bpy.ops, "sollumz") and hasattr(bpy.ops.sollumz, "import_assets")


def clear_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def import_with_skeleton(model: Path):
    """Kleidung (.ydd): mit Freemode-Skelett laden, sonst gehen die Bone-Gewichte verloren."""
    options = {}
    if SKELETON:
        options = {"dwd_import_external_skeleton": "SAVED", "dwd_import_external_skeleton_saved_path": str(SKELETON)}
    before = set(bpy.data.objects)
    res = bpy.ops.sollumz.import_assets(directory=str(model.parent), files=[{"name": model.name}], use_custom_settings=True, **options)
    if "FINISHED" not in res:
        raise RuntimeError(f"Sollumz konnte {model.name} nicht importieren.")
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        if o.type == "ARMATURE":
            o.data.pose_position = "REST"
    return new


def import_body_reference():
    """Grundkörper als gesperrte Referenz (wird nicht exportiert)."""
    if not BODY:
        return
    col = bpy.data.collections.get("Körper (Referenz)") or bpy.data.collections.new("Körper (Referenz)")
    if col.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(col)
    col.color_tag = "COLOR_02"
    for part in BODY:
        if not part.is_file():
            continue
        try:
            new = import_with_skeleton(part)
        except Exception as exc:  # noqa: BLE001
            print("Körperteil übersprungen:", part, exc)
            continue
        for o in new:
            for c in list(o.users_collection):
                c.objects.unlink(o)
            col.objects.link(o)
            o.hide_select = True
            o.name = "REF_" + o.name
    col.hide_select = True


def sollumz_import(model: Path):
    if IS_YDD:
        import_with_skeleton(model)
        return
    if EXT in (".ydr", ".ybn"):
        res = bpy.ops.sollumz.import_assets(directory=str(model.parent), files=[{"name": model.name}], use_custom_settings=True)
        if "FINISHED" not in res:
            raise RuntimeError(f"Sollumz konnte {model.name} nicht importieren.")
        return
    before = set(bpy.data.objects)
    try:
        res = bpy.ops.sollumz.import_assets(directory=str(model.parent), files=[{"name": model.name}], use_custom_settings=True)
        if "FINISHED" not in res:
            raise RuntimeError(f"Sollumz konnte {model.name} nicht importieren.")
        return
    except RuntimeError as exc:
        if "IndexError" not in str(exc):
            raise
    # Tuningteile verweisen oft auf Bones des Fahrzeugs → ohne Aufteilen erneut laden
    for obj in [o for o in bpy.data.objects if o not in before]:
        bpy.data.objects.remove(obj, do_unlink=True)
    res = bpy.ops.sollumz.import_assets(directory=str(model.parent), files=[{"name": model.name}],
                                        use_custom_settings=True, split_by_group=False)
    if "FINISHED" not in res:
        raise RuntimeError(f"Sollumz konnte {model.name} nicht importieren.")


def fragments():
    return [o for o in bpy.context.scene.objects if str(getattr(o, "sollum_type", "")) == "sollumz_fragment"]


def clothing_root():
    wanted = ROOT_TYPES.get(EXT, "sollumz_drawable_dictionary")
    roots = [o for o in bpy.context.scene.objects
             if str(getattr(o, "sollum_type", "")) == wanted and not o.name.startswith("REF_") and o.parent is None]
    return roots


def do_export_ydd():
    roots = clothing_root()
    if not roots:
        raise RuntimeError("Das Modell wurde in der Szene nicht gefunden. Wurde es gelöscht?")
    root = next((o for o in roots if o.name.lower() == MODEL), roots[0])
    root.name = MODEL
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    for o in bpy.data.objects:
        if o.type == "ARMATURE":
            o.data.pose_position = "REST"
    bpy.ops.object.select_all(action="DESELECT")
    for o in [root, *root.children_recursive]:
        o.hide_select = False
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    res = bpy.ops.sollumz.export_assets(
        directory=str(EXPORT_DIR),
        direct_export=True,
        limit_to_selected=True,
        apply_transforms=True,
        use_custom_settings=True,
        target_formats={"NATIVE"},
        target_versions={"GEN8"},
        export_ytds=False,
    )
    if "FINISHED" not in res:
        raise RuntimeError("Sollumz hat den Export abgebrochen (Details im Blender-Infofenster).")
    produced = [p for p in EXPORT_DIR.rglob("*" + EXT) if p.stem.lower() == MODEL]
    if not produced:
        raise RuntimeError(f"Der Export hat keine {MODEL}{EXT} erzeugt.")
    STATE["exports"] += 1
    STATE["last"] = time.time()
    STATE["error"] = None
    write_status("exported", files=[str(p) for p in produced])


def stype(o):
    return str(getattr(o, "sollum_type", ""))


def anim_import():
    """Animation: Freemode-Körper mit Skelett laden, YCD (XML) importieren, gewählten Clip aufs Skelett legen."""
    armatures = []
    for part in BODY:
        if not part.is_file():
            continue
        try:
            new = import_with_skeleton(part)
        except Exception as exc:  # noqa: BLE001
            print("Körperteil übersprungen:", part, exc)
            continue
        for o in new:
            if o.type == "ARMATURE":
                o.data.pose_position = "POSE"
                armatures.append(o)
    if not armatures:
        raise RuntimeError("Der Freemode-Körper konnte nicht geladen werden (Skelett fehlt).")
    target = armatures[0]
    target.name = "Skelett"
    for o in armatures[1:]:
        o.hide_set(True)

    before = set(bpy.data.objects)
    res = bpy.ops.sollumz.import_assets(directory=str(YFT.parent), files=[{"name": YFT.name}], use_custom_settings=True)
    if "FINISHED" not in res:
        raise RuntimeError(f"Sollumz konnte {YFT.name} nicht importieren.")
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        if stype(o) == "sollumz_animation":
            o.animation_properties.target_id_type = "ARMATURE"
            o.animation_properties.target_id = target.data

    clips = [o for o in new if stype(o) == "sollumz_clip"]
    if not clips:
        raise RuntimeError("In der Datei wurde kein Clip gefunden.")
    want = CLIP.lower()
    clip = next((c for c in clips if c.clip_properties.hash.lower() == want
                 or c.clip_properties.name.lower().replace("pack:/", "").replace(".clip", "") == want), clips[0])
    if not clip.clip_properties.animations or clip.clip_properties.animations[0].animation is None:
        raise RuntimeError("Der Clip enthält keine Animation.")
    ca = clip.clip_properties.animations[0]
    action = ca.animation.animation_properties.action
    for arm in armatures:
        ad = arm.animation_data_create()
        ad.action = action
        try:
            if getattr(ad, "action_slot", None) is None and len(action.slots):
                ad.action_slot = action.slots[0]
        except Exception:  # noqa: BLE001
            pass
    scene = bpy.context.scene
    scene.render.fps = 30
    scene.render.fps_base = 1.0
    start, end = action.frame_range
    scene.frame_start = int(round(start))
    scene.frame_end = max(int(round(end)), scene.frame_start + 1)
    scene.frame_set(scene.frame_start)
    ANIM["clip"] = clip.name
    ANIM["action_end"] = end
    # Clip deckt die ganze Animation ab → beim Export die (evtl. geänderte) Länge übernehmen
    ANIM["full"] = abs(ca.start_frame - start) < 0.5 and abs(ca.end_frame - end) < 0.5
    bpy.ops.object.select_all(action="DESELECT")
    target.select_set(True)
    bpy.context.view_layer.objects.active = target


def do_export_anim():
    dicts = [o for o in bpy.context.scene.objects if stype(o) == "sollumz_clip_dictionary"]
    if not dicts:
        raise RuntimeError("Das Clip-Dictionary wurde in der Szene nicht gefunden. Wurde es gelöscht?")
    root = next((o for o in dicts if o.name.lower() == MODEL), dicts[0])
    root.name = MODEL
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    clip = bpy.data.objects.get(ANIM["clip"] or "")
    if clip is not None and ANIM["full"] and clip.clip_properties.animations:
        ca = clip.clip_properties.animations[0]
        action = ca.animation.animation_properties.action if ca.animation else None
        if action is not None:
            start, end = action.frame_range
            if abs(end - ANIM["action_end"]) > 0.01:
                ca.start_frame = start
                ca.end_frame = end
                clip.clip_properties.duration = (end - start) / bpy.context.scene.render.fps
    bpy.ops.object.select_all(action="DESELECT")
    for o in [root, *root.children_recursive]:
        o.hide_select = False
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    res = bpy.ops.sollumz.export_assets(
        directory=str(EXPORT_DIR),
        direct_export=True,
        limit_to_selected=True,
        apply_transforms=False,
        use_custom_settings=True,
        target_formats={"CWXML"},
        target_versions={"GEN8"},
    )
    if "FINISHED" not in res:
        raise RuntimeError("Sollumz hat den Export abgebrochen (Details im Blender-Infofenster).")
    produced = [p for p in EXPORT_DIR.rglob("*.ycd.xml")]
    if not produced:
        raise RuntimeError("Der Export hat keine Animation (.ycd.xml) erzeugt.")
    STATE["exports"] += 1
    STATE["last"] = time.time()
    STATE["error"] = None
    write_status("exported", files=[str(p) for p in produced])


# ---------------------------------------------------------------- Neues Prop

def new_prop_setup():
    """Startszene: Würfel (1 m) mit Ursprung unten in der Mitte – das ist später der Standpunkt im Spiel."""
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0.0, 0.0, 0.0))
    cube = bpy.context.active_object
    for v in cube.data.vertices:
        v.co.z += 0.5
    cube.name = "Form"
    cube.data.name = "Form"
    mat = bpy.data.materials.new(f"{MODEL}_farbe")
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (0.55, 0.55, 0.58, 1.0)
    cube.data.materials.append(mat)
    bpy.context.scene.render.fps = 30
    bpy.ops.object.select_all(action="DESELECT")
    cube.select_set(True)
    bpy.context.view_layer.objects.active = cube


def _base_color_input(mat):
    if not mat or not mat.use_nodes or not mat.node_tree:
        return None, None
    for n in mat.node_tree.nodes:
        if n.bl_idname == "ShaderNodeBsdfPrincipled":
            return n, n.inputs.get("Base Color")
    return None, None


def _ensure_color_texture(mat, index, model=None):
    """Material ohne Bildtextur: Farbe als kleine Textur einbacken (GTA braucht eine Textur)."""
    bsdf, inp = _base_color_input(mat)
    if inp is None:
        return
    if inp.is_linked and inp.links[0].from_node.bl_idname == "ShaderNodeTexImage" and inp.links[0].from_node.image:
        return
    color = list(inp.default_value) if not inp.is_linked else [0.6, 0.6, 0.6, 1.0]
    img = bpy.data.images.new(f"{model or MODEL}_farbe{index}", width=16, height=16, alpha=False)
    # Blender speichert linear – Basisfarbe ist bereits linear, Bild ist sRGB → umrechnen
    def to_srgb(c):
        return 12.92 * c if c <= 0.0031308 else 1.055 * (c ** (1 / 2.4)) - 0.055
    px = [to_srgb(color[0]), to_srgb(color[1]), to_srgb(color[2]), 1.0] * (16 * 16)
    img.pixels = px
    img.pack()
    node = mat.node_tree.nodes.new("ShaderNodeTexImage")
    node.image = img
    for l in list(inp.links):
        mat.node_tree.links.remove(l)
    mat.node_tree.links.new(node.outputs["Color"], inp)


NEW_TEXTURES = []


TEMP_IMAGES = []
TEX_BY_IMAGE = {}


def _safe_tex_name(n):
    n = os.path.splitext(bpy.path.basename(n or ""))[0].lower()
    n = "".join(c if (c.isascii() and (c.isalnum() or c == "_")) else "_" for c in n).strip("_")
    return n or "textur"


def save_texture(node, tex_dir):
    """Bild einer Textur-Node als Datei ablegen und der Node eine Bild-Kopie geben, deren Dateiname den
    Texturnamen bestimmt (Sollumz nimmt den Namen aus dem Dateipfad). Die Bilder des Nutzers bleiben unverändert."""
    img = node.image
    if img.name in TEX_BY_IMAGE:
        node.image = TEX_BY_IMAGE[img.name]
        return
    base = _safe_tex_name(img.filepath if img.filepath else img.name)
    name, i = base, 1
    while name in NEW_TEXTURES:
        i += 1
        name = f"{base}_{i}"
    try:
        if img.packed_file:
            ext = "." + (img.file_format or "PNG").lower().replace("jpeg", "jpg").replace("targa", "tga")
            target = tex_dir / (name + ext)
            target.write_bytes(img.packed_file.data)
        else:
            src = Path(bpy.path.abspath(img.filepath)) if img.filepath else None
            if src and src.is_file():
                target = tex_dir / (name + src.suffix.lower())
                shutil.copyfile(src, target)
            else:
                target = tex_dir / (name + ".png")
                tmp = img.copy()
                tmp.filepath_raw = str(target)
                tmp.file_format = "PNG"
                tmp.save()
                bpy.data.images.remove(tmp)
        ref = img.copy()
        ref.filepath = str(target)
        TEMP_IMAGES.append(ref)
        TEX_BY_IMAGE[img.name] = ref
        node.image = ref
        NEW_TEXTURES.append(name)
    except Exception as exc:  # noqa: BLE001
        print("Textur übersprungen:", name, exc)


def plain_meshes(objs):
    return [o for o in objs if o.type == "MESH" and o.visible_get() and stype(o) in ("", "sollumz_none", "SollumType.NONE")]


def prepared_copies(meshes, model):
    """Meshes für GTA vorbereiten (Kopien): Modifikatoren angewendet, Weltlage eingebacken, Materialien kopiert,
    Farbe → Textur, UVs, „UVMap 0“, „Color 1“."""
    scene = bpy.context.scene
    dg = bpy.context.evaluated_depsgraph_get()
    dups = []
    for i, o in enumerate(meshes):
        eo = o.evaluated_get(dg)
        mesh = bpy.data.meshes.new_from_object(eo, preserve_all_data_layers=True, depsgraph=dg)
        mesh.transform(o.matrix_world)
        d = bpy.data.objects.new(f"{model}_teil{i}", mesh)
        scene.collection.objects.link(d)
        if not mesh.materials:
            m = bpy.data.materials.new(f"{model}_standard")
            m.use_nodes = True
            mesh.materials.append(m)
        for si in range(len(mesh.materials)):
            if mesh.materials[si] is None:
                continue
            mesh.materials[si] = mesh.materials[si].copy()
            _ensure_color_texture(mesh.materials[si], f"{i}_{si}", model)
        if not mesh.uv_layers:
            mesh.uv_layers.new(name="UVMap")
            bpy.ops.object.select_all(action="DESELECT")
            d.select_set(True)
            bpy.context.view_layer.objects.active = d
            try:
                bpy.ops.object.mode_set(mode="EDIT")
                bpy.ops.mesh.select_all(action="SELECT")
                bpy.ops.uv.smart_project()
            finally:
                bpy.ops.object.mode_set(mode="OBJECT")
        mesh.uv_layers[0].name = "UVMap 0"
        if "Color 1" not in mesh.color_attributes:
            ca = mesh.color_attributes.new(name="Color 1", type="BYTE_COLOR", domain="CORNER")
            ca.data.foreach_set("color", [1.0] * (len(ca.data) * 4))
        dups.append(d)
    return dups


def collision_material():
    for prefix in ("bl_ext.user_default.sollumz", "bl_ext.blender_org.sollumz", "sollumz"):
        try:
            import importlib
            cm = importlib.import_module(prefix + ".ybn.collision_materials")
            idx = next((i for i, m in enumerate(cm.collisionmats) if m.name == "CONCRETE"), 0)
            return cm.create_collision_material_from_index(idx)
        except Exception:  # noqa: BLE001
            continue
    return None


def new_prop_build(meshes=None, model=None, embed_col=True):
    """Meshes kopieren und in ein Sollumz-Drawable umwandeln (Kollision optional eingebettet, Texturen als Dateien).
    Die Arbeitsobjekte des Nutzers bleiben unverändert. Gibt (root, temporäre Objekte) zurück."""
    model = model or MODEL
    scene = bpy.context.scene
    if meshes is None:
        existing = [o for o in scene.objects if stype(o) == "sollumz_drawable" and o.parent is None and not o.name.startswith("REF_")]
        if existing:
            return existing[0], []
        meshes = plain_meshes(scene.objects)
    if not meshes:
        raise RuntimeError("Keine sichtbaren Objekte gefunden. Modelliere dein Prop als Mesh (z. B. ausgehend vom Würfel).")
    dups = prepared_copies(meshes, model)
    bpy.ops.object.select_all(action="DESELECT")
    for d in dups:
        d.select_set(True)
    bpy.context.view_layer.objects.active = dups[0]
    bpy.ops.sollumz.autoconvertmaterials()
    NEW_TEXTURES.clear()
    TEX_BY_IMAGE.clear()
    tex_dir = EXPORT_DIR / "texturen"
    shutil.rmtree(tex_dir, ignore_errors=True)
    tex_dir.mkdir(parents=True, exist_ok=True)
    for d in dups:
        for m in d.data.materials:
            if not m or not m.node_tree:
                continue
            for n in m.node_tree.nodes:
                if n.bl_idname == "ShaderNodeTexImage" and n.image is not None:
                    n.texture_properties.embedded = False
                    save_texture(n, tex_dir)
    scene.auto_create_embedded_col = embed_col
    scene.create_seperate_drawables = False
    scene.center_drawable_to_selection = False
    before = set(scene.objects)
    bpy.ops.object.select_all(action="DESELECT")
    for d in dups:
        d.select_set(True)
    bpy.context.view_layer.objects.active = dups[0]
    res = bpy.ops.sollumz.converttodrawable()
    if "FINISHED" not in res:
        raise RuntimeError("Sollumz konnte das Modell nicht umwandeln.")
    roots = [o for o in scene.objects if o not in before and stype(o) == "sollumz_drawable"]
    if not roots:
        raise RuntimeError("Sollumz hat kein Drawable erzeugt.")
    root = roots[0]
    temp = [root, *root.children_recursive]
    # Automatische Kollision: Materialien durch ein Kollisionsmaterial ersetzen (Beton = solide, normale Geräusche)
    colmat = collision_material() if embed_col else None
    if colmat is not None:
        for o in temp:
            if o.type == "MESH" and stype(o).startswith("sollumz_bound"):
                o.data.materials.clear()
                o.data.materials.append(colmat)
    return root, temp


def test_mlo():
    """Nur für Tests: zweiten Raum mit Durchgang anbauen, eine Tür innen."""
    W, D, H = MLO_SIZE
    shell = _coll(COLL_SHELL)
    o = bpy.data.objects.new("Anbau Boden", _box_mesh("anbau", (W / 2, -2, -0.2), (W / 2 + 4, 2, 0)))
    o.data.materials.append(_mat("Boden", (0.35, 0.30, 0.26)))
    shell.objects.link(o)
    mlo_add_room("Lager", (W / 2, -2, 0), (W / 2 + 4, 2, H))
    mlo_add_portal("Durchgang Lager", (W / 2 - 0.1, 0, 0), 1.2, 2.2, rot_z=1.5707963)


def test_model():
    """Nur für automatische Tests: wie ein Nutzer etwas modellieren."""
    cube = bpy.data.objects.get("Form")
    bsdf, inp = _base_color_input(cube.active_material)
    inp.default_value = (0.8, 0.05, 0.05, 1.0)
    mod = cube.modifiers.new("Abgerundet", "BEVEL")
    mod.width = 0.08
    mod.segments = 3
    bpy.ops.mesh.primitive_cylinder_add(radius=0.15, depth=1.0, location=(0.0, 0.0, 1.5))


def export_drawable(meshes, model, embed_col):
    """Meshes als <model>.ydr nach EXPORT_DIR (Texturen nach EXPORT_DIR/texturen). Gibt den Pfad zurück."""
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    root, temp = new_prop_build(meshes, model, embed_col)
    try:
        root.name = model
        bpy.ops.object.select_all(action="DESELECT")
        for o in [root, *root.children_recursive]:
            o.hide_select = False
            o.select_set(True)
        bpy.context.view_layer.objects.active = root
        for p in EXPORT_DIR.glob(f"{model}.ydr"):
            p.unlink()
        res = bpy.ops.sollumz.export_assets(
            directory=str(EXPORT_DIR), direct_export=True, limit_to_selected=True, apply_transforms=True,
            use_custom_settings=True, target_formats={"NATIVE"}, target_versions={"GEN8"}, export_ytds=False,
        )
        if "FINISHED" not in res:
            raise RuntimeError("Sollumz hat den Export abgebrochen (Details im Blender-Infofenster).")
        produced = [p for p in EXPORT_DIR.rglob("*.ydr") if p.stem.lower() == model]
        if not produced:
            raise RuntimeError(f"Der Export hat keine {model}.ydr erzeugt.")
        return produced[0]
    finally:
        cleanup_temp(temp)


def cleanup_temp(temp):
    """Umgewandelte Kopien wieder entfernen – der Nutzer arbeitet an seinen Originalen weiter."""
    for im in TEMP_IMAGES:
        try:
            bpy.data.images.remove(im)
        except Exception:  # noqa: BLE001
            pass
    TEMP_IMAGES.clear()
    for o in temp:
        try:
            data = o.data if o.type == "MESH" else None
            bpy.data.objects.remove(o, do_unlink=True)
            if data is not None and data.users == 0:
                bpy.data.meshes.remove(data)
        except Exception:  # noqa: BLE001
            pass


# ---------------------------------------------------------------- Neues Interior (MLO)

def _floats(s, n, default):
    try:
        v = [float(x) for x in str(s).split(",")]
        return v if len(v) == n else default
    except Exception:  # noqa: BLE001
        return default


MLO_POS = _floats(ARGS.get("pos"), 3, [0.0, 0.0, 0.0])
MLO_HEADING = _floats(ARGS.get("heading"), 1, [0.0])[0]
MLO_SIZE = _floats(ARGS.get("size"), 3, [10.0, 8.0, 3.5])
DOORS = []
if ARGS.get("doors"):
    try:
        DOORS = json.loads(Path(ARGS["doors"]).read_text(encoding="utf-8"))
    except Exception:  # noqa: BLE001
        DOORS = []
if not DOORS:
    DOORS = [{"name": "v_ilev_ra_door1_l", "bbMin": [-0.06, -0.06, -0.01], "bbMax": [1.0, 0.06, 2.12]}]
COLL_SHELL, COLL_ROOMS, COLL_PORTALS, COLL_DOORS = "Gebäude", "Räume", "Portale", "Türen"


def _coll(name, color=None):
    c = bpy.data.collections.get(name)
    if c is None:
        c = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(c)
    if color:
        c.color_tag = color
    return c


def _box_mesh(name, mn, mx):
    import bmesh
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    v = [bm.verts.new((mx[0] if i & 1 else mn[0], mx[1] if i & 2 else mn[1], mx[2] if i & 4 else mn[2])) for i in range(8)]
    for f in ((0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)):
        bm.faces.new([v[i] for i in f])
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    return me


def _mat(name, rgb):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    if b:
        b.inputs["Base Color"].default_value = (*rgb, 1.0)
    m.diffuse_color = (*rgb, 1.0)
    return m


def mlo_add_room(name, mn, mx):
    o = bpy.data.objects.new(f"Raum: {name}", _box_mesh(f"raum_{name}", mn, mx))
    _coll(COLL_ROOMS, "COLOR_04").objects.link(o)
    o.display_type = "WIRE"
    o.show_in_front = True
    o.hide_render = True
    o.color = (0.2, 0.6, 1.0, 1.0)
    return o


def mlo_add_portal(name, loc, width, height, rot_z=0.0):
    me = bpy.data.meshes.new(f"portal_{name}")
    w = width / 2
    me.from_pydata([(-w, 0, 0), (-w, 0, height), (w, 0, height), (w, 0, 0)], [], [(0, 1, 2, 3)])
    o = bpy.data.objects.new(f"Portal: {name}", me)
    _coll(COLL_PORTALS, "COLOR_03").objects.link(o)
    o.location = loc
    o.rotation_euler = (0, 0, rot_z)
    o.display_type = "WIRE"
    o.show_in_front = True
    o.hide_render = True
    return o


def mlo_add_door(door, loc, rot_z=0.0):
    mn, mx = door["bbMin"], door["bbMax"]
    o = bpy.data.objects.new(f"Tür: {door['name']}", _box_mesh(f"tuer_{door['name']}", mn, mx))
    _coll(COLL_DOORS, "COLOR_02").objects.link(o)
    o["gta_archetype"] = door["name"]
    # Unterkante auf den Boden (viele GTA-Türen haben den Drehpunkt auf halber Höhe)
    o.location = (loc[0], loc[1], loc[2] - mn[2])
    o.rotation_euler = (0, 0, rot_z)
    o.data.materials.append(_mat("Tür (Platzhalter)", (0.55, 0.35, 0.2)))
    return o


def new_mlo_setup():
    """Vorlage: Boden, vier Wände mit Türöffnung vorne, Decke, ein Raum, ein Portal, eine Tür.
    Ursprung (0/0/0) = Mitte des Bodens = Position auf der Karte."""
    W, D, H = MLO_SIZE
    t = 0.2
    shell = _coll(COLL_SHELL, "COLOR_05")
    def part(name, mn, mx, mat):
        o = bpy.data.objects.new(name, _box_mesh(name, mn, mx))
        o.data.materials.append(mat)
        shell.objects.link(o)
        return o
    wall = _mat("Wand", (0.75, 0.73, 0.70))
    floor = _mat("Boden", (0.35, 0.30, 0.26))
    ceil = _mat("Decke", (0.85, 0.85, 0.85))
    door = DOORS[0]
    dw = max(0.8, door["bbMax"][0] - door["bbMin"][0]) + 0.1
    dh = max(2.0, door["bbMax"][2] - door["bbMin"][2]) + 0.05
    part("Boden", (-W / 2, -D / 2, -t), (W / 2, D / 2, 0), floor)
    part("Decke", (-W / 2, -D / 2, H), (W / 2, D / 2, H + t), ceil)
    part("Wand hinten", (-W / 2, D / 2 - t, 0), (W / 2, D / 2, H), wall)
    part("Wand links", (-W / 2, -D / 2, 0), (-W / 2 + t, D / 2, H), wall)
    part("Wand rechts", (W / 2 - t, -D / 2, 0), (W / 2, D / 2, H), wall)
    part("Wand vorne links", (-W / 2, -D / 2, 0), (-dw / 2, -D / 2 + t, H), wall)
    part("Wand vorne rechts", (dw / 2, -D / 2, 0), (W / 2, -D / 2 + t, H), wall)
    part("Wand vorne oben", (-dw / 2, -D / 2, dh), (dw / 2, -D / 2 + t, H), wall)
    mlo_add_room("Hauptraum", (-W / 2 + t, -D / 2 + t, 0), (W / 2 - t, D / 2 - t, H))
    mlo_add_portal("Eingang", (0, -D / 2 + t / 2, 0), dw, dh)
    mlo_add_door(door, (-(door["bbMax"][0] - door["bbMin"][0]) / 2 - door["bbMin"][0], -D / 2 + t / 2, 0))
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"


def _bbox_world(o):
    from mathutils import Vector
    pts = [o.matrix_world @ Vector(c) for c in o.bound_box]
    return [min(p[i] for p in pts) for i in range(3)], [max(p[i] for p in pts) for i in range(3)]


def _room_of(point, rooms, margin=0.05):
    for i, r in enumerate(rooms):
        mn, mx = r["min"], r["max"]
        if all(mn[k] - margin <= point[k] <= mx[k] + margin for k in range(3)):
            return i + 1          # 0 = draußen (limbo)
    return 0


def mlo_collect():
    """Räume, Portale und Türen für das GTA Tool einsammeln (MLO-lokale Koordinaten = Blender-Welt)."""
    from mathutils import Vector
    bpy.context.view_layer.update()
    warnings = []
    rooms = []
    for o in sorted(_coll(COLL_ROOMS).all_objects, key=lambda x: x.name):
        if o.type != "MESH":
            continue
        mn, mx = _bbox_world(o)
        rooms.append({"name": o.name.replace("Raum:", "").strip() or f"raum{len(rooms) + 1}", "min": mn, "max": mx})
    if not rooms:
        raise RuntimeError("Es gibt keinen Raum. Bitte mindestens einen Raum anlegen (Knopf „Raum hinzufügen“).")
    portals = []
    for o in sorted(_coll(COLL_PORTALS).all_objects, key=lambda x: x.name):
        if o.type != "MESH" or len(o.data.vertices) < 4:
            continue
        loc = [v.co.copy() for v in o.data.vertices][:4]
        # Reihenfolge wie in GTA: unten-links, oben-links, oben-rechts, unten-rechts (in Portal-Ebene)
        loc.sort(key=lambda v: (round(v.x, 3), round(v.z, 3)))
        ordered = [loc[0], loc[1], loc[3], loc[2]] if loc[0].z <= loc[1].z else [loc[1], loc[0], loc[2], loc[3]]
        corners = [list(o.matrix_world @ v) for v in ordered]
        center = sum((Vector(c) for c in corners), Vector()) / 4
        n = (o.matrix_world.to_3x3() @ Vector((0, 1, 0))).normalized()
        a = _room_of(list(center + n * 0.3), rooms)
        b = _room_of(list(center - n * 0.3), rooms)
        if a == b:
            warnings.append(f"{o.name}: liegt nicht zwischen zwei Räumen (beide Seiten: {'draußen' if a == 0 else rooms[a - 1]['name']}).")
        rf, rt = (a, b) if a != 0 else (b, a)
        portals.append({"name": o.name, "corners": corners, "from": rf, "to": rt, "center": list(center)})
    doors = []
    for o in sorted(_coll(COLL_DOORS).all_objects, key=lambda x: x.name):
        arch = o.get("gta_archetype")
        if not arch:
            continue
        p = list(o.matrix_world.translation)
        q = o.matrix_world.to_quaternion()
        mn, mx = _bbox_world(o)
        mid = [(mn[i] + mx[i]) / 2 for i in range(3)]
        room = _room_of(mid, rooms)
        best, bd = -1, 2.0
        for i, pt in enumerate(portals):
            d = (Vector(pt["center"]) - Vector(mid)).length
            if d < bd:
                best, bd = i, d
        if room == 0 and best >= 0:
            room = portals[best]["from"] or portals[best]["to"]
        doors.append({"archetype": str(arch), "position": p, "rotation": [q.x, q.y, q.z, q.w], "room": room, "portal": best})
    return {"rooms": rooms, "portals": portals, "doors": doors, "warnings": warnings,
            "position": MLO_POS, "heading": MLO_HEADING}


def export_collision(meshes, name):
    """Kollision der Gebäudehülle als .ybn in WELTKOORDINATEN (an der Kartenposition, mit Drehung)."""
    from mathutils import Matrix
    import math
    scene = bpy.context.scene
    dg = bpy.context.evaluated_depsgraph_get()
    place = Matrix.Translation(MLO_POS) @ Matrix.Rotation(math.radians(MLO_HEADING), 4, "Z")
    dups = []
    for i, o in enumerate(meshes):
        mesh = bpy.data.meshes.new_from_object(o.evaluated_get(dg), depsgraph=dg)
        mesh.transform(place @ o.matrix_world)
        mesh.materials.clear()
        d = bpy.data.objects.new(f"{name}_teil{i}", mesh)
        scene.collection.objects.link(d)
        dups.append(d)
    scene.create_seperate_composites = False
    scene.center_composite_to_selection = False
    before = set(scene.objects)
    bpy.ops.object.select_all(action="DESELECT")
    for d in dups:
        d.select_set(True)
    bpy.context.view_layer.objects.active = dups[0]
    res = bpy.ops.sollumz.converttocomposite()
    if "FINISHED" not in res:
        cleanup_temp(dups)
        raise RuntimeError("Sollumz konnte die Kollision nicht erzeugen.")
    roots = [o for o in scene.objects if o not in before and stype(o) == "sollumz_bound_composite"]
    temp = list(dups)
    try:
        if not roots:
            raise RuntimeError("Sollumz hat keine Kollision erzeugt.")
        root = roots[0]
        temp += [root, *root.children_recursive]
        root.name = name
        colmat = collision_material()
        for o in [root, *root.children_recursive]:
            if o.type == "MESH" and colmat is not None:
                o.data.materials.clear()
                o.data.materials.append(colmat)
        bpy.ops.object.select_all(action="DESELECT")
        for o in [root, *root.children_recursive]:
            o.select_set(True)
        bpy.context.view_layer.objects.active = root
        for p in EXPORT_DIR.glob(f"{name}.ybn"):
            p.unlink()
        res = bpy.ops.sollumz.export_assets(directory=str(EXPORT_DIR), direct_export=True, limit_to_selected=True,
                                            apply_transforms=True, use_custom_settings=True,
                                            target_formats={"NATIVE"}, target_versions={"GEN8"})
        if "FINISHED" not in res or not (EXPORT_DIR / f"{name}.ybn").exists():
            raise RuntimeError("Die Kollision (.ybn) konnte nicht exportiert werden.")
    finally:
        cleanup_temp(list(dict.fromkeys(temp)))


def do_export_new_mlo():
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    shell_meshes = plain_meshes(_coll(COLL_SHELL).all_objects)
    if not shell_meshes:
        raise RuntimeError("Die Sammlung „Gebäude“ ist leer – dort gehört die Hülle (Wände, Boden, Decke) hinein.")
    data = mlo_collect()
    export_drawable(shell_meshes, f"{MODEL}_shell", embed_col=False)
    export_collision(shell_meshes, f"{MODEL}_col")
    (EXPORT_DIR / "mlo.json").write_text(json.dumps(data, indent=1), encoding="utf-8")
    if ARGS.get("blend-save"):
        try:
            bpy.ops.wm.save_as_mainfile(filepath=str(ARGS["blend-save"]), copy=True)
        except Exception as exc:  # noqa: BLE001
            print("Blend nicht gespeichert:", exc)
    STATE["exports"] += 1
    STATE["last"] = time.time()
    STATE["error"] = None
    write_status("exported", files=[str(EXPORT_DIR / "mlo.json")], warnings=data["warnings"])


class GTATOOL_OT_mlo_add_room(bpy.types.Operator):
    bl_idname = "gtatool.mlo_add_room"
    bl_label = "Raum hinzufügen"
    bl_description = "Neuer Raum (Box) am 3D-Cursor – mit S skalieren, sodass er den Raum ausfüllt"

    def execute(self, context):
        c = context.scene.cursor.location
        n = len([o for o in _coll(COLL_ROOMS).all_objects]) + 1
        o = mlo_add_room(f"Raum {n}", (c.x - 2, c.y - 2, c.z), (c.x + 2, c.y + 2, c.z + 3))
        bpy.ops.object.select_all(action="DESELECT")
        o.select_set(True)
        context.view_layer.objects.active = o
        return {"FINISHED"}


class GTATOOL_OT_mlo_add_portal(bpy.types.Operator):
    bl_idname = "gtatool.mlo_add_portal"
    bl_label = "Portal (Durchgang) hinzufügen"
    bl_description = "Rahmen für eine Tür-/Fensteröffnung am 3D-Cursor – in die Öffnung schieben (G) und drehen (R Z)"

    def execute(self, context):
        c = context.scene.cursor.location
        n = len([o for o in _coll(COLL_PORTALS).all_objects]) + 1
        o = mlo_add_portal(f"Durchgang {n}", (c.x, c.y, c.z), 1.2, 2.2)
        bpy.ops.object.select_all(action="DESELECT")
        o.select_set(True)
        context.view_layer.objects.active = o
        return {"FINISHED"}


def _door_items(self, context):
    return [(str(i), d["name"], f"{d['bbMax'][0] - d['bbMin'][0]:.2f} × {d['bbMax'][2] - d['bbMin'][2]:.2f} m") for i, d in enumerate(DOORS)]


class GTATOOL_OT_mlo_add_door(bpy.types.Operator):
    bl_idname = "gtatool.mlo_add_door"
    bl_label = "Tür einfügen"
    bl_description = "GTA-Tür am 3D-Cursor einfügen (Platzhalter in Originalgröße, Drehpunkt = Scharnier)"
    door: bpy.props.EnumProperty(name="Tür", items=_door_items)

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=360)

    def execute(self, context):
        d = DOORS[int(self.door)]
        o = mlo_add_door(d, tuple(context.scene.cursor.location))
        bpy.ops.object.select_all(action="DESELECT")
        o.select_set(True)
        context.view_layer.objects.active = o
        return {"FINISHED"}


def do_export_new_prop():
    produced = export_drawable(None, MODEL, True)
    STATE["exports"] += 1
    STATE["last"] = time.time()
    STATE["error"] = None
    write_status("exported", files=[str(produced)])


def do_export():
    EXPORT_DIR.mkdir(parents=True, exist_ok=True)
    if IS_MLO:
        do_export_new_mlo()
        return
    if IS_NEW:
        do_export_new_prop()
        return
    if IS_ANIM:
        do_export_anim()
        return
    if IS_GENERIC:
        do_export_ydd()
        return
    frags = fragments()
    if not frags:
        raise RuntimeError("Kein Sollumz-Fragment in der Szene gefunden. Wurde das Fahrzeug gelöscht?")
    if not any(o.name.lower() == MODEL for o in frags):
        names = ", ".join(o.name for o in frags)
        raise RuntimeError(f"Das Fragment muss „{MODEL}“ heißen (gefunden: {names}). Bitte zurück umbenennen.")
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    res = bpy.ops.sollumz.export_assets(
        directory=str(EXPORT_DIR),
        direct_export=True,
        limit_to_selected=False,
        apply_transforms=True,
        use_custom_settings=True,
        target_formats={"NATIVE"},
        target_versions={"GEN8"},
        export_ytds=False,
    )
    if "FINISHED" not in res:
        raise RuntimeError("Sollumz hat den Export abgebrochen (Details im Blender-Infofenster).")
    produced = [p for p in EXPORT_DIR.rglob("*.yft") if p.stem.lower() in (MODEL, MODEL + "_hi")]
    if not produced:
        raise RuntimeError(f"Der Export hat keine {MODEL}.yft erzeugt.")
    STATE["exports"] += 1
    STATE["last"] = time.time()
    STATE["error"] = None
    write_status("exported", files=[str(p) for p in produced])


def test_edit():
    """Nur für automatische Tests: Kopf-Rotation in allen Keyframes deutlich verändern."""
    arm = bpy.data.objects.get("Skelett")
    action = arm.animation_data.action
    try:
        from bpy_extras import anim_utils  # noqa: F401
        fcurves = list(action.fcurves)
    except Exception:  # noqa: BLE001
        fcurves = list(action.fcurves)
    target = [fc for fc in fcurves if fc.data_path == 'pose.bones["SKEL_Head"].rotation_quaternion' and fc.array_index == 1]
    if not target:
        target = [fc for fc in fcurves if fc.data_path.endswith("rotation_quaternion") and fc.array_index == 1][:1]
    for fc in target:
        for kp in fc.keyframe_points:
            kp.co[1] += 0.35
        fc.update()
    print("TEST-EDIT", [fc.data_path for fc in target])


class GTATOOL_OT_send_back(bpy.types.Operator):
    bl_idname = "gtatool.send_back"
    bl_label = "Speichern & zurück zum GTA Tool"
    bl_description = "Exportiert das Modell mit Sollumz und übernimmt es automatisch ins GTA Tool"

    def execute(self, context):
        try:
            do_export()
            self.report({"INFO"}, "Gespeichert – das GTA Tool übernimmt das Modell jetzt.")
            return {"FINISHED"}
        except Exception as exc:  # noqa: BLE001
            STATE["error"] = str(exc)
            write_status("error")
            self.report({"ERROR"}, str(exc))
            return {"CANCELLED"}


class GTATOOL_PT_panel(bpy.types.Panel):
    bl_label = "GTA Tool"
    bl_idname = "GTATOOL_PT_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "GTA Tool"

    def draw(self, context):
        col = self.layout.column(align=True)
        if IS_MLO:
            col.label(text=f"Interior: {MODEL}", icon="HOME")
            col.label(text=f"Karte: {MLO_POS[0]:.1f} / {MLO_POS[1]:.1f} / {MLO_POS[2]:.1f}")
            col.label(text=f"Drehung: {MLO_HEADING:.0f}°")
            col.separator()
            col.operator(GTATOOL_OT_mlo_add_room.bl_idname, icon="MESH_CUBE")
            col.operator(GTATOOL_OT_mlo_add_portal.bl_idname, icon="MOD_WIREFRAME")
            col.operator_menu_enum(GTATOOL_OT_mlo_add_door.bl_idname, "door", text="Tür einfügen", icon="OUTLINER_OB_EMPTY")
        elif IS_NEW:
            col.label(text=f"Neues Prop: {MODEL}", icon="MESH_CUBE")
        elif IS_ANIM:
            col.label(text=f"Animation: {MODEL}", icon="ARMATURE_DATA")
            if CLIP:
                col.label(text=f"Clip: {CLIP}")
        else:
            col.label(text=f"Modell: {MODEL}", icon="AUTO")
        col.separator()
        row = col.row()
        row.scale_y = 2.0
        row.operator(GTATOOL_OT_send_back.bl_idname, icon="EXPORT")
        col.separator()
        if STATE["error"]:
            box = col.box()
            box.alert = True
            for line in wrap(STATE["error"], 40):
                box.label(text=line)
        elif STATE["exports"]:
            col.label(text=f"Übernommen ({STATE['exports']}×)", icon="CHECKMARK")
        col.separator()
        if IS_MLO:
            for line in ("So geht's:", "• „Gebäude“: Wände, Boden, Decke,", "  Einrichtung – wird zur Hülle.", "• „Räume“: Drahtboxen = Räume", "  (mit S auf Raumgröße ziehen).",
                         "• „Portale“: Rahmen in jede", "  Öffnung zwischen Räumen/nach außen.", "• „Türen“: GTA-Türen (Platzhalter).",
                         "• 0/0/0 = Kartenposition (Boden).", "• 1 Einheit = 1 Meter.", "• Beliebig oft zurückschicken."):
                col.label(text=line)
        elif IS_NEW:
            for line in ("So geht's:", "• Modelliere ganz normal – alle", "  sichtbaren Objekte werden", "  zusammen zum Prop.",
                         "• Der Punkt 0/0/0 ist der Standpunkt", "  am Boden (Unterkante dorthin).",
                         "• 1 Blender-Einheit = 1 Meter.", "• Farbe oder Bild an „Base Color“", "  → wird zur Textur.",
                         "• Kollision entsteht automatisch.", "• Beliebig oft zurückschicken."):
                col.label(text=line)
        elif IS_ANIM:
            for line in ("So bearbeitest du:", "1. „Skelett“ anklicken", "2. Strg+Tab = Pose-Modus",
                         "3. Bone wählen, R = drehen", "4. I = Keyframe setzen", "Leertaste = abspielen"):
                col.label(text=line)
            col.label(text="Clip-Namen nicht ändern.", icon="INFO")
        else:
            col.label(text="Tipp: Fragment-Namen nicht ändern.", icon="INFO")


def wrap(text, width):
    words, line, out = text.split(), "", []
    for w in words:
        if len(line) + len(w) + 1 > width:
            out.append(line)
            line = w
        else:
            line = (line + " " + w).strip()
    if line:
        out.append(line)
    return out


def open_sidebar():
    try:
        for window in bpy.context.window_manager.windows:
            for area in window.screen.areas:
                if area.type == "VIEW_3D":
                    space = area.spaces.active
                    space.show_region_ui = True
                    for region in area.regions:
                        if region.type == "UI":
                            try:
                                region.active_panel_category = "GTA Tool"
                            except Exception:
                                pass
                    with bpy.context.temp_override(window=window, area=area):
                        try:
                            bpy.ops.view3d.view_all(center=False)
                        except Exception:
                            pass
    except Exception:
        pass
    return None


def main():
    try:
        bpy.utils.register_class(GTATOOL_OT_send_back)
        bpy.utils.register_class(GTATOOL_PT_panel)
    except ValueError:
        pass
    write_status("starting")
    if not ensure_sollumz():
        raise RuntimeError("Das Blender-Add-on Sollumz ist nicht installiert oder nicht aktiviert.")
    if IS_MLO:
        for cls in (GTATOOL_OT_mlo_add_room, GTATOOL_OT_mlo_add_portal, GTATOOL_OT_mlo_add_door):
            try:
                bpy.utils.register_class(cls)
            except ValueError:
                pass
        if bpy.data.collections.get(COLL_SHELL) is None:   # sonst: gespeicherte .blend wurde geöffnet
            clear_scene()
            new_mlo_setup()
        write_status("ready")
        if ARGS.get("auto-export"):
            if ARGS.get("test-model"):
                test_mlo()
            do_export()
            bpy.ops.wm.quit_blender()
            return
        bpy.app.timers.register(open_sidebar, first_interval=0.5)
        return
    if IS_NEW:
        clear_scene()
        new_prop_setup()
        write_status("ready")
        if ARGS.get("auto-export"):
            if ARGS.get("test-model"):
                test_model()
            do_export()
            bpy.ops.wm.quit_blender()
            return
        bpy.app.timers.register(open_sidebar, first_interval=0.5)
        return
    if not YFT.is_file():
        raise RuntimeError(f"Modelldatei nicht gefunden: {YFT}")
    clear_scene()
    if IS_ANIM:
        anim_import()
        write_status("ready")
        if ARGS.get("auto-export"):
            if ARGS.get("test-edit"):
                test_edit()
            do_export()
            bpy.ops.wm.quit_blender()
            return
        bpy.app.timers.register(open_sidebar, first_interval=0.5)
        return
    sollumz_import(YFT)
    if IS_YDD:
        import_body_reference()
    write_status("ready")
    if ARGS.get("auto-export"):
        do_export()
        bpy.ops.wm.quit_blender()
        return
    bpy.app.timers.register(open_sidebar, first_interval=0.5)


def heartbeat():
    write_status("alive")
    return 2.0


try:
    main()
    if not ARGS.get("auto-export"):
        bpy.app.timers.register(heartbeat, first_interval=2.0, persistent=True)
except Exception as exc:  # noqa: BLE001
    STATE["error"] = f"{exc}"
    traceback.print_exc()
    write_status("error", trace=traceback.format_exc()[-1500:])
    if ARGS.get("auto-export"):
        sys.exit(1)
