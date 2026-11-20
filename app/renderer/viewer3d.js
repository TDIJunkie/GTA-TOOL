// 3D-Ansicht (three.js) – eine WebGL-Fläche, die zwischen Ansichten umgehängt wird
import * as THREE from './vendor/three/three.module.js';
import { GLTFLoader } from './vendor/three/GLTFLoader.js';
import { OrbitControls } from './vendor/three/OrbitControls.js';
import { RoomEnvironment } from './vendor/three/RoomEnvironment.js';
import { TransformControls } from './vendor/three/TransformControls.js';

let renderer, scene, camera, controls, ground, host, resizeObs;
let main = null, overlay = null, extras = null;
let paintColor = new THREE.Color('#9ea3aa');
let needsRender = true;

function init() {
  renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, preserveDrawingBuffer: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.0;
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;

  scene = new THREE.Scene();
  scene.background = new THREE.Color('#1b1e25');
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;

  camera = new THREE.PerspectiveCamera(40, 1, 0.05, 500);
  camera.position.set(4.5, 2.2, 5.5);

  const sun = new THREE.DirectionalLight(0xffffff, 1.6);
  sun.position.set(5, 10, 6);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  Object.assign(sun.shadow.camera, { left: -6, right: 6, top: 6, bottom: -6, near: 0.5, far: 40 });
  scene.add(sun);
  scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x30302c, 0.5));

  ground = new THREE.Mesh(new THREE.CircleGeometry(12, 64), new THREE.MeshStandardMaterial({ color: '#2a2e37', roughness: 0.95 }));
  ground.rotation.x = -Math.PI / 2;
  ground.receiveShadow = true;
  scene.add(ground);
  const grid = new THREE.GridHelper(24, 48, 0x3a404d, 0x30353f);
  gridHelper = grid;
  grid.position.y = 0.002;
  scene.add(grid);

  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.maxPolarAngle = Math.PI * 0.495;
  controls.addEventListener('change', () => { needsRender = true; });

  resizeObs = new ResizeObserver(() => resize());
  const loop = () => {
    requestAnimationFrame(loop);
    const dt = animClock.getDelta();
    if (!host || !host.isConnected) return;
    if (animMixer && animAction && !animAction.paused) animStep(dt);
    const moved = controls.update();
    if (moved || needsRender) { renderer.render(scene, camera); needsRender = false; }
  };
  loop();
}

function resize() {
  if (!host) return;
  const w = host.clientWidth, h = host.clientHeight;
  if (!w || !h) return;
  renderer.setSize(w, h, false);
  renderer.domElement.style.width = '100%';
  renderer.domElement.style.height = '100%';
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  needsRender = true;
}

function mount(container) {
  if (!renderer) init();
  host = container;
  container.prepend(renderer.domElement);
  resizeObs.disconnect();
  resizeObs.observe(container);
  resize();
}

function disposeObject(o) {
  if (!o) return;
  scene.remove(o);
  o.traverse((c) => {
    if (c.geometry) c.geometry.dispose();
    const mats = Array.isArray(c.material) ? c.material : (c.material ? [c.material] : []);
    for (const m of mats) { if (m.map) m.map.dispose(); m.dispose(); }
  });
}

function parse(buffer) {
  return new Promise((resolve, reject) => {
    const ab = buffer.buffer ? buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength) : buffer;
    new GLTFLoader().parse(ab, '', (g) => resolve(g.scene), reject);
  });
}

function prepare(root, { ghost = false, highlight = false } = {}) {
  root.traverse((c) => {
    if (!c.isMesh) return;
    c.castShadow = !ghost;
    c.receiveShadow = !ghost;
    const m = c.material;
    if (m.name === 'paint') {
      m.color.copy(paintColor);
      m.userData.paint = true;
      // Folie: liegt mit Transparenz über der Lackfarbe (wie im Spiel).
      // Das Material wird von vielen Teilen geteilt – Folie deshalb am Material merken.
      if (m.map) { m.userData.liveryMap = m.map; m.map = null; }
      const liveryMap = m.userData.liveryMap;
      if (liveryMap && !ghost) {
        if (!m.userData.liveryMaterial) {
          m.userData.liveryMaterial = new THREE.MeshStandardMaterial({
            map: liveryMap, transparent: true, metalness: 0.3, roughness: 0.4,
            polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1, depthWrite: false,
          });
        }
        const layer = new THREE.Mesh(c.geometry, m.userData.liveryMaterial);
        layer.renderOrder = 1;
        c.add(layer);
      }
      m.map = null;
      m.needsUpdate = true;
    }
    if (ghost) {
      m.transparent = true;
      m.opacity = 0.18;
      m.depthWrite = false;
    }
    if (highlight) {
      m.emissive = new THREE.Color('#3dbb84');
      m.emissiveIntensity = 0.25;
    }
  });
}

/** Lädt das Hauptmodell. keepCamera: Kamera nicht neu ausrichten (bei Aktualisierung). */
let gridHelper = null;

async function load(buffer, { keepCamera = false, ghost = false } = {}) {
  sceneClear();
  const root = await parse(buffer);
  prepare(root, { ghost });
  disposeObject(main);
  main = root;
  scene.add(root);
  placeOnGround();
  if (!keepCamera) fit(main || overlay);
  needsRender = true;
}

async function loadOverlay(buffer, { keepCamera = false } = {}) {
  const root = await parse(buffer);
  prepare(root, { highlight: true });
  disposeObject(overlay);
  overlay = root;
  scene.add(root);
  placeOnGround();
  if (!keepCamera) fit(main || overlay);
  needsRender = true;
}

function clearOverlay() { disposeObject(overlay); overlay = null; needsRender = true; }
function clear() { animDispose(); disposeObject(main); disposeObject(overlay); disposeObject(extras); main = overlay = extras = null; needsRender = true; }

/** Angebaute Tuningteile (normal dargestellt, mit Folie). */
async function setExtras(buffers) {
  disposeObject(extras);
  extras = null;
  if (buffers && buffers.length) {
    extras = new THREE.Group();
    for (const b of buffers) {
      const root = await parse(b);
      prepare(root);
      extras.add(root);
    }
    scene.add(extras);
  }
  placeOnGround();
  needsRender = true;
}

// Fahrzeuge liegen im Modellraum etwas unter/über 0 – auf den Boden stellen
let groundMode = 'main';           // 'all' = Boden am Gesamtbild (Kleidung am Körper)
let mainOffset = { x: 0, y: 0, z: 0, s: 1 };
let mainCenterLocal = null;
let mainBaseY = 0;

function placeOnGround() {
  if (groundMode === 'all') {
    const objs = [main, overlay, extras].filter(Boolean);
    if (!objs.length) return;
    for (const o of objs) { o.position.set(0, 0, 0); o.scale.setScalar(1); }
    const box = new THREE.Box3();
    for (const o of objs) box.expandByObject(o);
    const dy = -box.min.y;
    for (const o of objs) o.position.y = dy;
    mainBaseY = dy;
    if (main) {
      const mb = new THREE.Box3().setFromObject(main);
      const c = mb.getCenter(new THREE.Vector3());
      mainCenterLocal = c.sub(main.position);
    }
    applyMainOffset();
    return;
  }
  const ref = main || overlay;
  if (!ref) return;
  ref.position.y = 0;
  if (overlay && ref !== overlay) overlay.position.y = 0;
  const box = new THREE.Box3().setFromObject(ref);
  const dy = -box.min.y;
  ref.position.y = dy;
  if (overlay && ref !== overlay) overlay.position.y = dy;
  if (extras) extras.position.y = dy;
}

function applyMainOffset() {
  if (!main) return;
  const s = mainOffset.s || 1;
  main.scale.setScalar(s);
  const c = mainCenterLocal || new THREE.Vector3();
  // GTA-Achsen (X rechts, Y vorne, Z oben) → three (x, z, -y)
  main.position.set(
    (1 - s) * c.x + mainOffset.x,
    mainBaseY + (1 - s) * c.y + mainOffset.z,
    (1 - s) * c.z - mainOffset.y,
  );
  needsRender = true;
}

/** Live-Vorschau für Verschieben (Meter) und Skalieren der Kleidung. */
function setMainOffset(x, y, z, s) { mainOffset = { x, y, z, s }; applyMainOffset(); }
function setGround(mode) { groundMode = mode; }
function fitAll() {
  const g = new THREE.Group();
  const objs = [main, overlay, extras].filter(Boolean);
  if (!objs.length) return;
  const box = new THREE.Box3();
  for (const o of objs) box.expandByObject(o);
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const radius = Math.max(size.length() / 2, 0.3);
  const dist = radius / Math.sin((camera.fov * Math.PI) / 360) * 0.8;
  const dir = new THREE.Vector3(0.35, 0.15, 1).normalize();
  camera.position.copy(center).addScaledVector(dir, dist);
  camera.near = Math.max(0.01, dist / 200); camera.far = dist * 30; camera.updateProjectionMatrix();
  controls.target.copy(center); controls.minDistance = radius * 0.2; controls.maxDistance = dist * 4; controls.update();
  void g;
  needsRender = true;
}

let fitDir = null;
function fit(obj) {
  if (!obj) return;
  const box = new THREE.Box3().setFromObject(obj);
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const radius = Math.max(size.length() / 2, 0.3);
  const dist = radius / Math.sin((camera.fov * Math.PI) / 360) * 0.72;
  const dir = (fitDir || new THREE.Vector3(0.75, 0.38, 0.85)).clone().normalize();
  camera.position.copy(center).addScaledVector(dir, dist);
  camera.near = Math.max(0.01, dist / 200);
  camera.far = dist * 30;
  camera.updateProjectionMatrix();
  controls.target.copy(center);
  controls.minDistance = radius * 0.4;
  controls.maxDistance = dist * 4;
  controls.update();
  needsRender = true;
}

function setPaint(hex) {
  paintColor = new THREE.Color(hex);
  for (const r of [main, overlay, extras]) {
    r && r.traverse((c) => { if (c.isMesh && c.material.userData.paint) c.material.color.copy(paintColor); });
  }
  needsRender = true;
}

function resetView() { fitDir = null; fit(main || overlay || animWrap); }
// Feste Blickrichtungen (x, y, z) – z. B. view(1,0.2,0) = Seite
function view(x, y, z) { fitDir = new THREE.Vector3(x, y, z); fit(main || overlay || animWrap); fitDir = null; }

// Diagnose (Entwicklung): Lack- und Folienschichten auflisten
function debugInfo() {
  const out = [];
  main && main.traverse((c) => {
    if (!c.isMesh || !c.material) return;
    if (c.material.userData.paint || (c.parent && c.parent.material && c.parent.material.userData && c.parent.material.userData.paint)) {
      const m = c.material;
      out.push({
        name: c.name, paint: !!m.userData.paint, layer: !m.userData.paint,
        map: m.map ? { w: m.map.image && (m.map.image.width || m.map.image.naturalWidth), channel: m.map.channel } : null,
        uv: !!c.geometry.attributes.uv, uv1: !!c.geometry.attributes.uv1, transparent: m.transparent, visible: c.visible,
      });
    }
  });
  return out;
}

// ================================================================ Szenenmodus (MLO)
// Jedes Modell (Archetyp) wird einmal geladen und für jedes Objekt geklont. GTA-Achsen: X rechts, Y vorne, Z oben.

let sceneRoot = null, collisionRoot = null, tctrl = null, selBox = null;
const templates = new Map();
let selectedId = null, onPick = null, onMoved = null, pickHooked = false;

const toThreePos = (p) => new THREE.Vector3(p[0], p[2], -p[1]);
const toThreeQuat = (q) => new THREE.Quaternion(q[0], q[2], -q[1], q[3]);
const toGtaPos = (v) => [v.x, -v.z, v.y];
const toGtaQuat = (q) => [q.x, -q.z, q.y, q.w];

function sceneClear() {
  animDispose();
  if (tctrl) { tctrl.detach(); }
  if (selBox) { scene.remove(selBox); selBox = null; }
  if (sceneRoot) { scene.remove(sceneRoot); sceneRoot = null; }
  if (collisionRoot) { disposeObject(collisionRoot); collisionRoot = null; }
  for (const t of templates.values()) disposeObject(t);
  templates.clear();
  selectedId = null;
  if (ground) ground.visible = true;
  if (gridHelper) gridHelper.visible = true;
  needsRender = true;
}

function ensureTransformControls() {
  if (tctrl) return;
  tctrl = new TransformControls(camera, renderer.domElement);
  tctrl.setSize(0.8);
  const helper = tctrl.getHelper ? tctrl.getHelper() : tctrl;
  scene.add(helper);
  tctrl.addEventListener('change', () => { if (selBox) selBox.update(); needsRender = true; });
  tctrl.addEventListener('dragging-changed', (e) => {
    controls.enabled = !e.value;
    if (!e.value && tctrl.object && onMoved) {
      const o = tctrl.object;
      onMoved(o.userData.entityId, toGtaPos(o.position), toGtaQuat(o.quaternion));
    }
  });
}

function hookPicking() {
  if (pickHooked) return;
  pickHooked = true;
  const el = renderer.domElement;
  let down = null;
  el.addEventListener('pointerdown', (e) => { down = { x: e.clientX, y: e.clientY }; });
  el.addEventListener('pointerup', (e) => {
    if (!down || !sceneRoot || (tctrl && tctrl.dragging)) return;
    if (Math.abs(e.clientX - down.x) + Math.abs(e.clientY - down.y) > 4) return;
    const r = el.getBoundingClientRect();
    const ray = new THREE.Raycaster();
    ray.setFromCamera(new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1), camera);
    const hits = ray.intersectObjects(sceneRoot.children.filter((c) => c.visible), true);
    for (const h of hits) {
      let o = h.object;
      while (o && o.userData.entityId === undefined) o = o.parent;
      if (o && o.visible) { sceneSelect(o.userData.entityId); if (onPick) onPick(o.userData.entityId); return; }
    }
  });
}

// Fehlendes Modell: gut sichtbare, halbtransparente orange Box mit Kanten
function placeholder() {
  const geo = new THREE.BoxGeometry(0.6, 0.6, 0.6);
  const m = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ color: 0xff8a1f, transparent: true, opacity: 0.45, depthWrite: false }));
  const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geo), new THREE.LineBasicMaterial({ color: 0xffb347 }));
  const g = new THREE.Group(); g.add(m); g.add(edges); return g;
}

function applyTransform(o, e) {
  o.position.copy(toThreePos(e.position));
  o.quaternion.copy(toThreeQuat(e.rotation));
  const sxy = (e.scale && e.scale[0]) || 1, sz = (e.scale && e.scale[1]) || 1;
  o.scale.set(sxy, sz, sxy);
}

/** items: [{ name, glb (Uint8Array|null) }], entities: [{ id, name, position, rotation, scale }] */
async function sceneLoad(items, entities, { keepCamera = false } = {}) {
  animDispose();
  disposeObject(main); disposeObject(overlay); disposeObject(extras); main = overlay = extras = null;
  const keepSel = selectedId;
  if (tctrl) tctrl.detach();
  if (selBox) { scene.remove(selBox); selBox = null; }
  if (sceneRoot) scene.remove(sceneRoot);
  for (const it of items) {
    if (templates.has(it.name)) continue;
    if (!it.glb) { templates.set(it.name, placeholder()); continue; }
    try {
      const root = await parse(it.glb);
      prepare(root);
      templates.set(it.name, root);
    } catch { templates.set(it.name, placeholder()); }
  }
  sceneRoot = new THREE.Group();
  for (const e of entities) {
    const t = templates.get(e.name) || placeholder();
    const o = t.clone(true);
    o.userData.entityId = e.id;
    applyTransform(o, e);
    sceneRoot.add(o);
  }
  scene.add(sceneRoot);
  if (ground) ground.visible = false;
  if (gridHelper) gridHelper.visible = false;
  hookPicking();
  ensureTransformControls();
  if (!keepCamera) fitObject(sceneRoot);
  if (keepSel) sceneSelect(keepSel);
  needsRender = true;
}

function fitObject(obj) {
  const box = new THREE.Box3().setFromObject(obj);
  if (box.isEmpty()) return;
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const radius = Math.max(size.length() / 2, 0.5);
  const dist = radius / Math.sin((camera.fov * Math.PI) / 360) * 0.7;
  camera.position.copy(center).addScaledVector(new THREE.Vector3(0.6, 0.9, 0.7).normalize(), dist);
  camera.near = Math.max(0.01, dist / 500); camera.far = dist * 40; camera.updateProjectionMatrix();
  controls.target.copy(center); controls.minDistance = 0.1; controls.maxDistance = dist * 5; controls.update();
  needsRender = true;
}

function sceneSelect(id) {
  selectedId = id;
  if (selBox) { scene.remove(selBox); selBox = null; }
  if (!sceneRoot) return;
  const o = sceneRoot.children.find((c) => c.userData.entityId === id);
  if (!o) { if (tctrl) tctrl.detach(); needsRender = true; return; }
  selBox = new THREE.BoxHelper(o, 0x3dbb84);
  scene.add(selBox);
  ensureTransformControls();
  tctrl.attach(o);
  needsRender = true;
}

function sceneFocus(id) {
  const o = sceneRoot && sceneRoot.children.find((c) => c.userData.entityId === id);
  if (!o) return;
  const box = new THREE.Box3().setFromObject(o);
  const c = box.getCenter(new THREE.Vector3());
  const r = Math.max(box.getSize(new THREE.Vector3()).length(), 0.8);
  const dir = camera.position.clone().sub(controls.target).normalize();
  controls.target.copy(c);
  camera.position.copy(c).addScaledVector(dir, r * 1.8);
  controls.update();
  needsRender = true;
}

function sceneMode(mode) { ensureTransformControls(); tctrl.setMode(mode); tctrl.setSpace(mode === 'rotate' ? 'local' : 'world'); needsRender = true; }

function sceneUpdate(id, e) {
  const o = sceneRoot && sceneRoot.children.find((c) => c.userData.entityId === id);
  if (!o) return;
  applyTransform(o, e);
  if (selBox) selBox.update();
  needsRender = true;
}

/** Sichtbarkeit je Objekt (z. B. Raumfilter). */
function sceneFilter(fn) {
  if (!sceneRoot) return;
  for (const o of sceneRoot.children) o.visible = fn(o.userData.entityId);
  needsRender = true;
}

async function sceneSetCollision(buffers, visible) {
  if (collisionRoot) { disposeObject(collisionRoot); collisionRoot = null; }
  if (visible && buffers && buffers.length) {
    collisionRoot = new THREE.Group();
    for (const b of buffers) {
      const r = await parse(b);
      r.traverse((c) => {
        if (!c.isMesh) return;
        c.material = new THREE.MeshBasicMaterial({ color: 0xff6a1a, transparent: true, opacity: 0.1, depthWrite: false, side: THREE.DoubleSide });
        c.add(new THREE.LineSegments(new THREE.WireframeGeometry(c.geometry), new THREE.LineBasicMaterial({ color: 0xff8a3a, transparent: true, opacity: 0.55 })));
        c.raycast = () => {};
      });
      collisionRoot.add(r);
    }
    scene.add(collisionRoot);
  }
  needsRender = true;
}

function sceneOn(pick, moved) { onPick = pick; onMoved = moved; }

// ================================================================ Animationsmodus
// Freemode-Körper als Skin-Modell (Bones mit userData.tag), YCD-Spuren werden als AnimationClip abgespielt.
// Laufweg (Mover) bewegt eine Hülle um das Modell; die Kamera kann mitlaufen.

const animClock = new THREE.Clock();
let animRig = null, animWrap = null, animMixer = null, animAction = null, animOnTick = null;
let animFollow = true, animDuration = 0, animLoop = true;
const animBones = new Map();          // tag -> Bone
const animBind = new Map();           // Bone -> {p,q,s}
const animLastWrap = new THREE.Vector3();

function animDispose() {
  if (animMixer) { animMixer.stopAllAction(); animMixer = null; }
  animAction = null;
  if (animWrap) { disposeObject(animWrap); animWrap = null; }
  animRig = null; animBones.clear(); animBind.clear();
}

/** Lädt das Rig (GLB mit Skelett). Bleibt geladen, bis ein anderes Modell kommt. */
async function animLoadRig(buffer, { keepCamera = false } = {}) {
  sceneClear();
  disposeObject(main); disposeObject(overlay); disposeObject(extras); main = overlay = extras = null;
  animDispose();
  const root = await parse(buffer);
  prepare(root);
  root.traverse((c) => {
    if (c.isSkinnedMesh) c.frustumCulled = false;
    if (c.isBone && c.userData && c.userData.tag !== undefined) {
      animBones.set(c.userData.tag, c);
      animBind.set(c, { p: c.position.clone(), q: c.quaternion.clone(), s: c.scale.clone() });
    }
  });
  animWrap = new THREE.Group();
  animWrap.add(root);
  scene.add(animWrap);
  animRig = root;
  // Auf den Boden stellen (Grundpose)
  root.updateMatrixWorld(true);
  root.traverse((c) => { if (c.isSkinnedMesh) { c.skeleton.update(); c.computeBoundingBox && c.computeBoundingBox(); } });
  const box = new THREE.Box3().setFromObject(root);
  if (!box.isEmpty()) root.position.y = -box.min.y;
  animMixer = new THREE.AnimationMixer(animWrap);
  if (!keepCamera) fit(animWrap);
  needsRender = true;
}

function animResetPose() {
  for (const [b, v] of animBind) { b.position.copy(v.p); b.quaternion.copy(v.q); b.scale.copy(v.s); }
  if (animWrap) { animWrap.position.set(0, 0, 0); animWrap.quaternion.identity(); }
  animLastWrap.set(0, 0, 0);
}

function b64f(s) {
  const bin = atob(s);
  const u = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
  return new Float32Array(u.buffer);
}

/** sample: Ergebnis von anim.sample. mover: Laufweg mitbewegen. Gibt Anzahl gefundener Bones zurück. */
function animSetClip(sample, { mover = true, play = true } = {}) {
  if (!animMixer) return 0;
  animMixer.stopAllAction();
  if (animAction) animMixer.uncacheAction(animAction.getClip());
  animAction = null;
  animResetPose();
  const n = sample.frames;
  const dur = Math.max(sample.duration, 1e-3);
  const times = new Float32Array(n);
  for (let k = 0; k < n; k++) times[k] = (dur * k) / (n - 1);
  const tracks = [];
  let matched = 0;
  for (const t of sample.tracks) {
    const data = b64f(t.data);
    if (t.type === 'mt' || t.type === 'mr') {
      if (!mover || t.bone !== 0) continue;
      if (t.type === 'mt') {
        const v = new Float32Array(n * 3);
        for (let k = 0; k < n; k++) { v[k * 3] = data[k * 3]; v[k * 3 + 1] = data[k * 3 + 2]; v[k * 3 + 2] = -data[k * 3 + 1]; }
        tracks.push(new THREE.VectorKeyframeTrack(`${animWrap.uuid}.position`, times, v));
      } else {
        const q = new Float32Array(n * 4);
        for (let k = 0; k < n; k++) { q[k * 4] = data[k * 4]; q[k * 4 + 1] = data[k * 4 + 2]; q[k * 4 + 2] = -data[k * 4 + 1]; q[k * 4 + 3] = data[k * 4 + 3]; }
        tracks.push(new THREE.QuaternionKeyframeTrack(`${animWrap.uuid}.quaternion`, times, q));
      }
      continue;
    }
    const bone = animBones.get(t.bone);
    if (!bone) continue;
    matched++;
    if (t.type === 'r' && data.length === n * 4) tracks.push(new THREE.QuaternionKeyframeTrack(`${bone.uuid}.quaternion`, times, data));
    else if (t.type === 't' && data.length === n * 3) tracks.push(new THREE.VectorKeyframeTrack(`${bone.uuid}.position`, times, data));
  }
  const clip = new THREE.AnimationClip(sample.name || 'clip', dur, tracks);
  animAction = animMixer.clipAction(clip);
  animDuration = dur;
  animAction.setLoop(animLoop ? THREE.LoopRepeat : THREE.LoopOnce, Infinity);
  animAction.clampWhenFinished = true;
  animAction.play();
  animAction.paused = !play;
  animMixer.update(0);
  animLastWrap.copy(animWrap.position);
  needsRender = true;
  return matched;
}

function animStep(dt) {
  animMixer.update(dt);
  if (animFollow && animWrap) {
    const d = animWrap.position.clone().sub(animLastWrap);
    if (d.lengthSq() > 0) { camera.position.add(d); controls.target.add(d); }
  }
  if (animWrap) animLastWrap.copy(animWrap.position);
  if (!animLoop && animAction.time >= animDuration - 1e-4) animAction.paused = true;
  needsRender = true;
  if (animOnTick) animOnTick(animState());
}

function animState() {
  return { time: animAction ? animAction.time : 0, duration: animDuration, playing: !!(animAction && !animAction.paused) };
}

function animPlay(on) {
  if (!animAction) return;
  if (on && !animLoop && animAction.time >= animDuration - 1e-3) { animAction.time = 0; animResetWrap(); }
  animAction.paused = !on;
  animClock.getDelta();
  if (animOnTick) animOnTick(animState());
}
function animResetWrap() { animMixer.update(0); if (animWrap) animLastWrap.copy(animWrap.position); }
function animSeek(t) {
  if (!animAction) return;
  animAction.time = Math.max(0, Math.min(t, animDuration));
  animMixer.update(0);
  if (animWrap) {
    const d = animWrap.position.clone().sub(animLastWrap);
    if (animFollow && d.lengthSq() > 0) { camera.position.add(d); controls.target.add(d); }
    animLastWrap.copy(animWrap.position);
  }
  needsRender = true;
  if (animOnTick) animOnTick(animState());
}
function animSpeed(s) { if (animMixer) animMixer.timeScale = s; }
function animSetLoop(on) {
  animLoop = on;
  if (animAction) { animAction.setLoop(on ? THREE.LoopRepeat : THREE.LoopOnce, Infinity); animAction.clampWhenFinished = true; }
}
function animSetFollow(on) { animFollow = on; }
function animOn(fn) { animOnTick = fn; }
function animHasRig() { return !!animRig; }
function animFit() { if (animWrap) { fit(animWrap); } }

window.Viewer3D = { animLoadRig, animSetClip, animPlay, animSeek, animSpeed, animSetLoop, animSetFollow, animOn, animState, animHasRig, animFit, animDispose, renderNow: () => { controls.update(); renderer.render(scene, camera); },
  sceneLoad, sceneSelect, sceneFocus, sceneMode, sceneUpdate, sceneFilter, sceneSetCollision, sceneOn, sceneClear, fitObject: () => sceneRoot && fitObject(sceneRoot),
   setMainOffset, setGround, fitAll, mount, load, loadOverlay, clearOverlay, clear, setExtras, setPaint, resetView, view, debugInfo };
window.dispatchEvent(new Event('viewer3d-ready'));
