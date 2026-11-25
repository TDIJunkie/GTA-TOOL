'use strict';
// Prop Studio: eigene Objekte öffnen, 3D, Texturen, Kollision, Blender, Archetypen (YTYP), GTA-Vorlagen, Export

const PropStudio = (() => {
  const { h, call, toast, modal, kb, base } = UI;

  const S = {
    model: null,       // Ergebnis von prop.analyze
    sel: null,         // { type: 'overview' } | { type: 'prop', name } | { type: 'file', file }
    dirty: false,
    blender: {},
    texCache: {},
    details: {},
    view: 'model',     // 'model' | 'collision'
  };
  const $ = (id) => document.getElementById(id);

  // ================================================================ Start

  function init() {
    document.querySelectorAll('[data-popen]').forEach((b) => b.addEventListener('click', () => pickAndOpen(b.dataset.popen, false)));
    $('ps-gta').addEventListener('click', () => gtaDialog());
    $('ps-new').addEventListener('click', () => newInBlender());
    $('ps-add').addEventListener('click', addDialog);
    $('ps-close').addEventListener('click', closeAll);
    $('ps-export').addEventListener('click', exportDialog);
    const view = $('view-prop');
    const dz = $('pdropzone');
    view.addEventListener('dragover', (e) => { e.preventDefault(); dz.classList.add('over'); });
    view.addEventListener('dragleave', (e) => { if (!view.contains(e.relatedTarget)) dz.classList.remove('over'); });
    view.addEventListener('drop', (e) => {
      e.preventDefault(); dz.classList.remove('over');
      const paths = [...e.dataTransfer.files].map((f) => window.api.pathForFile(f)).filter(Boolean);
      if (paths.length) open(paths, !!S.model);
    });
    window.api.on('tex:updated', ({ file, name }) => {
      if (App.view !== 'prop' || !S.model || !ownsFile(file)) return;
      S.dirty = true; delete S.texCache[file]; updateStatus();
      toast(`Textur „${name}“ wurde übernommen.`);
      render(true);
    });
    window.api.on('blender', async ({ model, state, error, studio, name }) => {
      if (studio !== 'prop') return;
      if (state === 'applied') {
        S.dirty = true; S.blender[model] = 'open'; S.texCache = {}; S.details = {};
        toast(name ? `✓ „${name}“ aus Blender übernommen.` : '✓ Aus Blender übernommen.');
        await reanalyze();
        if (name) { S.sel = { type: 'prop', name }; S.view = 'model'; }
        if (App.view === 'prop') renderAll(true);
      } else if (state === 'error') toast(`Blender: ${error}`, 'error', 10000);
      else if (state === 'closed') { delete S.blender[model]; if (App.view === 'prop') render(true); }
    });
  }

  const ownsFile = (file) => S.model.props.some((p) => p.file === file || p.ytds.includes(file)) || S.model.ytds.some((y) => y.file === file);

  async function show() {
    await call('studio:set', { studio: 'prop' }, { quiet: true });
    renderAll();
  }

  async function pickAndOpen(kind, add) {
    const paths = await call('dialog:open', { kind });
    if (paths && paths.length) await open(paths, add);
  }

  function addDialog() {
    modal({
      title: 'Weitere Props hinzufügen',
      body: h('p.muted', 'Die Dateien kommen zu den bereits geöffneten dazu.'),
      buttons: [
        { label: '✨ Neu in Blender', onClick: () => newInBlender() },
        { label: '🪑 Aus GTA-Objekt', onClick: () => gtaDialog() },
        { label: '🌐 Vom Server (SFTP)', onClick: () => Sftp.importTo(PropStudio, true) },
        { label: '📁 Ordner importieren', onClick: () => pickAndOpen('folder', true) },
        { label: '🗜 ZIP / RAR', onClick: () => pickAndOpen('archive', true) },
        { label: '📄 Dateien öffnen', primary: true, onClick: () => pickAndOpen('propfiles', true) },
      ],
    });
  }

  async function open(paths, add) {
    if (!add && S.model && S.dirty && !await UI.confirm('Neu öffnen?', 'Nicht exportierte Änderungen gehen verloren. Fortfahren?', 'Öffnen', { danger: true })) return;
    const m = await call('prop:open', { paths, add }, { busyText: 'Props werden gelesen …' });
    if (!m) return;
    if (!m.props.length) { toast('In den Dateien wurden keine Prop-Modelle (.ydr/.yft) gefunden.', 'warn', 8000); if (!add) return; }
    S.model = m;
    if (!add) { S.dirty = false; S.sel = null; }
    S.texCache = {}; S.details = {};
    if (!S.sel || !findSel()) S.sel = m.props.length ? { type: 'prop', name: m.props[0].name } : { type: 'overview' };
    toast(`${m.props.length} Prop(s) geladen.`);
    for (const w of m.warnings.slice(0, 4)) toast(w, 'warn', 9000);
    renderAll();
  }

  async function reanalyze() { const m = await call('prop:analyze'); if (m) S.model = m; }

  async function closeAll() {
    if (S.dirty && !await UI.confirm('Schließen?', 'Nicht exportierte Änderungen gehen verloren. Wirklich schließen?', 'Schließen', { danger: true })) return;
    await call('session:close');
    Object.assign(S, { model: null, sel: null, dirty: false, texCache: {}, details: {} });
    renderAll();
  }

  /** Nach einer Änderung: neu einlesen und anzeigen */
  async function changed(sel) {
    S.dirty = true; S.texCache = {}; S.details = {};
    await reanalyze();
    if (sel) S.sel = sel;
    if (!findSel()) S.sel = S.model.props.length ? { type: 'prop', name: S.model.props[0].name } : { type: 'overview' };
    renderAll(true);
  }

  // ================================================================ Rendering

  const findProp = (name) => S.model && S.model.props.find((p) => p.name === name);
  function findSel() {
    if (!S.model || !S.sel) return null;
    if (S.sel.type === 'overview') return S.sel;
    if (S.sel.type === 'prop') return findProp(S.sel.name) ? S.sel : null;
    if (S.sel.type === 'file') return [...S.model.ytds.map((y) => y.file), ...S.model.ybns, ...S.model.ytyps].includes(S.sel.file) ? S.sel : null;
    return null;
  }
  function select(sel) { S.sel = sel; S.view = 'model'; renderTree(); render(); }

  function renderAll(keepScroll) {
    const loaded = !!S.model;
    $('ps-empty').classList.toggle('hidden', loaded);
    $('ps-main').classList.toggle('hidden', !loaded);
    $('ps-export').disabled = !loaded || !S.model.props.length;
    updateStatus();
    if (!loaded) return;
    renderTree();
    render(keepScroll);
  }

  function updateStatus() {
    if (!S.model) { $('ps-status-text').textContent = 'Öffne Props (.ydr, .yft, .ytd, .ytyp) oder nimm ein GTA-Objekt als Vorlage.'; return; }
    const miss = S.model.missingArchetypes;
    $('ps-status-text').textContent = `${S.model.props.length} Props · ${S.model.ytds.length} Texturdatei(en)${miss ? ` · ${miss} ohne Archetyp (wird beim Export angelegt)` : ''}${S.dirty ? ' · ● Änderungen nicht exportiert' : ''}`;
  }

  function renderTree() {
    const tree = $('ps-tree');
    tree.innerHTML = '';
    const sel = S.sel || {};
    const item = (icon, label, s, extra, title) => h(`div.tree-item${(s.type === sel.type && (s.name || s.file) === (sel.name || sel.file)) ? '.active' : ''}`,
      { onclick: () => select(s), title: title || '' }, h('span.ico', icon), h('span.name', label), extra || null);
    tree.append(item('📋', 'Übersicht', { type: 'overview' }));
    tree.append(h('div.tree-label', `🪑 Props (${S.model.props.length})`));
    for (const p of S.model.props) {
      tree.append(item(p.type === 'yft' ? '🧱' : '🪑', p.name, { type: 'prop', name: p.name },
        p.encrypted ? h('span.lock', '🔒') : !p.archetype ? h('span.lock', { title: 'Kein Archetyp – wird beim Export angelegt' }, '⚠') : null, base(p.file)));
    }
    if (S.model.ytds.length) {
      tree.append(h('div.tree-label', '🖼 Texturdateien'));
      for (const y of S.model.ytds) tree.append(item('🖼', base(y.file), { type: 'file', file: y.file }, y.used ? null : h('span.muted.small', 'ungenutzt')));
    }
    if (S.model.ybns.length) {
      tree.append(h('div.tree-label', '🧱 Kollisionen'));
      for (const f of S.model.ybns) tree.append(item('🧱', base(f), { type: 'file', file: f }));
    }
    if (S.model.ytyps.length) {
      tree.append(h('div.tree-label', '📜 Archetypen (YTYP)'));
      for (const f of S.model.ytyps) tree.append(item('📜', base(f), { type: 'file', file: f }));
    }
  }

  let renderSeq = 0;
  async function render(keepScroll) {
    const my = ++renderSeq;
    const c = $('ps-content');
    const scroll = c.scrollTop;
    const s = findSel();
    let node;
    if (!s || s.type === 'overview') node = viewOverview();
    else if (s.type === 'prop') node = await viewProp(findProp(s.name));
    else if (s.file.endsWith('.ytd')) node = await viewYtd(s.file);
    else if (s.file.endsWith('.ybn')) node = viewYbn(s.file);
    else node = viewYtyp(s.file);
    if (my !== renderSeq) return; // inzwischen neu aufgebaut
    c.innerHTML = '';
    c.append(node);
    c.scrollTop = keepScroll ? scroll : 0;
    const v = c.querySelector('.viewer');
    if (v && v._start) v._start(v);
  }

  function action(icon, title, text, onClick, disabled) {
    return h('button.action', { onclick: onClick, disabled: disabled || null }, h('span.a-ico', icon), h('span', h('div.a-title', title), h('div.a-text', text)));
  }

  // ---------------------------------------------------------------- Übersicht

  function viewOverview() {
    const m = S.model;
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', 'Übersicht'), h('div.subtitle', `${m.props.length} Props · ${m.ytds.length} Texturdatei(en) · ${m.ytyps.length} YTYP`))),
      m.warnings.length ? h('div.notice.warn', h('b', 'Hinweise'), h('ul', m.warnings.slice(0, 12).map((w) => h('li', w)))) : null,
      ...Object.keys(S.blender).filter((k) => k.startsWith('new:')).map((k) => h('div.notice.info',
        `✨ „${k.slice(4)}“ ist in Blender geöffnet. Modellieren, dann rechts (Taste N) → „GTA Tool“ → „Speichern & zurück zum GTA Tool“. Du kannst beliebig oft zurückschicken.`)),
      m.missingArchetypes ? h('div.notice.warn', h('b', `${m.missingArchetypes} Prop(s) ohne Archetyp. `),
        'Ohne Archetyp (Eintrag in einer .ytyp) kann ein Prop im Spiel nicht gespawnt werden. Beim Export werden fehlende Archetypen automatisch angelegt – oder jetzt: ',
        h('button.btn.small', { onclick: ensureArchetypes }, '📜 Archetypen jetzt anlegen')) : null,
      h('div.notice.info', h('b', 'So funktionieren Props: '),
        'Das Modell (.ydr oder .yft) enthält die Form, die Texturdatei (.ytd) die Bilder, und die .ytyp sagt dem Spiel, dass es das Objekt gibt (Name, Texturdatei, Sichtweite). Gespawnt wird über den Namen, z. B. ',
        h('code', "CreateObject(joaat('mein_prop'), …)"), '.'),
      h('div.actions',
        action('✨', 'Neues Prop in Blender', 'Komplett selbst modellieren', () => newInBlender()),
        action('🪑', 'Aus GTA-Objekt erstellen', 'Original-Objekt als Vorlage für eigene Texturen', () => gtaDialog()),
        action('➕', 'Weitere Props öffnen', 'Dateien, Ordner oder vom Server', addDialog),
        action('📦', 'Als FiveM-Resource exportieren', 'Fertiges Script mit Test-Befehl /spawnprop', exportDialog)),
      h('div.section', h('div.section-head', h('h3', 'Alle Props')),
        h('div.list', m.props.map((p) => h('div.list-row.clickable', { onclick: () => select({ type: 'prop', name: p.name }) },
          h('span', p.type === 'yft' ? '🧱' : '🪑'),
          h('div.grow', h('div.name', p.name), h('div.muted.small', `${base(p.file)} · ${kb(p.size)}${p.archetype ? ` · Sichtweite ${Math.round(p.archetype.lodDist)} m` : ' · ⚠ kein Archetyp'}`)),
          p.encrypted ? h('span.lock', '🔒') : null)))));
  }

  async function ensureArchetypes() {
    const r = await call('prop:ensureArchetypes', { ytypName: '' }, { busyText: 'Archetypen werden angelegt …' });
    if (!r) return;
    toast(r.added.length ? `${r.added.length} Archetyp(en) in ${base(r.ytyp)} angelegt.` : 'Alle Props haben schon einen Archetyp.');
    await changed();
  }

  // ---------------------------------------------------------------- Prop

  async function viewProp(p) {
    const bKey = p.file;
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', p.name),
      h('div.subtitle', `${p.type === 'yft' ? 'Fragment-Modell (.yft, zerstörbar/beweglich)' : 'Modell (.ydr)'} · ${base(p.file)} · ${kb(p.size)}`))));
    if (p.encrypted) { wrap.append(h('div.notice.warn', 'Dieses Modell ist verschlüsselt (FiveM-Escrow) und kann weder angezeigt noch bearbeitet werden.')); return wrap; }
    if (S.blender[bKey] === 'open') wrap.append(h('div.notice.info', 'In Blender geöffnet. Nach dem Bearbeiten in Blender rechts (Taste N) → „GTA Tool“ → „Speichern & zurück zum GTA Tool“ – das Modell wird automatisch übernommen.'));
    if (!p.archetype) wrap.append(h('div.notice.warn', h('b', 'Kein Archetyp. '), 'Wird beim Export automatisch angelegt – oder jetzt: ', h('button.btn.small', { onclick: ensureArchetypes }, '📜 Archetyp anlegen')));
    if (p.txdMissing) wrap.append(h('div.notice.warn', `Der Archetyp verweist auf die Texturdatei „${p.archetype.txd}.ytd“, die nicht im Paket ist (evtl. eine GTA-Texturdatei).`));

    // Kurzinfos
    const facts = h('div.cards');
    wrap.append(facts);
    const fillFacts = (d) => {
      facts.innerHTML = '';
      facts.append(
        h('div.card', h('div.k', 'Größe (B × T × H)'), h('div.v', d ? d.size.map((x) => `${x.toFixed(2).replace('.', ',')} m`).join(' × ') : '…')),
        h('div.card', h('div.k', 'Kollision'), h('div.v', d ? (d.collision ? '✓ eingebettet' : p.ybn ? '✓ eigene .ybn' : '✗ keine – man läuft hindurch') : '…')),
        h('div.card', h('div.k', 'Sichtweite'), h('div.v', p.archetype ? `${Math.round(p.archetype.lodDist)} m` : '–')),
        h('div.card', h('div.k', 'Texturen'), h('div.v', p.ytds.length ? p.ytds.map(base).join(', ') : d ? `${d.embeddedTextures} eingebettet` : '…')));
    };
    fillFacts(S.details[p.file]);
    if (!S.details[p.file]) call('prop:details', { file: p.file }, { quiet: true }).then((d) => { if (d) { S.details[p.file] = d; fillFacts(d); } });

    // 3D
    const hasColl = () => (S.details[p.file] && S.details[p.file].collision) || p.ybn;
    const viewer = h('div.viewer', h('div.viewer-status', '3D wird erstellt …'),
      h('div.viewer-bar',
        h(`button.btn.small${S.view === 'model' ? '.primary' : ''}`, { onclick: () => { S.view = 'model'; render(true); } }, '🪑 Modell'),
        h(`button.btn.small${S.view === 'collision' ? '.primary' : ''}`, { onclick: () => { S.view = 'collision'; render(true); } }, '🧱 Kollision'),
        h('button.btn.small', { onclick: () => window.Viewer3D && window.Viewer3D.resetView() }, '⟲ Ansicht'),
        h('span.viewer-hint', 'Drehen: linke Maustaste · Verschieben: rechte · Zoomen: Mausrad')));
    viewer._start = (el) => show3d(el, p, hasColl);
    wrap.append(viewer);

    // Code
    const code = `local model = joaat('${p.name}')\nRequestModel(model)\nwhile not HasModelLoaded(model) do Wait(0) end\nlocal coords = GetOffsetFromEntityInWorldCoords(PlayerPedId(), 0.0, 2.0, 0.0)\nlocal obj = CreateObject(model, coords.x, coords.y, coords.z, true, true, false)\nPlaceObjectOnGroundProperly(obj)`;
    wrap.append(h('details.mount-panel', { open: S.codeOpen || null, ontoggle: (e) => { S.codeOpen = e.target.open; } },
      h('summary', '🧾 Im eigenen Script benutzen (Lua)'),
      h('pre.code', code),
      h('button.btn.small', { onclick: async () => { try { await navigator.clipboard.writeText(code); toast('Code kopiert.'); } catch { toast('Kopieren nicht möglich.', 'error'); } } }, '📋 Code kopieren')));

    // Aktionen
    wrap.append(h('div.actions',
      action('🧊', 'In Blender bearbeiten', 'Modell bearbeiten und direkt zurück übernehmen', async () => {
        const r = await call('mlo:blender', { file: p.file, ytds: p.ytds, studio: 'prop' }, { busyText: 'Blender wird vorbereitet …' });
        if (r) { S.blender[bKey] = 'open'; toast('Blender startet. Zum Übernehmen: Seitenleiste (N) → „GTA Tool“ → „Speichern & zurück“.', 'info', 9000); render(true); }
      }, S.blender[bKey] === 'open'),
      action('✏', 'Umbenennen', 'Spawn-Name, Dateien und Archetyp', () => renameProp(p)),
      action('🔭', 'Sichtweite', 'Ab welcher Entfernung das Prop zu sehen ist', () => lodDialog(p), !p.archetype),
      action('💾', 'Datei exportieren', `${base(p.file)} speichern`, async () => { const r = await call('file:export', { file: p.file }); if (r) toast(`Gespeichert: ${r.file}`); }),
      action('♻', 'Modell ersetzen', 'Durch eine andere Datei gleichen Typs', async () => {
        const f = await call('dialog:open', { kind: 'propfiles' });
        if (!f || !f[0]) return;
        const r = await call('file:replace', { file: p.file, source: f[0] }, { busyText: 'Wird ersetzt …' });
        if (r) { toast('Modell ersetzt.'); await changed(); }
      }),
      action('🗑', 'Löschen', 'Modell, Archetyp und eigene Texturen', async () => {
        if (!await UI.confirm('Prop löschen?', `„${p.name}“ mit Archetyp entfernen? Texturdateien, die kein anderes Prop benutzt, werden mit gelöscht.`, 'Löschen', { danger: true })) return;
        const r = await call('prop:delete', { name: p.name }, { busyText: 'Wird gelöscht …' });
        if (r) { toast('Gelöscht.'); await changed({ type: 'overview' }); }
      })));

    // Texturen
    wrap.append(await textureSection(p.file, 'Eingebettete Texturen', true));
    for (const y of p.ytds) wrap.append(await textureSection(y, `Texturen aus ${base(y)}`));
    if (!p.ytds.length) {
      wrap.append(h('div.notice.info', 'Dieses Prop hat keine eigene Texturdatei (.ytd). ',
        h('button.btn.small', { onclick: () => toast('Tipp: Eine .ytd mit dem Namen des Props über „+ Hinzufügen“ öffnen – sie wird automatisch zugeordnet.', 'info', 9000) }, 'Wie füge ich eine hinzu?')));
    }
    return wrap;
  }

  let token = 0;
  async function show3d(el, p, hasColl) {
    const my = ++token;
    if (!window.Viewer3D) await new Promise((r) => window.addEventListener('viewer3d-ready', r, { once: true }));
    const V = window.Viewer3D;
    V.mount(el); V.setGround('main'); V.setMainOffset(0, 0, 0, 1);
    const status = el.querySelector('.viewer-status');
    try {
      let r;
      if (S.view === 'collision') {
        const src = (S.details[p.file] && S.details[p.file].collision) || !p.ybn ? p.file : p.ybn;
        r = await window.api.invoke('mlo:collision', { ybn: src });
      } else {
        r = await window.api.invoke('model:preview', { file: p.file, ytds: p.ytds });
      }
      if (my !== token) return;
      if (!r.ok) throw new Error(r.error);
      await V.load(r.data.glb, {});
      await V.setExtras([]);
      status.textContent = S.view === 'collision' ? `${r.data.triangles.toLocaleString('de-DE')} Kollisionsflächen` : `${r.data.triangles.toLocaleString('de-DE')} Dreiecke${r.data.missingTextures ? ` · ${r.data.missingTextures} Textur(en) fehlen` : ''}`;
    } catch (e) {
      if (my !== token) return;
      status.textContent = S.view === 'collision' && !hasColl() ? 'Dieses Prop hat keine Kollision.' : `3D-Ansicht nicht möglich: ${e.message}`;
      status.classList.add('err');
    }
  }

  // ---------------------------------------------------------------- Texturen

  async function textureSection(file, title, embedded) {
    let info = S.texCache[file];
    if (!info) { info = await call('tex:list', { file }, { quiet: true }); if (info) S.texCache[file] = info; }
    const sec = h('div.section', h('div.section-head', h('h3', `${title} (${info ? info.textures.length : 0})`)));
    if (!info) return sec;
    if (!info.textures.length) { if (embedded) return h('div'); sec.append(h('p.muted', 'Keine.')); return sec; }
    const refresh = () => { S.dirty = true; delete S.texCache[file]; updateStatus(); render(true); };
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
          h('button.btn', { title: 'In Paint.NET öffnen – speichern übernimmt automatisch', onclick: async () => { const r = await call('tex:edit', { file, name: t.name }, { busyText: 'Wird geöffnet …' }); if (r) toast('In Paint.NET geöffnet – Speichern übernimmt automatisch.', 'info', 7000); } }, 'Bearbeiten'),
          h('button.btn', {
            title: 'Durch eine Bilddatei ersetzen',
            onclick: async () => {
              const pth = await call('dialog:open', { kind: 'image' });
              if (!pth || !pth[0]) return;
              const r = await call('tex:replace', { file, name: t.name, image: pth[0] }, { busyText: 'Wird ersetzt …' });
              if (r) { toast('Ersetzt.'); refresh(); }
            },
          }, 'Datei …'),
          h('button.btn', { onclick: async () => { const r = await call('tex:export', { file, names: [t.name], format: 'png' }, { busyText: 'Wird exportiert …' }); if (r) toast(`Exportiert nach ${r.outDir}`); } }, 'Export'),
          h('button.btn.danger', {
            disabled: info.textures.length < 2 || null,
            onclick: async () => {
              if (!await UI.confirm('Textur löschen?', `„${t.name}“ aus ${base(file)} entfernen? Das Modell zeigt sie dann nicht mehr an.`, 'Löschen', { danger: true })) return;
              const r = await call('tex:remove', { file, names: [t.name] }, { busyText: 'Wird gelöscht …' });
              if (r) refresh();
            },
          }, 'Löschen'))));
    }
    sec.append(grid);
    return sec;
  }

  async function viewYtd(file) {
    const users = S.model.props.filter((p) => p.ytds.includes(file)).map((p) => p.name);
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', base(file)), h('div.subtitle', users.length ? `Texturdatei von: ${users.join(', ')}` : 'Texturdatei (von keinem Prop benutzt)'))));
    wrap.append(h('div.actions',
      action('💾', 'Datei exportieren', base(file), async () => { const r = await call('file:export', { file }); if (r) toast(`Gespeichert: ${r.file}`); })));
    wrap.append(await textureSection(file, 'Texturen'));
    return wrap;
  }

  function viewYbn(file) {
    const viewer = h('div.viewer', h('div.viewer-status', 'Kollision wird geladen …'));
    const wrap = h('div',
      h('div.content-head', h('div.title-block', h('h1', base(file)), h('div.subtitle', 'Eigene Kollisionsdatei'))),
      S.blender[file] === 'open' ? h('div.notice.info', 'In Blender geöffnet – „Speichern & zurück zum GTA Tool“ übernimmt die Kollision automatisch.') : null,
      viewer,
      h('div.actions',
        action('🧊', 'In Blender bearbeiten', 'Kollision anpassen und zurück übernehmen', async () => {
          const r = await call('mlo:blender', { file, ytds: [], studio: 'prop' }, { busyText: 'Blender wird vorbereitet …' });
          if (r) { S.blender[file] = 'open'; toast('Blender startet mit der Kollision.', 'info', 7000); render(true); }
        }, S.blender[file] === 'open'),
        action('💾', 'Datei exportieren', base(file), async () => { const r = await call('file:export', { file }); if (r) toast(`Gespeichert: ${r.file}`); })));
    viewer._start = async () => {
      const V = window.Viewer3D;
      V.mount(viewer); V.setGround('main'); V.setMainOffset(0, 0, 0, 1);
      const r = await window.api.invoke('mlo:collision', { ybn: file });
      if (!r.ok) { viewer.querySelector('.viewer-status').textContent = r.error; return; }
      await V.load(r.data.glb, {});
      await V.setExtras([]);
      viewer.querySelector('.viewer-status').textContent = `${r.data.triangles.toLocaleString('de-DE')} Kollisionsflächen`;
    };
    return wrap;
  }

  function viewYtyp(file) {
    const props = S.model.props.filter((p) => p.archetype && p.archetype.ytyp === file);
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', base(file)), h('div.subtitle', `Archetyp-Datei · ${props.length} Prop(s) aus dem Paket`))),
      h('div.notice.info', 'Die .ytyp meldet die Props beim Spiel an. Sie wird vom Tool automatisch gepflegt (Umbenennen, Löschen, Sichtweite, neue Props) und beim Export mit „DLC_ITYP_REQUEST“ eingetragen.'),
      h('div.list', props.map((p) => h('div.list-row.clickable', { onclick: () => select({ type: 'prop', name: p.name }) },
        h('span', '🪑'), h('div.grow', h('div.name', p.name), h('div.muted.small', `Texturdatei: ${p.archetype.txd || '(eingebettet)'} · Sichtweite ${Math.round(p.archetype.lodDist)} m`))))),
      h('div.actions', action('💾', 'Datei exportieren', base(file), async () => { const r = await call('file:export', { file }); if (r) toast(`Gespeichert: ${r.file}`); })));
  }

  // ================================================================ Dialoge

  function askName(title, label, value, hint) {
    return new Promise((resolve) => {
      const input = h('input', { type: 'text', value: value || '' });
      let done = false;
      const m = modal({
        title,
        body: h('div', h('div.field', h('label', label), input, hint ? h('div.hint', hint) : null)),
        buttons: [{ label: 'Abbrechen' }, { label: 'OK', primary: true, onClick: () => { done = true; resolve(input.value.trim()); } }],
        onClose: () => { if (!done) resolve(null); },
      });
      setTimeout(() => { input.focus(); input.select(); }, 30);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { done = true; resolve(input.value.trim()); m.close(); } });
    });
  }

  const NAME_HINT = 'Nur Kleinbuchstaben, Zahlen, _ und - (keine Leerzeichen). Unter diesem Namen wird das Prop im Spiel gespawnt.';

  async function renameProp(p) {
    const name = await askName('Prop umbenennen', 'Neuer Name', p.name, NAME_HINT);
    if (!name || name === p.name) return;
    const r = await call('prop:rename', { name: p.name, newName: name }, { busyText: 'Wird umbenannt …' });
    if (r) { toast(`Umbenannt in „${r.name}“.`); await changed({ type: 'prop', name: r.name }); }
  }

  function lodDialog(p) {
    const st = { v: Math.round(p.archetype.lodDist) };
    const out = h('span.mono', `${st.v} m`);
    const slider = h('input', { type: 'range', min: 10, max: 1000, step: 10, value: st.v, oninput: (e) => { st.v = Number(e.target.value); out.textContent = `${st.v} m`; } });
    modal({
      title: `Sichtweite – ${p.name}`,
      body: h('div',
        h('p.muted', 'Bis zu dieser Entfernung zeigt das Spiel das Prop an. Kleine Props: 50–150 m, Möbel/Deko: 100–200 m, große Objekte: 300 m und mehr. Höher = mehr Leistung nötig.'),
        h('label.pos-row', h('span', 'Sichtweite'), slider, out)),
      buttons: [
        { label: 'Abbrechen' },
        { label: 'Übernehmen', primary: true, onClick: async () => {
          const r = await call('prop:setLod', { name: p.name, lodDist: st.v }, { busyText: 'Wird gespeichert …' });
          if (!r) return true;
          toast('Sichtweite gespeichert.'); await changed(); return false;
        } },
      ],
    });
  }

  async function ensureCatalog() {
    const st = await call('props:status', {}, { quiet: true });
    if (st && st.built) return true;
    const ok = await UI.confirm('GTA-Objekte einlesen',
      'Damit du Original-Objekte als Vorlage nehmen kannst, liest das Tool einmalig die Objektliste aus deiner GTA-Installation (wenige Sekunden).', 'Einlesen');
    if (!ok) return false;
    const r = await call('props:build', {}, { busyText: 'GTA-Objekte werden gelesen …' });
    if (r) toast(`${r.archetypes.toLocaleString('de-DE')} GTA-Objekte eingelesen.`);
    return !!r;
  }

  /** Komplett neues Prop: Blender startet mit einem Würfel, „Speichern & zurück“ macht daraus ein fertiges GTA-Prop. */
  async function newInBlender() {
    const name = await askName('✨ Neues Prop in Blender erstellen', 'Name deines Props',
      '', `${NAME_HINT} Blender startet mit einem Würfel als Ausgangsform. Farbe oder Bild an „Base Color“ wird automatisch zur Textur, Kollision und Archetyp entstehen automatisch.`);
    if (!name) return;
    const n = name.toLowerCase();
    if (S.model && S.model.props.some((p) => p.name === n)
      && !await UI.confirm('Name schon vorhanden', `Es gibt schon ein Prop „${n}“. Beim Zurückschicken aus Blender wird es ersetzt. Fortfahren?`, 'Fortfahren', { danger: true })) return;
    const r = await call('prop:newInBlender', { name: n }, { busyText: 'Blender wird gestartet …' });
    if (!r) return;
    S.blender[r.key] = 'open';
    toast(`Blender startet. Modelliere „${n}“ und klicke rechts (Taste N) → „GTA Tool“ → „Speichern & zurück“.`, 'info', 12000);
    if (S.model) render(true);
  }

  /** GTA-Objekt suchen, in 3D ansehen und als eigenes Prop übernehmen. */
  async function gtaDialog() {
    if (!await ensureCatalog()) return;
    const search = h('input', { type: 'text', placeholder: 'GTA-Objekt suchen, z. B. chair, table, sign, barrier, lamp …', style: { width: '100%', marginBottom: '6px' } });
    const list = h('div.gta-list');
    const nameIn = h('input', { type: 'text', placeholder: 'Name deines Props, z. B. mein_stuhl' });
    const viewer = h('div.viewer', { style: { height: '300px' } }, h('div.viewer-status', 'Objekt links auswählen'));
    let chosen = null;
    let tok = 0;
    const preview = async (n) => {
      const my = ++tok;
      const st = viewer.querySelector('.viewer-status');
      st.textContent = 'Wird geladen …'; st.classList.remove('err');
      const V = window.Viewer3D;
      V.mount(viewer); V.setGround('main'); V.setMainOffset(0, 0, 0, 1);
      const r = await window.api.invoke('prop:gtaPreview', { name: n });
      if (my !== tok) return;
      if (!r.ok) { st.textContent = r.error; st.classList.add('err'); return; }
      await V.load(r.data.glb, {}); await V.setExtras([]);
      st.textContent = n;
    };
    const fill = (names, note) => {
      list.innerHTML = '';
      if (note) list.append(h('p.muted.small', { style: { padding: '8px' } }, note));
      for (const n of names) {
        list.append(h('div.list-row.clickable', {
          onclick: (ev) => {
            chosen = n;
            list.querySelectorAll('.list-row').forEach((r) => r.classList.remove('active'));
            ev.currentTarget.classList.add('active');
            if (!nameIn.dataset.touched) nameIn.value = `${n}_custom`.slice(0, 60);
            preview(n);
          },
        }, h('span', '🪑'), h('div.grow', h('div.name', n))));
      }
    };
    nameIn.addEventListener('input', () => { nameIn.dataset.touched = '1'; });
    let timer = null;
    search.addEventListener('input', () => {
      clearTimeout(timer);
      timer = setTimeout(async () => {
        const r = await call('props:search', { term: search.value }, { quiet: true });
        if (r) fill(r.items, r.items.length >= 300 ? 'Die ersten 300 Treffer – Suche genauer eingeben.' : r.items.length ? null : 'Nichts gefunden.');
      }, 250);
    });
    fill([], 'Oben einen Suchbegriff eingeben (englische Namen, z. B. „chair“).');
    modal({
      title: '🪑 Aus GTA-Objekt erstellen',
      wide: true,
      onClose: () => { if (S.model && App.view === 'prop') setTimeout(() => render(true), 0); },
      body: h('div',
        h('p.muted', 'Das Original-Objekt wird mit Modell und Texturen unter einem eigenen Namen ins Paket kopiert – danach kannst du z. B. die Texturen austauschen. Das Original im Spiel bleibt unverändert.'),
        h('div.row.gap', { style: { alignItems: 'stretch' } },
          h('div', { style: { flex: '1', minWidth: '0' } }, search, list),
          h('div', { style: { flex: '1.2', minWidth: '0' } }, viewer)),
        h('div.field', h('label', 'Name deines Props'), nameIn, h('div.hint', NAME_HINT))),
      buttons: [
        { label: 'Abbrechen' },
        { label: '✓ Übernehmen', primary: true, onClick: async () => {
          if (!chosen) { toast('Bitte ein Objekt auswählen.', 'warn'); return true; }
          const r = await call('prop:addFromGta', { gtaName: chosen, newName: nameIn.value.trim() || `${chosen}_custom` }, { busyText: 'Objekt wird übernommen …' });
          if (!r) return true;
          if (!S.model) { const m = await call('prop:analyze'); if (m) S.model = m; }
          toast(`„${r.name}“ angelegt.`);
          if (r.note) toast(r.note, 'warn', 10000);
          await changed({ type: 'prop', name: r.name });
          return false;
        } },
      ],
    });
    setTimeout(() => search.focus(), 30);
  }

  async function exportDialog() {
    if (!S.model) return;
    const settings = await call('settings:get');
    const defName = (S.model.ytyps[0] && base(S.model.ytyps[0]).replace(/\.ytyp$/, '')) || (S.model.props.length === 1 ? S.model.props[0].name : 'props');
    const orig = S.model.original || await Sftp.originalName();
    const nameIn = h('input', { type: 'text', value: orig || defName });
    const dirIn = h('input', { type: 'text', value: (settings && (settings.lastResourceDir || settings.lastExportDir)) || '', placeholder: 'Bitte Ordner wählen …' });
    const testCmd = h('input', { type: 'checkbox', checked: true });
    const localField = h('div.field', h('label', 'Zielordner'), h('div.input-row', dirIn, h('button.btn', {
      onclick: async () => { const r = await call('dialog:open', { kind: 'outdir' }); if (r && r[0]) dirIn.value = r[0]; },
    }, 'Durchsuchen …')));
    const target = Sftp.targetField(localField, dirIn);
    modal({
      title: 'Props als FiveM-Resource exportieren',
      body: h('div',
        orig
          ? Sftp.keepNotice(orig)
          : h('p.muted', 'Erstellt eine fertige Resource: stream-Ordner mit Modellen, Texturen und .ytyp, fxmanifest.lua (mit DLC_ITYP_REQUEST) und LIESMICH.txt. Fehlende Archetypen werden automatisch angelegt.'),
        h('div.field', h('label', 'Name der Resource'), nameIn),
        target.el,
        localField,
        orig ? null : h('label.check', testCmd, ' Test-Befehl einbauen (', h('code', '/spawnprop name'), ' und ', h('code', '/delprops'), ')')),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: '📦 Exportieren', primary: true,
          onClick: async () => {
            const resourceName = nameIn.value.trim();
            const r = await Sftp.runExport(target.get(), resourceName,
              (outDir) => call('prop:export', { outDir, resourceName, testCommand: !orig && testCmd.checked }, { busyText: 'Resource wird erstellt …' }));
            if (!r) return true;
            S.model = r.model; S.dirty = false; renderAll(true);
            modal({
              title: '✓ Export fertig',
              body: h('div', Sftp.resultPath(r),
                h('p', `${r.props} Prop(s) · ${r.files} Dateien`),
                r.addedArchetypes && r.addedArchetypes.added.length ? h('div.notice.info', `Automatisch angelegte Archetypen: ${r.addedArchetypes.added.join(', ')}`) : null,
                r.warnings.length ? h('div.notice.warn', h('ul', r.warnings.map((w) => h('li', w)))) : null,
                h('div.notice.info', r.remote ? 'server.cfg: ' : 'Ordner nach „resources“ kopieren → server.cfg: ', h('code', `ensure ${base(r.folder)}`),
                  !orig && testCmd.checked ? h('span', ' · Im Spiel testen: ', h('code', '/spawnprop name')) : null)),
              buttons: Sftp.resultButtons(r),
            });
            return false;
          },
        },
      ],
    });
  }

  return {
    init, show, open,
    get dirty() { return S.dirty; },
    _state: S,
  };
})();
