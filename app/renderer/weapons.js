'use strict';
// Weapon Studio: Addon-/Replace-Waffen öffnen, 3D, Texturen, Blender (normal + Hochdetail), Anzeigenamen, Export

const WeaponStudio = (() => {
  const { h, call, toast, modal, kb, base } = UI;

  const S = {
    model: null,       // Ergebnis von weapon.analyze
    sel: null,         // { type: 'overview' } | { type: 'weapon', name } | { type: 'model', name } | { type: 'file', file }
    dirty: false,
    blender: {},
    texCache: {},
    view: 'main',      // 3D: 'main' | 'hi'
  };
  const $ = (id) => document.getElementById(id);

  const GROUPS = {
    PISTOL: 'Pistole', SMG: 'Maschinenpistole', RIFLE: 'Sturmgewehr', MG: 'Maschinengewehr', SHOTGUN: 'Schrotflinte',
    SNIPER: 'Scharfschützengewehr', HEAVY: 'Schwere Waffe', THROWN: 'Wurfwaffe', MELEE: 'Nahkampf', STUNGUN: 'Elektroschocker',
    PETROLCAN: 'Benzinkanister', FIREEXTINGUISHER: 'Feuerlöscher', UNARMED: 'Unbewaffnet', DIGISCANNER: 'Scanner', NIGHTVISION: 'Nachtsicht', PARACHUTE: 'Fallschirm',
  };
  const COMP_KINDS = {
    Clip: 'Magazin', Scope: 'Zielfernrohr', Suppressor: 'Schalldämpfer', FlashLight: 'Taschenlampe', VariantModel: 'Lackierung/Variante',
    '': 'Aufsatz', ProgrammableTargeting: 'Zielsystem', LaserSight: 'Laservisier', Grip: 'Griff',
  };

  // ================================================================ Start

  function init() {
    document.querySelectorAll('[data-wopen]').forEach((b) => b.addEventListener('click', () => pickAndOpen(b.dataset.wopen, false)));
    $('ws-add').addEventListener('click', addDialog);
    $('ws-close').addEventListener('click', closeAll);
    $('ws-export').addEventListener('click', exportDialog);
    const view = $('view-weapon');
    const dz = $('wdropzone');
    view.addEventListener('dragover', (e) => { e.preventDefault(); dz.classList.add('over'); });
    view.addEventListener('dragleave', (e) => { if (!view.contains(e.relatedTarget)) dz.classList.remove('over'); });
    view.addEventListener('drop', (e) => {
      e.preventDefault(); dz.classList.remove('over');
      const paths = [...e.dataTransfer.files].map((f) => window.api.pathForFile(f)).filter(Boolean);
      if (paths.length) open(paths, !!S.model);
    });
    $('ws-gta').addEventListener('click', () => gtaDialog());
    window.api.on('tex:updated', async ({ file, name }) => {
      if (App.view !== 'weapon' || !S.model || !ownsFile(file)) return;
      S.dirty = true; delete S.texCache[file]; updateStatus();
      // im Bildprogramm bearbeitet → auch in die passende normale/+hi-Texturdatei übernehmen
      const sib = hiSibling(file);
      if (sib && await siblingHas(sib, name)) {
        const r = await call('tex:copyTo', { file, name, target: sib }, { quiet: true });
        if (r) delete S.texCache[sib];
        toast(`Textur „${name}“ wurde übernommen${r ? ` – auch in ${base(sib)}` : ''}.`);
      } else toast(`Textur „${name}“ wurde übernommen.`);
      render(true);
    });
    window.api.on('blender', async ({ model, state, error, studio }) => {
      if (studio !== 'weapon') return;
      if (state === 'applied') {
        S.dirty = true; S.blender[model] = 'open'; S.texCache = {};
        toast('✓ Aus Blender übernommen.');
        const m = S.model && S.model.models.find((x) => x.file === model);
        if (m && m.hi) toast('Tipp: Diese Waffe hat ein Hochdetail-Modell (_hi), das man in der Hand sieht. Mit „Auch als Hochdetail-Modell“ übernimmst du die Änderung dorthin.', 'info', 12000);
        await reanalyze();
        if (App.view === 'weapon') renderAll(true);
      } else if (state === 'error') toast(`Blender: ${error}`, 'error', 10000);
      else if (state === 'closed') { delete S.blender[model]; if (App.view === 'weapon') render(true); }
    });
  }

  const ownsFile = (file) => S.model.models.some((m) => m.file === file || m.hi === file || m.ytds.includes(file)) || S.model.ytds.some((y) => y.file === file);

  async function show() {
    await call('studio:set', { studio: 'weapon' }, { quiet: true });
    renderAll();
  }

  async function pickAndOpen(kind, add) {
    const paths = await call('dialog:open', { kind });
    if (paths && paths.length) await open(paths, add);
  }

  function addDialog() {
    modal({
      title: 'Weitere Waffen hinzufügen',
      body: h('p.muted', 'Die Dateien kommen zu den bereits geöffneten dazu.'),
      buttons: [
        { label: '🔫 Original-GTA-Waffe', onClick: () => gtaDialog() },
        { label: '🌐 Vom Server (SFTP)', onClick: () => Sftp.importTo(WeaponStudio, true) },
        { label: '📁 Ordner importieren', onClick: () => pickAndOpen('folder', true) },
        { label: '📄 Dateien öffnen', primary: true, onClick: () => pickAndOpen('weaponfiles', true) },
      ],
    });
  }

  async function open(paths, add) {
    if (!add && S.model && S.dirty && !await UI.confirm('Neu öffnen?', 'Nicht exportierte Änderungen gehen verloren. Fortfahren?', 'Öffnen', { danger: true })) return;
    const m = await call('weapon:open', { paths, add }, { busyText: 'Waffen werden gelesen …' });
    if (!m) return;
    if (!m.weapons.length && !m.models.length) { toast('In den Dateien wurden keine Waffen gefunden.', 'warn', 8000); if (!add) return; }
    S.model = m;
    if (!add) { S.dirty = false; S.sel = null; }
    S.texCache = {};
    if (!S.sel || !findSel()) S.sel = firstSel();
    toast(m.weapons.length ? `${m.weapons.length} Waffe(n) geladen.` : `${m.replaces.length} Waffenmodell(e) geladen (Replace).`);
    for (const w of m.warnings.slice(0, 4)) toast(w, 'warn', 9000);
    renderAll();
  }

  function firstSel() {
    const m = S.model;
    if (m.weapons.length) return { type: 'weapon', name: m.weapons[0].name };
    if (m.replaces.length) return { type: 'model', name: m.replaces[0] };
    return { type: 'overview' };
  }

  async function reanalyze() { const m = await call('weapon:analyze'); if (m) S.model = m; }

  async function closeAll() {
    if (S.dirty && !await UI.confirm('Schließen?', 'Nicht exportierte Änderungen gehen verloren. Wirklich schließen?', 'Schließen', { danger: true })) return;
    await call('session:close');
    Object.assign(S, { model: null, sel: null, dirty: false, texCache: {} });
    renderAll();
  }

  async function changed(sel) {
    S.dirty = true; S.texCache = {};
    await reanalyze();
    if (sel) S.sel = sel;
    if (!findSel()) S.sel = firstSel();
    renderAll(true);
  }

  // ================================================================ Rendering

  const findWeapon = (name) => S.model && S.model.weapons.find((w) => w.name === name);
  const findModel = (name) => S.model && S.model.models.find((m) => m.name === name);
  const modelByFile = (file) => S.model && S.model.models.find((m) => m.file === file);
  const title = (w) => w.labelText || w.name;

  function findSel() {
    if (!S.model || !S.sel) return null;
    if (S.sel.type === 'overview') return S.sel;
    if (S.sel.type === 'weapon') return findWeapon(S.sel.name) ? S.sel : null;
    if (S.sel.type === 'model') return findModel(S.sel.name) ? S.sel : null;
    if (S.sel.type === 'file') return [...S.model.ytds.map((y) => y.file), ...S.model.metas.map((x) => x.file)].includes(S.sel.file) ? S.sel : null;
    return null;
  }
  function select(sel) { S.sel = sel; S.view = 'main'; renderTree(); render(); }

  function renderAll(keepScroll) {
    const loaded = !!S.model;
    $('ws-empty').classList.toggle('hidden', loaded);
    $('ws-main').classList.toggle('hidden', !loaded);
    $('ws-export').disabled = !loaded || (!S.model.weapons.length && !S.model.models.length);
    updateStatus();
    if (!loaded) return;
    renderTree();
    render(keepScroll);
  }

  function updateStatus() {
    if (!S.model) { $('ws-status-text').textContent = 'Öffne eine Waffen-Resource, ein Addon (dlc.rpf) oder einzelne Dateien.'; return; }
    const m = S.model;
    const parts = [];
    if (m.weapons.length) parts.push(`${m.weapons.length} Waffe(n)`);
    if (m.replaces.length) parts.push(m.weapons.length ? `${m.replaces.length} weitere Waffenmodell(e)` : `${m.replaces.length} ersetzte GTA-Waffe(n)`);
    parts.push(`${m.ytds.length} Texturdatei(en)`);
    $('ws-status-text').textContent = parts.join(' · ') + (S.dirty ? ' · ● Änderungen nicht exportiert' : '');
  }

  function renderTree() {
    const tree = $('ws-tree');
    tree.innerHTML = '';
    const sel = S.sel || {};
    const item = (icon, label, s, extra, tip) => h(`div.tree-item${(s.type === sel.type && (s.name || s.file) === (sel.name || sel.file)) ? '.active' : ''}`,
      { onclick: () => select(s), title: tip || '' }, h('span.ico', icon), h('span.name', label), extra || null);
    const m = S.model;
    tree.append(item('📋', 'Übersicht', { type: 'overview' }));
    if (m.weapons.length) {
      tree.append(h('div.tree-label', `🔫 Waffen (${m.weapons.length})`));
      for (const w of m.weapons) tree.append(item('🔫', title(w), { type: 'weapon', name: w.name }, w.encrypted ? h('span.lock', '🔒') : !w.file ? h('span.lock', { title: 'Modell nicht im Paket' }, '⚠') : null, w.name));
    }
    if (m.replaces.length) {
      tree.append(h('div.tree-label', m.weapons.length ? '🧩 Weitere Waffenmodelle' : '♻ Ersetzte GTA-Waffen'));
      for (const n of m.replaces) tree.append(item('🧩', n, { type: 'model', name: n }, findModel(n).encrypted ? h('span.lock', '🔒') : null));
    }
    if (m.others.length) {
      tree.append(h('div.tree-label', '📦 Sonstige Modelle'));
      for (const n of m.others) tree.append(item('📦', n, { type: 'model', name: n }));
    }
    if (m.ytds.length) {
      tree.append(h('div.tree-label', '🖼 Texturdateien'));
      for (const y of m.ytds) tree.append(item('🖼', base(y.file), { type: 'file', file: y.file }, y.used ? null : h('span.muted.small', 'ungenutzt')));
    }
    if (m.metas.length) {
      tree.append(h('div.tree-label', '📜 Waffen-Metas'));
      for (const x of m.metas) tree.append(item('📜', base(x.file), { type: 'file', file: x.file }));
    }
  }

  let renderSeq = 0;
  async function render(keepScroll) {
    const my = ++renderSeq;
    const c = $('ws-content');
    const scroll = c.scrollTop;
    const s = findSel();
    let node;
    if (!s || s.type === 'overview') node = viewOverview();
    else if (s.type === 'weapon') node = await viewWeapon(findWeapon(s.name));
    else if (s.type === 'model') node = await viewModel(findModel(s.name));
    else if (s.file.endsWith('.ytd')) node = await viewYtd(s.file);
    else node = viewMeta(s.file);
    if (my !== renderSeq) return;
    c.innerHTML = '';
    c.append(node);
    c.scrollTop = keepScroll ? scroll : 0;
    const v = c.querySelector('.viewer');
    if (v && v._start) v._start(v);
  }

  function action(icon, t, text, onClick, disabled) {
    return h('button.action', { onclick: onClick, disabled: disabled || null }, h('span.a-ico', icon), h('span', h('div.a-title', t), h('div.a-text', text)));
  }

  const num = (v, digits = 0) => (v == null ? '–' : Number(v).toFixed(digits).replace('.', ','));

  // ---------------------------------------------------------------- Übersicht

  function viewOverview() {
    const m = S.model;
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', 'Übersicht'),
        h('div.subtitle', `${m.weapons.length} Waffe(n) · ${m.models.length} Modell(e) · ${m.ytds.length} Texturdatei(en) · ${m.metas.length} Waffen-Meta(s)`))),
      m.warnings.length ? h('div.notice.warn', h('b', 'Hinweise'), h('ul', m.warnings.slice(0, 12).map((w) => h('li', w)))) : null,
      !m.weapons.length && m.replaces.length ? h('div.notice.info', h('b', 'Replace-Waffen: '),
        'Diese Modelle ersetzen die gleichnamigen GTA-Waffen (z. B. w_pi_pistol = Pistole). Es werden keine Metas gebraucht – die Dateien kommen einfach in den stream-Ordner.') : null,
      h('div.notice.info', h('b', 'So funktionieren Waffen: '),
        'Das Modell (.ydr) enthält die Form, die Texturdatei (.ytd) die Bilder. Das „_hi“-Modell ist die detailreiche Fassung, die man in der Hand und in der Ego-Perspektive sieht. ',
        'Addon-Waffen brauchen außerdem Metas: weapons.meta (Werte), weaponarchetypes.meta (Modell ↔ Textur), oft weaponcomponents.meta (Magazine, Aufsätze) und weaponanimations.meta.'),
      h('div.actions',
        action('🔫', 'Original-GTA-Waffe laden', 'Als Vorlage für eigene Texturen (Replace)', () => gtaDialog()),
        action('➕', 'Weitere Dateien öffnen', 'Dateien, Ordner oder vom Server', addDialog),
        action('📦', 'Als FiveM-Resource exportieren', 'Fertiges Script mit Test-Befehl /waffe', exportDialog)),
      m.weapons.length ? h('div.section', h('div.section-head', h('h3', 'Alle Waffen')),
        h('div.list', m.weapons.map((w) => h('div.list-row.clickable', { onclick: () => select({ type: 'weapon', name: w.name }) },
          h('span', '🔫'),
          h('div.grow', h('div.name', title(w)), h('div.muted.small', `${w.name} · ${GROUPS[w.group] || w.group || '–'} · Modell ${w.model || '–'}`)),
          w.encrypted ? h('span.lock', '🔒') : null)))) : null,
      m.replaces.length ? h('div.section', h('div.section-head', h('h3', m.weapons.length ? 'Weitere Waffenmodelle' : 'Ersetzte GTA-Waffen')),
        h('div.list', m.replaces.map((n) => { const x = findModel(n); return h('div.list-row.clickable', { onclick: () => select({ type: 'model', name: n }) },
          h('span', '🧩'), h('div.grow', h('div.name', n), h('div.muted.small', `${base(x.file)} · ${kb(x.size)}${x.hi ? ' · mit Hochdetail-Modell' : ''}`))); }))) : null);
  }

  // ---------------------------------------------------------------- Waffe

  async function viewWeapon(w) {
    const mdl = w.file ? modelByFile(w.file) : null;
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', title(w)),
      h('div.subtitle', `${w.name} · ${GROUPS[w.group] || w.group || 'Waffe'}${w.model ? ` · Modell ${w.model}` : ''} · ${base(w.meta)}`))));
    if (w.encrypted) { wrap.append(h('div.notice.warn', 'Das Modell ist verschlüsselt (FiveM-Escrow) und kann weder angezeigt noch bearbeitet werden.')); }
    if (!mdl) wrap.append(h('div.notice.warn', `Das Modell „${w.model}.ydr“ ist nicht im Paket – evtl. benutzt die Waffe ein GTA-Modell.`));
    if (mdl && (S.blender[mdl.file] === 'open' || (mdl.hi && S.blender[mdl.hi] === 'open'))) wrap.append(h('div.notice.info', 'In Blender geöffnet. Nach dem Bearbeiten rechts (Taste N) → „GTA Tool“ → „Speichern & zurück zum GTA Tool“ – das Modell wird automatisch übernommen.'));

    wrap.append(h('div.cards',
      h('div.card', h('div.k', 'Anzeigename'), h('div.v', w.labelText || '–')),
      h('div.card', h('div.k', 'Art'), h('div.v', GROUPS[w.group] || w.group || '–')),
      h('div.card', h('div.k', 'Schaden'), h('div.v', w.damage ? num(w.damage, 1) : w.damageType === 'EXPLOSIVE' ? 'Explosion' : '–')),
      h('div.card', h('div.k', 'Magazin'), h('div.v', w.clipSize != null ? `${w.clipSize} Schuss` : '–')),
      h('div.card', h('div.k', 'Reichweite'), h('div.v', w.range != null ? `${num(w.range)} m` : '–')),
      h('div.card', h('div.k', 'Schüsse pro Minute'), h('div.v', w.timeBetweenShots ? num(60 / w.timeBetweenShots) : '–'))));

    if (mdl && !mdl.encrypted) wrap.append(viewer3d(mdl));

    const code = `GiveWeaponToPed(PlayerPedId(), joaat('${w.name}'), 250, false, true)`;
    wrap.append(h('details.mount-panel', { open: S.codeOpen || null, ontoggle: (e) => { S.codeOpen = e.target.open; } },
      h('summary', '🧾 Im eigenen Script benutzen (Lua)'),
      h('pre.code', code),
      h('button.btn.small', { onclick: async () => { try { await navigator.clipboard.writeText(code); toast('Code kopiert.'); } catch { toast('Kopieren nicht möglich.', 'error'); } } }, '📋 Code kopieren')));

    wrap.append(h('div.actions',
      action('✏', 'Anzeigename', 'Name im Waffenrad und in Meldungen', () => labelDialog(w)),
      ...(mdl ? modelActions(mdl) : [])));

    if (w.components.length) {
      wrap.append(h('div.section', h('div.section-head', h('h3', `Aufsätze & Magazine (${w.components.length})`)),
        h('div.list', w.components.map((c) => h(`div.list-row${c.model ? '.clickable' : ''}`, { onclick: c.model ? () => select({ type: 'model', name: c.model.name }) : null },
          h('span', c.kind === 'Clip' ? '🔋' : c.kind === 'Scope' ? '🔭' : c.kind === 'Suppressor' ? '🔇' : c.kind === 'FlashLight' ? '🔦' : '🧩'),
          h('div.grow', h('div.name', c.label || c.name),
            h('div.muted.small', `${COMP_KINDS[c.kind] || c.kind || 'Aufsatz'}${c.isDefault ? ' · Standard' : ''}${c.model ? ` · Modell ${c.model.name}` : c.known ? '' : ' · aus GTA'}${c.label ? ` · ${c.name}` : ''}`)))))));
    }
    if (w.ammoModel && (!mdl || w.ammoModel !== mdl.name)) {
      wrap.append(h('div.notice.info', 'Geschoss-Modell: ', h('button.btn.small', { onclick: () => select({ type: 'model', name: w.ammoModel }) }, `🚀 ${w.ammoModel}`)));
    }
    if (mdl && !mdl.encrypted) await appendTextures(wrap, mdl);
    return wrap;
  }

  // ---------------------------------------------------------------- Modell (Replace, Aufsatz, Geschoss …)

  async function viewModel(mdl) {
    const users = mdl.usedBy.map((n) => findWeapon(n)).filter(Boolean);
    const isReplace = !S.model.weapons.length && mdl.name.startsWith('w_');
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', mdl.name),
      h('div.subtitle', `${base(mdl.file)} · ${kb(mdl.size)}${mdl.hi ? ' · mit Hochdetail-Modell (_hi)' : ''}`))));
    if (mdl.encrypted) { wrap.append(h('div.notice.warn', 'Dieses Modell ist verschlüsselt (FiveM-Escrow) und kann weder angezeigt noch bearbeitet werden.')); return wrap; }
    if (isReplace) wrap.append(h('div.notice.info', `Ersetzt im Spiel das GTA-Modell „${mdl.name}“ – überall, wo diese Waffe vorkommt.`));
    if (users.length) wrap.append(h('div.notice.info', 'Gehört zu: ', ...users.map((w) => h('button.btn.small', { onclick: () => select({ type: 'weapon', name: w.name }) }, `🔫 ${title(w)}`))));
    if (S.blender[mdl.file] === 'open' || (mdl.hi && S.blender[mdl.hi] === 'open')) wrap.append(h('div.notice.info', 'In Blender geöffnet – „Speichern & zurück zum GTA Tool“ übernimmt das Modell automatisch.'));
    wrap.append(viewer3d(mdl));
    wrap.append(h('div.actions', ...modelActions(mdl)));
    await appendTextures(wrap, mdl);
    return wrap;
  }

  function modelActions(mdl) {
    const openBl = async (file, label) => {
      const r = await call('mlo:blender', { file, ytds: mdl.ytds, studio: 'weapon' }, { busyText: 'Blender wird vorbereitet …' });
      if (r) { S.blender[file] = 'open'; toast(`Blender startet mit dem ${label}. Zum Übernehmen: Seitenleiste (N) → „GTA Tool“ → „Speichern & zurück“.`, 'info', 9000); render(true); }
    };
    return [
      action('🧊', 'In Blender bearbeiten', mdl.hi ? 'Normales Modell (aus der Entfernung)' : 'Modell bearbeiten und direkt zurück übernehmen', () => openBl(mdl.file, 'Modell'), S.blender[mdl.file] === 'open'),
      mdl.hi ? action('🔎', 'Hochdetail-Modell in Blender', 'Fassung in der Hand / Ego-Perspektive (_hi)', () => openBl(mdl.hi, 'Hochdetail-Modell'), S.blender[mdl.hi] === 'open') : null,
      mdl.hi ? action('⇄', 'Auch als Hochdetail-Modell', 'Normales Modell nach _hi kopieren (gleiches Aussehen in der Hand)', async () => {
        if (!await UI.confirm('Hochdetail-Modell ersetzen?', `${base(mdl.hi)} wird durch ${base(mdl.file)} ersetzt. So sieht die Waffe in der Hand genauso aus wie dein bearbeitetes Modell (eine Sicherung wird angelegt).`, 'Ersetzen')) return;
        const r = await call('weapon:copyToHi', { file: mdl.file, hi: mdl.hi }, { busyText: 'Wird kopiert …' });
        if (r) { toast('Hochdetail-Modell ersetzt.'); await changed(); }
      }) : null,
      action('💾', 'Datei exportieren', `${base(mdl.file)} speichern`, async () => { const r = await call('file:export', { file: mdl.file }); if (r) toast(`Gespeichert: ${r.file}`); }),
      action('♻', 'Modell ersetzen', 'Durch eine andere .ydr-Datei', async () => {
        const f = await call('dialog:open', { kind: 'ydr' });
        if (!f || !f[0]) return;
        const r = await call('file:replace', { file: mdl.file, source: f[0] }, { busyText: 'Wird ersetzt …' });
        if (r) { toast('Modell ersetzt.'); await changed(); }
      }),
    ].filter(Boolean);
  }

  let token = 0;
  function viewer3d(mdl) {
    const hasHi = !!mdl.hi;
    if (!hasHi) S.view = 'main';
    const viewer = h('div.viewer', h('div.viewer-status', '3D wird erstellt …'),
      h('div.viewer-bar',
        hasHi ? h(`button.btn.small${S.view === 'main' ? '.primary' : ''}`, { onclick: () => { S.view = 'main'; render(true); } }, '🔫 Normal') : null,
        hasHi ? h(`button.btn.small${S.view === 'hi' ? '.primary' : ''}`, { onclick: () => { S.view = 'hi'; render(true); } }, '🔎 Hochdetail') : null,
        h('button.btn.small', { onclick: () => window.Viewer3D && window.Viewer3D.resetView() }, '⟲ Ansicht'),
        h('span.viewer-hint', 'Drehen: linke Maustaste · Verschieben: rechte · Zoomen: Mausrad')));
    viewer._start = async (el) => {
      const my = ++token;
      if (!window.Viewer3D) await new Promise((r) => window.addEventListener('viewer3d-ready', r, { once: true }));
      const V = window.Viewer3D;
      V.mount(el); V.setGround('main'); V.setMainOffset(0, 0, 0, 1);
      const status = el.querySelector('.viewer-status');
      try {
        const r = await window.api.invoke('model:preview', { file: S.view === 'hi' && mdl.hi ? mdl.hi : mdl.file, ytds: mdl.ytds });
        if (my !== token) return;
        if (!r.ok) throw new Error(r.error);
        await V.load(r.data.glb, {});
        await V.setExtras([]);
        status.textContent = `${r.data.triangles.toLocaleString('de-DE')} Dreiecke${r.data.missingTextures ? ` · ${r.data.missingTextures} Textur(en) fehlen` : ''}`;
      } catch (e) {
        if (my !== token) return;
        status.textContent = `3D-Ansicht nicht möglich: ${e.message}`;
        status.classList.add('err');
      }
    };
    return viewer;
  }

  // ---------------------------------------------------------------- Texturen

  async function appendTextures(wrap, mdl) {
    wrap.append(await textureSection(mdl.file, 'Eingebettete Texturen', true));
    if (mdl.hi) wrap.append(await textureSection(mdl.hi, 'Eingebettete Texturen (Hochdetail)', true));
    for (const y of mdl.ytds) wrap.append(await textureSection(y, `Texturen aus ${base(y)}`));
    if (!mdl.ytds.length) wrap.append(h('div.notice.info', 'Dieses Modell hat keine eigene Texturdatei (.ytd) – Texturen sind eingebettet oder kommen aus GTA.'));
  }

  async function textureSection(file, t, embedded) {
    let info = S.texCache[file];
    if (!info) { info = await call('tex:list', { file }, { quiet: true }); if (info) S.texCache[file] = info; }
    const sec = h('div.section', h('div.section-head', h('h3', `${t} (${info ? info.textures.length : 0})`)));
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
          if (!r) return;
          const sib = hiSibling(file);
          const r2 = sib ? await call('tex:add', { file: sib, images: imgs }, { busyText: 'Wird auch in die Partner-Datei übernommen …' }) : null;
          if (r2) delete S.texCache[sib];
          toast(`${r.added.length} Textur(en) importiert (gleichnamige ersetzt)${r2 ? ` – auch in ${base(sib)}` : ''}.`);
          refresh();
        },
      }, '⬆ Textur importieren')));
    const grid = h('div.tex-grid');
    for (const tx of info.textures) {
      grid.append(h('div.tex',
        h('div.tex-img', tx.thumb ? h('img', { src: tx.thumb }) : h('span.muted.small', 'keine Vorschau')),
        h('div.tex-info', h('div.tex-name', { title: tx.name }, tx.name), h('div.tex-meta', `${tx.width}×${tx.height} · ${tx.format} · ${tx.mips} Mips`)),
        h('div.tex-btns.wrap',
          h('button.btn', { title: 'Im Bildprogramm öffnen – speichern übernimmt automatisch', onclick: async () => { const r = await call('tex:edit', { file, name: tx.name }, { busyText: 'Wird geöffnet …' }); if (r) toast('Im Bildprogramm geöffnet – Speichern übernimmt automatisch.', 'info', 7000); } }, 'Bearbeiten'),
          h('button.btn', {
            title: 'Durch eine Bilddatei ersetzen',
            onclick: async () => {
              const pth = await call('dialog:open', { kind: 'image' });
              if (!pth || !pth[0]) return;
              const r = await call('tex:replace', { file, name: tx.name, image: pth[0] }, { busyText: 'Wird ersetzt …' });
              if (!r) return;
              const sib = hiSibling(file);
              const r2 = sib && await siblingHas(sib, tx.name) ? await call('tex:replace', { file: sib, name: tx.name, image: pth[0] }, { busyText: 'Wird auch in der Partner-Datei ersetzt …' }) : null;
              if (r2) delete S.texCache[sib];
              toast(r2 ? `Ersetzt – auch in ${base(sib)}.` : 'Ersetzt.');
              refresh();
            },
          }, 'Datei …'),
          h('button.btn', { onclick: async () => { const r = await call('tex:export', { file, names: [tx.name], format: 'png' }, { busyText: 'Wird exportiert …' }); if (r) toast(`Exportiert nach ${r.outDir}`); } }, 'Export'),
          h('button.btn.danger', {
            disabled: info.textures.length < 2 || null,
            onclick: async () => {
              if (!await UI.confirm('Textur löschen?', `„${tx.name}“ aus ${base(file)} entfernen? Das Modell zeigt sie dann nicht mehr an.`, 'Löschen', { danger: true })) return;
              const r = await call('tex:remove', { file, names: [tx.name] }, { busyText: 'Wird gelöscht …' });
              if (!r) return;
              const sib = hiSibling(file);
              if (sib && await siblingHas(sib, tx.name)) {
                const r2 = await call('tex:remove', { file: sib, names: [tx.name] }, { busyText: 'Wird auch in der Partner-Datei gelöscht …' });
                if (r2) { delete S.texCache[sib]; toast(`Gelöscht – auch in ${base(sib)}.`); }
              }
              refresh();
            },
          }, 'Löschen'))));
    }
    sec.append(grid);
    return sec;
  }

  async function viewYtd(file) {
    const users = S.model.models.filter((m) => m.ytds.includes(file)).map((m) => m.name);
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', base(file)),
      h('div.subtitle', users.length ? `Texturdatei von: ${users.join(', ')}` : 'Texturdatei (keinem Modell zugeordnet)'))));
    wrap.append(h('div.actions',
      action('💾', 'Datei exportieren', base(file), async () => { const r = await call('file:export', { file }); if (r) toast(`Gespeichert: ${r.file}`); })));
    wrap.append(await textureSection(file, 'Texturen'));
    return wrap;
  }

  const META_INFO = {
    weapons: 'Werte der Waffe: Schaden, Magazin, Reichweite, Sound, Modell, Anzeigename, Aufsätze.',
    weaponcomponents: 'Magazine, Zielfernrohre, Schalldämpfer und andere Aufsätze.',
    weaponarchetypes: 'Meldet die Waffenmodelle beim Spiel an (Modell ↔ Texturdatei, Sichtweite).',
    weaponanimations: 'Welche Animationen die Waffe benutzt (Halten, Zielen, Nachladen).',
    pedpersonality: 'Wie Figuren die Waffe ziehen und wegstecken.',
  };

  function viewMeta(file) {
    const x = S.model.metas.find((m) => m.file === file);
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', base(file)), h('div.subtitle', `Waffen-Meta · ${x ? x.kind : ''}`))),
      h('div.notice.info', (x && META_INFO[x.kind]) || 'Waffen-Datei.', ' Sie wird beim Export automatisch im fxmanifest eingetragen.'),
      h('div.actions', action('💾', 'Datei exportieren', base(file), async () => { const r = await call('file:export', { file }); if (r) toast(`Gespeichert: ${r.file}`); })));
  }

  // ================================================================ Dialoge

  function labelDialog(w) {
    const input = h('input', { type: 'text', value: w.labelText || '' , placeholder: 'z. B. Goldene Pistole' });
    let done = false;
    const save = async () => {
      const text = input.value.trim();
      if (!text) { toast('Bitte einen Namen eingeben.', 'warn'); return true; }
      const r = await call('weapon:setLabel', { weapon: w.name, text }, { busyText: 'Wird gespeichert …' });
      if (!r) return true;
      done = true;
      toast(`Anzeigename gespeichert (${r.key}).`);
      await changed();
      return false;
    };
    const m = modal({
      title: `Anzeigename – ${w.name}`,
      body: h('div',
        h('p.muted', 'So heißt die Waffe im Spiel (Waffenrad, Meldungen). Beim Export wird der Name automatisch per AddTextEntry gesetzt.'),
        h('div.field', h('label', 'Anzeigename'), input)),
      buttons: [{ label: 'Abbrechen' }, { label: 'Speichern', primary: true, onClick: save }],
    });
    setTimeout(() => { input.focus(); input.select(); }, 30);
    input.addEventListener('keydown', async (e) => { if (e.key === 'Enter' && !done) { if (await save() === false) m.close(); } });
  }

  /** Partner-Texturdatei: w_x.ytd ⇄ w_x+hi.ytd (die +hi-Fassung sieht man in der Hand). */
  function hiSibling(file) {
    if (!S.model || !file.endsWith('.ytd')) return null;
    const o = file.endsWith('+hi.ytd') ? `${file.slice(0, -7)}.ytd` : `${file.slice(0, -4)}+hi.ytd`;
    return S.model.ytds.some((y) => y.file === o) ? o : null;
  }
  async function siblingHas(sib, name) {
    let info = S.texCache[sib];
    if (!info) info = await call('tex:list', { file: sib }, { quiet: true });
    return !!(info && info.textures.some((t) => t.name.toLowerCase() === String(name).toLowerCase()));
  }

  async function ensureGtaCatalog() {
    const st = await call('weapon:gtaStatus', {}, { quiet: true });
    if (st && st.built) return true;
    const ok = await UI.confirm('GTA-Waffen einlesen',
      'Damit du Original-Waffen laden kannst, liest das Tool einmalig die Waffenliste aus deiner GTA-Installation (dauert einen Moment).', 'Einlesen');
    if (!ok) return false;
    const r = await call('weapon:gtaBuild', {}, { busyText: 'GTA-Waffen werden gelesen …' });
    if (r) toast(`${r.weapons} GTA-Waffen und ${r.parts} Einzelteile eingelesen.`);
    return !!r;
  }

  /** Original-GTA-Waffe suchen, in 3D ansehen und als Replace laden (gleicher Name → ersetzt die Waffe im Spiel). */
  async function gtaDialog() {
    if (!await ensureGtaCatalog()) return;
    const search = h('input', { type: 'text', placeholder: 'Waffe suchen, z. B. Pistole, Karabiner, w_ar_ …', style: { width: '100%', marginBottom: '6px' } });
    const partsCb = h('input', { type: 'checkbox' });
    const list = h('div.gta-list');
    const viewer = h('div.viewer', { style: { height: '300px' } }, h('div.viewer-status', 'Waffe links auswählen'));
    let chosen = null;
    let tok = 0;
    const preview = async (item) => {
      const my = ++tok;
      const st = viewer.querySelector('.viewer-status');
      st.textContent = 'Wird geladen …'; st.classList.remove('err');
      const V = window.Viewer3D;
      V.mount(viewer); V.setGround('main'); V.setMainOffset(0, 0, 0, 1);
      const r = await window.api.invoke('weapon:gtaPreview', { model: item.model });
      if (my !== tok) return;
      if (!r.ok) { st.textContent = r.error; st.classList.add('err'); return; }
      await V.load(r.data.glb, {}); await V.setExtras([]);
      st.textContent = `${item.text || item.name || item.model} · ${item.model}`;
    };
    const fill = (items, note) => {
      list.innerHTML = '';
      if (note) list.append(h('p.muted.small', { style: { padding: '8px' } }, note));
      for (const it of items) {
        list.append(h('div.list-row.clickable', {
          onclick: (ev) => {
            chosen = it;
            list.querySelectorAll('.list-row').forEach((r) => r.classList.remove('active'));
            ev.currentTarget.classList.add('active');
            preview(it);
          },
        }, h('span', it.part ? '🧩' : '🔫'), h('div.grow', h('div.name', it.text || it.name || it.model),
          h('div.muted.small', [it.name, it.model].filter(Boolean).join(' · ')))));
      }
    };
    let timer = null;
    const load = async () => {
      const r = await call('weapon:gtaSearch', { term: search.value, parts: partsCb.checked }, { quiet: true });
      if (r) fill(r.items, r.items.length ? (r.items.length >= 400 ? 'Die ersten 400 Treffer – Suche genauer eingeben.' : null) : 'Nichts gefunden.');
    };
    search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(load, 200); });
    partsCb.addEventListener('change', load);
    load();
    modal({
      title: '🔫 Original-GTA-Waffe laden',
      wide: true,
      onClose: () => { if (S.model && App.view === 'weapon') setTimeout(() => render(true), 0); },
      body: h('div',
        h('p.muted', 'Die Waffe wird mit Modellen und Texturen aus deiner GTA-Installation geladen – unter ihrem Original-Namen. Danach kannst du Texturen ersetzen, hinzufügen oder löschen. Beim Export entsteht eine Replace-Resource: Sie ersetzt das Aussehen dieser Waffe im Spiel.'),
        h('div.row.gap', { style: { alignItems: 'stretch' } },
          h('div', { style: { flex: '1', minWidth: '0' } }, search, h('label.check', { style: { margin: '0 0 6px' } }, partsCb, h('span', 'Auch Einzelteile zeigen (Magazine, Zielfernrohre, Aufsätze)')), list),
          h('div', { style: { flex: '1.2', minWidth: '0' } }, viewer))),
      buttons: [
        { label: 'Abbrechen' },
        { label: '✓ Laden', primary: true, onClick: async () => {
          if (!chosen) { toast('Bitte eine Waffe auswählen.', 'warn'); return true; }
          const r = await call('weapon:addFromGta', { model: chosen.model }, { busyText: 'Waffe wird geladen …' });
          if (!r) return true;
          toast(`„${chosen.text || chosen.model}“ geladen (${r.files.length} Dateien).`);
          await changed({ type: 'model', name: r.model });
          return false;
        } },
      ],
    });
    setTimeout(() => search.focus(), 30);
  }

  async function exportDialog() {
    if (!S.model) return;
    const settings = await call('settings:get');
    const w0 = S.model.weapons[0];
    const defName = w0 ? w0.name.toLowerCase().replace(/^weapon_/, '') : 'waffen';
    const orig = S.model.original || await Sftp.originalName();
    const nameIn = h('input', { type: 'text', value: orig || defName });
    const dirIn = h('input', { type: 'text', value: (settings && (settings.lastResourceDir || settings.lastExportDir)) || '', placeholder: 'Bitte Ordner wählen …' });
    const testCmd = h('input', { type: 'checkbox', checked: !!S.model.weapons.length });
    const localField = h('div.field', h('label', 'Zielordner'), h('div.input-row', dirIn, h('button.btn', {
      onclick: async () => { const r = await call('dialog:open', { kind: 'outdir' }); if (r && r[0]) dirIn.value = r[0]; },
    }, 'Durchsuchen …')));
    const target = Sftp.targetField(localField, dirIn);
    modal({
      title: 'Waffen als FiveM-Resource exportieren',
      body: h('div',
        orig
          ? Sftp.keepNotice(orig)
          : h('p.muted', 'Erstellt eine fertige Resource: stream-Ordner mit Modellen und Texturen, die Waffen-Metas mit den passenden data_file-Einträgen im fxmanifest, Anzeigenamen und LIESMICH.txt.'),
        h('div.field', h('label', 'Name der Resource'), nameIn),
        target.el,
        localField,
        orig || !S.model.weapons.length ? null : h('label.check', testCmd, ' Test-Befehl einbauen (', h('code', '/waffe WEAPON_NAME'), ')')),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: '📦 Exportieren', primary: true,
          onClick: async () => {
            const resourceName = nameIn.value.trim();
            const r = await Sftp.runExport(target.get(), resourceName,
              (outDir) => call('weapon:export', { outDir, resourceName, testCommand: !orig && testCmd.checked && S.model.weapons.length > 0 }, { busyText: 'Resource wird erstellt …' }));
            if (!r) return true;
            S.model = r.model; S.dirty = false; renderAll(true);
            modal({
              title: '✓ Export fertig',
              body: h('div', Sftp.resultPath(r),
                h('p', `${r.weapons ? `${r.weapons} Waffe(n) · ` : ''}${r.files} Dateien`),
                r.warnings.length ? h('div.notice.warn', h('ul', r.warnings.map((x) => h('li', x)))) : null,
                h('div.notice.info', r.remote ? 'server.cfg: ' : 'Ordner nach „resources“ kopieren → server.cfg: ', h('code', `ensure ${base(r.folder)}`),
                  !orig && testCmd.checked && S.model.weapons.length ? h('span', ' · Im Spiel testen: ', h('code', `/waffe ${S.model.weapons[0].name}`)) : null)),
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
