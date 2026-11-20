'use strict';
// Animation Studio: Animationen (.ycd) öffnen, auf dem Freemode-Körper abspielen, bearbeiten, Blender, Export

const AnimStudio = (() => {
  const { h, call, toast, modal, kb, base } = UI;

  const S = {
    model: null,        // Ergebnis von anim.analyze
    sel: null,          // { type: 'overview' } | { type: 'dict', file } | { type: 'clip', file, id }
    dirty: false,
    ped: 'm',           // Vorschau-Körper
    mover: true,        // Laufweg mitbewegen
    loop: true,
    speed: 1,
    rigPed: null,       // geladenes Rig in der 3D-Ansicht
    version: 0,         // für Cache-Ungültigkeit
    blender: {},        // key -> 'open'
    noRig: null,        // Fehlermeldung, falls der Körper nicht geladen werden kann
  };
  const $ = (id) => document.getElementById(id);
  const rigCache = new Map();     // ped -> Promise<glb>
  const sampleCache = new Map();  // key -> Promise<sample>

  const KIND = {
    ped: { icon: '🧍', text: 'Personen-Animation' },
    face: { icon: '🙂', text: 'Gesichts-Animation' },
    object: { icon: '📦', text: 'Objekt-Animation' },
    uv: { icon: '🌀', text: 'Textur-Animation (UV)' },
    camera: { icon: '🎥', text: 'Kamera-Animation' },
  };
  const sec = (s) => `${Number(s || 0).toFixed(2).replace('.', ',')} s`;

  // ================================================================ Start

  function init() {
    document.querySelectorAll('[data-aopen]').forEach((b) => b.addEventListener('click', () => pickAndOpen(b.dataset.aopen, false)));
    $('as-add').addEventListener('click', addDialog);
    $('as-close').addEventListener('click', closeAll);
    $('as-export').addEventListener('click', exportDialog);
    const view = $('view-anim');
    const dz = $('adropzone');
    view.addEventListener('dragover', (e) => { e.preventDefault(); dz.classList.add('over'); });
    view.addEventListener('dragleave', (e) => { if (!view.contains(e.relatedTarget)) dz.classList.remove('over'); });
    view.addEventListener('drop', (e) => {
      e.preventDefault(); dz.classList.remove('over');
      const paths = [...e.dataTransfer.files].map((f) => window.api.pathForFile(f)).filter(Boolean);
      if (paths.length) open(paths, !!S.model);
    });
    window.api.on('blender', async ({ model, state, error, studio }) => {
      if (studio !== 'anim') return;
      if (state === 'applied') {
        S.dirty = true; S.blender[model] = 'open';
        await reanalyze();
        changed();
        toast('✓ Animation aus Blender übernommen.');
        if (App.view === 'anim') renderAll(true);
      } else if (state === 'error') toast(`Blender: ${error}`, 'error', 10000);
      else if (state === 'closed') { delete S.blender[model]; if (App.view === 'anim') render(true); }
    });
    document.addEventListener('keydown', (e) => {
      if (App.view !== 'anim' || e.code !== 'Space' || !S.sel || S.sel.type !== 'clip') return;
      if (e.target.closest('input, textarea, select, button') || document.querySelector('.modal-back')) return;
      e.preventDefault();
      const V = window.Viewer3D;
      if (V) V.animPlay(!V.animState().playing);
    });
  }

  function changed() { S.version++; sampleCache.clear(); }

  async function show() {
    await call('studio:set', { studio: 'anim' }, { quiet: true });
    renderAll();
  }

  async function pickAndOpen(kind, add) {
    const paths = await call('dialog:open', { kind });
    if (paths && paths.length) await open(paths, add);
  }

  function addDialog() {
    modal({
      title: 'Weitere Animationen hinzufügen',
      body: h('p.muted', 'Die Dateien kommen zu den bereits geöffneten dazu.'),
      buttons: [
        { label: '🌐 Vom Server (SFTP)', onClick: () => Sftp.importTo(AnimStudio, true) },
        { label: '📁 Ordner importieren', onClick: () => pickAndOpen('folder', true) },
        { label: '📄 Dateien öffnen', primary: true, onClick: () => pickAndOpen('animfiles', true) },
      ],
    });
  }

  async function open(paths, add) {
    if (!add && S.model && S.dirty && !await UI.confirm('Neu öffnen?', 'Nicht exportierte Änderungen gehen verloren. Fortfahren?', 'Öffnen', { danger: true })) return;
    const m = await call('anim:open', { paths, add }, { busyText: 'Animationen werden gelesen …' });
    if (!m) return;
    if (!m.dicts.length) { toast('In den Dateien wurden keine Animationen (.ycd) gefunden.', 'warn', 8000); if (!add) return; }
    S.model = m;
    if (!add) { S.dirty = false; S.sel = null; }
    changed();
    if (!S.sel || !findSel()) S.sel = firstClipSel() || { type: 'overview' };
    toast(`${m.dicts.length} Animations-Datei(en) mit ${m.clipCount} Clip(s) geladen.`);
    for (const w of m.warnings.slice(0, 4)) toast(w, 'warn', 9000);
    renderAll();
  }

  function firstClipSel() {
    const d = S.model.dicts.find((x) => x.clips.some((c) => c.kind === 'ped')) || S.model.dicts.find((x) => x.clips.length);
    if (!d) return null;
    const c = d.clips.find((x) => x.kind === 'ped') || d.clips[0];
    return { type: 'clip', file: d.file, id: c.id };
  }

  async function reanalyze() {
    const m = await call('anim:analyze');
    if (m) S.model = m;
  }

  async function closeAll() {
    if (S.dirty && !await UI.confirm('Schließen?', 'Nicht exportierte Änderungen gehen verloren. Wirklich schließen?', 'Schließen', { danger: true })) return;
    await call('session:close');
    Object.assign(S, { model: null, sel: null, dirty: false });
    changed();
    renderAll();
  }

  // ================================================================ Auswahl

  const dictOf = (file) => S.model && S.model.dicts.find((d) => d.file === file);
  function findSel() {
    if (!S.model || !S.sel) return null;
    if (S.sel.type === 'overview') return { type: 'overview' };
    const d = dictOf(S.sel.file);
    if (!d) return null;
    if (S.sel.type === 'dict') return { type: 'dict', dict: d };
    const c = d.clips.find((x) => x.id === S.sel.id);
    return c ? { type: 'clip', dict: d, clip: c } : null;
  }
  function select(sel) { S.sel = sel; renderTree(); render(); }

  /** Ergebnis einer Bearbeitung übernehmen. */
  function applyResult(r, sel) {
    if (!r) return false;
    S.model = r.model;
    S.dirty = true;
    changed();
    if (sel) S.sel = sel;
    if (!findSel()) S.sel = firstClipSel() || { type: 'overview' };
    renderAll(true);
    return true;
  }

  // ================================================================ Rendering

  function renderAll(keepScroll) {
    const loaded = !!S.model;
    $('as-empty').classList.toggle('hidden', loaded);
    $('as-main').classList.toggle('hidden', !loaded);
    $('as-export').disabled = !loaded || !S.model.dicts.length;
    updateStatus();
    if (!loaded) return;
    renderTree();
    render(keepScroll);
  }

  function updateStatus() {
    if (!S.model) { $('as-status-text').textContent = 'Öffne Animationen (.ycd) – einzeln, als Ordner oder aus einer FiveM-Resource.'; return; }
    $('as-status-text').textContent = `${S.model.dicts.length} Animations-Datei(en) · ${S.model.clipCount} Clips${S.dirty ? ' · ● Änderungen nicht exportiert' : ''}`;
  }

  function renderTree() {
    const tree = $('as-tree');
    tree.innerHTML = '';
    const sel = S.sel || {};
    tree.append(h(`div.tree-item${sel.type === 'overview' ? '.active' : ''}`, { onclick: () => select({ type: 'overview' }) }, h('span.ico', '📋'), h('span.name', 'Übersicht')));
    tree.append(h('div.tree-label', '🎞 Animations-Dateien'));
    for (const d of S.model.dicts) {
      tree.append(h(`div.tree-item${sel.type === 'dict' && sel.file === d.file ? '.active' : ''}`, { onclick: () => select({ type: 'dict', file: d.file }), title: `${d.name}.ycd` },
        h('span.ico', '📁'), h('span.name', d.name), h('span.pill', `${d.clips.length}`), d.encrypted ? h('span.lock', '🔒') : null));
      for (const c of d.clips) {
        tree.append(h(`div.tree-item.sub${sel.type === 'clip' && sel.file === d.file && sel.id === c.id ? '.active' : ''}`, {
          onclick: () => select({ type: 'clip', file: d.file, id: c.id }), title: `${KIND[c.kind].text} · ${sec(c.duration)}`,
        }, h('span.ico', KIND[c.kind].icon), h('span.name', c.name), h('span.muted.small', sec(c.duration))));
      }
    }
  }

  async function render(keepScroll) {
    const c = $('as-content');
    const scroll = c.scrollTop;
    c.innerHTML = '';
    const s = findSel();
    if (!s || s.type === 'overview') c.append(viewOverview());
    else if (s.type === 'dict') c.append(viewDict(s.dict));
    else c.append(viewClip(s.dict, s.clip));
    c.scrollTop = keepScroll ? scroll : 0;
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
      h('div.content-head', h('div.title-block', h('h1', 'Übersicht'), h('div.subtitle', `${m.dicts.length} Animations-Datei(en) · ${m.clipCount} Clips`))),
      m.warnings.length ? h('div.notice.warn', h('b', 'Hinweise'), h('ul', m.warnings.slice(0, 12).map((w) => h('li', w)))) : null,
      h('div.notice.info',
        h('b', 'So funktionieren GTA-Animationen: '),
        'Eine Animations-Datei (.ycd, „Dictionary“) enthält einen oder mehrere Clips. Im Script brauchst du beide Namen: ',
        h('code', "TaskPlayAnim(ped, 'datei', 'clip', …)"), '.'),
      h('div.actions',
        action('➕', 'Weitere Animationen öffnen', 'Dateien oder Ordner hinzufügen', addDialog),
        action('📦', 'Als FiveM-Resource exportieren', 'Fertiges Script mit Test-Befehl', exportDialog)),
      h('div.section', h('div.section-head', h('h3', 'Alle Clips')),
        h('div.list', m.dicts.flatMap((d) => d.clips.map((c) => h('div.list-row.clickable', { onclick: () => select({ type: 'clip', file: d.file, id: c.id }) },
          h('span', KIND[c.kind].icon),
          h('div.grow', h('div.name', c.name), h('div.muted.small', `${d.name} · ${KIND[c.kind].text}`)),
          h('span.muted.small', sec(c.duration))))))));
  }

  // ---------------------------------------------------------------- Datei (Dictionary)

  function viewDict(d) {
    const wrap = h('div',
      h('div.content-head', h('div.title-block', h('h1', d.name), h('div.subtitle', `Animations-Datei · ${d.clips.length} Clip(s) · ${kb(d.size)}`))));
    if (d.encrypted) { wrap.append(h('div.notice.warn', 'Diese Datei ist verschlüsselt (FiveM-Escrow) und kann nicht gelesen oder bearbeitet werden.')); }
    else if (d.broken) wrap.append(h('div.notice.warn', 'Diese Datei konnte nicht gelesen werden (beschädigt oder unbekanntes Format).'));
    wrap.append(h('div.notice.info', 'Der Dateiname ist der Name, den Scripts bei ', h('code', 'RequestAnimDict'), ' und ', h('code', 'TaskPlayAnim'), ' verwenden.'));
    wrap.append(h('div.actions',
      action('✏', 'Datei umbenennen', 'Neuer Dictionary-Name', () => renameDict(d), d.encrypted),
      action('💾', 'Als .ycd speichern', 'Datei einzeln exportieren', () => exportFile(d, 'ycd'), d.encrypted),
      action('📄', 'Als XML speichern', 'Für CodeWalker oder Blender (Sollumz)', () => exportFile(d, 'xml'), d.encrypted || d.broken),
      action('🗑', 'Datei löschen', 'Mit allen Clips entfernen', () => deleteDict(d))));
    if (d.clips.length) {
      wrap.append(h('div.section', h('div.section-head', h('h3', 'Clips')),
        h('div.list', d.clips.map((c) => h('div.list-row.clickable', { onclick: () => select({ type: 'clip', file: d.file, id: c.id }) },
          h('span', KIND[c.kind].icon),
          h('div.grow', h('div.name', c.name), h('div.muted.small', `${KIND[c.kind].text} · ${c.frames} Frames${c.rate !== 1 ? ` · Tempo ${Math.round(c.rate * 100)} %` : ''}`)),
          h('span.muted.small', sec(c.duration)))))));
    }
    return wrap;
  }

  // ---------------------------------------------------------------- Clip

  function viewClip(d, c) {
    const key = `${d.file}|${c.id}`;
    const wrap = h('div',
      h('div.content-head', h('div.title-block',
        h('h1', c.name),
        h('div.subtitle', `Datei „${d.name}“ · ${sec(c.duration)} · ${c.frames} Frames · ${KIND[c.kind].text}${c.rate !== 1 ? ` · Tempo ${Math.round(c.rate * 100)} %` : ''}`))));
    if (S.blender[key] === 'open') wrap.append(h('div.notice.info', 'In Blender geöffnet. Nach dem Bearbeiten in Blender rechts (Taste N) → „GTA Tool“ → „Speichern & zurück zum GTA Tool“ – die Animation wird automatisch übernommen.'));

    if (c.kind === 'ped' || c.kind === 'face') {
      // Körperwahl + 3D + Abspielleiste
      wrap.append(h('div.livery-chooser',
        h('span.muted.small', 'Vorschau auf:'),
        h(`button.btn.small${S.ped === 'm' ? '.primary' : ''}`, { onclick: () => { S.ped = 'm'; render(true); } }, '👨 Mann'),
        h(`button.btn.small${S.ped === 'f' ? '.primary' : ''}`, { onclick: () => { S.ped = 'f'; render(true); } }, '👩 Frau'),
        h('span.spacer'),
        c.mover ? h('label.check.small', { title: 'Die Figur läuft den Weg aus der Animation mit (Kamera folgt)' },
          h('input', { type: 'checkbox', checked: S.mover || null, onchange: (e) => { S.mover = e.target.checked; render(true); } }), ' Laufweg mitbewegen') : null));
      const viewer = h('div.viewer.tall', h('div.viewer-status', '3D wird vorbereitet …'),
        h('div.viewer-bar', h('button.btn.small', { onclick: () => window.Viewer3D && window.Viewer3D.animFit() }, '⟲ Ansicht'),
          h('span.viewer-hint', 'Linke Maustaste: drehen · Rechte: verschieben · Mausrad: zoomen · Leertaste: Start/Pause')));
      viewer._dict = d; viewer._clip = c;
      wrap.append(viewer);
      wrap.append(playerBar(c));
    } else {
      wrap.append(h('div.notice.info', `${KIND[c.kind].icon} ${KIND[c.kind].text}: `,
        c.kind === 'uv' ? 'bewegt eine Textur auf einem Objekt (z. B. Bildschirme, Schilder) – '
          : c.kind === 'object' ? 'bewegt Teile eines Objekts (z. B. Türen, Ventilatoren) – '
            : 'steuert eine Kamera – ',
        'die 3D-Vorschau gibt es nur für Personen-Animationen. Umbenennen, Tempo, Kopieren und Export funktionieren trotzdem.'));
    }

    // Code für Scripts
    const code = `RequestAnimDict('${d.name}')\nwhile not HasAnimDictLoaded('${d.name}') do Wait(0) end\nTaskPlayAnim(PlayerPedId(), '${d.name}', '${c.name}', 8.0, -8.0, -1, 1, 0, false, false, false)`;
    wrap.append(h('details.mount-panel', { open: S.codeOpen || null, ontoggle: (e) => { S.codeOpen = e.target.open; } },
      h('summary', '🧾 Im eigenen Script benutzen (Lua)'),
      h('pre.code', code),
      h('div.row.gap-s', h('button.btn.small', {
        onclick: async () => { try { await navigator.clipboard.writeText(code); toast('Code kopiert.'); } catch { toast('Kopieren nicht möglich.', 'error'); } },
      }, '📋 Code kopieren'), h('span.muted.small', 'Flag 1 = Wiederholen. Mit 49 läuft die Animation nur am Oberkörper und man kann sich bewegen.'))));

    const isList = c.type === 'list';
    wrap.append(h('div.actions',
      action('🧊', 'In Blender bearbeiten', 'Mit Freemode-Körper und Skelett', () => openBlender(d, c), c.kind !== 'ped' || S.blender[key] === 'open'),
      action('✏', 'Umbenennen', 'Neuer Clip-Name', () => renameClip(d, c)),
      action('⏱', 'Tempo & Länge', isList ? 'Bei diesem Clip-Typ nicht möglich' : 'Schneller, langsamer, zuschneiden', () => timingDialog(d, c), isList),
      action('⧉', 'Duplizieren', 'Kopie in derselben Datei', () => duplicateClip(d, c)),
      action('📤', 'In andere Datei kopieren', 'Oder eine neue Datei daraus machen', () => copyDialog(d, c)),
      action('🗑', 'Löschen', 'Clip entfernen', () => deleteClip(d, c))));
    return wrap;
  }

  function playerBar(c) {
    const playBtn = h('button.btn.primary.small', { title: 'Start / Pause (Leertaste)', onclick: () => { const V = window.Viewer3D; if (V) V.animPlay(!V.animState().playing); } }, '⏸');
    const slider = h('input.grow', { type: 'range', min: 0, max: Math.max(c.duration, 0.01), step: 0.001, value: 0 });
    const label = h('span.muted.small.mono', `0,00 / ${sec(c.duration)}`);
    let dragging = false;
    slider.addEventListener('pointerdown', () => { dragging = true; const V = window.Viewer3D; if (V) V.animPlay(false); });
    slider.addEventListener('pointerup', () => { dragging = false; });
    slider.addEventListener('input', () => { const V = window.Viewer3D; if (V) V.animSeek(Number(slider.value)); });
    const speed = h('select', { title: 'Abspielgeschwindigkeit (nur Vorschau)', onchange: (e) => { S.speed = Number(e.target.value); window.Viewer3D && window.Viewer3D.animSpeed(S.speed); } },
      [0.1, 0.25, 0.5, 1, 1.5, 2].map((v) => h('option', { value: v, selected: v === S.speed || null }, `${String(v).replace('.', ',')}×`)));
    const loop = h('label.check.small', h('input', { type: 'checkbox', checked: S.loop || null, onchange: (e) => { S.loop = e.target.checked; window.Viewer3D && window.Viewer3D.animSetLoop(S.loop); } }), ' Wiederholen');
    const bar = h('div.player-bar', playBtn,
      h('button.btn.small', { title: 'Zum Anfang', onclick: () => window.Viewer3D && window.Viewer3D.animSeek(0) }, '⏮'),
      slider, label, speed, loop);
    bar._update = (st) => {
      playBtn.textContent = st.playing ? '⏸' : '▶';
      if (!dragging) slider.value = st.time;
      label.textContent = `${st.time.toFixed(2).replace('.', ',')} / ${sec(st.duration)}`;
    };
    return bar;
  }

  // ================================================================ 3D

  let token = 0;

  function rigGlb(ped) {
    if (!rigCache.has(ped)) {
      rigCache.set(ped, window.api.invoke('anim:rig', { ped }).then((r) => { if (!r.ok) { rigCache.delete(ped); throw new Error(r.error); } return r.data.glb; }));
    }
    return rigCache.get(ped);
  }

  function sample(d, c) {
    const key = `${d.file}|${c.id}|${S.version}`;
    if (!sampleCache.has(key)) {
      sampleCache.set(key, window.api.invoke('anim:sample', { file: d.file, clip: c.id }).then((r) => { if (!r.ok) { sampleCache.delete(key); throw new Error(r.error); } return r.data; }));
    }
    return sampleCache.get(key);
  }

  async function attachViewer(el) {
    const my = ++token;
    if (!window.Viewer3D) await new Promise((r) => window.addEventListener('viewer3d-ready', r, { once: true }));
    const V = window.Viewer3D;
    V.mount(el);
    const status = el.querySelector('.viewer-status');
    const bar = el.parentElement && el.parentElement.querySelector('.player-bar');
    try {
      if (!V.animHasRig() || S.rigPed !== S.ped) {
        status.textContent = 'Freemode-Körper wird geladen (beim ersten Mal aus GTA, dauert etwas) …';
        const glb = await rigGlb(S.ped);
        if (my !== token) return;
        await V.animLoadRig(glb, { keepCamera: S.rigPed !== null && V.animHasRig() });
        S.rigPed = S.ped;
      }
      status.textContent = 'Animation wird gelesen …';
      const smp = await sample(el._dict, el._clip);
      if (my !== token) return;
      V.animSetLoop(S.loop);
      V.animSpeed(S.speed);
      V.animSetFollow(true);
      V.animOn((st) => { if (bar && bar._update) bar._update(st); });
      const matched = V.animSetClip(smp, { mover: S.mover, play: true });
      status.textContent = matched
        ? `${matched} Bones animiert · ${S.ped === 'f' ? 'weiblicher' : 'männlicher'} Freemode-Körper`
        : 'Keine passenden Bones – diese Animation ist wohl für ein anderes Skelett (z. B. Tier).';
      if (!matched) status.classList.add('err');
      if (bar && bar._update) bar._update(V.animState());
    } catch (e) {
      if (my !== token) return;
      status.textContent = `3D-Vorschau nicht möglich: ${e.message}`;
      status.classList.add('err');
    }
  }

  // ================================================================ Aktionen

  function askName(title, label, value, hint) {
    return new Promise((resolve) => {
      const input = h('input', { type: 'text', value: value || '' });
      let done = false;
      const m = modal({
        title,
        body: h('div', h('div.field', h('label', label), input, hint ? h('div.hint', hint) : null)),
        buttons: [
          { label: 'Abbrechen' },
          { label: 'OK', primary: true, onClick: () => { done = true; resolve(input.value.trim()); } },
        ],
        onClose: () => { if (!done) resolve(null); },
      });
      setTimeout(() => { input.focus(); input.select(); }, 30);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { done = true; resolve(input.value.trim()); m.close(); } });
    });
  }

  const NAME_HINT = 'Nur Kleinbuchstaben, Zahlen und _ - @ . (keine Leerzeichen).';

  async function renameClip(d, c) {
    const name = await askName('Clip umbenennen', 'Neuer Clip-Name', c.name, NAME_HINT);
    if (!name || name === c.name) return;
    const r = await call('anim:renameClip', { file: d.file, clip: c.id, name }, { busyText: 'Wird umbenannt …' });
    if (r) { applyResult(r, { type: 'clip', file: d.file, id: r.extra.id }); toast('Clip umbenannt.'); }
  }

  async function duplicateClip(d, c) {
    const name = await askName('Clip duplizieren', 'Name der Kopie', `${c.name}_kopie`, NAME_HINT);
    if (!name) return;
    const r = await call('anim:duplicateClip', { file: d.file, clip: c.id, name }, { busyText: 'Wird dupliziert …' });
    if (r) { applyResult(r, { type: 'clip', file: d.file, id: r.extra.id }); toast('Kopie angelegt – z. B. mit anderem Tempo nutzen.'); }
  }

  async function deleteClip(d, c) {
    const last = d.clips.length === 1;
    if (!await UI.confirm('Clip löschen?', last ? `„${c.name}“ ist der einzige Clip – die ganze Datei „${d.name}“ wird entfernt.` : `„${c.name}“ aus „${d.name}“ entfernen?`, 'Löschen', { danger: true })) return;
    const r = await call('anim:deleteClip', { file: d.file, clip: c.id }, { busyText: 'Wird gelöscht …' });
    if (r) applyResult(r, last ? { type: 'overview' } : { type: 'dict', file: d.file });
  }

  async function renameDict(d) {
    const name = await askName('Animations-Datei umbenennen', 'Neuer Dateiname (Dictionary)', d.name, `${NAME_HINT} Scripts, die den alten Namen benutzen, müssen angepasst werden.`);
    if (!name || name === d.name) return;
    const r = await call('anim:renameDict', { file: d.file, name }, { busyText: 'Wird umbenannt …' });
    if (r) { applyResult(r, { type: 'dict', file: r.extra.file }); toast('Datei umbenannt.'); }
  }

  async function deleteDict(d) {
    if (!await UI.confirm('Datei löschen?', `„${d.name}.ycd“ mit allen ${d.clips.length} Clips entfernen?`, 'Löschen', { danger: true })) return;
    const r = await call('anim:deleteDict', { file: d.file }, { busyText: 'Wird gelöscht …' });
    if (r) applyResult(r, { type: 'overview' });
  }

  async function exportFile(d, format) {
    const r = await call('anim:exportFile', { file: d.file, format }, { busyText: 'Wird gespeichert …' });
    if (r) toast(`Gespeichert: ${r.file}`);
  }

  function timingDialog(d, c) {
    const total = c.animDuration || c.duration;
    const st = { rate: c.rate || 1, start: c.start || 0, end: c.end || total };
    const info = h('div.notice.info');
    const upd = () => { info.textContent = `Neue Länge: ${sec((st.end - st.start) / st.rate)} (vorher ${sec(c.duration)})`; };
    const rateOut = h('span.mono');
    const row = (label, el, out) => h('label.pos-row', h('span', label), el, out || null);
    const rate = h('input', { type: 'range', min: 10, max: 400, step: 5, value: Math.round(st.rate * 100), oninput: (e) => { st.rate = Number(e.target.value) / 100; rateOut.textContent = `${e.target.value} %`; upd(); } });
    rateOut.textContent = `${Math.round(st.rate * 100)} %`;
    const startOut = h('span.mono', sec(st.start));
    const endOut = h('span.mono', sec(st.end));
    const start = h('input', { type: 'range', min: 0, max: total, step: 0.01, value: st.start });
    const end = h('input', { type: 'range', min: 0, max: total, step: 0.01, value: st.end });
    start.addEventListener('input', () => { st.start = Math.min(Number(start.value), st.end - 0.05); start.value = st.start; startOut.textContent = sec(st.start); upd(); });
    end.addEventListener('input', () => { st.end = Math.max(Number(end.value), st.start + 0.05); end.value = st.end; endOut.textContent = sec(st.end); upd(); });
    upd();
    modal({
      title: `Tempo & Länge – ${c.name}`,
      body: h('div',
        h('p.muted', 'Ändert, wie schnell der Clip im Spiel läuft und welcher Ausschnitt der Animation benutzt wird. Die Bewegung selbst bleibt erhalten.'),
        h('div.pos-grid',
          row('Tempo', rate, rateOut),
          row('Anfang', start, startOut),
          row('Ende', end, endOut)),
        info),
      buttons: [
        { label: 'Abbrechen' },
        { label: 'Zurücksetzen', onClick: async () => {
          const r = await call('anim:setTiming', { file: d.file, clip: c.id, rate: 1, start: 0, end: total }, { busyText: 'Wird gespeichert …' });
          if (r) applyResult(r, { type: 'clip', file: d.file, id: c.id });
        } },
        { label: 'Übernehmen', primary: true, onClick: async () => {
          const r = await call('anim:setTiming', { file: d.file, clip: c.id, rate: st.rate, start: st.start, end: st.end }, { busyText: 'Wird gespeichert …' });
          if (r) { applyResult(r, { type: 'clip', file: d.file, id: c.id }); toast('Tempo und Länge übernommen.'); }
          else return true;
          return false;
        } },
      ],
    });
  }

  function copyDialog(d, c) {
    const others = S.model.dicts.filter((x) => x.file !== d.file && !x.encrypted && !x.broken);
    const target = h('select', others.map((x) => h('option', { value: x.file }, x.name)), h('option', { value: '' }, '➕ Neue Datei …'));
    const newDict = h('input', { type: 'text', placeholder: 'z. B. meine@animationen' });
    const newName = h('input', { type: 'text', value: c.name });
    const newRow = h('div.field', h('label', 'Name der neuen Datei'), newDict, h('div.hint', NAME_HINT));
    const sync = () => { newRow.style.display = target.value ? 'none' : ''; };
    target.addEventListener('change', sync);
    sync();
    modal({
      title: `„${c.name}“ kopieren`,
      body: h('div',
        h('p.muted', 'Kopiert den Clip mit seiner Bewegung in eine andere Animations-Datei – so kannst du Animationen aus verschiedenen Paketen zusammenstellen.'),
        h('div.field', h('label', 'Ziel'), target),
        newRow,
        h('div.field', h('label', 'Clip-Name im Ziel'), newName)),
      buttons: [
        { label: 'Abbrechen' },
        { label: 'Kopieren', primary: true, onClick: async () => {
          let r;
          if (target.value) {
            r = await call('anim:copyClip', { file: d.file, clip: c.id, target: target.value, name: newName.value.trim() }, { busyText: 'Wird kopiert …' });
            if (r) { applyResult(r, { type: 'clip', file: target.value, id: r.extra.id }); toast('Clip kopiert.'); }
          } else {
            if (!newDict.value.trim()) { toast('Bitte einen Namen für die neue Datei eingeben.', 'warn'); return true; }
            r = await call('anim:newDictFromClip', { file: d.file, clip: c.id, name: newDict.value.trim() }, { busyText: 'Neue Datei wird erstellt …' });
            if (!r) return true;
            const nf = r.extra.file;
            let id = c.id;
            if (newName.value.trim() && newName.value.trim() !== c.name) {
              const r2 = await call('anim:renameClip', { file: nf, clip: c.id, name: newName.value.trim() }, { busyText: 'Wird umbenannt …' });
              if (r2) { r = r2; id = r2.extra.id; }
            }
            applyResult(r, { type: 'clip', file: nf, id });
            toast('Neue Datei erstellt.');
          }
          return r ? false : true;
        } },
      ],
    });
  }

  async function openBlender(d, c) {
    const r = await call('anim:blender', { file: d.file, clip: c.id, clipName: c.name, ped: S.ped }, { busyText: 'Blender wird vorbereitet …' });
    if (!r) return;
    S.blender[`${d.file}|${c.id}`] = 'open';
    toast('Blender startet mit Körper und Animation. Zum Übernehmen: Seitenleiste (N) → „GTA Tool“ → „Speichern & zurück“.', 'info', 10000);
    render(true);
  }

  async function exportDialog() {
    if (!S.model) return;
    const settings = await call('settings:get');
    const defName = (S.model.dicts[0] && S.model.dicts[0].name.replace(/[^a-z0-9_-]/gi, '_')) || 'animationen';
    const nameIn = h('input', { type: 'text', value: defName });
    const dirIn = h('input', { type: 'text', value: (settings && (settings.lastResourceDir || settings.lastExportDir)) || '', placeholder: 'Bitte Ordner wählen …' });
    const testCmd = h('input', { type: 'checkbox', checked: true });
    const localField = h('div.field', h('label', 'Zielordner'), h('div.input-row', dirIn, h('button.btn', {
      onclick: async () => { const r = await call('dialog:open', { kind: 'outdir' }); if (r && r[0]) dirIn.value = r[0]; },
    }, 'Durchsuchen …')));
    const target = Sftp.targetField(localField, dirIn);
    modal({
      title: 'Animationen als FiveM-Resource exportieren',
      body: h('div',
        h('p.muted', 'Erstellt eine fertige Resource: stream-Ordner mit allen .ycd-Dateien, fxmanifest.lua und LIESMICH.txt mit allen Datei- und Clip-Namen.'),
        h('div.field', h('label', 'Name der Resource'), nameIn),
        target.el,
        localField,
        h('label.check', testCmd, ' Test-Befehl einbauen (', h('code', '/animtest datei clip'), ' und ', h('code', '/animstop'), ')')),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: '📦 Exportieren', primary: true,
          onClick: async () => {
            const resourceName = nameIn.value.trim();
            const r = await Sftp.runExport(target.get(), resourceName,
              (outDir) => call('anim:export', { outDir, resourceName, testCommand: testCmd.checked }, { busyText: 'Resource wird erstellt …' }));
            if (!r) return true;
            S.dirty = false; updateStatus();
            modal({
              title: '✓ Export fertig',
              body: h('div', Sftp.resultPath(r),
                h('p', `${r.files} Animations-Datei(en) · ${r.clips} Clips`),
                r.warnings.length ? h('div.notice.warn', h('ul', r.warnings.map((w) => h('li', w)))) : null,
                h('div.notice.info', r.remote ? 'server.cfg: ' : 'Ordner nach „resources“ kopieren → server.cfg: ', h('code', `ensure ${base(r.folder)}`),
                  testCmd.checked ? h('span', ' · Im Spiel testen: ', h('code', '/animtest datei clip')) : null)),
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
