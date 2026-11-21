'use strict';
// SFTP in der Oberfläche: Server verwalten, Server durchsuchen, vom Server importieren, auf den Server exportieren.

const Sftp = (() => {
  const { h, call, toast, modal } = UI;
  const fmtSize = (b) => (b == null ? '' : b > 1048576 ? `${(b / 1048576).toFixed(1)} MB` : b > 1024 ? `${Math.round(b / 1024)} KB` : `${b} B`);
  const joinPath = (a, b) => (a.endsWith('/') ? a + b : `${a}/${b}`);
  const parent = (p) => { if (!p || p === '/') return '/'; const s = p.replace(/\/+$/, ''); const i = s.lastIndexOf('/'); return i <= 0 ? '/' : s.slice(0, i); };

  async function servers() { return (await call('sftp:servers', null, { quiet: true })) || []; }

  // ================================================================ Server verwalten

  /** Liste + Bearbeiten. Gibt die (evtl. neue) Server-ID zurück, wenn ein Server gespeichert wurde. */
  function manage({ editId = null } = {}) {
    return new Promise((resolve) => {
      let lastSaved = null;
      const body = h('div');
      const m = modal({ title: '🌐 SFTP-Server', wide: true, body, onClose: () => resolve(lastSaved) });
      const showList = async () => {
        const list = await servers();
        body.innerHTML = '';
        body.append(
          h('p.muted', 'Hier legst du deine Server an (z. B. den FiveM-Server). Passwörter werden verschlüsselt mit Windows gespeichert.'),
          list.length
            ? h('div.list', list.map((s) => h('div.list-row',
              h('span', '🖥'),
              h('div.grow', h('div.name', s.name), h('div.muted.small', `${s.username}@${s.host}:${s.port}${s.remoteDir ? ` · Start: ${s.remoteDir}` : ''} · ${s.auth === 'key' ? 'Schlüsseldatei' : 'Passwort'}`)),
              h('button.btn.small', { onclick: () => showForm(s) }, 'Bearbeiten'),
              h('button.btn.small.danger', {
                onclick: async () => {
                  if (!await UI.confirm('Server löschen?', `„${s.name}“ aus der Liste entfernen? (Auf dem Server selbst wird nichts gelöscht.)`, 'Löschen', { danger: true })) return;
                  await call('sftp:delete', { id: s.id });
                  showList();
                },
              }, 'Löschen'))))
            : h('div.notice.info', 'Noch kein Server angelegt.'),
          h('div.row.gap-s', { style: { marginTop: '10px' } }, h('button.btn.primary', { onclick: () => showForm(null) }, '+ Server hinzufügen')));
      };
      const showForm = (s) => {
        const isNew = !s;
        s = s || { name: '', host: '', port: 22, username: '', auth: 'password', keyPath: '', remoteDir: '', hasSecret: false };
        const f = {
          name: h('input', { type: 'text', value: s.name, placeholder: 'z. B. Mein FiveM-Server' }),
          host: h('input', { type: 'text', value: s.host, placeholder: 'z. B. 123.45.67.89 oder server.de' }),
          port: h('input', { type: 'number', value: s.port || 22, min: 1, max: 65535, style: { width: '100px' } }),
          username: h('input', { type: 'text', value: s.username }),
          secret: h('input', { type: 'password', placeholder: s.hasSecret ? '•••••• (gespeichert – leer lassen = unverändert)' : '' }),
          keyPath: h('input', { type: 'text', value: s.keyPath, placeholder: 'z. B. C:\\Users\\…\\.ssh\\id_ed25519' }),
          remoteDir: h('input', { type: 'text', value: s.remoteDir, placeholder: 'z. B. /home/fivem/server-data/resources' }),
        };
        let auth = s.auth || 'password';
        const secretLabel = h('label');
        const keyRow = h('div.field', h('label', 'Schlüsseldatei (privater Schlüssel, OpenSSH-Format)'),
          h('div.input-row', f.keyPath, h('button.btn', { onclick: async () => { const p = await call('sftp:pickKey'); if (p) f.keyPath.value = p; } }, 'Durchsuchen …')));
        const authBtns = h('div.row.gap-s');
        const syncAuth = () => {
          authBtns.innerHTML = '';
          authBtns.append(
            h(`button.btn.small${auth === 'password' ? '.primary' : ''}`, { onclick: () => { auth = 'password'; syncAuth(); } }, '🔑 Passwort'),
            h(`button.btn.small${auth === 'key' ? '.primary' : ''}`, { onclick: () => { auth = 'key'; syncAuth(); } }, '🗝 Schlüsseldatei'));
          keyRow.style.display = auth === 'key' ? '' : 'none';
          secretLabel.textContent = auth === 'key' ? 'Passphrase des Schlüssels (falls vorhanden)' : 'Passwort';
        };
        syncAuth();
        const result = h('div');
        const draft = () => ({ id: s.id, name: f.name.value, host: f.host.value, port: Number(f.port.value) || 22, username: f.username.value, auth, keyPath: f.keyPath.value.trim(), remoteDir: f.remoteDir.value.trim() });
        const secretVal = () => (f.secret.value === '' && s.hasSecret ? undefined : f.secret.value);
        body.innerHTML = '';
        body.append(
          h('div.field', h('label', 'Anzeigename'), f.name),
          h('div.row.gap', h('div.field.grow', h('label', 'Server-Adresse (Host oder IP)'), f.host), h('div.field', h('label', 'Port'), f.port)),
          h('div.field', h('label', 'Benutzername'), f.username),
          h('div.field', h('label', 'Anmeldung'), authBtns),
          keyRow,
          h('div.field', secretLabel, f.secret),
          h('div.field', h('label', 'Startordner auf dem Server (optional)'), f.remoteDir, h('div.hint', 'Hier öffnet sich der Server-Browser, z. B. dein resources-Ordner.')),
          result,
          h('div.row.gap-s', { style: { marginTop: '10px' } },
            h('button.btn', { onclick: () => showList() }, '← Zurück'),
            h('span.spacer'),
            h('button.btn', {
              onclick: async () => {
                result.innerHTML = '';
                const r = await call('sftp:test', { server: draft(), secret: secretVal(), useSaved: true }, { busyText: 'Verbindung wird getestet …' });
                if (r) result.append(h('div.notice.info', `✓ Verbindung klappt. Home-Ordner: ${r.home}`, r.remoteDirOk ? '' : ' · ⚠ Der Startordner existiert nicht.'));
              },
            }, '🔌 Verbindung testen'),
            h('button.btn.primary', {
              onclick: async () => {
                const r = await call('sftp:save', { server: draft(), secret: secretVal() }, { busyText: 'Wird gespeichert …' });
                if (!r) return;
                lastSaved = r.id;
                toast(isNew ? 'Server angelegt.' : 'Server gespeichert.');
                if (editId !== null) { m.close(); return; }
                showList();
              },
            }, '💾 Speichern')));
        setTimeout(() => (isNew ? f.name : f.host).focus(), 30);
      };
      if (editId === 'new') showForm(null); else showList();
    });
  }

  // ================================================================ Server durchsuchen

  /**
   * mode 'import': Dateien/Ordner auswählen → { id, paths }
   * mode 'folder': Zielordner wählen → { id, path }
   */
  function browse({ mode = 'import', id = null, path = null } = {}) {
    return new Promise(async (resolve) => {
      let list = await servers();
      const last = (await call('sftp:last', null, { quiet: true })) || {};
      if (!list.length) {
        const nid = await manage({ editId: 'new' });
        list = await servers();
        if (!list.length) return resolve(null);
        id = nid || list[0].id;
      }
      const st = { id: id || (list.some((s) => s.id === last.id) ? last.id : list[0].id), path: path || (mode === 'import' ? last.importDir : last.exportDir) || null, sel: new Set(), entries: [] };
      if (last.id && st.id !== last.id) st.path = path || null;
      let done = false;
      const serverSel = h('select');
      const fillServers = () => {
        serverSel.innerHTML = '';
        for (const s of list) serverSel.append(h('option', { value: s.id, selected: s.id === st.id || null }, `${s.name} (${s.host})`));
      };
      fillServers();
      serverSel.addEventListener('change', () => { st.id = serverSel.value; st.path = null; load(); });
      const pathIn = h('input', { type: 'text', style: { flex: '1' } });
      pathIn.addEventListener('keydown', (e) => { if (e.key === 'Enter') { st.path = pathIn.value.trim() || '/'; load(); } });
      const listEl = h('div.list.sftp-list');
      const info = h('span.muted.small');
      const load = async () => {
        listEl.innerHTML = '';
        listEl.append(h('div.list-row', h('span.muted', 'Wird geladen …')));
        st.sel.clear(); updInfo();
        const r = await call('sftp:list', { id: st.id, path: st.path }, { quiet: true });
        listEl.innerHTML = '';
        if (!r) {
          const err = await window.api.invoke('sftp:list', { id: st.id, path: st.path });
          listEl.append(h('div.notice.warn', err.error || 'Fehler beim Laden.'));
          if (st.path) listEl.append(h('button.btn.small', { onclick: () => { st.path = null; load(); } }, 'Zum Startordner'));
          return;
        }
        st.path = r.path; st.entries = r.entries; pathIn.value = r.path;
        if (r.path !== '/') listEl.append(h('div.list-row.clickable', { ondblclick: () => go(parent(r.path)), onclick: () => go(parent(r.path)) }, h('span', '⬆'), h('div.grow', h('div.name', '.. (Ordner darüber)'))));
        const items = mode === 'folder' ? r.entries.filter((e) => e.type !== 'file') : r.entries;
        if (!items.length) listEl.append(h('div.list-row', h('span.muted.small', mode === 'folder' ? 'Keine Unterordner.' : 'Ordner ist leer.')));
        for (const e of items) {
          const full = joinPath(r.path, e.name);
          const isDir = e.type !== 'file';
          const cb = mode === 'import' ? h('input', { type: 'checkbox', onclick: (ev) => { ev.stopPropagation(); if (ev.target.checked) st.sel.add(full); else st.sel.delete(full); updInfo(); } }) : null;
          listEl.append(h('div.list-row.clickable', {
            title: isDir ? 'Doppelklick: Ordner öffnen' : '',
            onclick: (ev) => { if (cb && ev.target !== cb) { cb.checked = !cb.checked; if (cb.checked) st.sel.add(full); else st.sel.delete(full); updInfo(); } },
            ondblclick: () => { if (isDir) go(full); },
          }, cb, h('span', isDir ? '📁' : fileIcon(e.name)), h('div.grow', h('div.name', e.name)),
          isDir ? h('button.btn.small', { onclick: (ev) => { ev.stopPropagation(); go(full); } }, 'Öffnen ›') : h('span.muted.small', fmtSize(e.size))));
        }
      };
      const go = (p) => { st.path = p; load(); };
      const updInfo = () => {
        info.textContent = mode === 'import'
          ? (st.sel.size ? `${st.sel.size} ausgewählt` : 'Nichts ausgewählt → der ganze aktuelle Ordner wird importiert')
          : '';
      };
      const m = modal({
        title: mode === 'import' ? '🌐 Vom Server importieren (SFTP)' : '🌐 Zielordner auf dem Server wählen',
        wide: true,
        body: h('div',
          h('div.row.gap-s', { style: { alignItems: 'center', marginBottom: '8px' } },
            h('span.muted.small', 'Server:'), serverSel,
            h('button.btn.small', { onclick: async () => { await manage(); list = await servers(); if (!list.length) { m.close(); return; } if (!list.some((s) => s.id === st.id)) st.id = list[0].id; fillServers(); load(); } }, '⚙ Server verwalten')),
          h('div.row.gap-s', { style: { marginBottom: '8px' } }, pathIn,
            h('button.btn.small', { onclick: () => { st.path = pathIn.value.trim() || '/'; load(); } }, 'Gehe zu'),
            h('button.btn.small', { onclick: () => load() }, '⟳')),
          listEl,
          h('div.row.gap-s', { style: { marginTop: '6px' } }, info),
          mode === 'import' ? h('p.muted.small', 'Tipp: Einen Resource-Ordner (z. B. mit stream/ und fxmanifest.lua) einfach anhaken – das Tool sucht darin alles automatisch.') : null),
        buttons: [
          { label: 'Abbrechen' },
          mode === 'import'
            ? { label: '⬇ Importieren', primary: true, onClick: () => { done = true; resolve({ id: st.id, paths: st.sel.size ? [...st.sel] : [st.path] }); } }
            : { label: '✓ Diesen Ordner wählen', primary: true, onClick: () => { done = true; resolve({ id: st.id, path: st.path }); } },
        ],
        onClose: () => { if (!done) resolve(null); },
      });
      updInfo();
      load();
    });
  }

  const fileIcon = (n) => (/\.(ytd|png|dds|jpg)$/i.test(n) ? '🖼' : /\.(yft|ydr|ydd)$/i.test(n) ? '🧊' : /\.(meta|xml|lua|cfg|json)$/i.test(n) ? '📝' : /\.ycd$/i.test(n) ? '🕺' : /\.rpf$/i.test(n) ? '📦' : '📄');

  // ================================================================ Import

  /** Vom Server laden und im Studio öffnen. studio: Objekt mit open(paths, add). */
  async function importTo(studio, add) {
    const pick = await browse({ mode: 'import' });
    if (!pick) return;
    const r = await call('sftp:download', pick, { busyText: 'Wird vom Server geladen …' });
    if (!r) return;
    toast(`${r.files} Datei(en) vom Server geladen (${fmtSize(r.bytes)}).`);
    await studio.open(r.paths, add);
  }

  // ================================================================ Export-Ziel

  /**
   * Umschalter „PC-Ordner“ / „Server (SFTP)“ für Export-Dialoge.
   * localField: das vorhandene Zielordner-Feld (wird bei SFTP ausgeblendet). dirIn: dessen Eingabefeld.
   */
  function targetField(localField, dirIn) {
    const st = { mode: 'local', id: null, remoteDir: '' };
    const remoteIn = h('input', { type: 'text', placeholder: 'Ordner auf dem Server, z. B. /home/fivem/resources/[autos]' });
    const serverSel = h('select');
    const remoteField = h('div.field', h('label', 'Server und Zielordner'),
      h('div.row.gap-s', { style: { marginBottom: '6px' } }, serverSel,
        h('button.btn.small', { onclick: async () => { await manage(); fill(); } }, '⚙ Server verwalten')),
      h('div.input-row', remoteIn, h('button.btn', {
        onclick: async () => {
          const r = await browse({ mode: 'folder', id: serverSel.value || null, path: remoteIn.value.trim() || null });
          if (r) { remoteIn.value = r.path; await fill(r.id); }
        },
      }, 'Durchsuchen …')),
      h('div.hint', 'Den Ordner wählen, in dem die Resource liegt (z. B. resources/[mlo]) – nicht die Resource selbst. Danach auf dem Server „refresh“ + „ensure <name>“ bzw. „restart <name>“.'));
    const fill = async (selectId) => {
      const list = await servers();
      const last = (await call('sftp:last', null, { quiet: true })) || {};
      serverSel.innerHTML = '';
      if (!list.length) serverSel.append(h('option', { value: '' }, '– noch kein Server –'));
      const want = selectId || serverSel.dataset.cur || last.id;
      for (const s of list) serverSel.append(h('option', { value: s.id, selected: s.id === want || null }, `${s.name} (${s.host})`));
      serverSel.dataset.cur = serverSel.value;
      if (!remoteIn.value) remoteIn.value = (last.id === serverSel.value && (last.importDir || last.exportDir)) || (list.find((s) => s.id === serverSel.value) || {}).remoteDir || '';
    };
    serverSel.addEventListener('change', () => { serverSel.dataset.cur = serverSel.value; });
    const btns = h('div.row.gap-s');
    const sync = () => {
      btns.innerHTML = '';
      btns.append(
        h(`button.btn.small${st.mode === 'local' ? '.primary' : ''}`, { onclick: () => { st.mode = 'local'; sync(); } }, '💻 Ordner auf dem PC'),
        h(`button.btn.small${st.mode === 'sftp' ? '.primary' : ''}`, { onclick: () => { st.mode = 'sftp'; sync(); fill(); } }, '🌐 Server (SFTP)'));
      localField.style.display = st.mode === 'local' ? '' : 'none';
      remoteField.style.display = st.mode === 'sftp' ? '' : 'none';
    };
    sync();
    return {
      el: h('div', h('div.field', h('label', 'Exportieren nach'), btns), remoteField),
      get: () => ({ mode: st.mode, dir: dirIn.value.trim(), id: serverSel.value, remoteDir: remoteIn.value.trim() }),
    };
  }

  /**
   * Führt den Export aus – lokal oder per SFTP (erst in einen Zwischenordner, dann hochladen).
   * doExport(outDir) muss das Ergebnis des Studio-Exports liefern (mit .folder).
   * Ergebnis: das Export-Ergebnis, bei SFTP zusätzlich .remote / .server.
   */
  async function runExport(target, resourceName, doExport) {
    if (!resourceName) { toast('Bitte einen Namen eingeben.', 'warn'); return null; }
    if (target.mode === 'sftp') {
      if (!target.id) { toast('Bitte zuerst einen Server anlegen (⚙ Server verwalten).', 'warn'); return null; }
      if (!target.remoteDir) { toast('Bitte den Zielordner auf dem Server angeben.', 'warn'); return null; }
      // Häufiger Fehler: die Resource selbst als Zielordner gewählt → es entstünde <resource>/<resource>
      const parentOf = (p) => { const s = p.replace(/\/+$/, ''); const i = s.lastIndexOf('/'); return i > 0 ? s.slice(0, i) : '/'; };
      const baseOf = (p) => p.replace(/\/+$/, '').split('/').pop();
      if (baseOf(target.remoteDir) === resourceName) {
        target.remoteDir = parentOf(target.remoteDir);
        toast(`Zielordner korrigiert: hochgeladen wird nach ${target.remoteDir} (die Resource selbst war als Ziel gewählt).`, 'info', 9000);
      } else {
        const isRes = await call('sftp:exists', { id: target.id, remoteDir: target.remoteDir, resourceName: 'fxmanifest.lua' }, { busyText: 'Server wird geprüft …' });
        if (isRes === null) return null;
        if (isRes) {
          if (!await UI.confirm('Zielordner ist selbst eine Resource', `„${target.remoteDir}“ enthält eine fxmanifest.lua – das ist eine Resource, kein Ordner für Resources. Die Resource „${resourceName}“ stattdessen nach „${parentOf(target.remoteDir)}“ hochladen?`, 'Ja, Ordner darüber')) return null;
          target.remoteDir = parentOf(target.remoteDir);
        }
      }
      const ex = await call('sftp:exists', { id: target.id, remoteDir: target.remoteDir, resourceName }, { busyText: 'Server wird geprüft …' });
      if (ex === null) return null;
      if (ex && !await UI.confirm('Auf dem Server aktualisieren?', `Im Ordner ${target.remoteDir} gibt es schon eine Resource mit diesem Namen. Es werden nur geänderte Dateien hochgeladen; im Tool gelöschte Dateien werden entfernt. Alles, was überschrieben oder gelöscht wird, wird vorher auf deinem PC gesichert.`, 'Aktualisieren')) return null;
      const stage = await call('sftp:stage');
      if (!stage) return null;
      const r = await doExport(stage);
      if (!r) return null;
      const up = await call('sftp:upload', { id: target.id, local: r.folder, remoteDir: target.remoteDir }, { busyText: 'Wird auf den Server hochgeladen …' });
      if (!up) { toast('Der Export wurde erstellt, aber das Hochladen ist fehlgeschlagen.', 'error'); return null; }
      const s = (await servers()).find((x) => x.id === target.id);
      return { ...r, remote: up.remote, server: s ? s.name : '', uploaded: up.uploaded, newFiles: up.newFiles, unchanged: up.unchanged, deleted: up.deleted, untouched: up.untouched || [], backup: up.backup || null };
    }
    if (!target.dir) { toast('Bitte einen Zielordner wählen.', 'warn'); return null; }
    if (await call('fivem:exists', { outDir: target.dir, resourceName })) {
      if (!await UI.confirm('Überschreiben?', 'Eine Resource mit diesem Namen existiert dort schon. Überschreiben?', 'Überschreiben', { danger: true })) return null;
    }
    return doExport(target.dir);
  }

  /** Ergebnis-Zeile für Export-Dialoge: lokaler Ordner oder Server-Pfad. */
  function resultPath(r) {
    if (!r.remote) return h('p', h('code', r.folder));
    return h('div',
      h('p', '🌐 Server ', h('b', r.server || 'Server'), ': ', h('code', r.remote)),
      h('p', r.uploaded || r.deleted
        ? `⬆ ${r.uploaded} Datei(en) hochgeladen (${r.newFiles || 0} neu, ${r.uploaded - (r.newFiles || 0)} geändert) · ${r.unchanged || 0} unverändert übersprungen${r.deleted ? ` · ${r.deleted} gelöscht` : ''}`
        : `✓ Auf dem Server war schon alles aktuell – nichts hochgeladen (${r.unchanged || 0} Dateien geprüft).`),
      r.untouched && r.untouched.length
        ? h('div.notice.warn', `${r.untouched.length} Datei(en) auf dem Server gehören nicht zu diesem Export und wurden nicht angetastet: `, h('code', r.untouched.slice(0, 6).join(', ') + (r.untouched.length > 6 ? ' …' : '')))
        : null,
      r.backup ? h('p.muted', '💾 Die vorherige Server-Version wurde gesichert: ', h('code', r.backup)) : null);
  }
  function resultButtons(r) {
    return r.remote
      ? [...(r.backup ? [{ label: '💾 Sicherung öffnen', onClick: () => { call('shell:open', { target: r.backup }); return false; } }] : []), { label: 'Schließen', primary: true }]
      : [{ label: 'Schließen' }, { label: '📂 Ordner öffnen', primary: true, onClick: () => call('shell:open', { target: r.folder }) }];
  }

  /** Name der geöffneten Original-Resource (Ordner mit fxmanifest.lua) oder null. */
  async function originalName() {
    try { const r = await call('session:original', {}); return (r && r.name) || null; } catch { return null; }
  }
  /** Hinweis im Export-Dialog, wenn eine Original-Resource erhalten bleibt. */
  function keepNotice(orig) {
    return orig
      ? h('div.notice.info', '♻ Die Original-Resource „', h('b', orig), '“ bleibt erhalten: gleicher Name, gleiche Ordner und Dateinamen, fxmanifest.lua und Skripte. Nur deine Änderungen werden übernommen – auf den Server werden nur geänderte Dateien hochgeladen.')
      : null;
  }

  return { manage, browse, importTo, targetField, runExport, resultPath, resultButtons, originalName, keepNotice };
})();
