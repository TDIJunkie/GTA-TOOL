"""GTA Tool – Blender-Brücke.

Start (vom GTA Tool):
    blender --python gta_tool_bridge.py -- --yft <pfad.yft> --export <ordner> --status <status.json> [--auto-export]

- importiert das Modell mit Sollumz (Texturen liegen als DDS in <modellordner>/<modellname>/)
- zeigt in der Seitenleiste (Taste N) den Reiter „GTA Tool“ mit dem Knopf „Speichern & zurück zum GTA Tool“
- der Knopf exportiert mit Sollumz (nativ, GEN8) in den Export-Ordner und meldet das per Statusdatei
- --auto-export: nur für Tests (importieren, sofort exportieren, beenden)
"""

import json
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
MODEL = YFT.name[:-len(".ycd.xml")].lower() if IS_ANIM else YFT.stem.lower()
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


def do_export():
    EXPORT_DIR.mkdir(parents=True, exist_ok=True)
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
        if IS_ANIM:
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
        if IS_ANIM:
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
