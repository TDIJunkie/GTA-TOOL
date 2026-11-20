'use strict';
// 3D-Platzierung in der echten GTA-Welt: Umgebung laden, Objekt (Box oder Interior) verschieben/drehen, auf den Boden setzen.

const WorldPlace = (() => {
  const { h, call, toast, modal } = UI;
  let busyToken = 0;

  async function ensureIndex() {
    const st = await call('world:status', null, { quiet: true });
    if (st && st.built) return true;
    const ok = await UI.confirm('GTA-Welt einlesen',
      'Für die 3D-Umgebung liest das Tool einmalig ein, wo in GTA welche Objekte stehen (dauert etwa 15–30 Sekunden). Danach geht es sofort.', 'Einlesen');
    if (!ok) return false;
    const r = await call('world:build', null, { busyText: 'GTA-Welt wird eingelesen …' });
    if (r) toast(`${r.ymaps.toLocaleString('de-DE')} Kartenteile eingelesen.`);
    return !!r;
  }

  /**
   * opts: { pos:[x,y,z], heading, box:[w,d,h] | fromScene:true, title }
   * Ergebnis: { pos, heading } oder null (abgebrochen).
   */
  function open(opts) {
    return new Promise(async (resolve) => {
      if (!await ensureIndex()) { resolve(null); return; }
      const V = window.Viewer3D;
      const st = { pos: opts.pos.slice(), heading: opts.heading || 0, radius: 120 };
      const my = ++busyToken;
      let done = false;
      const num = (v, step) => h('input.num', { type: 'number', step, value: Number(v).toFixed(2), style: { width: '110px' } });
      const xIn = num(st.pos[0], '0.1'), yIn = num(st.pos[1], '0.1'), zIn = num(st.pos[2], '0.05'), hIn = num(st.heading, '1');
      const sync = () => { xIn.value = st.pos[0].toFixed(2); yIn.value = st.pos[1].toFixed(2); zIn.value = st.pos[2].toFixed(2); hIn.value = st.heading.toFixed(1); };
      for (const [el, i] of [[xIn, 0], [yIn, 1], [zIn, 2]]) el.addEventListener('change', () => { const v = parseFloat(el.value); if (!Number.isNaN(v)) { st.pos[i] = v; V.worldSetValues(st.pos, st.heading); } });
      hIn.addEventListener('change', () => { const v = parseFloat(hIn.value); if (!Number.isNaN(v)) { st.heading = ((v % 360) + 360) % 360; V.worldSetValues(st.pos, st.heading); } });
      const status = h('div.viewer-status', 'Umgebung wird geladen …');
      const viewEl = h('div.viewer', { style: { height: '520px' } }, status,
        h('div.viewer-bar', h('span.viewer-hint', 'Pfeile ziehen: verschieben · Ring (Modus „Drehen“): drehen · Linke Maustaste: Ansicht drehen · Rechte: verschieben · Mausrad: zoomen')));
      let mode = 'translate';
      const modeBtns = h('span.row.gap-s');
      const drawModes = () => {
        modeBtns.innerHTML = '';
        modeBtns.append(
          h(`button.btn.small${mode === 'translate' ? '.primary' : ''}`, { onclick: () => { mode = 'translate'; V.worldModeSet(mode); drawModes(); } }, '✥ Verschieben'),
          h(`button.btn.small${mode === 'rotate' ? '.primary' : ''}`, { onclick: () => { mode = 'rotate'; V.worldModeSet(mode); drawModes(); } }, '⟳ Drehen'));
      };
      drawModes();
      const radiusSel = h('select', { onchange: (e) => { st.radius = Number(e.target.value); load(); } },
        [60, 120, 200, 300].map((r) => h('option', { value: r, selected: r === st.radius || null }, `Umkreis ${r} m`)));
      modal({
        title: opts.title || '🎯 In der GTA-Welt platzieren',
        wide: true,
        onClose: () => { busyToken++; V.worldHide(); if (!done) resolve(null); if (opts.onClose) opts.onClose(); },
        body: h('div',
          h('div.mlo-toolbar', modeBtns,
            h('button.btn.small', { onclick: () => { const r = V.worldSnap(); if (!r) toast('Unter dem Objekt wurde nichts gefunden (Umgebung noch nicht geladen?).', 'warn'); } }, '⬇ Auf den Boden setzen'),
            h('button.btn.small', { onclick: () => V.worldFocusObject() }, '🎯 Zum Objekt'),
            h('span.spacer'), radiusSel),
          viewEl,
          h('div.pos-grid', { style: { marginTop: '8px' } },
            h('label.pos-row', h('span', 'X'), xIn), h('label.pos-row', h('span', 'Y'), yIn),
            h('label.pos-row', h('span', 'Z (Boden des Interiors)'), zIn), h('label.pos-row', h('span', 'Drehung °'), hIn)),
          h('p.muted.small', 'Tipp: Die rote Box ist dein Interior (Pfeil = Eingangsseite). Bei Interiors, die in ein bestehendes Gebäude sollen, die Box so verschieben, dass sie in das Gebäude passt.')),
        buttons: [
          { label: 'Abbrechen' },
          { label: '✓ Position übernehmen', primary: true, onClick: () => { done = true; resolve({ pos: st.pos.slice(), heading: st.heading }); } },
        ],
      });

      const load = async () => {
        const tok = ++busyToken;
        V.worldShow(st.pos, { fromScene: !!opts.fromScene });
        V.worldSetObject(st.pos, st.heading, opts.box);
        V.worldOn((r) => { st.pos = r.pos; st.heading = r.heading; sync(); });
        status.textContent = 'Umgebung wird abgefragt …';
        const area = await call('world:area', { x: st.pos[0], y: st.pos[1], radius: st.radius }, { quiet: true });
        if (tok !== busyToken) return;
        if (!area) { status.textContent = 'Die Umgebung konnte nicht geladen werden (GTA-Ordner/Schlüssel prüfen).'; return; }
        const byName = new Map();
        for (const e of area.entities) { if (!byName.has(e.name)) byName.set(e.name, []); byName.get(e.name).push(e); }
        let n = 0, failed = 0;
        for (const name of area.archetypes) {
          if (tok !== busyToken) return;
          n++;
          status.textContent = `Umgebung wird geladen: ${n} / ${area.archetypes.length} Modelle …`;
          if (V.worldHas(name)) { await V.worldAdd(name, null, byName.get(name)); continue; }
          const r = await window.api.invoke('world:archGlb', { name });
          if (tok !== busyToken) return;
          if (!r.ok) { failed++; continue; }
          await V.worldAdd(name, r.data.glb, byName.get(name));
        }
        status.textContent = `${area.entities.length} Objekte in ${st.radius} m Umkreis${failed ? ` · ${failed} Modelle nicht darstellbar` : ''}`;
      };
      setTimeout(() => { if (my !== busyToken) return; V.mount(viewEl); load(); }, 30);
    });
  }

  return { open };
})();
