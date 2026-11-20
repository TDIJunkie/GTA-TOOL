"""GTA Tool – Kleidung männlich ⇄ weiblich anpassen (läuft ohne Fenster: blender -b --python …).

Aufruf:
    blender -b --python clothing_convert.py -- --model <teil.ydd> --stem <jbib_000_u>
        --source-skel <mp_m_freemode_01.yft> --target-skel <mp_f_freemode_01.yft>
        --source-base <ordner mit head_000…> --target-base <ordner> --out <ordner> --result <ergebnis.json>
        [--fit 1.0] [--clearance 0.003] [--smoothing 4]

Ablauf (aus dem FiveM Asset Studio übernommen):
 1. Skelett: Jeder Punkt folgt über seine Bone-Gewichte dem Unterschied der beiden Grundhaltungen.
 2. Körperform: Abstand zwischen angepasstem Ausgangskörper und Zielkörper wird geglättet übertragen.
 3. Abstand: Punkte, die danach in der Haut liegen, werden herausgedrückt.
"""

import json
import shutil
import sys
import traceback
from pathlib import Path

import bpy

ARGS = {}
if "--" in sys.argv:
    rest = sys.argv[sys.argv.index("--") + 1:]
    i = 0
    while i < len(rest):
        if rest[i].startswith("--") and i + 1 < len(rest):
            ARGS[rest[i][2:]] = rest[i + 1]
            i += 2
        else:
            i += 1

FIT_COMPONENTS = ("head", "uppr", "lowr", "feet")


def result(**data):
    Path(ARGS["result"]).write_text(json.dumps(data), encoding="utf-8")


def sollum(obj):
    return str(getattr(obj, "sollum_type", "") or "")


def ensure_sollumz():
    import addon_utils
    for name in ("bl_ext.user_default.sollumz", "bl_ext.blender_org.sollumz", "sollumz"):
        try:
            if addon_utils.check(name)[1] or addon_utils.enable(name, default_set=True):
                return
        except Exception:
            pass
    if not hasattr(bpy.ops.sollumz, "import_assets"):
        raise RuntimeError("Sollumz ist nicht installiert oder nicht aktiviert.")


def clear_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def import_with_skeleton(model: Path, skeleton: str):
    before = set(bpy.data.objects)
    res = bpy.ops.sollumz.import_assets(
        directory=str(model.parent), files=[{"name": model.name}], use_custom_settings=True,
        dwd_import_external_skeleton="SAVED", dwd_import_external_skeleton_saved_path=str(skeleton),
    )
    if "FINISHED" not in res:
        raise RuntimeError(f"{model.name} konnte nicht gelesen werden.")
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        if o.type == "ARMATURE":
            o.data.pose_position = "REST"
    bpy.context.view_layer.update()
    return new


def lod_meshes(obj):
    meshes = [obj.data]
    for level in ("very_high", "high", "medium", "low", "very_low"):
        m = getattr(getattr(getattr(obj, "sz_lods", None), level, None), "mesh", None)
        if m is not None and all(m is not o for o in meshes):
            meshes.append(m)
    return meshes


def vertex_weights(vertex, names):
    items = [(names[g.group], g.weight) for g in vertex.groups if g.weight > 1e-4 and g.group in names]
    total = sum(w for _, w in items)
    return [(n, w / total) for n, w in items] if total > 0 else []


def bone_matrices(armature):
    world = armature.matrix_world
    return {b.name: world @ b.matrix_local for b in armature.data.bones}


def base_model(base: Path, comp: str):
    return next(iter(sorted(base.glob(f"{comp}_000_*.ydd"))), None)


def collect_body(base: Path, skeleton: str, warnings: list, keep_host: bool):
    host = None
    points, triangles, weights = [], [], []
    for comp in FIT_COMPONENTS:
        model = base_model(base, comp)
        if model is None:
            warnings.append(f"Grundteil {comp} fehlt; die Anpassung dort ist ungenauer.")
            continue
        try:
            new = import_with_skeleton(model, skeleton)
        except RuntimeError as exc:
            warnings.append(str(exc))
            continue
        for obj in new:
            if obj.type != "MESH" or sollum(obj) != "sollumz_drawable_model":
                continue
            offset = len(points)
            world = obj.matrix_world
            names = {g.index: g.name for g in obj.vertex_groups}
            for v in obj.data.vertices:
                points.append(world @ v.co)
                weights.append(vertex_weights(v, names))
            obj.data.calc_loop_triangles()
            triangles.extend(tuple(offset + i for i in t.vertices) for t in obj.data.loop_triangles)
        dictionary = next((o for o in new if sollum(o) == "sollumz_drawable_dictionary" and o.type == "ARMATURE"), None)
        for obj in new:
            if keep_host and host is None and obj is dictionary:
                continue
            bpy.data.objects.remove(obj, do_unlink=True)
        if keep_host and host is None and dictionary is not None:
            host = dictionary
            for child in list(host.children):
                bpy.data.objects.remove(child, do_unlink=True)
    return host, points, triangles, weights


def retarget(points, weights, deltas, fallback):
    from mathutils import Vector
    out = []
    for p, items in zip(points, weights):
        total = Vector((0.0, 0.0, 0.0))
        used = 0.0
        for name, w in items:
            d = deltas.get(name)
            if d is None:
                continue
            total += (d @ p) * w
            used += w
        out.append(total / used if used > 0 else (fallback @ p if fallback is not None else p.copy()))
    return out


def neighbours(mesh):
    nb = [[] for _ in mesh.vertices]
    for e in mesh.edges:
        a, b = e.vertices
        nb[a].append(b)
        nb[b].append(a)
    return nb


def smooth(values, nb, iterations):
    from mathutils import Vector
    for _ in range(max(0, iterations)):
        values = [v if not around else v * 0.5 + (sum((values[i] for i in around), Vector((0.0, 0.0, 0.0))) / len(around)) * 0.5
                  for v, around in zip(values, nb)]
    return values


def unify(points, values):
    from mathutils import Vector
    groups = {}
    for i, p in enumerate(points):
        groups.setdefault((round(p.x, 5), round(p.y, 5), round(p.z, 5)), []).append(i)
    out = list(values)
    for idx in groups.values():
        if len(idx) < 2:
            continue
        mean = sum((values[i] for i in idx), Vector((0.0, 0.0, 0.0))) / len(idx)
        for i in idx:
            out[i] = mean
    return out


def main():
    from mathutils import Vector
    from mathutils.bvhtree import BVHTree
    from mathutils.kdtree import KDTree

    model = Path(ARGS["model"])
    stem = ARGS["stem"].lower()
    src_skel, dst_skel = ARGS["source-skel"], ARGS["target-skel"]
    src_base, dst_base = Path(ARGS["source-base"]), Path(ARGS["target-base"])
    out = Path(ARGS["out"])
    fit = max(0.0, min(1.0, float(ARGS.get("fit", 1.0))))
    clearance = max(0.0, min(0.03, float(ARGS.get("clearance", 0.003))))
    iterations = max(0, min(20, int(ARGS.get("smoothing", 4))))
    warnings = []

    ensure_sollumz()
    clear_scene()
    new = import_with_skeleton(model, src_skel)
    dicts = [o for o in new if sollum(o) == "sollumz_drawable_dictionary"]
    if len(dicts) != 1:
        raise RuntimeError("Die Datei enthält nicht genau ein Kleidungsstück.")
    src_dict = dicts[0]
    models = [o for o in src_dict.children_recursive if o.type == "MESH" and sollum(o) == "sollumz_drawable_model"]
    if not models:
        raise RuntimeError("Das Kleidungsstück enthält kein Modell.")
    src_bones = bone_matrices(src_dict)

    parts, seen, unweighted = [], [], 0
    for obj in models:
        world = obj.matrix_world.copy()
        names = {g.index: g.name for g in obj.vertex_groups}
        for mesh in lod_meshes(obj):
            if any(mesh is o for o in seen):
                continue
            seen.append(mesh)
            pts = [world @ v.co for v in mesh.vertices]
            wts = [vertex_weights(v, names) for v in mesh.vertices]
            unweighted += sum(1 for w in wts if not w)
            parts.append({"obj": obj, "mesh": mesh, "points": pts, "weights": wts})

    _, sp, st, sw = collect_body(src_base, src_skel, warnings, keep_host=False)
    host, tp, tt, _ = collect_body(dst_base, dst_skel, warnings, keep_host=True)
    if host is None or not sp or not tp:
        raise RuntimeError("Die Grundkörper konnten nicht geladen werden.")
    dst_bones = bone_matrices(host)
    deltas = {n: dst_bones[n] @ m.inverted() for n, m in src_bones.items() if n in dst_bones}
    used = {n for p in parts for items in p["weights"] for n, _ in items}
    missing = sorted(n for n in used if n not in dst_bones)
    if missing:
        raise RuntimeError("Das Zielskelett kennt diese Bones nicht: " + ", ".join(missing[:8]))

    for part in parts:
        fallback = None
        if any(not w for w in part["weights"]):
            centre = sum(part["points"], Vector((0.0, 0.0, 0.0))) / len(part["points"])
            nearest = min(deltas, key=lambda n: (src_bones[n].translation - centre).length)
            fallback = deltas[nearest]
        part["retargeted"] = retarget(part["points"], part["weights"], deltas, fallback)
    body_rt = retarget(sp, sw, deltas, None)

    t_tree = BVHTree.FromPolygons(tp, tt, all_triangles=True)
    s_tree = BVHTree.FromPolygons(sp, st, all_triangles=True)
    disp = []
    for p in body_rt:
        loc, _n, _i, _d = t_tree.find_nearest(p, 0.15)
        if loc is None:
            disp.append(Vector((0.0, 0.0, 0.0)))
            continue
        d = loc - p
        if d.length > 0.08:
            d = d.normalized() * 0.08
        disp.append(d)
    kd = KDTree(len(body_rt))
    for i, p in enumerate(body_rt):
        kd.insert(p, i)
    kd.balance()

    shifts = []
    for part in parts:
        nb = neighbours(part["mesh"])
        moves = []
        for p in part["retargeted"]:
            hits = kd.find_n(p, 8)
            if not hits or fit <= 0:
                moves.append(Vector((0.0, 0.0, 0.0)))
                continue
            total, wsum = Vector((0.0, 0.0, 0.0)), 0.0
            for _co, idx, dist in hits:
                w = 1.0 / (dist * dist + 1e-6)
                total += disp[idx] * w
                wsum += w
            reach = hits[0][2]
            falloff = 1.0 if reach <= 0.06 else max(0.35, 1.0 - (reach - 0.06) / 0.3 * 0.65)
            moves.append(total / wsum * falloff * fit)
        moves = unify(part["points"], smooth(moves, nb, iterations))
        fitted = [p + m for p, m in zip(part["retargeted"], moves)]
        pushes = []
        for orig, p in zip(part["points"], fitted):
            push = Vector((0.0, 0.0, 0.0))
            before = s_tree.find_nearest(orig, 0.1)
            after = t_tree.find_nearest(p, 0.1)
            if before[0] is not None and after[0] is not None:
                og = (orig - before[0]).dot(before[1])
                if og > -0.01:
                    wanted = max(clearance, min(og, 0.02))
                    gap = (p - after[0]).dot(after[1])
                    if gap < wanted:
                        push = after[1] * min(wanted - gap, 0.05)
            pushes.append(push)
        pushes = unify(part["points"], smooth(pushes, nb, min(2, iterations)))
        final = []
        for orig, p, push in zip(part["points"], fitted, pushes):
            r = p + push
            shifts.append((r - orig).length)
            final.append(r)
        part["final"] = final

    # Zielskelett übernehmen
    for child in list(src_dict.children):
        world = child.matrix_world.copy()
        child.parent = host
        child.matrix_world = world
    for obj in bpy.data.objects:
        for mod in getattr(obj, "modifiers", []):
            if mod.type == "ARMATURE" and mod.object == src_dict:
                mod.object = host
    for leftover in [src_dict, *src_dict.children_recursive]:
        if leftover.name in bpy.data.objects:
            bpy.data.objects.remove(leftover, do_unlink=True)
    host.name = stem
    host.data.pose_position = "REST"
    bpy.context.view_layer.update()
    for part in parts:
        inv = part["obj"].matrix_world.inverted()
        for v, p in zip(part["mesh"].vertices, part["final"]):
            v.co = inv @ p
        part["mesh"].update()

    out.mkdir(parents=True, exist_ok=True)
    res = bpy.ops.sollumz.export_assets(
        directory=str(out), direct_export=True, limit_to_selected=False, apply_transforms=True,
        use_custom_settings=True, target_formats={"NATIVE"}, target_versions={"GEN8"}, export_ytds=False,
    )
    if "FINISHED" not in res:
        raise RuntimeError("Sollumz hat den Export abgebrochen.")
    target = next((p for p in out.rglob("*.ydd") if p.stem.lower() == stem), None)
    if target is None:
        raise RuntimeError(f"Der Export hat kein {stem}.ydd erzeugt.")
    if unweighted:
        warnings.append(f"{unweighted} Punkte ohne Bone-Gewicht folgten dem nächstgelegenen Bone.")
    result(ok=True, file=str(target), warnings=warnings,
           maxShiftCm=round(max(shifts, default=0.0) * 100, 2),
           meanShiftCm=round((sum(shifts) / len(shifts) if shifts else 0.0) * 100, 2))


try:
    main()
except Exception as exc:  # noqa: BLE001
    traceback.print_exc()
    result(ok=False, error=str(exc))
bpy.ops.wm.quit_blender()
