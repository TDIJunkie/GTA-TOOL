'use strict';
// MLO Studio: Interiors anzeigen, Objekte verschieben/drehen/einfügen/löschen, Kollisionen, Texturen, Blender, YMAP-Platzierung, Export

const MloStudio = (() => {
  const { h, call, toast, modal, kb, base } = UI;

  const S = {
    model: null,        // mlo.analyze
    sel: { type: 'overview' },
    mloKey: null,
    scene: null,        // mlo.scene
    entityId: null,
    mode: 'translate',
    showCollision: false,
    room: -1,
    filter: '',
    dirty: false,
    version: {},        // Archetyp/Datei → Version (für 3D-Cache)
    texCache: {},
    blender: {},
  };
  const $ = (id) => document.getElementById(id);
  const glbCache = new Map();

  // ================================================================ Grundgerüst

  function init() {
    document.querySelectorAll('[data-mopen]').forEach((b) => b.addEventListener('click', () => pickAndOpen(b.dataset.mopen, false)));
    $('ms-add').addEventListener('click', () => modal({
      title: 'Weitere Dateien hinzufügen', body: h('p.muted', 'Dateien kommen zum geöffneten Interior dazu (z. B. eigene Props).'),
      buttons: [{ label: '📁 Ordner importieren', onClick: () => pickAndOpen('folder', true) }, { label: '📄 Dateien öffnen', primary: true, onClick: () => pickAndOpen('files', true) }],
    }));
    $('ms-close').addEventListener('click', closeAll);
    $('ms-export').addEventListener('click', exportDialog);
    const view = $('view-mlo');
    const dz = $('mdropzone');
    view.addEventListener('dragover', (e) => { e.preventDefault(); dz.classList.add('over'); });
    view.addEventListener('dragleave', (e) => { if (!view.contains(e.relatedTarget)) dz.classList.remove('over'); });
    view.addEventListener('drop', (e) => {
      e.preventDefault(); dz.classList.remove('over');
      const paths = [...e.dataTransfer.files].map((f) => window.api.pathForFile(f)).filter(Boolean);
      if (paths.length) open(paths, !!S.model);
    });
    window.api.on('tex:updated', ({ file, name }) => {
      if (App.view !== 'mlo' || !S.model) return;
      changedFile(file);
      toast(`Textur „${name}“ wurde übernommen.`);
      render(true);
    });
    window.api.on('blender', async ({ model, state, error, studio }) => {
      if (studio !== 'mlo') return;
      if (state === 'applied') { S.blender[model] = 'open'; changedFile(model); toast('✓ Aus Blender übernommen.'); await reanalyze(); render(true); }
      else if (state === 'error') toast(`Blender: ${error}`, 'error');
      else if (state === 'closed') { delete S.blender[model]; if (App.view === 'mlo') render(true); }
    });
  }

  async function show() {
    await call('studio:set', { studio: 'mlo' }, { quiet: true });
    renderAll();
  }

  async function pickAndOpen(kind, add) {
    const paths = await call('dialog:open', { kind });
    if (paths && paths.length) await open(paths, add);
  }

  async function open(paths, add) {
    if (!add && S.model && S.dirty && !await UI.confirm('Neu öffnen?', 'Nicht exportierte Änderungen gehen verloren. Fortfahren?', 'Öffnen', { danger: true })) return;
    const m = await call('mlo:open', { paths, add }, { busyText: 'Interior wird gelesen …' });
    if (!m) return;
    S.model = m;
    if (!add) { S.dirty = false; S.version = {}; glbCache.clear(); S.texCache = {}; S.scene = null; S.entityId = null; }
    if (!m.mlos.length) toast('Kein Interior (MLO) gefunden – Modelle, Texturen und Kollisionen lassen sich trotzdem bearbeiten.', 'warn', 8000);
    S.mloKey = m.mlos[0] ? m.mlos[0].key : null;
    S.sel = S.mloKey ? { type: 'mlo' } : { type: 'overview' };
    await ensureCatalog();
    renderAll();
  }

  async function ensureCatalog() {
    const st = await call('props:status', {}, { quiet: true });
    if (st && st.built) return;
    const ok = await UI.confirm('GTA-Objekte einlesen',
      'Interiors benutzen meist Original-Objekte aus GTA (Möbel, Pflanzen …). Damit sie angezeigt und neu eingefügt werden können, liest das Tool einmalig die Objektliste aus deiner GTA-Installation (wenige Sekunden).', 'Einlesen');
    if (!ok) return;
    const r = await call('props:build', {}, { busyText: 'GTA-Objekte werden gelesen …' });
    if (r) toast(`${r.archetypes.toLocaleString('de-DE')} GTA-Objekte eingelesen.`);
  }

  async function reanalyze() { const m = await call('mlo:analyze'); if (m) S.model = m; }

  async function closeAll() {
    if (S.dirty && !await UI.confirm('Schließen?', 'Nicht exportierte Änderungen gehen verloren. Wirklich schließen?', 'Schließen', { danger: true })) return;
    await call('session:close');
    Object.assign(S, { model: null, sel: { type: 'overview' }, mloKey: null, scene: null, entityId: null, dirty: false });
    if (window.Viewer3D) window.Viewer3D.sceneClear();
    renderAll();
  }

  function changedFile(file) {
    const name = base(file).replace(/\.[^.]+$/, '');
    S.version[name] = (S.version[name] || 0) + 1;
    for (const k of [...glbCache.keys()]) if (k.startsWith(name + '|')) glbCache.delete(k);
    delete S.texCache[file];
    // Archetypen, deren Texturdatei geändert wurde, ebenfalls neu laden
    if (S.scene) for (const a of S.scene.archetypes) if (a.ytds.some((y) => y === file) || a.model === file) { S.version[a.name] = (S.version[a.name] || 0) + 1; }
    S.dirty = true;
    updateStatus();
  }

  // ================================================================ Rendering

  function renderAll() {
    const loaded = !!S.model;
    $('ms-empty').classList.toggle('hidden', loaded);
    $('ms-main').classList.toggle('hidden', !loaded);
    $('ms-export').disabled = !loaded;
    updateStatus();
    if (!loaded) return;
    renderTree();
    render();
  }

  function updateStatus() {
    if (!S.model) { $('ms-status-text').textContent = 'Öffne einen MLO-Ordner (stream mit .ytyp, .ymap, .ydr …).'; return; }
    const m = S.model.mlos.find((x) => x.key === S.mloKey);
    $('ms-status-text').textContent = `${S.model.mlos.length} Interior(s) · ${S.model.models.length} Modelle · ${S.model.collisions.length} Kollisionen${m ? ` · ${m.entities} Objekte` : ''}${S.dirty ? ' · ● Änderungen nicht exportiert' : ''}`;
  }

  function select(sel) { S.sel = sel; renderTree(); render(); }

  function item(icon, label, selObj, extra) {
    const active = S.sel.type === selObj.type && (S.sel.file || '') === (selObj.file || '') && (selObj.type !== 'mlo' || S.mloKey === selObj.key);
    return h(`div.tree-item${active ? '.active' : ''}`, {
      title: label, onclick: () => { if (selObj.key) { S.mloKey = selObj.key; S.scene = null; S.entityId = null; } select(selObj); },
    }, h('span.ico', icon), h('span.name', label), extra || null);
  }

  function renderTree() {
    const t = $('ms-tree');
    t.innerHTML = '';
    const m = S.model;
    t.append(item('📋', 'Übersicht', { type: 'overview' }));
    if (m.mlos.length) {
      t.append(h('div.tree-label', 'Interiors'));
      for (const x of m.mlos) t.append(item('🏠', x.name, { type: 'mlo', key: x.key }, h('span.pill', String(x.entities))));
    }
    if (m.models.length) {
      t.append(h('div.tree-label', `Modelle im Paket (${m.models.length})`));
      for (const x of m.models) t.append(item('🧱', x.name, { type: 'model', file: x.file }, x.encrypted ? h('span.lock', '🔒') : null));
    }
    if (m.ytds.length) {
      t.append(h('div.tree-label', 'Texturdateien'));
      for (const y of m.ytds) t.append(item('🎨', base(y), { type: 'ytd', file: y }));
    }
    if (m.collisions.length) {
      t.append(h('div.tree-label', 'Kollisionen'));
      for (const c of m.collisions) t.append(item('🧊', base(c), { type: 'collision', file: c }));
    }
  }

  async function render(keepScroll) {
    const c = $('ms-content');
    const scroll = c.scrollTop;
    c.innerHTML = '';
    const s = S.sel;
    if (s.type === 'mlo') c.append(await viewMlo());
    else if (s.type === 'model') c.append(await viewModel(s.file));
    else if (s.type === 'ytd') c.append(await viewYtd(s.file));
    else if (s.type === 'collision') c.append(viewCollision(s.file));
    else c.append(viewOverview());
    if (keepScroll) c.scrollTop = scroll;
  }

  const action = (icon, title, text, onClick, disabled) =>
    h('button.action', { onclick: onClick, disabled: disabled || null }, h('span.a-ico', icon), h('span', h('div.a-title', title), h('div.a-text', text)));

  // ---------------------------------------------------------------- Übersicht

  function viewOverview() {
    const m = S.model;
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', 'Übersicht'), h('div.subtitle', `${m.mlos.length} Interior(s)`))),
      m.warnings.length ? h('div.notice.warn', h('b', 'Hinweise'), h('ul', m.warnings.map((w) => h('li', w)))) : null,
      h('div.cards',
        card('Interiors', m.mlos.length), card('Modelle', m.models.length), card('Texturdateien', m.ytds.length),
        card('Kollisionen', m.collisions.length), card('YMAPs', m.ymaps.length), card('YTYPs', m.ytyps.length)),
      h('div.actions', action('📦', 'Als FiveM-Resource exportieren', 'Fertige Map-Resource (this_is_a_map)', exportDialog)));
  }
  const card = (k, v) => h('div.card', h('div.k', k), h('div.v', String(v)));

  // ---------------------------------------------------------------- Interior

  const curMlo = () => S.model && S.model.mlos.find((x) => x.key === S.mloKey);
  const entity = (id) => S.scene && S.scene.entities.find((e) => e.id === id);

  async function loadScene(keepCamera) {
    S.scene = await call('mlo:scene', { key: S.mloKey });
    if (!S.scene) return;
    const status = document.querySelector('#ms-content .viewer-status');
    const items = [];
    let n = 0;
    for (const a of S.scene.archetypes) {
      n++;
      if (status) status.textContent = `Objekte werden geladen (${n}/${S.scene.archetypes.length}) …`;
      if (a.source === 'missing') { items.push({ name: a.name, glb: null }); continue; }
      const key = `${a.name}|${S.version[a.name] || 0}`;
      if (!glbCache.has(key)) {
        glbCache.set(key, window.api.invoke('mlo:archGlb', { name: a.name, version: S.version[a.name] || 0 }).then((r) => (r.ok ? r.data.glb : null)));
      }
      items.push({ name: a.name, glb: await glbCache.get(key) });
    }
    const V = window.Viewer3D;
    await V.sceneLoad(items, S.scene.entities, { keepCamera });
    applyFilter();
    if (S.entityId) V.sceneSelect(S.entityId);
    if (S.showCollision) await loadCollision();
    const miss = S.scene.archetypes.filter((a) => a.source === 'missing').length;
    if (status) status.textContent = `${S.scene.entities.length} Objekte · ${S.scene.archetypes.length} Modelle${miss ? ` · ${miss} nicht gefunden (orange Boxen)` : ''}`;
    renderMissing();
  }

  /** Hinweis: Objekte, deren Modell weder im Paket noch in GTA existiert (z. B. Türen aus einem Extra-Paket). */
  function renderMissing() {
    const box = $('ms-missing');
    if (!box || !S.scene) return;
    box.innerHTML = '';
    const miss = S.scene.archetypes.filter((a) => a.source === 'missing');
    if (!miss.length) return;
    const count = (n) => S.scene.entities.filter((e) => e.name === n).length;
    box.append(h('div.notice.warn',
      h('div', h('b', `${miss.length} Modell${miss.length > 1 ? 'e fehlen' : ' fehlt'} – im 3D-Fenster als orange Boxen. `),
        'Sie sind weder in diesem Paket noch in GTA enthalten, stammen also aus einer anderen Resource (oft Türen oder Deko aus einem Extra-Paket des MLO-Erstellers). ',
        'Im Spiel sind sie ebenfalls unsichtbar. Lösung: die fehlenden Dateien mit „+ Hinzufügen“ dazuladen oder die Objekte durch ein anderes Modell ersetzen (z. B. eine GTA-Tür).'),
      h('div.row.gap-s.wrap', { style: { marginTop: '8px' } }, miss.map((a) => {
        const first = S.scene.entities.find((e) => e.name === a.name);
        return h('button.btn.small', {
          onclick: () => { if (!first) return; S.entityId = first.id; window.Viewer3D.sceneSelect(first.id); window.Viewer3D.sceneFocus(first.id); renderEntity(); highlightList(); },
        }, `⚠ ${a.name} (${count(a.name)}×)`);
      }))));
  }

  async function loadCollision() {
    const m = curMlo();
    const bufs = [];
    // nur der Bereich des Interiors (Räume), nicht die ganze Umgebung
    let clipMin = null, clipMax = null;
    for (const r of (S.scene && S.scene.rooms) || []) {
      if (r.name === 'limbo' && S.scene.rooms.length > 1) continue;
      clipMin = clipMin ? clipMin.map((v, i) => Math.min(v, r.bbMin[i])) : [...r.bbMin];
      clipMax = clipMax ? clipMax.map((v, i) => Math.max(v, r.bbMax[i])) : [...r.bbMax];
    }
    for (const c of S.model.collisions) {
      const r = await call('mlo:collision', { ybn: c, pos: m && m.instance ? m.instance.position : null, rot: m && m.instance ? m.instance.rotation : null, clipMin, clipMax }, { quiet: true });
      if (r) bufs.push(r.glb);
    }
    await window.Viewer3D.sceneSetCollision(bufs, true);
  }

  function applyFilter() {
    const q = S.filter.trim().toLowerCase();
    window.Viewer3D.sceneFilter((id) => {
      const e = entity(id);
      if (!e) return true;
      if (S.room >= 0 && e.room !== S.room) return false;
      return !q || e.name.toLowerCase().includes(q);
    });
  }

  async function viewMlo() {
    const m = curMlo();
    if (!m) return h('p', 'Interior nicht gefunden.');
    if (!window.Viewer3D) await new Promise((r) => window.addEventListener('viewer3d-ready', r, { once: true }));
    const V = window.Viewer3D;
    const wrap = h('div',
      h('div.content-head', h('div.title-block', h('h1', m.name), h('div.subtitle#ms-sub',
        `${m.entities} Objekte · ${(m.rooms || []).length} Räume · ${m.portals} Portale · ${base(m.ytyp)}${m.instance ? ` · platziert in ${base(m.instance.ymap)}` : ' · ⚠ keine YMAP-Platzierung'}`))));

    const modeBtn = (mode, label) => h(`button.btn.small${S.mode === mode ? '.primary' : ''}`, { onclick: () => { S.mode = mode; V.sceneMode(mode); render(true); } }, label);
    const roomSel = h('select', { onchange: (e) => { S.room = Number(e.target.value); applyFilter(); renderList(); } },
      h('option', { value: -1 }, 'Alle Räume'), (m.rooms || []).map((r) => h('option', { value: r.index, selected: S.room === r.index || null }, `Raum: ${r.name}`)));
    wrap.append(h('div.mlo-toolbar',
      modeBtn('translate', '✥ Verschieben'), modeBtn('rotate', '⟳ Drehen'),
      h('label.check.small', h('input', { type: 'checkbox', checked: S.showCollision || null, onchange: async (e) => { S.showCollision = e.target.checked; if (S.showCollision) await loadCollision(); else await V.sceneSetCollision([], false); } }), ' Kollision anzeigen'),
      roomSel,
      h('span.spacer'),
      h('button.btn.small', { onclick: () => addDialog() }, '➕ Objekt einfügen'),
      h('button.btn.small', { onclick: () => V.fitObject() }, '⟲ Ansicht')));

    const viewer = h('div.viewer.tall', h('div.viewer-status', 'Interior wird geladen …'),
      h('div.viewer-bar', h('span.viewer-hint', 'Klick: Objekt wählen · Pfeile/Ringe am Objekt ziehen: verschieben/drehen · Rechte Maustaste: Ansicht verschieben')));
    wrap.append(viewer);
    wrap.append(h('div#ms-missing'));
    wrap.append(h('div#ms-entity'));
    wrap.append(placementPanel(m));
    wrap.append(h('div.section', h('div.section-head', h('h3', 'Objekte'), h('span.spacer'),
      h('input.search', { type: 'text', placeholder: 'Suchen …', value: S.filter, oninput: (e) => { S.filter = e.target.value; applyFilter(); renderList(); } })),
      h('div#ms-list.list.entity-list')));

    setTimeout(async () => {
      V.mount(viewer);
      V.sceneOn((id) => { S.entityId = id; renderEntity(); highlightList(); }, onMoved);
      const keep = S.scene && S.lastMloShown === S.mloKey;
      if (!S.scene || !keep) await loadScene(false); else await loadScene(true);
      S.lastMloShown = S.mloKey;
      V.sceneMode(S.mode);
      renderEntity(); renderList();
    }, 0);
    return wrap;
  }

  async function onMoved(id, pos, rot) {
    const r = await call('mlo:setEntity', { key: S.mloKey, id, pos, rot }, { quiet: false });
    if (!r) return;
    const e = entity(id);
    if (e) { e.position = pos; e.rotation = rot; }
    S.dirty = true; updateStatus();
    renderEntity();
    $('ms-status-text').textContent += ' · gespeichert ✓';
  }

  // Quaternion ⇄ Winkel (Grad), GTA-Achsen
  function toEuler(q) {
    const [x, y, z, w] = q;
    const roll = Math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y));
    const pitch = Math.asin(Math.max(-1, Math.min(1, 2 * (w * y - z * x))));
    const yaw = Math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z));
    return [roll, pitch, yaw].map((v) => Math.round(v * 180 / Math.PI * 100) / 100);
  }
  function fromEuler(rx, ry, rz) {
    const [r, p, y] = [rx, ry, rz].map((d) => d * Math.PI / 360);
    const cr = Math.cos(r), sr = Math.sin(r), cp = Math.cos(p), sp = Math.sin(p), cy = Math.cos(y), sy = Math.sin(y);
    return [sr * cp * cy - cr * sp * sy, cr * sp * cy + sr * cp * sy, cr * cp * sy - sr * sp * cy, cr * cp * cy + sr * sp * sy];
  }

  function renderEntity() {
    const box = $('ms-entity');
    if (!box) return;
    box.innerHTML = '';
    const e = entity(S.entityId);
    if (!e) { box.append(h('div.notice.info', 'Klicke im 3D-Fenster oder in der Liste auf ein Objekt, um es zu verschieben, zu drehen oder zu löschen.')); return; }
    const num = (v) => h('input.num', { type: 'number', step: '0.01', value: Number(v).toFixed(3) });
    const px = num(e.position[0]), py = num(e.position[1]), pz = num(e.position[2]);
    const eul = toEuler(e.rotation);
    const rx = num(eul[0]), ry = num(eul[1]), rz = num(eul[2]);
    const sc = num(e.scale[0]);
    const room = S.scene.rooms && e.room >= 0 ? S.scene.rooms[e.room].name : '–';
    const arch = S.scene.archetypes.find((a) => a.name === e.name);
    const apply = async () => {
      const pos = [px, py, pz].map((i) => Number(i.value));
      const rot = fromEuler(Number(rx.value), Number(ry.value), Number(rz.value));
      const s = Number(sc.value) || 1;
      const r = await call('mlo:setEntity', { key: S.mloKey, id: e.id, pos, rot, scale: [s, s] }, { busyText: 'Wird gespeichert …' });
      if (!r) return;
      e.position = pos; e.rotation = rot; e.scale = [s, s];
      window.Viewer3D.sceneUpdate(e.id, e);
      S.dirty = true; updateStatus();
      toast('✓ Objekt gespeichert.');
    };
    const same = S.scene.entities.filter((x) => x.name === e.name).length;
    box.append(h('div.entity-panel',
      e.source === 'missing' ? h('div.notice.warn', { style: { marginBottom: '10px' } },
        `Das Modell „${e.name}“ fehlt (nicht im Paket, nicht in GTA) – es kommt aus einer anderen Resource und ist so auch im Spiel unsichtbar. `,
        'Fehlende Dateien mit „+ Hinzufügen“ dazuladen oder unten „Modell ersetzen“ wählen.') : null,
      h('div.entity-head',
        h('div', h('b', e.name), h('div.muted.small', `${e.source === 'pack' ? 'Aus dem Paket' : e.source === 'gta' ? 'GTA-Original' : '⚠ Modell nicht gefunden'} · Raum: ${room}${e.set ? ` · Objekt-Set: ${e.set}` : ''} · ${e.id}`)),
        h('span.spacer'),
        h('button.btn.small', { onclick: () => window.Viewer3D.sceneFocus(e.id) }, '🎯 Hinspringen')),
      h('div.entity-grid',
        h('label', 'Position X', px), h('label', 'Position Y', py), h('label', 'Position Z (Höhe)', pz),
        h('label', 'Drehung X°', rx), h('label', 'Drehung Y°', ry), h('label', 'Drehung Z° (links/rechts)', rz),
        h('label', 'Größe', sc)),
      h('div.row.gap-s.wrap',
        h('button.btn.primary', { onclick: apply }, 'Übernehmen'),
        h('button.btn', {
          onclick: async () => {
            const pos = [e.position[0] + 0.5, e.position[1], e.position[2]];
            const r = await call('mlo:addEntity', { key: S.mloKey, copyFrom: e.id, pos }, { busyText: 'Wird dupliziert …' });
            if (r) { S.entityId = r.id; S.dirty = true; await afterStructureChange(); toast('Objekt dupliziert – jetzt verschieben.'); }
          },
        }, '⧉ Duplizieren'),
        h('button.btn.danger', {
          onclick: async () => {
            if (!await UI.confirm('Objekt löschen?', `„${e.name}“ aus dem Interior entfernen?`, 'Löschen', { danger: true })) return;
            const r = await call('mlo:deleteEntity', { key: S.mloKey, id: e.id }, { busyText: 'Wird gelöscht …' });
            if (r) { S.entityId = null; S.dirty = true; await afterStructureChange(); toast('Objekt gelöscht.'); }
          },
        }, '🗑 Löschen'),
        h('button.btn', { onclick: () => addDialog({ swap: e, same }) }, '🔁 Modell ersetzen …'),
        arch && arch.model ? h('button.btn', { onclick: () => select({ type: 'model', file: arch.model }) }, '🧱 Modell / Texturen bearbeiten') : null)));
  }

  async function afterStructureChange() {
    await reanalyze();
    const m = curMlo();
    const sub = document.getElementById('ms-sub');
    if (m && sub) sub.textContent = `${m.entities} Objekte · ${(m.rooms || []).length} Räume · ${m.portals} Portale · ${base(m.ytyp)}${m.instance ? ` · platziert in ${base(m.instance.ymap)}` : ''}`;
    renderTree();
    await loadScene(true);
    renderEntity(); renderList(); updateStatus();
  }

  function renderList() {
    const box = $('ms-list');
    if (!box || !S.scene) return;
    box.innerHTML = '';
    const q = S.filter.trim().toLowerCase();
    const list = S.scene.entities.filter((e) => (S.room < 0 || e.room === S.room) && (!q || e.name.toLowerCase().includes(q)));
    for (const e of list.slice(0, 500)) {
      box.append(h(`div.list-row.clickable${e.id === S.entityId ? '.active' : ''}`, {
        'data-id': e.id,
        onclick: () => { S.entityId = e.id; window.Viewer3D.sceneSelect(e.id); window.Viewer3D.sceneFocus(e.id); renderEntity(); highlightList(); },
      }, h('span', e.source === 'pack' ? '🧱' : e.source === 'gta' ? '🪑' : '⚠'),
      h('div.grow', h('div.name', e.name), h('div.muted.small', `${S.scene.rooms && e.room >= 0 ? S.scene.rooms[e.room].name : '–'}${e.set ? ' · ' + e.set : ''} · ${e.position.map((v) => v.toFixed(2)).join(' / ')}`))));
    }
    if (!list.length) box.append(h('p.muted', { style: { padding: '10px' } }, 'Keine Objekte.'));
  }

  function highlightList() {
    document.querySelectorAll('#ms-list .list-row').forEach((r) => r.classList.toggle('active', r.dataset.id === S.entityId));
    // nur die Liste scrollen, nicht die ganze Seite
    const list = document.getElementById('ms-list');
    const a = document.querySelector('#ms-list .list-row.active');
    if (a && list) {
      const top = a.offsetTop;
      if (top < list.scrollTop || top + a.offsetHeight > list.scrollTop + list.clientHeight) list.scrollTop = top - list.clientHeight / 2;
    }
  }

  function placementPanel(m) {
    if (!m.instance) {
      return h('div.notice.warn', 'Für dieses Interior gibt es keine YMAP mit Platzierung. Ohne sie erscheint es nicht im Spiel – bitte die passende .ymap mit „+ Hinzufügen“ dazuladen.');
    }
    const num = (v) => h('input.num', { type: 'number', step: '0.01', value: Number(v).toFixed(3) });
    const px = num(m.instance.position[0]), py = num(m.instance.position[1]), pz = num(m.instance.position[2]);
    const heading = num(toEuler(m.instance.rotation)[2]);
    return h('details.mount-panel', { open: S.placeOpen || null, ontoggle: (e) => { S.placeOpen = e.target.open; } },
      h('summary', `🗺 Platzierung in der Welt (${base(m.instance.ymap)})`),
      h('p.muted.small', 'Wo das Interior in der GTA-Welt liegt. Achtung: Kollisionen (.ybn) liegen in Weltkoordinaten und werden nicht mitbewegt.'),
      h('div.entity-grid', h('label', 'X', px), h('label', 'Y', py), h('label', 'Z', pz), h('label', 'Drehung Z°', heading)),
      h('button.btn.primary', {
        onclick: async () => {
          const r = await call('mlo:setPlacement', { ymap: m.instance.ymap, index: m.instance.index, pos: [px, py, pz].map((i) => Number(i.value)), rot: fromEuler(0, 0, Number(heading.value)) }, { busyText: 'YMAP wird gespeichert …' });
          if (r) { S.dirty = true; await reanalyze(); toast(`✓ Platzierung gespeichert. ${r.note}`, 'ok', 9000); render(true); }
        },
      }, 'Platzierung übernehmen'));
  }

  // ---------------------------------------------------------------- Objekt einfügen

  function addDialog(opts = {}) {
    const m = curMlo();
    const swap = opts.swap || null;
    const e = swap || entity(S.entityId);
    const roomIdx = S.room >= 0 ? S.room : e ? e.room : (m.rooms && m.rooms.length > 1 ? 1 : 0);
    const room = S.scene && S.scene.rooms && S.scene.rooms[roomIdx];
    const pos = e ? [e.position[0] + 0.5, e.position[1], e.position[2]]
      : room ? [(room.bbMin[0] + room.bbMax[0]) / 2, (room.bbMin[1] + room.bbMax[1]) / 2, room.bbMin[2] + 0.1] : [0, 0, 0];
    const search = h('input', { type: 'text', placeholder: swap ? 'GTA-Objekt suchen, z. B. door, v_ilev, gate …' : 'GTA-Objekt suchen, z. B. chair, plant, desk, lamp …' });
    const allSame = h('input', { type: 'checkbox', checked: true });
    const list = h('div.gta-list');
    let chosen = null;
    const packNames = [...new Set((S.scene ? S.scene.archetypes.filter((a) => a.source === 'pack').map((a) => a.name) : []))];
    const fill = (names, note) => {
      list.innerHTML = '';
      if (note) list.append(h('p.muted.small', { style: { padding: '8px' } }, note));
      for (const n of names) {
        list.append(h('div.list-row.clickable', {
          onclick: (ev) => { chosen = n; list.querySelectorAll('.list-row').forEach((r) => r.classList.remove('active')); ev.currentTarget.classList.add('active'); },
        }, h('span', packNames.includes(n) ? '🧱' : '🪑'), h('div.grow', h('div.name', n))));
      }
    };
    let timer = null;
    search.addEventListener('input', () => {
      clearTimeout(timer);
      timer = setTimeout(async () => {
        const r = await call('props:search', { term: search.value }, { quiet: true });
        if (r) fill(r.items, r.items.length >= 300 ? 'Die ersten 300 Treffer – Suche genauer eingeben.' : null);
      }, 250);
    });
    fill(packNames, packNames.length ? 'Modelle aus dem Paket – oder oben nach GTA-Objekten suchen.' : 'Oben nach GTA-Objekten suchen.');
    if (swap) { search.value = 'door'; search.dispatchEvent(new Event('input')); }
    modal({
      title: swap ? 'Modell ersetzen' : 'Objekt einfügen',
      body: h('div',
        swap
          ? h('p.muted', `Neues Modell für „${swap.name}“ wählen. Position und Drehung bleiben erhalten – ggf. danach nachjustieren (GTA-Türen haben den Drehpunkt meist am Scharnier).`)
          : h('p.muted', `Das Objekt wird in den Raum „${room ? room.name : '–'}“ gesetzt${e ? ' (neben das ausgewählte Objekt)' : ''}. Danach mit dem Griff im 3D-Fenster an die richtige Stelle schieben.`),
        search, list,
        swap && opts.same > 1 ? h('label.check', { style: { marginTop: '8px' } }, allSame, ` Alle ${opts.same} Objekte „${swap.name}“ ersetzen`) : null,
        h('div.row.gap-s', { style: { marginTop: '10px' } },
          h('button.btn', {
            onclick: async () => {
              const r = await call('mlo:importModel', { key: S.mloKey }, { busyText: 'Modell wird ins Paket übernommen …' });
              if (r) { packNames.push(r.name); fill(packNames, `Eigenes Modell „${r.name}“ übernommen – auswählen und „Einfügen“ klicken.`); chosen = r.name; await reanalyze(); renderTree(); }
            },
          }, '⬆ Eigenes Modell (.ydr) importieren'))),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: swap ? 'Ersetzen' : 'Einfügen', primary: true,
          onClick: async () => {
            if (!chosen) { toast('Bitte ein Objekt auswählen.', 'warn'); return true; }
            if (swap) {
              const all = opts.same > 1 && allSame.checked;
              const r = await call('mlo:swapArchetype', { key: S.mloKey, id: all ? '' : swap.id, from: swap.name, to: chosen }, { busyText: 'Modell wird ersetzt …' });
              if (!r) return true;
              S.entityId = swap.id; S.dirty = true;
              await afterStructureChange();
              window.Viewer3D.sceneFocus(swap.id);
              toast(`✓ ${r.changed} Objekt${r.changed > 1 ? 'e' : ''} jetzt „${r.name}“.`);
              return false;
            }
            const r = await call('mlo:addEntity', { key: S.mloKey, name: chosen, pos, rot: [0, 0, 0, 1], room: roomIdx }, { busyText: 'Wird eingefügt …' });
            if (!r) return true;
            S.entityId = r.id; S.dirty = true;
            await afterStructureChange();
            window.Viewer3D.sceneFocus(r.id);
            toast(`„${chosen}“ eingefügt – jetzt mit dem Griff platzieren.`);
            return false;
          },
        },
      ],
    });
  }

  // ---------------------------------------------------------------- Modell / Texturen / Kollision

  async function viewModel(file) {
    const info = S.model.models.find((x) => x.file === file) || { file, name: base(file) };
    const ytds = S.scene ? (S.scene.archetypes.find((a) => a.model === file) || { ytds: [] }).ytds : [];
    const bState = S.blender[file];
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', info.name), h('div.subtitle', `${base(file)} · ${kb(info.size)}${info.archetype ? '' : ' · kein Archetyp im Paket (z. B. Außenbereich/Map)'}`))));
    if (info.encrypted) { wrap.append(h('div.notice.warn', 'Verschlüsselt (Escrow) – nicht bearbeitbar.')); return wrap; }
    if (bState === 'open') wrap.append(h('div.notice.info', 'In Blender geöffnet – „Speichern & zurück zum GTA Tool“ übernimmt das Modell automatisch.'));
    const viewer = h('div.viewer', h('div.viewer-status', '3D wird erstellt …'), h('div.viewer-bar', h('span.viewer-hint', 'Drehen: linke Maustaste · Zoomen: Mausrad')));
    wrap.append(viewer);
    wrap.append(h('div.actions',
      action('🧊', 'In Blender bearbeiten', 'Bearbeiten und direkt zurück übernehmen', async () => {
        const r = await call('mlo:blender', { file, ytds }, { busyText: 'Blender wird vorbereitet …' });
        if (r) { S.blender[file] = 'open'; toast('Blender startet. Zum Übernehmen: Seitenleiste (N) → „GTA Tool“ → „Speichern & zurück“.', 'info', 9000); render(true); }
      }, bState === 'open'),
      action('💾', 'Datei exportieren', `${base(file)} speichern`, async () => { const r = await call('file:export', { file }); if (r) toast(`Gespeichert: ${r.file}`); }),
      action('♻', 'Datei ersetzen', 'Durch eine andere Datei ersetzen', async () => {
        const p = await call('dialog:open', { kind: 'files' });
        if (!p || !p[0]) return;
        const r = await call('file:replace', { file, source: p[0] }, { busyText: 'Wird ersetzt …' });
        if (r) { changedFile(file); toast('Ersetzt.'); render(true); }
      })));
    wrap.append(await textureSection(file, 'Eingebettete Texturen'));
    for (const y of ytds) wrap.append(await textureSection(y, `Texturen aus ${base(y)}`));
    setTimeout(async () => {
      const V = window.Viewer3D;
      V.mount(viewer); V.setGround('main'); V.setMainOffset(0, 0, 0, 1);
      const r = await window.api.invoke('model:preview', { file, ytds });
      if (!r.ok) { viewer.querySelector('.viewer-status').textContent = r.error; return; }
      await V.load(r.data.glb, {});
      await V.setExtras([]);
      viewer.querySelector('.viewer-status').textContent = `${r.data.triangles.toLocaleString('de-DE')} Dreiecke`;
    }, 0);
    return wrap;
  }

  async function viewYtd(file) {
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', base(file)), h('div.subtitle', 'Texturdatei'))));
    wrap.append(await textureSection(file, 'Texturen'));
    return wrap;
  }

  async function textureSection(file, title) {
    let info = S.texCache[file];
    if (!info) { info = await call('tex:list', { file }, { quiet: true }); if (info) S.texCache[file] = info; }
    const sec = h('div.section', h('div.section-head', h('h3', `${title} (${info ? info.textures.length : 0})`)));
    if (!info) return sec;
    if (!info.textures.length) { sec.append(h('p.muted', 'Keine.')); return sec; }
    const refresh = () => { changedFile(file); render(true); };
    sec.append(h('div.tex-toolbar',
      h('button.btn', { onclick: async () => { const r = await call('tex:export', { file, names: [], format: 'png' }, { busyText: 'Wird exportiert …' }); if (r) { toast(`${r.files.length} exportiert.`); call('shell:open', { target: r.outDir }); } } }, '⬇ Alle exportieren (PNG)'),
      h('button.btn', { onclick: async () => { const r = await call('tex:export', { file, names: [], format: 'dds' }, { busyText: 'Wird exportiert …' }); if (r) toast(`${r.files.length} exportiert.`); } }, '⬇ Alle als DDS'),
      h('button.btn', {
        onclick: async () => {
          const imgs = await call('dialog:open', { kind: 'images' });
          if (!imgs || !imgs.length) return;
          const r = await call('tex:add', { file, images: imgs }, { busyText: 'Wird importiert …' });
          if (r) { toast(`${r.added.length} Textur(en) importiert (gleichnamige ersetzt).`); refresh(); }
        },
      }, '⬆ Textur importieren')));
    const grid = h('div.tex-grid');
    for (const t of info.textures) {
      grid.append(h('div.tex',
        h('div.tex-img', t.thumb ? h('img', { src: t.thumb }) : h('span.muted.small', 'keine Vorschau')),
        h('div.tex-info', h('div.tex-name', { title: t.name }, t.name), h('div.tex-meta', `${t.width}×${t.height} · ${t.format} · ${t.mips} Mips`)),
        h('div.tex-btns.wrap',
          h('button.btn', { title: 'In Paint.NET öffnen – speichern übernimmt automatisch', onclick: async () => { const r = await call('tex:edit', { file, name: t.name }, { busyText: 'Wird geöffnet …' }); if (r) toast('In Paint.NET geöffnet – Speichern übernimmt automatisch.', 'info', 7000); } }, 'Ersetzen'),
          h('button.btn', {
            onclick: async () => {
              const p = await call('dialog:open', { kind: 'image' });
              if (!p || !p[0]) return;
              const r = await call('tex:replace', { file, name: t.name, image: p[0] }, { busyText: 'Wird ersetzt …' });
              if (r) { toast('Ersetzt.'); refresh(); }
            },
          }, 'Datei …'),
          h('button.btn', { onclick: async () => { const r = await call('tex:export', { file, names: [t.name], format: 'png' }, { busyText: 'Wird exportiert …' }); if (r) toast(`Exportiert nach ${r.outDir}`); } }, 'Export'))));
    }
    sec.append(grid);
    return sec;
  }

  function viewCollision(file) {
    const m = curMlo();
    const bState = S.blender[file];
    const viewer = h('div.viewer', h('div.viewer-status', 'Kollision wird geladen …'));
    const wrap = h('div',
      h('div.content-head', h('div.title-block', h('h1', base(file)), h('div.subtitle', 'Kollision (wo man laufen kann und nicht hindurchkommt)'))),
      bState === 'open' ? h('div.notice.info', 'In Blender geöffnet – „Speichern & zurück zum GTA Tool“ übernimmt die Kollision automatisch.') : null,
      viewer,
      h('div.actions',
        action('🧊', 'In Blender bearbeiten', 'Kollision anpassen und zurück übernehmen', async () => {
          const r = await call('mlo:blender', { file, ytds: [] }, { busyText: 'Blender wird vorbereitet …' });
          if (r) { S.blender[file] = 'open'; toast('Blender startet mit der Kollision.', 'info', 7000); render(true); }
        }, bState === 'open'),
        action('💾', 'Datei exportieren', base(file), async () => { const r = await call('file:export', { file }); if (r) toast(`Gespeichert: ${r.file}`); }),
        action('♻', 'Datei ersetzen', 'Andere .ybn verwenden', async () => {
          const p = await call('dialog:open', { kind: 'files' });
          if (!p || !p[0]) return;
          const r = await call('file:replace', { file, source: p[0] }, { busyText: 'Wird ersetzt …' });
          if (r) { S.dirty = true; toast('Ersetzt.'); render(true); }
        })),
      h('div.notice.info', 'Tipp: Im Interior lässt sich die Kollision über „Kollision anzeigen“ zusammen mit den Objekten einblenden.'));
    setTimeout(async () => {
      const V = window.Viewer3D;
      V.mount(viewer); V.setGround('main'); V.setMainOffset(0, 0, 0, 1);
      const r = await window.api.invoke('mlo:collision', { ybn: file, pos: m && m.instance ? m.instance.position : null, rot: m && m.instance ? m.instance.rotation : null });
      if (!r.ok) { viewer.querySelector('.viewer-status').textContent = r.error; return; }
      await V.load(r.data.glb, {});
      await V.setExtras([]);
      viewer.querySelector('.viewer-status').textContent = `${r.data.triangles.toLocaleString('de-DE')} Kollisionsflächen`;
    }, 0);
    return wrap;
  }

  // ---------------------------------------------------------------- Export

  async function exportDialog() {
    if (!S.model) return;
    const settings = await call('settings:get');
    const m = S.model.mlos[0];
    const nameIn = h('input', { type: 'text', value: m ? m.name.replace(/_collision$/, '') : 'interior' });
    const dirIn = h('input', { type: 'text', value: (settings && (settings.lastResourceDir || settings.lastExportDir)) || '', placeholder: 'Bitte Ordner wählen …' });
    modal({
      title: 'Interior als FiveM-Resource exportieren',
      body: h('div',
        h('p.muted', 'Erstellt eine fertige Map-Resource: alle Dateien im stream-Ordner, fxmanifest.lua mit „this_is_a_map“.'),
        h('div.field', h('label', 'Name der Resource'), nameIn),
        h('div.field', h('label', 'Zielordner'), h('div.input-row', dirIn, h('button.btn', { onclick: async () => { const r = await call('dialog:open', { kind: 'outdir' }); if (r && r[0]) dirIn.value = r[0]; } }, 'Durchsuchen …')))),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: '📦 Exportieren', primary: true,
          onClick: async () => {
            if (!dirIn.value.trim()) { toast('Bitte einen Zielordner wählen.', 'warn'); return true; }
            if (await call('fivem:exists', { outDir: dirIn.value.trim(), resourceName: nameIn.value.trim() })) {
              if (!await UI.confirm('Überschreiben?', 'Eine Resource mit diesem Namen existiert dort schon. Überschreiben?', 'Überschreiben', { danger: true })) return true;
            }
            const r = await call('mlo:export', { outDir: dirIn.value.trim(), resourceName: nameIn.value.trim() }, { busyText: 'Resource wird erstellt …' });
            if (!r) return true;
            S.dirty = false; updateStatus();
            modal({
              title: '✓ Export fertig',
              body: h('div', h('p', h('code', r.folder)), h('p', `${r.files} Dateien`),
                r.warnings.length ? h('div.notice.warn', h('ul', r.warnings.map((w) => h('li', w)))) : null,
                h('div.notice.info', 'Ordner nach „resources“ kopieren → server.cfg: ', h('code', `ensure ${base(r.folder)}`))),
              buttons: [{ label: 'Schließen' }, { label: '📂 Ordner öffnen', primary: true, onClick: () => call('shell:open', { target: r.folder }) }],
            });
            return false;
          },
        },
      ],
    });
  }

  return { init, show, open, get dirty() { return S.dirty; } };
})();
