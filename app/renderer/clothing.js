'use strict';
// Clothing Studio: Kleidung öffnen, auf dem Charakter zeigen, Position, Texturen, Blender, m/w, Addon ⇄ Replace, Export

const ClothingStudio = (() => {
  const { h, call, toast, modal, kb, base } = UI;

  const S = {
    model: null,       // Ergebnis von clothing.analyze
    sel: null,         // { type: 'overview' } | { type: 'item', id }
    variant: {},       // id -> Texturdatei (gewählte Variante)
    showBody: true,
    offset: { x: 0, y: 0, z: 0, s: 1 },
    dirty: false,
    body: null,        // Freemode-Grundkörper (aus GTA)
    previewVersion: 0,
    texCache: {},
    blender: {},
  };
  const $ = (id) => document.getElementById(id);
  const cache = new Map(); // GLB-Cache

  // ================================================================ Start

  function init() {
    document.querySelectorAll('[data-copen]').forEach((b) => b.addEventListener('click', () => pickAndOpen(b.dataset.copen, false)));
    $('cs-gta').addEventListener('click', () => gtaBrowser());
    $('cs-gta2').addEventListener('click', () => gtaBrowser());
    $('cs-add').addEventListener('click', () => addDialog());
    $('cs-close').addEventListener('click', closeAll);
    $('cs-export').addEventListener('click', exportDialog);
    const view = $('view-clothing');
    const dz = $('cdropzone');
    view.addEventListener('dragover', (e) => { e.preventDefault(); dz.classList.add('over'); });
    view.addEventListener('dragleave', (e) => { if (!view.contains(e.relatedTarget)) dz.classList.remove('over'); });
    view.addEventListener('drop', (e) => {
      e.preventDefault(); dz.classList.remove('over');
      const paths = [...e.dataTransfer.files].map((f) => window.api.pathForFile(f)).filter(Boolean);
      if (paths.length) open(paths, !!S.model);
    });
    window.api.on('tex:updated', ({ file, name }) => {
      if (App.view !== 'clothing' || !S.model) return;
      if (!S.model.items.some((i) => i.textures.some((t) => t.file === file))) return;
      S.dirty = true; changed(); delete S.texCache[file];
      toast(`Textur „${name}“ wurde übernommen.`);
      render(true);
    });
    window.api.on('blender', async ({ model, state, error, studio }) => {
      if (studio === 'mlo' || !S.model || !S.model.items.some((i) => i.id === model) && studio !== 'clothing') return;
      if (state === 'applied') {
        S.dirty = true; changed(); S.blender[model] = 'open';
        toast('✓ Aus Blender übernommen.');
        await reanalyze(); render(true);
      } else if (state === 'error') toast(`Blender: ${error}`, 'error');
      else if (state === 'closed') { delete S.blender[model]; if (App.view === 'clothing') render(true); }
    });
  }

  function changed() { cache.clear(); S.previewVersion++; }

  async function show() {
    await call('studio:set', { studio: 'clothing' }, { quiet: true });
    renderAll();
  }

  async function pickAndOpen(kind, add) {
    const paths = await call('dialog:open', { kind });
    if (paths && paths.length) await open(paths, add);
  }

  function addDialog() {
    modal({
      title: 'Weitere Kleidung hinzufügen',
      body: h('p.muted', 'Dateien kommen zur aktuellen Auswahl dazu.'),
      buttons: [
        { label: '👕 Aus GTA-Kleidung', onClick: () => gtaBrowser() },
        { label: '🌐 Vom Server (SFTP)', onClick: () => Sftp.importTo(ClothingStudio, true) },
        { label: '📁 Ordner importieren', onClick: () => pickAndOpen('folder', true) },
        { label: '📄 Dateien öffnen', primary: true, onClick: () => pickAndOpen('files', true) },
      ],
    });
  }

  async function open(paths, add) {
    if (!add && S.model && S.dirty && !await UI.confirm('Neu öffnen?', 'Nicht exportierte Änderungen gehen verloren. Fortfahren?', 'Öffnen', { danger: true })) return;
    const m = await call('clothing:open', { paths, add }, { busyText: 'Kleidung wird gelesen …' });
    if (!m) return;
    if (!m.items.length) { toast('In den Dateien wurde keine Freemode-Kleidung gefunden (Namen wie jbib_000_u.ydd).', 'warn', 9000); if (!add) return; }
    S.model = m;
    if (!add) { S.dirty = false; S.sel = null; S.variant = {}; }
    changed();
    if (!S.sel) S.sel = m.items.length ? { type: 'item', id: m.items[0].id } : { type: 'overview' };
    toast(`${m.items.length} Kleidungsstück(e) geladen.`);
    renderAll();
  }

  async function reanalyze() {
    const m = await call('clothing:analyze');
    if (m) S.model = m;
  }

  async function closeAll() {
    if (S.dirty && !await UI.confirm('Schließen?', 'Nicht exportierte Änderungen gehen verloren. Wirklich schließen?', 'Schließen', { danger: true })) return;
    await call('session:close');
    Object.assign(S, { model: null, sel: null, variant: {}, dirty: false });
    renderAll();
  }

  // ================================================================ Rendering

  const genderName = (g) => (g === 'f' ? 'Weiblich' : g === 'm' ? 'Männlich' : 'Geschlecht unbekannt');
  const itemTitle = (it) => `${it.label} ${String(it.index).padStart(3, '0')}`;
  const selItem = () => S.model && S.sel && S.sel.type === 'item' ? S.model.items.find((i) => i.id === S.sel.id) : null;

  function renderAll() {
    const loaded = !!S.model;
    $('cs-empty').classList.toggle('hidden', loaded);
    $('cs-main').classList.toggle('hidden', !loaded);
    $('cs-export').disabled = !loaded || !S.model.items.length;
    updateStatus();
    if (!loaded) return;
    renderTree();
    render();
  }

  function updateStatus() {
    if (!S.model) { $('cs-status-text').textContent = 'Öffne Kleidung oder wähle ein Teil aus der GTA-Kleidung.'; return; }
    $('cs-status-text').textContent = `${S.model.items.length} Kleidungsstücke · ${S.model.addonCount} Addon · ${S.model.replaceCount} Replace${S.dirty ? ' · ● Änderungen nicht exportiert' : ''}`;
  }

  function select(sel) { S.sel = sel; S.offset = { x: 0, y: 0, z: 0, s: 1 }; renderTree(); render(); }

  function renderTree() {
    const tree = $('cs-tree');
    tree.innerHTML = '';
    const isSel = (id) => S.sel && S.sel.type === 'item' && S.sel.id === id;
    tree.append(h(`div.tree-item${S.sel && S.sel.type === 'overview' ? '.active' : ''}`, { onclick: () => select({ type: 'overview' }) }, h('span.ico', '📋'), h('span.name', 'Übersicht')));
    const byGender = {};
    for (const it of S.model.items) (byGender[it.gender] = byGender[it.gender] || []).push(it);
    for (const g of ['m', 'f', '?']) {
      const list = byGender[g];
      if (!list) continue;
      tree.append(h('div.tree-label', `${g === 'f' ? '👩' : g === 'm' ? '👨' : '❔'} ${genderName(g)}`));
      const groups = {};
      for (const it of list) (groups[it.coll || ''] = groups[it.coll || ''] || []).push(it);
      for (const [coll, items] of Object.entries(groups)) {
        tree.append(h('div.tree-sub-label', coll ? `Addon „${coll}“` : 'Replace (ersetzt GTA-Kleidung)'));
        for (const it of items) {
          tree.append(h(`div.tree-item${isSel(it.id) ? '.active' : ''}`, { onclick: () => select({ type: 'item', id: it.id }), title: base(it.file) },
            h('span.ico', it.prop ? '🎩' : '👕'), h('span.name', itemTitle(it)), h('span.pill', `${it.textures.length}`),
            it.encrypted ? h('span.lock', '🔒') : null));
        }
      }
    }
  }

  async function render(keepScroll) {
    const c = $('cs-content');
    const scroll = c.scrollTop;
    c.innerHTML = '';
    const it = selItem();
    c.append(it ? await viewItem(it) : viewOverview());
    if (keepScroll) c.scrollTop = scroll;
    const v = c.querySelector('.viewer');
    if (v) attachViewer(v);
  }

  function action(icon, title, text, onClick, disabled) {
    return h('button.action', { onclick: onClick, disabled: disabled || null }, h('span.a-ico', icon), h('span', h('div.a-title', title), h('div.a-text', text)));
  }

  // ---------------------------------------------------------------- Übersicht

  function viewOverview() {
    const m = S.model;
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', 'Übersicht'), h('div.subtitle', `${m.items.length} Kleidungsstücke`))),
      m.warnings.length ? h('div.notice.warn', h('b', 'Hinweise'), h('ul', m.warnings.slice(0, 12).map((w) => h('li', w)))) : null,
      h('div.notice.info',
        h('b', 'Addon oder Replace? '),
        'Addon-Kleidung kommt zusätzlich ins Spiel (eigene Sammlung). Replace-Kleidung ersetzt ein vorhandenes GTA-Kleidungsstück mit derselben Nummer.'),
      h('div.actions',
        action('👕', 'Aus GTA-Kleidung übernehmen', 'Original-Teil als Grundlage für eigene Texturen', () => gtaBrowser()),
        action('📦', 'Als FiveM-Resource exportieren', 'Fertiges Script inkl. YMT und Meta', exportDialog)),
      m.collections.length ? h('div.section', h('div.section-head', h('h3', 'Addon-Sammlungen')),
        h('div.list', m.collections.map((c) => h('div.list-row', h('span', c.ped.startsWith('mp_f') ? '👩' : '👨'),
          h('div.grow', h('div.name', c.name), h('div.muted.small', `${c.ped} · ${c.items} Teile`)))))) : null);
  }

  // ---------------------------------------------------------------- Kleidungsstück

  async function viewItem(it) {
    const variantFile = S.variant[it.id] && it.textures.some((t) => t.file === S.variant[it.id]) ? S.variant[it.id] : (it.textures[0] && it.textures[0].file);
    S.variant[it.id] = variantFile;
    const bState = S.blender[it.id];
    const wrap = h('div',
      h('div.content-head', h('div.title-block',
        h('h1', itemTitle(it)),
        h('div.subtitle', `${genderName(it.gender)} · ${it.coll ? `Addon-Sammlung „${it.coll}“` : `Replace – ersetzt GTA ${it.label} Nr. ${it.index}`} · ${base(it.file)}`))));
    if (it.encrypted) { wrap.append(h('div.notice.warn', 'Dieses Modell ist verschlüsselt (Escrow) und kann weder angezeigt noch bearbeitet werden.')); return wrap; }
    if (it.gender === '?') wrap.append(h('div.notice.warn', h('b', 'Geschlecht unbekannt. '), 'Bitte festlegen, damit das Teil auf dem richtigen Körper angezeigt und exportiert wird: ',
      h('button.btn.small', { onclick: () => setGender(it, 'm') }, '👨 Männlich'), ' ', h('button.btn.small', { onclick: () => setGender(it, 'f') }, '👩 Weiblich')));
    if (bState === 'open') wrap.append(h('div.notice.info', 'In Blender geöffnet. Nach dem Bearbeiten in Blender rechts (Taste N) → „GTA Tool“ → „Speichern & zurück zum GTA Tool“ – das Modell wird automatisch übernommen.'));

    // 3D + Variantenwahl
    const chooser = h('div.livery-chooser',
      h('span.muted.small', 'Textur:'),
      it.textures.map((t) => h(`button.btn.small${t.file === variantFile ? '.primary' : ''}`, {
        onclick: () => { S.variant[it.id] = t.file; render(true); },
      }, `Variante ${t.letter.toUpperCase()}`)),
      h('span.spacer'),
      h('label.check.small', h('input', { type: 'checkbox', checked: S.showBody || null, onchange: (e) => { S.showBody = e.target.checked; render(true); } }), ' Körper anzeigen'));
    wrap.append(chooser);
    const viewer = h('div.viewer.tall', h('div.viewer-status', '3D wird erstellt …'),
      h('div.viewer-bar', h('button.btn.small', { onclick: () => window.Viewer3D && window.Viewer3D.fitAll() }, '⟲ Ansicht'),
        h('span.viewer-hint', 'Linke Maustaste: drehen · Rechte: verschieben · Mausrad: zoomen')));
    viewer._item = it; viewer._variant = variantFile;
    wrap.append(viewer);

    // Position
    wrap.append(positionPanel(it));

    // Aktionen
    wrap.append(h('div.actions',
      action('🧊', 'In Blender bearbeiten', 'Mit Skelett und Körper als Referenz', () => openBlender(it, variantFile), it.gender === '?' || bState === 'open'),
      action('⚧', it.gender === 'f' ? 'Für Männer umwandeln' : 'Für Frauen umwandeln', 'Passt das Teil an den anderen Körper an', () => convertGender(it), it.gender === '?'),
      it.coll
        ? action('🔁', 'In Replace umwandeln', 'Ersetzt ein GTA-Kleidungsstück', () => toReplaceDialog(it), it.gender === '?')
        : action('🔁', 'In Addon umwandeln', 'Eigene Sammlung statt GTA-Teil ersetzen', () => toAddonDialog([it.id], it.gender), it.gender === '?'),
      action('🗑', 'Löschen', 'Modell und Texturen entfernen', async () => {
        if (!await UI.confirm('Löschen?', `${itemTitle(it)} mit allen Texturen entfernen?`, 'Löschen', { danger: true })) return;
        const r = await call('clothing:delete', { id: it.id }, { busyText: 'Wird gelöscht …' });
        if (r) { S.model = r.model; S.dirty = true; changed(); S.sel = { type: 'overview' }; renderAll(); }
      })));

    // Texturen
    wrap.append(await texturesSection(it, variantFile));
    return wrap;
  }

  function positionPanel(it) {
    const o = S.offset;
    const val = h('span.muted.small');
    const show = () => { val.textContent = `X ${(o.x * 100).toFixed(1)} cm · Y ${(o.y * 100).toFixed(1)} cm · Z ${(o.z * 100).toFixed(1)} cm · Größe ${(o.s * 100).toFixed(1)} %`; };
    const live = () => { show(); if (window.Viewer3D) window.Viewer3D.setMainOffset(o.x, o.y, o.z, o.s); };
    const slider = (label, key, min, max, step, factor) => h('label.pos-row', h('span', label),
      h('input', {
        type: 'range', min, max, step, value: o[key] * factor,
        oninput: (e) => { o[key] = Number(e.target.value) / factor; live(); },
      }));
    show();
    return h('details.mount-panel', { open: S.posOpen || null, ontoggle: (e) => { S.posOpen = e.target.open; } },
      h('summary', '📐 Position und Größe auf dem Charakter anpassen'),
      h('p.muted.small', 'Verschieben und skalieren mit Live-Vorschau. Erst „Übernehmen“ ändert das Modell (vorher wird ein Backup angelegt).'),
      h('div.pos-grid',
        slider('← Links / Rechts →', 'x', -5, 5, 0.1, 100),
        slider('↓ Hinten / Vorne ↑', 'y', -5, 5, 0.1, 100),
        slider('Runter / Hoch', 'z', -5, 5, 0.1, 100),
        slider('Größe', 's', 90, 110, 0.1, 100)),
      val,
      h('div.row.gap-s', { style: { marginTop: '8px' } },
        h('button.btn.primary', {
          onclick: async () => {
            if (o.x === 0 && o.y === 0 && o.z === 0 && o.s === 1) { toast('Keine Änderung eingestellt.', 'info'); return; }
            const r = await call('clothing:transform', { id: it.id, dx: o.x, dy: o.y, dz: o.z, scale: o.s }, { busyText: 'Modell wird angepasst …' });
            if (r) { S.dirty = true; changed(); S.offset = { x: 0, y: 0, z: 0, s: 1 }; toast('✓ Position übernommen.'); render(true); }
          },
        }, 'Übernehmen'),
        h('button.btn', { onclick: () => { S.offset = { x: 0, y: 0, z: 0, s: 1 }; render(true); } }, 'Zurücksetzen')));
  }

  async function texturesSection(it, variantFile) {
    const sec = h('div.section', h('div.section-head', h('h3', `Texturen (${it.textures.length} Varianten)`)));
    sec.append(h('div.tex-toolbar',
      h('button.btn', {
        onclick: async () => {
          const imgs = await call('dialog:open', { kind: 'images' });
          if (!imgs || !imgs.length) return;
          const r = await call('clothing:addVariant', { id: it.id, images: imgs }, { busyText: 'Neue Variante wird erstellt …' });
          if (!r) return;
          S.model = r.model; S.dirty = true; changed();
          toast(`${r.added.length} neue Variante(n) hinzugefügt.`);
          if (r.warning) toast(r.warning, 'warn', 10000);
          renderAll();
        },
      }, '⬆ Textur importieren (neue Variante)'),
      h('button.btn', { onclick: () => call('clothing:uv', { file: it.file, ytd: variantFile, mode: 'edit' }, { busyText: 'UV-Vorlage wird erstellt …' }) }, '📐 UV-Vorlage in Paint.NET'),
      h('button.btn', {
        onclick: async () => { const r = await call('clothing:uv', { file: it.file, ytd: null, mode: 'save' }, { busyText: 'UV-Vorlage wird erstellt …' }); if (r) toast(`Gespeichert: ${r.file}`); },
      }, '💾 UV-Vorlage speichern'),
      h('button.btn', {
        onclick: async () => {
          for (const t of it.textures) await call('tex:export', { file: t.file, names: [], format: 'png' }, { busyText: 'Wird exportiert …' });
        },
      }, '⬇ Alle exportieren')));
    const grid = h('div.tex-grid');
    for (const t of it.textures) {
      let info = S.texCache[t.file];
      if (!info) { info = await call('tex:list', { file: t.file }, { quiet: true }); if (info) S.texCache[t.file] = info; }
      const tex = info && info.textures.find((x) => !/_n$|_s$/.test(x.name)) || (info && info.textures[0]);
      grid.append(h(`div.tex${t.file === variantFile ? '.selected' : ''}`,
        h('div.tex-img', { onclick: () => { S.variant[it.id] = t.file; render(true); }, title: 'Auf dem Modell anzeigen' },
          tex && tex.thumb ? h('img', { src: tex.thumb }) : h('span.muted.small', 'keine Vorschau')),
        h('div.tex-info', h('div.tex-name', `Variante ${t.letter.toUpperCase()}`), h('div.tex-meta', tex ? `${tex.width}×${tex.height} · ${tex.format}` : base(t.file))),
        h('div.tex-btns.wrap',
          h('button.btn', { title: 'In Paint.NET öffnen – speichern übernimmt automatisch', disabled: !tex || null, onclick: () => editTex(t, tex) }, 'Ersetzen'),
          h('button.btn', {
            title: 'Durch eine Bilddatei ersetzen', disabled: !tex || null,
            onclick: async () => {
              const p = await call('dialog:open', { kind: 'image' });
              if (!p || !p[0]) return;
              const r = await call('tex:replace', { file: t.file, name: tex.name, image: p[0] }, { busyText: 'Wird ersetzt …' });
              if (r) { S.dirty = true; changed(); delete S.texCache[t.file]; toast('Textur ersetzt.'); render(true); }
            },
          }, 'Datei …'),
          h('button.btn', { onclick: async () => { const r = await call('tex:export', { file: t.file, names: [], format: 'png' }, { busyText: 'Wird exportiert …' }); if (r) toast(`Exportiert nach ${r.outDir}`); } }, 'Export'),
          h('button.btn.danger', {
            disabled: it.textures.length < 2 || null,
            onclick: async () => {
              if (!await UI.confirm('Variante löschen?', `Variante ${t.letter.toUpperCase()} entfernen? Die folgenden rücken nach.`, 'Löschen', { danger: true })) return;
              const r = await call('clothing:removeVariant', { id: it.id, file: t.file }, { busyText: 'Wird gelöscht …' });
              if (r) { S.model = r.model; S.dirty = true; changed(); S.texCache = {}; renderAll(); }
            },
          }, 'Löschen'))));
    }
    sec.append(grid);
    return sec;
  }

  async function editTex(t, tex) {
    const r = await call('tex:edit', { file: t.file, name: tex.name }, { busyText: 'Wird in Paint.NET geöffnet …' });
    if (r) toast('In Paint.NET geöffnet. Speichern (Strg+S) übernimmt die Textur automatisch.', 'info', 8000);
  }

  // ================================================================ 3D

  const PROP_BONES = { p_head: 'SKEL_Head', p_eyes: 'SKEL_Head', p_ears: 'SKEL_Head', p_mouth: 'SKEL_Head', p_lwrist: 'SKEL_L_Hand', p_rwrist: 'SKEL_R_Hand', p_lhand: 'SKEL_L_Hand', p_rhand: 'SKEL_R_Hand', p_lhip: 'SKEL_Pelvis', p_lfoot: 'SKEL_L_Foot', p_rfoot: 'SKEL_R_Foot' };
  let token = 0;

  function glb(spec) {
    const key = JSON.stringify(spec) + '|' + S.previewVersion;
    if (!cache.has(key)) {
      cache.set(key, window.api.invoke('model:preview', spec).then((r) => { if (!r.ok) { cache.delete(key); throw new Error(r.error); } return r.data; }));
    }
    return cache.get(key);
  }

  async function ensureBody() {
    if (S.body) return S.body;
    S.body = await call('clothing:base', {}, { busyText: 'Freemode-Körper wird aus GTA geladen (einmalig) …', quiet: false });
    return S.body;
  }

  async function attachViewer(el) {
    const it = el._item;
    const my = ++token;
    if (!window.Viewer3D) await new Promise((r) => window.addEventListener('viewer3d-ready', r, { once: true }));
    const V = window.Viewer3D;
    V.mount(el);
    V.setGround('all');
    V.setMainOffset(0, 0, 0, 1);
    const status = el.querySelector('.viewer-status');
    try {
      const body = it.gender === '?' ? null : await ensureBody();
      const ped = it.gender === 'f' ? 'mp_f_freemode_01' : 'mp_m_freemode_01';
      const bodyInfo = body && body[ped];
      const spec = { file: it.file, ytds: el._variant ? [el._variant] : [], variant: true };
      if (it.prop && bodyInfo) { spec.attach = bodyInfo.skeleton; spec.bone = PROP_BONES[it.comp] || 'SKEL_Head'; }
      const parts = S.showBody && bodyInfo ? bodyInfo.parts.filter((p) => p.comp !== it.comp) : [];
      const [m, ...ex] = await Promise.all([glb(spec), ...parts.map((p) => glb({ file: p.model, ytds: p.ytd ? [p.ytd] : [] }))]);
      if (my !== token) return;
      const sameItem = S.lastViewItem === it.id;
      await V.load(m.glb, { keepCamera: true });
      await V.setExtras(ex.map((x) => x.glb));
      if (!sameItem) V.fitAll();
      S.lastViewItem = it.id;
      V.setMainOffset(S.offset.x, S.offset.y, S.offset.z, S.offset.s);
      status.textContent = `${m.triangles.toLocaleString('de-DE')} Dreiecke${parts.length ? ' · auf dem Freemode-Körper' : ''}`;
    } catch (e) {
      if (my !== token) return;
      status.textContent = `3D-Ansicht nicht möglich: ${e.message}`;
      status.classList.add('err');
    }
  }

  // ================================================================ Aktionen

  async function setGender(it, g) {
    const r = await call('clothing:setGender', { id: it.id, gender: g }, { busyText: 'Wird zugeordnet …' });
    if (r) { S.model = r.model; S.sel = { type: 'item', id: r.id }; S.dirty = true; changed(); renderAll(); }
  }

  async function openBlender(it, ytd) {
    const r = await call('clothing:blender', { id: it.id, ytd }, { busyText: 'Blender wird vorbereitet …' });
    if (!r) return;
    S.blender[it.id] = 'open';
    toast('Blender startet mit Kleidung, Skelett und Körper-Referenz. Zum Übernehmen: Seitenleiste (N) → „GTA Tool“ → „Speichern & zurück“.', 'info', 10000);
    render(true);
  }

  async function convertGender(it) {
    const target = it.gender === 'f' ? 'männlichen' : 'weiblichen';
    if (!await UI.confirm('Umwandeln', `Das Teil wird automatisch an den ${target} Freemode-Körper angepasst (Skelett, Körperform, Abstand zur Haut) und als neues Kleidungsstück angelegt. Das Original bleibt erhalten. Bei sehr eng anliegender Kleidung kann Nacharbeit in Blender nötig sein.`, 'Umwandeln')) return;
    const r = await call('clothing:gender', { id: it.id }, { busyText: 'Wird umgewandelt (Blender arbeitet im Hintergrund) …' });
    if (!r) return;
    S.model = r.model; S.dirty = true; changed();
    S.sel = { type: 'item', id: r.id };
    renderAll();
    toast(`✓ Umgewandelt. Verschiebung im Mittel ${r.report.meanShiftCm} cm, maximal ${r.report.maxShiftCm} cm.`, 'ok', 9000);
    for (const w of r.report.warnings || []) toast(w, 'warn', 9000);
  }

  function toAddonDialog(ids, gender) {
    const nameIn = h('input', { type: 'text', placeholder: 'z. B. meine_kleidung' });
    const existing = (S.model.collections || []).filter((c) => (gender === 'f') === c.ped.startsWith('mp_f'));
    modal({
      title: 'In Addon-Kleidung umwandeln',
      body: h('div',
        h('p.muted', 'Das Teil kommt in eine eigene Sammlung und ersetzt kein GTA-Kleidungsstück mehr. Die Nummer wird automatisch vergeben.'),
        h('div.field', h('label', 'Name der Sammlung'), nameIn,
          h('div.hint', `Wird automatisch zu „${gender === 'f' ? 'mp_f_' : 'mp_m_'}name“.`)),
        existing.length ? h('div.row.gap-s.wrap', h('span.muted.small', 'Vorhanden:'), existing.map((c) => h('button.btn.small', { onclick: () => { nameIn.value = c.name; } }, c.name))) : null),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: 'Umwandeln', primary: true,
          onClick: async () => {
            if (!nameIn.value.trim()) { toast('Bitte einen Namen eingeben.', 'warn'); return true; }
            const r = await call('clothing:toAddon', { ids, collection: nameIn.value.trim() }, { busyText: 'Wird umgewandelt …' });
            if (!r) return true;
            S.model = r.model; S.dirty = true; changed();
            S.sel = { type: 'item', id: r.moved[0] };
            renderAll();
            toast('✓ In Addon umgewandelt.');
            return false;
          },
        },
      ],
    });
  }

  function toReplaceDialog(it) {
    const idxIn = h('input', { type: 'text', value: String(it.index) });
    modal({
      title: 'In Replace umwandeln',
      body: h('div',
        h('p.muted', `Das Teil ersetzt dann im Spiel das GTA-Kleidungsstück „${it.label}“ mit der gewählten Nummer (${genderName(it.gender)}).`),
        h('div.field', h('label', 'Nummer des GTA-Teils, das ersetzt wird'), idxIn,
          h('div.hint', 'Die Nummer findest du im Kleidungsmenü des Spiels bzw. über „Aus GTA-Kleidung übernehmen“. Hat das Original weniger Texturen, werden überzählige Varianten im Spiel nicht angezeigt.'))),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: 'Umwandeln', primary: true,
          onClick: async () => {
            const n = parseInt(idxIn.value, 10);
            if (!(n >= 0)) { toast('Bitte eine gültige Nummer eingeben.', 'warn'); return true; }
            const r = await call('clothing:toReplace', { id: it.id, index: n }, { busyText: 'Wird umgewandelt …' });
            if (!r) return true;
            S.model = r.model; S.dirty = true; changed();
            S.sel = { type: 'item', id: r.moved[0] };
            renderAll();
            toast('✓ In Replace umgewandelt.');
            return false;
          },
        },
      ],
    });
  }

  // ---------------------------------------------------------------- GTA-Kleidung

  async function gtaBrowser() {
    let st = await call('clothing:catalogStatus');
    if (!st) return;
    if (!st.built) {
      if (!await UI.confirm('GTA-Kleidung einlesen', 'Das Tool liest einmalig die Liste aller Original-Kleidungsstücke aus deiner GTA-Installation (wenige Sekunden).', 'Einlesen')) return;
      const r = await call('clothing:catalogBuild', {}, { busyText: 'GTA-Kleidung wird gelesen …' });
      if (!r) return;
      toast(`${r.count} Original-Kleidungsstücke gefunden.`);
    }
    const st2 = { ped: 'mp_m_freemode_01', comp: 'jbib', coll: '', sel: null };
    const pedSel = h('select', { onchange: (e) => { st2.ped = e.target.value; st2.coll = ''; loadList(); } },
      h('option', { value: 'mp_m_freemode_01' }, '👨 Männlich'), h('option', { value: 'mp_f_freemode_01' }, '👩 Weiblich'));
    const compSel = h('select', { onchange: (e) => { st2.comp = e.target.value; st2.coll = ''; loadList(); } },
      ['jbib', 'uppr', 'lowr', 'feet', 'accs', 'task', 'teef', 'berd', 'hand', 'decl', 'hair', 'p_head', 'p_eyes', 'p_ears', 'p_lwrist', 'p_rwrist']
        .map((c) => h('option', { value: c }, LABELS[c] || c)));
    const collSel = h('select', { onchange: (e) => { st2.coll = e.target.value; loadList(); } });
    const list = h('div.gta-list');
    const viewerBox = h('div.viewer.small', h('div.viewer-status', 'Teil links auswählen'));
    const nameIn = h('input', { type: 'text', placeholder: 'Name der Sammlung, z. B. meine_jacken' });
    const keep = h('input', { type: 'checkbox', checked: true });

    async function loadList() {
      const r = await call('clothing:browse', { ped: st2.ped, comp: st2.comp, coll: st2.coll || '' });
      if (!r) return;
      collSel.innerHTML = '';
      for (const c of r.collections) collSel.append(h('option', { value: c, selected: c === st2.coll || null }, c === '' ? 'Basisspiel' : c));
      list.innerHTML = '';
      for (const x of r.items) {
        list.append(h('div.list-row.clickable', { onclick: (e) => { list.querySelectorAll('.list-row').forEach((n) => n.classList.remove('active')); e.currentTarget.classList.add('active'); preview(x); } },
          h('span', '👕'), h('div.grow', h('div.name', `Nr. ${String(x.index).padStart(3, '0')}`), h('div.muted.small', `${x.textures} Texturen`))));
      }
      if (!r.items.length) list.append(h('p.muted', 'Keine Teile.'));
    }

    async function preview(x) {
      st2.sel = x;
      const r = await call('clothing:gtaPreview', { id: x.id }, { busyText: 'Wird aus GTA geladen …' });
      if (!r) return;
      const V = window.Viewer3D;
      V.mount(viewerBox); V.setGround('all'); V.setMainOffset(0, 0, 0, 1);
      try {
        const body = await ensureBody();
        const b = body[r.ped];
        const spec = { file: r.model, ytds: r.textures.slice(0, 1), variant: true };
        if (r.prop) { spec.attach = b.skeleton; spec.bone = PROP_BONES[r.comp] || 'SKEL_Head'; }
        const parts = b.parts.filter((p) => p.comp !== r.comp);
        const [m, ...ex] = await Promise.all([glb(spec), ...parts.map((p) => glb({ file: p.model, ytds: p.ytd ? [p.ytd] : [] }))]);
        await V.load(m.glb, { keepCamera: true });
        await V.setExtras(ex.map((y) => y.glb));
        V.fitAll();
        viewerBox.querySelector('.viewer-status').textContent = `Nr. ${x.index} · ${x.textures} Texturen`;
      } catch (e) { viewerBox.querySelector('.viewer-status').textContent = e.message; }
    }

    modal({
      title: 'Aus GTA-Kleidung übernehmen',
      wide: true,
      body: h('div',
        h('p.muted', 'Wähle ein Original-Kleidungsstück. Es wird als Addon in deine Sammlung kopiert – danach kannst du eigene Texturen hinzufügen oder die vorhandenen ändern. Das Original im Spiel bleibt unverändert.'),
        h('div.row.gap.wrap', pedSel, compSel, collSel),
        h('div.gta-browser', list, viewerBox),
        h('div.field', h('label', 'In Sammlung übernehmen'), nameIn),
        h('label.check', keep, ' Original-Texturen mitnehmen (sonst nur das Modell – dann eigene Textur importieren)')),
      buttons: [
        { label: 'Schließen' },
        {
          label: 'Übernehmen', primary: true,
          onClick: async () => {
            if (!st2.sel) { toast('Bitte zuerst ein Teil auswählen.', 'warn'); return true; }
            if (!nameIn.value.trim()) { toast('Bitte einen Namen für die Sammlung eingeben.', 'warn'); return true; }
            const r = await call('clothing:addFromGta', { catalogId: st2.sel.id, collection: nameIn.value.trim(), keepTextures: keep.checked }, { busyText: 'Wird übernommen …' });
            if (!r) return true;
            S.model = r.model; S.dirty = true; changed();
            S.sel = { type: 'item', id: r.id };
            renderAll();
            toast(`✓ Übernommen (${r.textures} Texturen). Jetzt eigene Texturen hinzufügen oder ändern.`, 'ok', 8000);
            return false;
          },
        },
      ],
      onClose: () => { if (S.model && App.view === 'clothing') render(true); },
    });
    if (!S.model) { await call('clothing:new'); }
    loadList();
  }

  // ---------------------------------------------------------------- Export

  async function exportDialog() {
    if (!S.model) return;
    const settings = await call('settings:get');
    const defName = (S.model.collections[0] && S.model.collections[0].name.replace(/^mp_[mf]_/, '')) || 'kleidung';
    const nameIn = h('input', { type: 'text', value: defName });
    const dirIn = h('input', { type: 'text', value: (settings && (settings.lastResourceDir || settings.lastExportDir)) || '', placeholder: 'Bitte Ordner wählen …' });
    const localField = h('div.field', h('label', 'Zielordner'), h('div.input-row', dirIn, h('button.btn', {
      onclick: async () => { const r = await call('dialog:open', { kind: 'outdir' }); if (r && r[0]) dirIn.value = r[0]; },
    }, 'Durchsuchen …')));
    const target = Sftp.targetField(localField, dirIn);
    modal({
      title: 'Kleidung als FiveM-Resource exportieren',
      body: h('div',
        h('p.muted', 'Erstellt eine fertige Resource: stream-Ordner mit Modellen/Texturen, YMT und Shop-Meta je Sammlung, fxmanifest.lua.'),
        h('div.field', h('label', 'Name der Resource'), nameIn),
        target.el,
        localField),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: '📦 Exportieren', primary: true,
          onClick: async () => {
            const resourceName = nameIn.value.trim();
            const r = await Sftp.runExport(target.get(), resourceName,
              (outDir) => call('clothing:export', { outDir, resourceName }, { busyText: 'Resource wird erstellt …' }));
            if (!r) return true;
            S.model = r.model; S.dirty = false; renderAll();
            modal({
              title: '✓ Export fertig',
              body: h('div', Sftp.resultPath(r),
                h('p', `${r.items} Kleidungsstücke · ${r.collections} Addon-Sammlung(en)`),
                r.warnings.length ? h('div.notice.warn', h('ul', r.warnings.map((w) => h('li', w)))) : null,
                h('div.notice.info', r.remote ? 'server.cfg: ' : 'Ordner nach „resources“ kopieren → server.cfg: ', h('code', `ensure ${base(r.folder)}`))),
              buttons: Sftp.resultButtons(r),
            });
            return false;
          },
        },
      ],
    });
  }

  const LABELS = {
    head: 'Kopf', berd: 'Maske / Bart', hair: 'Haare', uppr: 'Arme / Oberkörper', lowr: 'Hose', hand: 'Tasche', feet: 'Schuhe',
    teef: 'Accessoire / Kette', accs: 'Unterhemd', task: 'Weste', decl: 'Aufdruck', jbib: 'Oberteil / Jacke',
    p_head: 'Hut / Helm', p_eyes: 'Brille', p_ears: 'Ohrring', p_lwrist: 'Uhr', p_rwrist: 'Armband',
  };

  return { init, show, open, get dirty() { return S.dirty; } };
})();
