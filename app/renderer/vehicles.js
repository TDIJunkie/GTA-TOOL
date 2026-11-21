'use strict';
// Vehicle Studio: Öffnen, Übersicht, Modelle (Blender), Texturen, FiveM-Export

const VehicleStudio = (() => {
  const { h, call, toast, modal, kb, base } = UI;

  const S = {
    model: null,          // Analyse-Ergebnis aus dem Backend
    sel: null,            // {type, vehicle, part, file}
    open: new Set(),      // aufgeklappte Fahrzeuge
    blender: {},          // model -> 'open' | 'applied' | 'error'
    texCache: {},         // file -> list result
    dirty: false,
    paint: '#9ea3aa',
    mounted: {},          // Fahrzeug -> { Kategorie: Teilname }  (nur Vorschau)
  };

  // 3D-Vorschau: Cache je Datei, wird bei jeder Änderung geleert
  const preview = { cache: new Map(), lastKey: null, token: 0 };
  function previewChanged() { preview.cache.clear(); }

  const $ = (id) => document.getElementById(id);

  // ================================================================ Init / Öffnen

  function init() {
    document.querySelectorAll('[data-open]').forEach((b) => b.addEventListener('click', () => pickAndOpen(b.dataset.open, false)));
    $('vs-add').addEventListener('click', () => addDialog());
    $('vs-close').addEventListener('click', closeAll);
    $('vs-export').addEventListener('click', exportDialog);

    // Drag & Drop: leer → öffnen, geladen → hinzufügen
    const view = $('view-vehicles');
    const dz = $('dropzone');
    view.addEventListener('dragover', (e) => { e.preventDefault(); dz.classList.add('over'); });
    view.addEventListener('dragleave', (e) => { if (e.target === dz || !view.contains(e.relatedTarget)) dz.classList.remove('over'); });
    view.addEventListener('drop', (e) => {
      e.preventDefault();
      dz.classList.remove('over');
      const paths = [...e.dataTransfer.files].map((f) => window.api.pathForFile(f)).filter(Boolean);
      if (paths.length) open(paths, !!S.model);
    });

    window.api.on('blender', onBlender);
    window.api.on('audio:updated', ({ name, note }) => {
      S.dirty = true;
      updateStatus();
      toast(`Sound „${name}“ wurde übernommen.${note ? ' ' + note : ''}`, note ? 'warn' : 'ok', 8000);
    });
    window.api.on('tex:updated', async ({ file, name }) => {
      S.dirty = true;
      delete S.texCache[file];
      previewChanged();
      toast(`Textur „${name}“ wurde übernommen.`);
      if (S.sel && (S.sel.file === file)) await render(true, name);
    });
  }

  function show() {
    renderAll();
  }

  async function pickAndOpen(kind, add) {
    const paths = await call('dialog:open', { kind });
    if (paths && paths.length) await open(paths, add);
  }

  function addDialog() {
    modal({
      title: 'Weitere Dateien hinzufügen',
      body: h('p.muted', 'Die Dateien werden zur aktuellen Auswahl hinzugefügt (z. B. Felgen oder ein zweites Fahrzeug).'),
      buttons: [
        { label: '🌐 Vom Server (SFTP)', onClick: () => Sftp.importTo(VehicleStudio, true) },
        { label: '📁 Ordner importieren', onClick: () => pickAndOpen('folder', true) },
        { label: '📄 Dateien öffnen', primary: true, onClick: () => pickAndOpen('files', true) },
      ],
    });
  }

  async function open(paths, add) {
    if (!add && S.model && S.dirty) {
      const ok = await UI.confirm('Neu öffnen?', 'Nicht exportierte Änderungen gehen verloren. Fortfahren?', 'Öffnen', { danger: true });
      if (!ok) return;
    }
    const r = await call('session:open', { paths, add }, { busyText: 'Dateien werden gelesen …' });
    if (!r) {
      if (!App.keys.loaded) toast('Tipp: Verschlüsselte dlc.rpf brauchen die GTA-Schlüssel. Bitte den GTA-V-Ordner in den Einstellungen angeben.', 'warn', 9000);
      return;
    }
    S.model = r.model;
    S.texCache = {};
    if (!add) { S.dirty = false; S.open = new Set(); S.sel = null; }
    if (S.model.vehicles.length === 1) S.open.add(S.model.vehicles[0].model);
    if (!S.sel) S.sel = S.model.vehicles.length ? { type: 'vehicle', vehicle: S.model.vehicles[0].model } : { type: 'overview' };
    for (const w of r.warnings || []) toast(w, 'warn', 7000);
    const n = S.model.vehicles.length;
    toast(n ? `${n} Fahrzeug${n > 1 ? 'e' : ''} geladen.` : 'Dateien geladen.');
    renderAll();
  }

  async function reanalyze() {
    const m = await call('session:analyze');
    if (m) S.model = m;
  }

  async function closeAll() {
    if (S.dirty) {
      const ok = await UI.confirm('Schließen?', 'Nicht exportierte Änderungen gehen verloren. Wirklich schließen?', 'Schließen', { danger: true });
      if (!ok) return;
    }
    await call('session:close');
    Object.assign(S, { model: null, sel: null, open: new Set(), texCache: {}, dirty: false });
    renderAll();
  }

  // ================================================================ Rendering

  function renderAll() {
    const loaded = !!S.model;
    $('vs-empty').classList.toggle('hidden', loaded);
    $('vs-main').classList.toggle('hidden', !loaded);
    $('vs-export').disabled = !loaded;
    if (!loaded) { $('vs-status-text').textContent = 'Öffne ein Fahrzeug, um zu beginnen.'; return; }
    $('vs-source').textContent = `Geöffnet: ${S.model.sources.join(', ') || '–'}`;
    const v = S.model.vehicles.length, p = S.model.vehicles.reduce((a, x) => a + x.parts.length, 0);
    $('vs-status-text').textContent = `${v} Fahrzeug${v === 1 ? '' : 'e'} · ${p} Tuningteile · ${S.model.isReplace ? 'Replace' : 'Addon'}${S.dirty ? ' · ● Änderungen nicht exportiert' : ''}`;
    renderTree();
    render();
  }

  function isSel(o) {
    const s = S.sel;
    return s && s.type === o.type && s.vehicle === o.vehicle && s.file === o.file;
  }

  function item(icon, label, selObj, { lock, title, extra } = {}) {
    return h(`div.tree-item${isSel(selObj) ? '.active' : ''}`, {
      title: title || label,
      onclick: () => select(selObj),
    }, h('span.ico', icon), h('span.name', label), extra || null, lock ? h('span.lock', { title: 'verschlüsselt' }, '🔒') : null);
  }

  function select(o) {
    S.sel = o;
    renderTree();
    render();
  }

  function renderTree() {
    const tree = $('vs-tree');
    tree.innerHTML = '';
    tree.append(item('📋', 'Übersicht', { type: 'overview' }));
    if (S.model.soundOnly || (!S.model.vehicles.length && S.model.audioFiles)) tree.append(item('🔊', 'Sound-Pack', { type: 'sound', vehicle: null, file: 'sound:' }));

    for (const v of S.model.vehicles) {
      const isOpen = S.open.has(v.model);
      const title = h(`div.tree-title${isOpen ? '.open' : ''}${isSel({ type: 'vehicle', vehicle: v.model }) ? '.active' : ''}`, {
        onclick: () => {
          if (isSel({ type: 'vehicle', vehicle: v.model })) { isOpen ? S.open.delete(v.model) : S.open.add(v.model); }
          else S.open.add(v.model);
          select({ type: 'vehicle', vehicle: v.model });
        },
        title: v.model,
      }, h('span.chev', '▶'), h('span', '🚗'), h('span.name', v.displayName));
      const group = h('div.tree-group', title);
      if (isOpen) {
        const sub = h('div.tree-sub');
        sub.append(h('div.tree-label', 'Modell'));
        if (v.file) sub.append(item('🚙', `Karosserie (${base(v.file)})`, { type: 'model', vehicle: v.model, file: v.file }, { lock: v.encrypted }));
        const wheels = v.parts.filter((p) => p.category === 'Felgen');
        const parts = v.parts.filter((p) => p.category !== 'Felgen');
        if (parts.length) {
          sub.append(h('div.tree-label', `Tuningteile (${parts.length})`));
          for (const p of parts.sort((a, b) => a.category.localeCompare(b.category) || a.name.localeCompare(b.name))) {
            sub.append(item('🔧', `${p.category}: ${p.name}`, { type: 'model', vehicle: v.model, file: p.file }, { lock: p.encrypted, title: `${p.name}${p.label ? ' – ' + p.label : ''}` }));
          }
        }
        if (wheels.length) {
          sub.append(h('div.tree-label', `Felgen (${wheels.length})`));
          for (const p of wheels) sub.append(item('⭕', p.name, { type: 'model', vehicle: v.model, file: p.file }, { lock: p.encrypted }));
        }
        sub.append(h('div.tree-label', 'Sound'));
        sub.append(item('🔊', `Motorsound${S.model.audioFiles ? '' : ' (Standard)'}`, { type: 'sound', vehicle: v.model, file: `sound:${v.model}` }));
        if (v.file && !v.encrypted) {
          sub.append(h('div.tree-label', 'Folierungen'));
          sub.append(item('🖌', 'Folierungen (Livery)', { type: 'livery', vehicle: v.model, file: `livery:${v.model}` }));
        }
        const ytds = [...v.ytds, ...v.parts.filter((p) => p.ytd).map((p) => p.ytd)];
        if (ytds.length) {
          sub.append(h('div.tree-label', 'Texturen'));
          for (const y of ytds) sub.append(item('🎨', base(y), { type: 'ytd', vehicle: v.model, file: y }));
        }
        group.append(sub);
      }
      tree.append(group);
    }

    const others = [...S.model.unassignedModels.map((p) => ({ file: p.file, encrypted: p.encrypted, part: p })), ...S.model.otherFiles];
    if (others.length) {
      tree.append(h('div.tree-label', 'Weitere Dateien'));
      for (const o of others) {
        const f = o.file;
        if (f.endsWith('.ytd')) tree.append(item('🎨', base(f), { type: 'ytd', file: f }, { lock: o.encrypted }));
        else if (f.endsWith('.yft')) tree.append(item('🔧', base(f), { type: 'model', file: f }, { lock: o.encrypted }));
        else tree.append(item('📄', base(f), { type: 'other', file: f }));
      }
    }
    if (S.model.dataFiles.length) {
      tree.append(h('div.tree-label', 'Meta-Dateien'));
      tree.append(item('📝', `${S.model.dataFiles.length} Dateien`, { type: 'meta' }));
    }
  }

  const vehicleOf = (m) => S.model.vehicles.find((v) => v.model === m);
  function findModel(file) {
    for (const v of S.model.vehicles) {
      if (v.file === file) return { vehicle: v, part: null, name: v.model, encrypted: v.encrypted, hiFile: v.hiFile, size: v.size, label: 'Karosserie' };
      const p = v.parts.find((x) => x.file === file);
      if (p) return { vehicle: v, part: p, name: p.name, encrypted: p.encrypted, hiFile: p.hiFile, size: p.size, label: p.category };
    }
    const u = S.model.unassignedModels.find((x) => x.file === file);
    if (u) return { vehicle: null, part: u, name: u.name, encrypted: u.encrypted, hiFile: u.hiFile, size: u.size, label: u.category };
    const o = S.model.otherFiles.find((x) => x.file === file);
    return { vehicle: null, part: null, name: base(file).replace(/\.yft$/, ''), encrypted: o && o.encrypted, size: o && o.size, label: 'Modell' };
  }

  async function render(keepScroll = false, flashName = null) {
    const c = $('vs-content');
    const scroll = c.scrollTop;
    const s = S.sel || { type: 'overview' };
    c.innerHTML = '';
    if (s.type === 'overview') c.append(viewOverview());
    else if (s.type === 'vehicle') c.append(viewVehicle(vehicleOf(s.vehicle)));
    else if (s.type === 'model') c.append(await viewModel(s));
    else if (s.type === 'ytd') c.append(await viewYtd(s.file, flashName));
    else if (s.type === 'meta') c.append(viewMeta());
    else if (s.type === 'livery') c.append(await viewLivery(vehicleOf(s.vehicle)));
    else if (s.type === 'sound') c.append(await viewSound(s.vehicle ? vehicleOf(s.vehicle) : null));
    else c.append(viewOther(s.file));
    if (keepScroll) c.scrollTop = scroll;
    const vEl = c.querySelector('.viewer');
    if (vEl) attachViewer(vEl);
  }

  function warningsBox(list) {
    if (!list || !list.length) return null;
    return h('div.notice.warn', h('b', 'Hinweise'), h('ul', list.map((w) => h('li', w))));
  }

  // ---------------------------------------------------------------- Übersicht

  function viewOverview() {
    const m = S.model;
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', 'Übersicht'), h('div.subtitle', `Quelle: ${m.sources.join(', ')}`))),
      warningsBox(m.warnings),
      m.isReplace ? h('div.notice.info', 'Keine vehicles.meta gefunden – das ist ein Replace: die Fahrzeuge ersetzen gleichnamige GTA-Fahrzeuge. Öffne ein Fahrzeug und klicke „In Addon umwandeln“, um daraus ein eigenes Fahrzeug zu machen.') : null,
      m.soundOnly ? h('div.notice.info', h('b', 'Das ist ein reiner Sound-Pack (Motorsound) ohne Fahrzeug. '),
        'Du kannst ihn anhören, bearbeiten und als eigene FiveM-Resource exportieren. ',
        h('button.btn.small', { onclick: () => select({ type: 'sound', vehicle: null, file: 'sound:' }) }, '🔊 Sounds öffnen')) : null,
      h('div.section',
        h('div.section-head', h('h3', 'Fahrzeuge')),
        m.vehicles.length
          ? h('div.list', m.vehicles.map((v) => h('div.list-row',
            h('span', '🚗'),
            h('div.grow', h('div.name', v.displayName), h('div.muted.small', `Spawn-Name: ${v.model} · ${v.parts.length} Tuningteile · ${kb(v.size)}`)),
            v.encrypted ? h('span.pill.yellow', 'verschlüsselt') : null,
            h('button.btn.small', { onclick: () => { S.open.add(v.model); select({ type: 'vehicle', vehicle: v.model }); } }, 'Öffnen'))))
          : h('p.muted', 'Keine Fahrzeuge erkannt. Einzelne Dateien findest du links unter „Weitere Dateien“.')),
      h('div.actions',
        action('📦', 'Als FiveM-Resource exportieren', 'Fertiges Script für deinen Server', exportDialog)));
  }

  function action(icon, title, text, onClick, disabled) {
    return h('button.action', { onclick: onClick, disabled: disabled || null },
      h('span.a-ico', icon), h('span', h('div.a-title', title), h('div.a-text', text)));
  }

  // ---------------------------------------------------------------- Fahrzeug

  function viewVehicle(v) {
    if (!v) return h('p', 'Fahrzeug nicht gefunden.');
    const card = (k, val) => h('div.card', h('div.k', k), h('div.v', val || '–'));
    const warn = [];
    if (v.encrypted) warn.push('Das Modell ist verschlüsselt (FiveM-Escrow). Es kann exportiert, aber nicht bearbeitet werden.');
    if (!v.file) warn.push(`Die Modelldatei ${v.model}.yft fehlt.`);
    if (!v.ytds.length) warn.push(`Keine Texturdatei (${v.txdName || v.model}.ytd) gefunden.`);
    const bState = S.blender[v.model];
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', v.displayName), h('div.subtitle', `Spawn-Name: `, h('code', v.model)))),
      warningsBox(warn),
      viewerPanel(v.encrypted || !v.file ? null : carSpec(v)),
      v.encrypted || !v.file ? null : mountPanel(v),
      bState === 'open' ? h('div.notice.info', 'Die Karosserie ist in Blender geöffnet. Klicke dort in der Seitenleiste (Taste N) unter „GTA Tool“ auf „Speichern & zurück zum GTA Tool“.') : null,
      !v.hasMeta ? h('div.notice.warn',
        h('b', 'Replace-Fahrzeug: '), `Ersetzt im Spiel das GTA-Fahrzeug „${v.model}“. `,
        'Mit „In Addon umwandeln“ wird daraus ein eigenes Fahrzeug mit neuem Spawn-Namen – das Original bleibt im Spiel erhalten.') : null,
      h('div.actions',
        !v.hasMeta
          ? action('🔁', 'In Addon umwandeln', 'Eigener Spawn-Name, Anzeigename, Marke', () => convertDialog(v))
          : action('✏', 'Namen ändern', 'Spawn-Name, Anzeigename, Marke', () => renameDialog(v)),
        action('🧊', 'Karosserie in Blender bearbeiten', 'Öffnet das Modell mit Sollumz', () => blender(v.model, v.ytds), !v.file || v.encrypted || bState === 'open'),
        action('🎨', 'Texturen bearbeiten', v.ytds.length ? base(v.ytds[0]) : 'keine Texturdatei', () => select({ type: 'ytd', vehicle: v.model, file: v.ytds[0] }), !v.ytds.length),
        action('📦', 'Als FiveM-Resource exportieren', 'Fertiges Script für deinen Server', exportDialog)),
      h('div.cards',
        card('Spawn-Name', v.model), card('Marke', v.make), card('Handling', v.handlingId),
        card('Typ', (v.type || '').replace('VEHICLE_TYPE_', '')), card('Modellgröße', kb(v.size)),
        card('Tuningteile', String(v.parts.length))),
      v.parts.length ? h('div.section',
        h('div.section-head', h('h3', 'Tuningteile & Felgen')),
        h('div.list', v.parts.slice().sort((a, b) => a.category.localeCompare(b.category)).map((p) => h('div.list-row',
          h('span', p.category === 'Felgen' ? '⭕' : '🔧'),
          h('div.grow', h('div.name', p.name), h('div.muted.small', `${p.category}${p.label ? ' · ' + p.label : ''} · ${kb(p.size)}`)),
          p.encrypted ? h('span.pill.yellow', 'verschlüsselt') : null,
          S.blender[p.name] === 'open' ? h('span.pill.green', 'in Blender') : null,
          h('button.btn.small', { onclick: () => select({ type: 'model', vehicle: v.model, file: p.file }) }, 'Details'),
          h('button.btn.small', { disabled: p.encrypted || S.blender[p.name] === 'open' || null, onclick: () => blender(p.name, [...v.ytds, ...(p.ytd ? [p.ytd] : [])]) }, 'In Blender'))))) : null);
  }

  // ---------------------------------------------------------------- Modell (yft)

  async function viewModel(s) {
    const info = findModel(s.file);
    const v = info.vehicle;
    const ytds = [...(v ? v.ytds : []), ...(info.part && info.part.ytd ? [info.part.ytd] : [])];
    const bState = S.blender[info.name];
    const wrap = h('div',
      h('div.content-head', h('div.title-block', h('h1', info.name), h('div.subtitle', `${info.label}${v ? ' · ' + v.displayName : ''}`))),
      info.encrypted ? h('div.notice.warn', 'Diese Datei ist verschlüsselt (FiveM-Escrow) und kann nicht bearbeitet werden.') : null,
      viewerPanel(info.encrypted ? null : (v && info.part && v.file && !v.encrypted
        ? { main: { file: v.hiFile || v.file, ytds: v.ytds }, overlay: { file: info.hiFile || s.file, ytds, attach: v.file, bone: info.part.bone || 'chassis', livery: liveryFor(v) }, ghost: true }
        : { main: { file: info.hiFile || s.file, ytds } })),
      bState === 'open' ? h('div.notice.info', 'In Blender geöffnet. Nach dem Bearbeiten in Blender rechts in der Seitenleiste (Taste N) → „GTA Tool“ → „Speichern & zurück zum GTA Tool“ klicken. Das Modell wird dann automatisch übernommen.') : null,
      h('div.actions',
        action('🧊', 'In Blender bearbeiten', 'Bearbeiten und direkt zurück importieren', () => blender(info.name, ytds), info.encrypted || bState === 'open'),
        action('💾', 'Datei exportieren', `${base(s.file)} speichern`, async () => {
          const r = await call('file:export', { file: s.file });
          if (r) toast(`Gespeichert: ${r.file}`);
        }),
        action('♻', 'Datei ersetzen', 'Durch eine andere .yft ersetzen', async () => {
          const p = await call('dialog:open', { kind: 'yft' });
          if (!p || !p[0]) return;
          const r = await call('file:replace', { file: s.file, source: p[0] }, { busyText: 'Datei wird ersetzt …' });
          if (r) { S.dirty = true; previewChanged(); toast(`${base(s.file)} wurde ersetzt.`); await reanalyze(); renderAll(); }
        })),
      h('div.cards',
        h('div.card', h('div.k', 'Datei'), h('div.v', base(s.file))),
        h('div.card', h('div.k', 'Größe'), h('div.v', kb(info.size))),
        h('div.card', h('div.k', 'Hohe Detailstufe (_hi)'), h('div.v', info.hiFile ? 'vorhanden' : 'nein')),
        ytds.length ? h('div.card', h('div.k', 'Texturdateien'), h('div.v', ytds.map(base).join(', '))) : null));
    if (!info.encrypted) {
      const sec = h('div.section', h('div.section-head', h('h3', 'Eingebettete Texturen')), h('p.muted', 'Wird geladen …'));
      wrap.append(sec);
      const list = await loadTextures(s.file);
      sec.lastChild.remove();
      if (list && list.textures.length) sec.append(textureGrid(s.file, list));
      else sec.append(h('p.muted', 'Dieses Modell hat keine eingebetteten Texturen – die Texturen liegen in der .ytd.'));
    }
    return wrap;
  }

  // ---------------------------------------------------------------- Texturdatei (ytd)

  async function viewYtd(file, flashName) {
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', base(file)), h('div.subtitle', 'Texturdatei'))));
    const owner = S.sel && S.sel.vehicle ? vehicleOf(S.sel.vehicle) : null;
    if (owner && owner.file && !owner.encrypted) wrap.append(viewerPanel(carSpec(owner), true));
    const list = await loadTextures(file);
    if (!list) { wrap.append(h('div.notice.err', 'Die Texturdatei konnte nicht gelesen werden.')); return wrap; }
    wrap.append(h('div.notice.info',
      h('b', 'So bearbeitest du Texturen: '),
      '„Ersetzen“ öffnet die Textur direkt in Paint.NET – bearbeiten oder neues Bild einfügen, speichern (Strg+S), fertig: das Tool übernimmt sie automatisch. ',
      '„Datei …“ ersetzt sie durch eine fertige Bilddatei. Format und Mipmaps werden automatisch erzeugt.'));
    wrap.append(textureGrid(file, list, flashName));
    return wrap;
  }

  async function loadTextures(file, force) {
    if (!force && S.texCache[file]) return S.texCache[file];
    const r = await call('tex:list', { file });
    if (r) S.texCache[file] = r;
    return r;
  }

  function textureGrid(file, list, flashName) {
    const selected = new Set();
    const grid = h('div.tex-grid');
    const count = h('span.muted.small', '');
    const updateCount = () => {
      count.textContent = selected.size ? `${selected.size} ausgewählt` : `${list.textures.length} Texturen`;
      delBtn.disabled = !selected.size;
    };

    const refresh = async (name) => {
      delete S.texCache[file];
      previewChanged();
      S.dirty = true;
      await render(true, name);
      updateStatus();
    };

    const exportSel = async (format) => {
      const names = selected.size ? [...selected] : [];
      const r = await call('tex:export', { file, names, format }, { busyText: 'Texturen werden exportiert …' });
      if (r) {
        toast(`${r.files.length} Textur(en) exportiert.`);
        for (const w of r.warnings) toast(w, 'warn');
        call('shell:open', { target: r.outDir });
      }
    };
    const importTex = async () => {
      const imgs = await call('dialog:open', { kind: 'images' });
      if (!imgs || !imgs.length) return;
      const r = await call('tex:add', { file, images: imgs }, { busyText: 'Texturen werden importiert …' });
      if (r) { toast(`${r.added.length} Textur(en) importiert: ${r.added.join(', ')}. Gleichnamige wurden ersetzt.`); await refresh(); }
    };
    const delBtn = h('button.btn.danger', {
      disabled: true,
      onclick: async () => {
        if (!await UI.confirm('Texturen löschen?', `${selected.size} Textur(en) aus ${base(file)} entfernen?`, 'Löschen', { danger: true })) return;
        const r = await call('tex:remove', { file, names: [...selected] }, { busyText: 'Wird gelöscht …' });
        if (r) { toast(`${r.removed} Textur(en) gelöscht.`); await refresh(); }
      },
    }, '🗑 Löschen');

    const toolbar = h('div.tex-toolbar',
      h('button.btn', { onclick: () => exportSel('png') }, '⬇ Exportieren (PNG)'),
      h('button.btn', { onclick: () => exportSel('dds') }, '⬇ Exportieren (DDS)'),
      h('button.btn', { onclick: importTex }, '⬆ Textur importieren'),
      delBtn,
      h('div.spacer'),
      count);

    for (const t of list.textures) {
      const check = h('input.tex-check', { type: 'checkbox', title: 'Auswählen' });
      const card = h(`div.tex${flashName && flashName === t.name ? '.flash' : ''}`,
        check,
        h('div.tex-img', { onclick: () => previewModal(file, t, refresh) }, t.thumb ? h('img', { src: t.thumb, loading: 'lazy' }) : h('span.muted.small', 'keine Vorschau')),
        h('div.tex-info',
          h('div.tex-name', { title: t.name }, t.name),
          h('div.tex-meta', `${t.width}×${t.height} · ${t.format} · ${t.mips} Mips`)),
        h('div.tex-btns',
          h('button.btn', { title: 'In Paint.NET öffnen – speichern ersetzt die Textur automatisch', onclick: () => editTex(file, t.name) }, 'Ersetzen'),
          h('button.btn', { title: 'Durch eine Bilddatei ersetzen (PNG, JPG, DDS …)', onclick: () => replaceTex(file, t.name, refresh) }, 'Datei …')));
      check.addEventListener('change', () => {
        if (check.checked) selected.add(t.name); else selected.delete(t.name);
        card.classList.toggle('selected', check.checked);
        updateCount();
      });
      grid.append(card);
    }
    updateCount();
    return h('div', toolbar, grid);
  }

  async function editTex(file, name) {
    const r = await call('tex:edit', { file, name }, { busyText: 'Textur wird im Bildprogramm geöffnet …' });
    if (r) toast(`„${name}“ ist in Paint.NET geöffnet. Einfach speichern (Strg+S) – das Tool übernimmt die Änderung automatisch.`, 'info', 9000);
  }

  async function replaceTex(file, name, refresh) {
    const p = await call('dialog:open', { kind: 'image' });
    if (!p || !p[0]) return;
    const r = await call('tex:replace', { file, name, image: p[0] }, { busyText: `${name} wird ersetzt …` });
    if (r) { toast(`„${name}“ ersetzt (${r.width}×${r.height}, ${r.format}).`); await refresh(name); }
  }

  async function previewModal(file, t, refresh) {
    const img = h('img', { src: t.thumb || '' });
    const m = modal({
      title: t.name,
      wide: true,
      body: h('div',
        h('div.preview-big', img),
        h('p.muted', { style: { marginTop: '10px' } }, `${t.width}×${t.height} · ${t.format} · ${t.mips} Mipmaps · ${t.sizeKb} KB`)),
      buttons: [
        { label: '⬇ Exportieren', onClick: async () => { const r = await call('tex:export', { file, names: [t.name], format: 'png' }, { busyText: 'Wird exportiert …' }); if (r) toast(`Exportiert nach ${r.outDir}`); return true; } },
        { label: 'Aus Datei ersetzen', onClick: async (close) => { close(); await replaceTex(file, t.name, refresh); } },
        { label: 'Ersetzen in Paint.NET', primary: true, onClick: async () => { await editTex(file, t.name); } },
      ],
    });
    const r = await call('tex:preview', { file, name: t.name }, { quiet: true });
    if (r && r.image && document.body.contains(m.box)) img.src = r.image;
  }

  // ---------------------------------------------------------------- Folierungen (Livery)

  async function viewLivery(v) {
    if (!v) return h('p', 'Fahrzeug nicht gefunden.');
    const wrap = h('div',
      h('div.content-head', h('div.title-block', h('h1', 'Folierungen'), h('div.subtitle', v.displayName))));
    const info = await call('livery:info', { yft: v.file, ytds: v.ytds });
    if (!info) return wrap;
    const list = info.liveries;
    const liveryMods = v.parts.filter((p) => p.category === 'Lackierung (Livery)');
    const base = info.bases[0];
    const target = list.length ? list[0].ytd : info.targetYtd;
    // Vorlage/neue Folie: mindestens 2048 px (Platzhalter-Folien sind oft winzig)
    const size = Math.max(2048, ...list.map((l) => Math.max(l.width, l.height)));
    const modelFile = v.hiFile || v.file;

    if (!info.supported) {
      wrap.append(h('div.notice.warn',
        h('b', 'Dieses Fahrzeug hat keinen Folien-Slot. '),
        'Im Modell fehlt ein Lack-Material mit Folien-Textur (Shader vehicle_paint3 mit „DiffuseSampler2“). ',
        'Das lässt sich in Blender mit Sollumz einrichten: Lack-Material auf vehicle_paint3 stellen und als Folien-Textur z. B. ',
        h('code', `${v.model}_sign_1`), ' eintragen.'));
    } else {
      wrap.append(h('div.notice.info',
        h('b', 'So funktionieren Folien: '),
        `Jede Folie ist eine Textur ${base}_1, ${base}_2 … in ${base ? 'der Fahrzeug-Texturdatei' : ''}. `,
        'Durchsichtige Stellen zeigen die Lackfarbe. Im Spiel wählst du die Folie im Tuning-Menü oder per Script (SetVehicleLivery). ',
        'Für eigene Folien zuerst die UV-Vorlage öffnen – sie zeigt, wo welche Stelle des Autos auf dem Bild liegt.'));

      // 3D mit Folienauswahl
      if (S.liveryShown === undefined || (S.liveryShown && S.liveryShown !== '-' && !list.find((l) => l.name === S.liveryShown))) {
        // erste Folie mit Inhalt zeigen (leere Platzhalter überspringen)
        const firstReal = list.find((l) => !l.empty) || list[0];
        S.liveryShown = firstReal ? firstReal.name : '-';
        S.liveryFor = v.model;
      }
      const chooser = h('div.livery-chooser',
        h('span.muted.small', 'In 3D zeigen:'),
        h(`button.btn.small${S.liveryShown === '-' ? '.primary' : ''}`, { onclick: () => { S.liveryShown = '-'; render(true); } }, 'Ohne Folie'),
        list.map((l) => h(`button.btn.small${S.liveryShown === l.name ? '.primary' : ''}`, {
          onclick: () => { S.liveryShown = l.name; render(true); },
        }, `Folie ${l.number}${l.empty ? ' (leer)' : ''}`)));
      wrap.append(chooser);
      S.liveryFor = v.model;
      wrap.append(viewerPanel(carSpec(v)));
      wrap.append(mountPanel(v));

      const refreshAll = async () => { previewChanged(); delete S.texCache[target]; S.dirty = true; updateStatus(); await render(true); };

      wrap.append(h('div.actions',
        action('➕', 'Neue Folie erstellen', 'Leere Folie anlegen und in Paint.NET öffnen', async () => {
          const r = await call('livery:create', { ytd: target, base, size }, { busyText: 'Neue Folie wird angelegt …' });
          if (!r) return;
          S.liveryShown = r.name;
          await refreshAll();
          await call('livery:template', { yft: modelFile, size, mode: 'edit' }, { busyText: 'UV-Vorlage wird erstellt …' });
          await call('tex:edit', { file: target, name: r.name });
          liveryHelp(r.name);
        }),
        action('⬆', 'Folie importieren', 'Fertiges Bild (PNG, JPG, DDS) als neue Folie', async () => {
          const imgs = await call('dialog:open', { kind: 'images' });
          if (!imgs || !imgs.length) return;
          const r = await call('livery:add', { ytd: target, base, images: imgs }, { busyText: 'Folie wird importiert …' });
          if (r) { toast(`Neue Folie(n): ${r.added.join(', ')}`); S.liveryShown = r.added[r.added.length - 1]; await refreshAll(); }
        }),
        action('📐', 'UV-Vorlage in Paint.NET', 'Zeigt, wo welche Fahrzeugstelle liegt', () =>
          call('livery:template', { yft: modelFile, size, mode: 'edit' }, { busyText: 'UV-Vorlage wird erstellt …' })),
        action('💾', 'UV-Vorlage speichern', `PNG ${size}×${size} zum Bearbeiten`, async () => {
          const r = await call('livery:template', { yft: modelFile, size, mode: 'save' }, { busyText: 'UV-Vorlage wird erstellt …' });
          if (r) toast(`Vorlage gespeichert: ${r.file}`);
        })));

      const sec = h('div.section', h('div.section-head', h('h3', `Vorhandene Folien (${list.length})`)));
      if (!list.length) sec.append(h('p.muted', 'Noch keine Folie vorhanden. Erstelle eine neue oder importiere ein Bild.'));
      const grid = h('div.livery-grid');
      for (const l of list) {
        grid.append(h(`div.livery-card${S.liveryShown === l.name ? '.selected' : ''}`,
          h('div.tex-img', { onclick: () => { S.liveryShown = l.name; render(true); }, title: 'In 3D zeigen' },
            l.thumb ? h('img', { src: l.thumb }) : h('span.muted.small', 'keine Vorschau')),
          h('div.tex-info',
            h('div.tex-name', `Folie ${l.number}`, l.empty ? h('span.pill', { style: { marginLeft: '8px' }, title: 'Diese Folie ist komplett durchsichtig – im Spiel sieht man nur die Lackfarbe (üblich als „keine Folie“).' }, 'leer') : null),
            h('div.tex-meta', `${l.name} · ${l.width}×${l.height} · ${l.format}`)),
          h('div.tex-btns.wrap',
            h('button.btn', { title: 'In Paint.NET bearbeiten – speichern übernimmt automatisch', onclick: async () => {
              await call('tex:edit', { file: l.ytd, name: l.name }, { busyText: 'Wird in Paint.NET geöffnet …' });
              S.liveryShown = l.name;
              liveryHelp(l.name);
            } }, 'Bearbeiten'),
            h('button.btn', { title: 'Durch eine Bilddatei ersetzen', onclick: () => replaceTex(l.ytd, l.name, refreshAll) }, 'Datei …'),
            h('button.btn', { title: 'Als PNG exportieren', onclick: async () => {
              const r = await call('tex:export', { file: l.ytd, names: [l.name], format: 'png' }, { busyText: 'Wird exportiert …' });
              if (r) { toast(`Exportiert nach ${r.outDir}`); call('shell:open', { target: r.outDir }); }
            } }, 'Export'),
            h('button.btn', { title: 'UV-Vorlage mit dieser Folie als Hintergrund speichern', onclick: async () => {
              const r = await call('livery:template', { yft: modelFile, size: Math.max(1024, l.width, l.height), bgYtd: l.ytd, bgName: l.name, mode: 'save' }, { busyText: 'Wird erstellt …' });
              if (r) toast(`Gespeichert: ${r.file}`);
            } }, '+ Vorlage'),
            h('button.btn.danger', { title: 'Folie löschen (die folgenden rücken nach)', onclick: async () => {
              if (!await UI.confirm('Folie löschen?', `Folie ${l.number} (${l.name}) löschen? Die folgenden Folien rücken eine Nummer nach vorne.`, 'Löschen', { danger: true })) return;
              const r = await call('livery:remove', { ytd: l.ytd, base, name: l.name }, { busyText: 'Wird gelöscht …' });
              if (r) { toast(`Folie gelöscht${r.renamed.length ? ' – umbenannt: ' + r.renamed.join(', ') : ''}.`); S.liveryShown = undefined; await refreshAll(); }
            } }, 'Löschen'))));
      }
      sec.append(grid);
      wrap.append(sec);
    }

    if (liveryMods.length) {
      wrap.append(h('div.section',
        h('div.section-head', h('h3', `Folien als Tuningteil (${liveryMods.length})`)),
        h('p.muted.small', 'Manche Fahrzeuge bringen Folien als eigenes 3D-Teil mit (Tuning „Livery“). Diese bearbeitest du wie Tuningteile.'),
        h('div.list', liveryMods.map((p) => h('div.list-row', h('span', '🖌'),
          h('div.grow', h('div.name', p.name), h('div.muted.small', p.label || p.category)),
          h('button.btn.small', { onclick: () => select({ type: 'model', vehicle: v.model, file: p.file }) }, 'Öffnen'))))));
    }
    return wrap;
  }

  function liveryHelp(name) {
    toast(`„${name}“ ist in Paint.NET geöffnet (daneben die UV-Vorlage). Tipp: Vorlage mit Strg+A, Strg+C kopieren und in der Folie mit Strg+Umschalt+V als Ebene einfügen. Vor dem Speichern die Vorlagen-Ebene löschen. Speichern (Strg+S) übernimmt die Folie automatisch.`, 'info', 16000);
  }

  // ---------------------------------------------------------------- Replace → Addon / Namen ändern

  const field = (label, input, hint) => h('div.field', h('label', label), input, hint ? h('div.hint', hint) : null);
  const cleanName = (s) => String(s || '').toLowerCase().replace(/[^a-z0-9_]/g, '');

  async function ensureVanilla() {
    const st = await call('vanilla:status');
    if (!st) return false;
    if (st.built && !st.outdated) return true;
    const ok = await UI.confirm('GTA-Fahrzeugdaten einlesen',
      `${st.outdated ? 'GTA wurde aktualisiert. ' : ''}Für die Umwandlung braucht das Tool die Original-Fahrzeugdaten aus deiner GTA-Installation (vehicles.meta, Handling, Farben). Das dauert nur wenige Sekunden und wird gespeichert.`, 'Jetzt einlesen');
    if (!ok) return false;
    const r = await call('vanilla:build', {}, { busyText: 'GTA-Fahrzeugdaten werden gelesen …' });
    if (r) toast(`${r.count} GTA-Fahrzeuge eingelesen.`);
    return !!r;
  }

  async function convertDialog(v) {
    if (!await ensureVanilla()) return;
    const look = await call('vanilla:lookup', { models: [v.model] });
    const orig = look && look.vehicles[0];
    const origIn = h('input', { type: 'text', value: v.model });
    const nameIn = h('input', { type: 'text', value: `${v.model}_addon`.slice(0, 30) });
    const dispIn = h('input', { type: 'text', value: '', placeholder: 'z. B. Sultan RS Custom' });
    const makeIn = h('input', { type: 'text', value: '', placeholder: 'z. B. Karin (optional)' });
    const handlingCb = h('input', { type: 'checkbox', checked: true });
    const origInfo = h('div.hint', orig && orig.found
      ? `✓ GTA-Original gefunden: ${orig.gameName || v.model}${orig.make ? ' · Marke ' + orig.make : ''} · Handling ${orig.handlingId || '–'}`
      : '⚠ Kein GTA-Fahrzeug mit diesem Namen gefunden – bitte den Namen des Originals eintragen, das ersetzt wird (z. B. sultan).');
    nameIn.addEventListener('input', () => { const c = cleanName(nameIn.value); if (c !== nameIn.value) nameIn.value = c; });
    modal({
      title: 'In Addon-Fahrzeug umwandeln',
      body: h('div',
        h('p.muted', 'Aus dem Replace wird ein eigenes Fahrzeug: Die Dateien bekommen den neuen Spawn-Namen und das Tool erzeugt vehicles.meta, carvariations.meta und handling.meta mit den Original-Werten aus GTA. Das Original-Fahrzeug bleibt im Spiel unverändert.'),
        field('Original-Fahrzeug (wird ersetzt)', origIn, null), origInfo,
        field('Neuer Spawn-Name', nameIn, 'Damit spawnst du das Auto (z. B. /car name). Nur Kleinbuchstaben, Zahlen und „_“.'),
        field('Anzeigename', dispIn, 'So heißt das Auto im Spiel (z. B. in der Fahrzeuganzeige).'),
        field('Marke', makeIn, 'Leer lassen = Marke des Originals.'),
        h('label.check', handlingCb, ' Eigenes Handling anlegen (Kopie des Originals – kann später angepasst werden, ohne das Original zu verändern)')),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: '🔁 Umwandeln', primary: true,
          onClick: async () => {
            const r = await call('vehicle:replaceToAddon', {
              oldModel: origIn.value.trim().toLowerCase(), newModel: nameIn.value.trim(),
              displayName: dispIn.value.trim(), makeName: makeIn.value.trim(), ownHandling: handlingCb.checked,
            }, { busyText: 'Fahrzeug wird umgewandelt …' });
            if (!r) return true;
            S.model = r.model2;
            S.dirty = true;
            previewChanged();
            S.texCache = {};
            S.open.add(r.model);
            S.sel = { type: 'vehicle', vehicle: r.model };
            renderAll();
            toast(`✓ Umgewandelt: Spawn-Name „${r.model}“ · Handling: ${r.handling} · Motorsound: ${r.sound}`, 'ok', 9000);
            for (const n of r.notes || []) toast(n, 'warn', 12000);
            return false;
          },
        },
      ],
    });
  }

  function renameDialog(v) {
    const nameIn = h('input', { type: 'text', value: v.model });
    const dispIn = h('input', { type: 'text', value: v.displayName && v.displayName !== v.model ? v.displayName : '', placeholder: 'z. B. Nissan Skyline GT-R' });
    const makeIn = h('input', { type: 'text', value: '', placeholder: 'unverändert lassen' });
    nameIn.addEventListener('input', () => { const c = cleanName(nameIn.value); if (c !== nameIn.value) nameIn.value = c; });
    modal({
      title: 'Namen ändern',
      body: h('div',
        field('Spawn-Name', nameIn, 'Dateien und Meta-Einträge werden automatisch umbenannt.'),
        field('Anzeigename', dispIn, 'Name im Spiel. Die Marke wird automatisch davor angezeigt, wenn sie nicht schon im Namen steht.'),
        field('Marke', makeIn, 'Optional, z. B. Nissan.')),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: 'Übernehmen', primary: true,
          onClick: async () => {
            const r = await call('vehicle:rename', { oldModel: v.model, newModel: nameIn.value.trim(), displayName: dispIn.value.trim(), makeName: makeIn.value.trim() },
              { busyText: 'Wird umbenannt …' });
            if (!r) return true;
            S.model = r.model2;
            S.dirty = true;
            previewChanged();
            S.texCache = {};
            S.open.add(r.model);
            S.sel = { type: 'vehicle', vehicle: r.model };
            renderAll();
            toast(`✓ Gespeichert: ${r.model}${r.renamed.length ? ' · Dateien: ' + r.renamed.join(', ') : ''}`, 'ok', 8000);
            return false;
          },
        },
      ],
    });
  }

  // ---------------------------------------------------------------- Motorsound (mitgelieferte Sounds)

  let audioEl = null;
  async function playSound(awc, index, btn) {
    if (audioEl) { audioEl.pause(); audioEl = null; document.querySelectorAll('.btn.playing').forEach((b) => { b.classList.remove('playing'); b.textContent = '▶'; }); }
    if (btn && btn.dataset.playing === '1') { btn.dataset.playing = ''; return; }
    const r = await call('audio:wav', { awc, index });
    if (!r) return;
    const url = URL.createObjectURL(new Blob([r.wav], { type: 'audio/wav' }));
    audioEl = new Audio(url);
    if (btn) { btn.classList.add('playing'); btn.textContent = '■'; btn.dataset.playing = '1'; }
    audioEl.onended = () => { if (btn) { btn.classList.remove('playing'); btn.textContent = '▶'; btn.dataset.playing = ''; } URL.revokeObjectURL(url); };
    audioEl.play();
  }

  async function viewSound(v) {
    const wrap = h('div', h('div.content-head', h('div.title-block', h('h1', v ? 'Motorsound' : 'Sound-Pack'), h('div.subtitle', v ? v.displayName : 'ohne Fahrzeug'))));
    const info = await call('audio:info');
    if (!info) return wrap;
    const veh = (v && info.vehicles.find((x) => x.model === v.model)) || {};
    const hasPack = info.packs.some((p) => p.waves.length || p.rels.length);

    // Status
    if (!v) {
      wrap.append(h('div.notice.info',
        h('b', 'Sound-Pack ohne Fahrzeug. '),
        info.customSounds.length ? ['Enthaltene Fahrzeug-Sounds: ', h('code', info.customSounds.join(', ')), '. '] : '',
        'Ein Auto nutzt ihn, wenn in seiner vehicles.meta „audioNameHash“ auf diesen Namen gesetzt ist. ',
        'Öffne dazu das Auto zusätzlich (Hinzufügen) und wähle den Sound unter „Motorsound“ – oder exportiere den Pack als eigene FiveM-Resource.'));
    } else if (hasPack) {
      wrap.append(h('div.notice.info',
        h('b', 'Dieses Auto bringt eigene Sounds mit. '),
        veh.custom
          ? ['Es nutzt den mitgelieferten Sound ', h('code', veh.audioName), '. ']
          : info.customSounds.length
            ? ['In der vehicles.meta steht aber ', h('code', veh.audioName || '–'), ' – unten den mitgelieferten Sound-Namen auswählen und übernehmen, damit das Auto ihn nutzt. ']
            : ['In der vehicles.meta steht ', h('code', veh.audioName || '–'), '. Ein Fahrzeug-Sound-Name wurde in den Sound-Einstellungen nicht gefunden – meist heißt er wie das Auto oder der Pack. '],
        'Die Sounds werden beim FiveM-Export automatisch mit eingebunden (audioconfig + sfx).'));
    } else {
      wrap.append(h('div.notice.warn',
        h('b', 'Bei diesem Auto sind keine eigenen Sounds dabei. '),
        'Es nutzt den GTA-Sound ', h('code', veh.audioName || 'Standard'), '. ',
        'Hast du einen Sound-Pack (Ordner mit audioconfig/sfx oder eine dlc.rpf mit Sounds)? Dann über „Sound-Pack importieren“ hinzufügen.'));
    }

    // Verknüpfung (audioNameHash)
    if (v) {
    const nameIn = h('input', { type: 'text', value: veh.audioName || '', list: 'sound-names', placeholder: 'z. B. Name aus dem Sound-Pack' });
    wrap.append(h('div.section',
      h('div.section-head', h('h3', 'Verknüpfung mit dem Auto')),
      h('p.muted.small', 'Das Auto spielt den Sound, dessen Name in der vehicles.meta unter „audioNameHash“ steht. Mitgelieferte Sounds stehen in der Liste.'),
      h('datalist#sound-names', info.customSounds.map((n) => h('option', { value: n }))),
      h('div.input-row', { style: { maxWidth: '520px' } },
        nameIn,
        h('button.btn.primary', {
          onclick: async () => {
            const r = await call('audio:setName', { model: v.model, name: nameIn.value }, { busyText: 'vehicles.meta wird gespeichert …' });
            if (r) { S.dirty = true; updateStatus(); toast(`Sound gesetzt: ${r.audioName}`); render(true); }
          },
        }, 'Übernehmen')),
      info.customSounds.length ? h('div.row.gap-s.wrap', { style: { marginTop: '8px' } },
        h('span.muted.small', 'Mitgeliefert:'),
        info.customSounds.map((n) => h('button.btn.small', { onclick: () => { nameIn.value = n; } }, n))) : null));
    }

    wrap.append(h('div.actions',
      action('⬆', 'Sound-Pack importieren', 'Ordner oder dlc.rpf mit Sounds hinzufügen', () => addDialog())));

    // Packs
    for (const pack of info.packs) {
      if (!pack.waves.length && !pack.rels.length) continue;
      const sec = h('div.section',
        h('div.section-head', h('h3', `Sound-Pack „${pack.source}“`), h('div.spacer'),
          h('button.btn.small', { onclick: () => call('audio:exportPack', { source: pack.source }, { busyText: 'Wird exportiert …' }) }, '⬇ Ganzen Pack exportieren')));

      for (const w of pack.waves) {
        const streams = w.streams || [];
        const box = h('div.list', { style: { marginBottom: '12px' } },
          h('div.list-row.head',
            h('span', '🎵'),
            h('div.grow', h('div.name', w.name), h('div.muted.small', `Wavepack ${w.pack} · ${streams.length} Sounds · ${kb(w.size)}`)),
            h('button.btn.small', { onclick: () => call('audio:exportAll', { awc: w.file }, { busyText: 'Sounds werden exportiert …' }) }, '⬇ Alle als WAV')),
          w.error ? h('div.list-row', h('span.muted', `Nicht lesbar: ${w.error}`)) : null,
          streams.map((st) => h('div.list-row',
            h('button.btn.small.play', { title: 'Abspielen', onclick: (e) => playSound(w.file, st.index, e.currentTarget) }, '▶'),
            h('div.grow',
              h('div.name', st.name),
              h('div.muted.small', `${st.seconds.toFixed(2)} s · ${st.sampleRate} Hz`, st.granular ? ' · Motor-Sound (granular)' : '')),
            st.midi ? h('span.pill', 'MIDI') : [
              h('button.btn.small', { title: 'Als WAV speichern', onclick: () => call('audio:exportWav', { awc: w.file, index: st.index, name: st.name }, { busyText: 'Wird exportiert …' }) }, 'Export'),
              h('button.btn.small', {
                title: 'Durch eine WAV-Datei ersetzen',
                onclick: async () => {
                  const f = await call('dialog:open', { kind: 'wav' });
                  if (!f || !f[0]) return;
                  const r = await call('audio:replace', { awc: w.file, index: st.index, wav: f[0] }, { busyText: 'Sound wird ersetzt …' });
                  if (r) { S.dirty = true; updateStatus(); toast(`„${st.name}“ ersetzt.${r.note ? ' ' + r.note : ''}`, r.note ? 'warn' : 'ok', 8000); }
                },
              }, 'Ersetzen'),
              h('button.btn.small', {
                title: 'Im Audioprogramm bearbeiten – speichern übernimmt automatisch',
                onclick: async () => {
                  const r = await call('audio:edit', { awc: w.file, index: st.index, name: st.name }, { busyText: 'Sound wird geöffnet …' });
                  if (r) toast(`„${st.name}“ ist geöffnet. Bearbeiten und als WAV speichern (gleicher Name) – das Tool übernimmt es automatisch.`, 'info', 10000);
                },
              }, 'Bearbeiten'),
            ])));
        sec.append(box);
      }

      if (pack.rels.length) {
        sec.append(h('div.list', pack.rels.map((r) => h('div.list-row',
          h('span', '⚙'),
          h('div.grow', h('div.name', r.name), h('div.muted.small', `${r.kind}${r.items ? ` · ${r.items} Einträge` : ''}${r.cars && r.cars.length ? ` · Fahrzeug-Sounds: ${r.cars.join(', ')}` : ''}${r.error ? ' · Fehler: ' + r.error : ''}`)),
          h('button.btn.small', { title: 'Einstellungen als XML bearbeiten (für Fortgeschrittene)', onclick: () => relEditor(r) }, 'Einstellungen')))));
      }
      wrap.append(sec);
    }

    if (hasPack) {
      wrap.append(h('div.notice.warn', { style: { marginTop: '10px' } },
        h('b', 'Hinweis zu Motor-Sounds: '),
        'Motorgeräusche in GTA sind „granular“ – das Spiel schneidet sie anhand von Zeitmarken in kleine Stücke. ',
        'Beim Ersetzen wird die neue WAV deshalb automatisch auf Mono, die Original-Samplerate und die Original-Länge gebracht. ',
        'Am besten die exportierte WAV bearbeiten (z. B. Klang/Lautstärke) statt eine komplett andere Aufnahme einzusetzen.'));
    }
    return wrap;
  }

  async function relEditor(r) {
    const res = await call('audio:relGet', { file: r.file }, { busyText: 'Wird geladen …' });
    if (!res) return;
    const ta = h('textarea.xml-editor', { spellcheck: 'false' });
    ta.value = res.xml;
    modal({
      title: `Sound-Einstellungen: ${r.name}`,
      wide: true,
      body: h('div',
        h('p.muted.small', 'Für Fortgeschrittene: Werte wie Lautstärken oder verknüpfte Sounds direkt ändern. Beim Speichern wird die Datei neu erzeugt (vorher wird ein Backup angelegt).'),
        ta),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: 'Speichern', primary: true,
          onClick: async () => {
            const s = await call('audio:relSet', { file: r.file, xml: ta.value }, { busyText: 'Wird gespeichert …' });
            if (!s) return true;
            S.dirty = true; updateStatus();
            toast(`${r.name} gespeichert.`);
            return false;
          },
        },
      ],
    });
  }

  // ---------------------------------------------------------------- Tuning anbauen (nur Vorschau)

  function liveryFor(v) {
    return S.liveryFor === v.model && S.liveryShown ? S.liveryShown : null;
  }

  function mountedParts(v) {
    const m = S.mounted[v.model] || {};
    return Object.values(m).map((name) => v.parts.find((p) => p.name === name)).filter((p) => p && !p.encrypted);
  }

  /** 3D-Beschreibung des Fahrzeugs inkl. angebauter Teile und gewählter Folie. */
  function carSpec(v) {
    const parts = mountedParts(v);
    const livery = liveryFor(v);
    const hide = [...new Set(parts.flatMap((p) => p.turnOffBones || []))].sort();
    return {
      main: { file: v.hiFile || v.file, ytds: v.ytds, livery, hide },
      extras: parts.map((p) => ({
        file: p.hiFile || p.file, ytds: [...v.ytds, ...(p.ytd ? [p.ytd] : [])],
        attach: v.file, bone: p.bone || 'chassis', livery,
      })),
    };
  }

  function mountPanel(v) {
    const parts = v.parts.filter((p) => !p.encrypted && p.category !== 'Felgen');
    if (!parts.length) return null;
    const cur = S.mounted[v.model] || (S.mounted[v.model] = {});
    const cats = [...new Set(parts.map((p) => p.category))].sort((a, b) => a.localeCompare(b));
    const count = Object.keys(cur).length;
    const body = h('div.mount-grid', cats.map((cat) => {
      const sel = h('select', {
        onchange: (e) => {
          if (e.target.value) cur[cat] = e.target.value; else delete cur[cat];
          render(true);
        },
      }, h('option', { value: '' }, 'Serie'),
      parts.filter((p) => p.category === cat).sort((a, b) => a.name.localeCompare(b.name)).map((p) =>
        h('option', { value: p.name, selected: cur[cat] === p.name || null }, p.label ? `${p.label} (${p.name})` : p.name)));
      return h('label.mount-item', h('span.muted.small', cat), sel);
    }));
    const det = h('details.mount-panel', { open: S.mountOpen || null, ontoggle: (e) => { S.mountOpen = e.target.open; } },
      h('summary', `🔧 Tuningteile anbauen (nur Vorschau)${count ? ` – ${count} angebaut` : ''}`),
      h('p.muted.small', 'Zeigt das Auto mit den gewählten Teilen und der Folie. Ändert nichts an den Dateien.'),
      body,
      count ? h('button.btn.small', { onclick: () => { S.mounted[v.model] = {}; render(true); } }, 'Alle auf Serie zurücksetzen') : null);
    return det;
  }

  // ---------------------------------------------------------------- 3D-Ansicht

  const PAINTS = [['#9ea3aa', 'Silber'], ['#15171a', 'Schwarz'], ['#e9ebee', 'Weiß'], ['#a3161b', 'Rot'], ['#1c4fa8', 'Blau'], ['#e3b81c', 'Gelb'], ['#2f6b3a', 'Grün']];

  function viewerPanel(spec, small) {
    if (!spec) return null;
    const status = h('div.viewer-status', '3D-Modell wird erstellt …');
    const swatches = h('div.swatches', PAINTS.map(([c, n]) => h(`button.swatch${S.paint === c ? '.on' : ''}`, {
      title: `Lackfarbe ${n} (nur Vorschau)`,
      style: { background: c },
      onclick: (e) => {
        S.paint = c;
        if (window.Viewer3D) window.Viewer3D.setPaint(c);
        const me = e.currentTarget;
        me.parentNode.querySelectorAll('.swatch').forEach((x) => x.classList.toggle('on', x === me));
      },
    })));
    const el = h(`div.viewer${small ? '.small' : ''}`,
      status,
      h('div.viewer-bar',
        swatches,
        h('button.btn.small', { onclick: () => window.Viewer3D && window.Viewer3D.resetView(), title: 'Kamera zurücksetzen' }, '⟲ Ansicht'),
        h('span.viewer-hint', 'Linke Maustaste: drehen · Rechte: verschieben · Mausrad: zoomen')));
    el._spec = spec;
    el._status = status;
    return el;
  }

  function viewerReady() {
    if (window.Viewer3D) return Promise.resolve();
    return new Promise((r) => window.addEventListener('viewer3d-ready', r, { once: true }));
  }

  function getGlb(m) {
    const key = `${m.file}|${(m.ytds || []).join(',')}|${m.attach || ''}|${m.bone || ''}|${m.livery || ''}|${(m.hide || []).join(',')}`;
    if (!preview.cache.has(key)) {
      const p = window.api.invoke('model:preview', { file: m.file, ytds: m.ytds, attach: m.attach, bone: m.bone, livery: m.livery, hide: m.hide }).then((r) => {
        if (!r.ok) { preview.cache.delete(key); throw new Error(r.error); }
        return r.data;
      });
      preview.cache.set(key, p);
    }
    return preview.cache.get(key);
  }

  async function attachViewer(el) {
    const spec = el._spec;
    const token = ++preview.token;
    const key = `${spec.main.file}|${spec.overlay ? spec.overlay.file : ""}`;
    const keepCamera = preview.lastKey === key;
    await viewerReady();
    const V = window.Viewer3D;
    V.mount(el);
    V.setGround('main');
    V.setMainOffset(0, 0, 0, 1);
    V.setPaint(S.paint);
    try {
      const [m, o] = await Promise.all([getGlb(spec.main), spec.overlay ? getGlb(spec.overlay) : null]);
      if (token !== preview.token) return;
      await V.load(m.glb, { keepCamera, ghost: !!spec.ghost });
      if (o) await V.loadOverlay(o.glb, { keepCamera }); else V.clearOverlay();
      const ex = spec.extras && spec.extras.length ? await Promise.all(spec.extras.map(getGlb)) : [];
      if (token !== preview.token) return;
      await V.setExtras(ex.map((x) => x.glb));
      preview.lastKey = key;
      const info = o || m;
      const missing = (m.missingTextures || 0) + (o ? o.missingTextures || 0 : 0);
      el._status.textContent = `${info.triangles.toLocaleString('de-DE')} Dreiecke` +
        (missing ? ` · ${missing} Standard-Texturen aus GTA nicht enthalten` : '') +
        (keepCamera ? ' · aktualisiert ✓' : '');
    } catch (e) {
      if (token !== preview.token) return;
      V.clear();
      el._status.textContent = `3D-Ansicht nicht möglich: ${e.message}`;
      el._status.classList.add('err');
    }
  }

  // ---------------------------------------------------------------- Meta / Sonstiges

  function viewMeta() {
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', 'Meta-Dateien'), h('div.subtitle', 'Werden beim Export automatisch in die fxmanifest.lua eingetragen.'))),
      h('div.list', S.model.dataFiles.map((f) => h('div.list-row', h('span', '📝'), h('div.grow', h('div.name', base(f)), h('div.muted.small', f))))));
  }

  function viewOther(file) {
    return h('div',
      h('div.content-head', h('div.title-block', h('h1', base(file)))),
      h('div.actions', action('💾', 'Datei exportieren', 'Kopie speichern', async () => {
        const r = await call('file:export', { file });
        if (r) toast(`Gespeichert: ${r.file}`);
      })));
  }

  // ================================================================ Blender

  async function blender(model, ytds) {
    const r = await call('model:blender', { model, ytds }, { busyText: `${model} wird für Blender vorbereitet …` });
    if (!r) return;
    S.blender[model] = 'open';
    toast(`Blender startet mit ${model}. Zum Übernehmen in Blender: Seitenleiste (N) → „GTA Tool“ → „Speichern & zurück“.`, 'info', 10000);
    render(true);
  }

  async function onBlender({ model, state, error, files, studio }) {
    if (studio === 'clothing' || studio === 'mlo' || studio === 'anim' || (S.model && !S.model.vehicles.some((v) => v.model === model || v.parts.some((p) => p.name === model)) && state !== 'closed')) return;
    if (state === 'applied') {
      S.dirty = true;
      S.blender[model] = 'open';
      for (const f of files || []) delete S.texCache[`stream/${f}`];
      previewChanged();
      toast(`✓ Aus Blender übernommen: ${(files || []).join(', ')}`);
      await reanalyze();
      updateStatus();
      render(true);
    } else if (state === 'error') {
      toast(`Blender (${model}): ${error}`, 'error');
    } else if (state === 'closed') {
      delete S.blender[model];
      if (App.view === 'vehicles' && S.model) render(true);
    }
  }

  function updateStatus() {
    if (!S.model) return;
    const v = S.model.vehicles.length, p = S.model.vehicles.reduce((a, x) => a + x.parts.length, 0);
    $('vs-status-text').textContent = `${v} Fahrzeug${v === 1 ? '' : 'e'} · ${p} Tuningteile · ${S.model.isReplace ? 'Replace' : 'Addon'}${S.dirty ? ' · ● Änderungen nicht exportiert' : ''}`;
  }

  // ================================================================ FiveM-Export

  async function exportDialog() {
    if (!S.model) return;
    const settings = await call('settings:get');
    const defName = S.model.vehicles.length === 1 ? S.model.vehicles[0].model : (S.model.sources[0] || 'fahrzeuge');
    const orig = await Sftp.originalName();
    const nameIn = h('input', { type: 'text', value: orig || defName });
    const dirIn = h('input', { type: 'text', value: (settings && (settings.lastResourceDir || settings.lastExportDir)) || '', placeholder: 'Bitte Ordner wählen …' });
    const preview = h('div.hint');
    const upd = () => { preview.textContent = dirIn.value ? `Wird erstellt: ${dirIn.value.replace(/[\\/]$/, '')}\\${nameIn.value.trim().replace(/[^\x21-\x7E]|[<>:"/\\|?*]/g, '_').replace(/\.+$/, '')}` : ''; };
    nameIn.addEventListener('input', upd); dirIn.addEventListener('input', upd); upd();
    const localField = h('div.field', h('label', 'Zielordner'), h('div.input-row', dirIn, h('button.btn', {
      onclick: async () => { const r = await call('dialog:open', { kind: 'outdir' }); if (r && r[0]) { dirIn.value = r[0]; upd(); } },
    }, 'Durchsuchen …')), preview);
    const target = Sftp.targetField(localField, dirIn);
    modal({
      title: 'Als FiveM-Resource exportieren',
      body: h('div',
        h('p.muted', 'Erstellt einen fertigen Resource-Ordner (fxmanifest.lua, stream, data) – auf deinem PC oder direkt auf deinem Server. Danach in der server.cfg „ensure <name>“ eintragen.'),
        Sftp.keepNotice(orig),
        h('div.field', h('label', 'Name der Resource'), nameIn, h('div.hint', orig ? 'Gleicher Name = die Resource wird aktualisiert. Anderer Name = neue Resource.' : 'Am besten nur Buchstaben, Zahlen, „_“ und „-“.')),
        target.el,
        localField),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: '📦 Exportieren', primary: true,
          onClick: async () => {
            const resourceName = nameIn.value.trim();
            const r = await Sftp.runExport(target.get(), resourceName,
              (outDir) => call('fivem:export', { outDir, resourceName }, { busyText: 'FiveM-Resource wird erstellt …' }));
            if (!r) return true;
            S.dirty = false;
            updateStatus();
            exportResult(r);
            return false;
          },
        },
      ],
    });
  }

  function exportResult(r) {
    modal({
      title: '✓ Export fertig',
      body: h('div',
        h('p', 'Die Resource wurde erstellt:'),
        Sftp.resultPath(r),
        h('div.cards',
          h('div.card', h('div.k', 'Stream-Dateien'), h('div.v', String(r.streamFiles))),
          h('div.card', h('div.k', 'Meta-Dateien'), h('div.v', String(r.metaFiles))),
          r.vehicles.length ? h('div.card', h('div.k', 'Spawn-Namen'), h('div.v', r.vehicles.join(', '))) : null),
        warningsBox(r.warnings),
        h('div.notice.info', r.remote ? 'Auf dem Server: in server.cfg ' : 'Auf dem Server: Ordner nach „resources“ kopieren → in server.cfg ', h('code', `ensure ${base(r.folder)}`), ' → Server neu starten.')),
      buttons: Sftp.resultButtons(r),
    });
  }

  return {
    init, show, open,
    refreshPreview: () => { previewChanged(); return render(true); },
    get dirty() { return S.dirty; },
  };
})();
