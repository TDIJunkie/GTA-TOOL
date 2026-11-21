'use strict';
// SFTP: Server verwalten (Zugangsdaten verschlüsselt per Windows-DPAPI/safeStorage), Ordner durchsuchen,
// Dateien/Ordner herunterladen (Import) und fertige Resources hochladen (Export).

const path = require('path');
const fs = require('fs');
const crypto = require('crypto');
const Client = require('ssh2-sftp-client');

let ctx = null; // { getSettings, saveSettings, safeStorage, send, tempRoot, log }

function init(c) { ctx = c; }

const servers = () => (ctx.getSettings().sftpServers || []);
const findServer = (id) => servers().find((s) => s.id === id);

function encrypt(text) {
  if (!text) return '';
  if (!ctx.safeStorage.isEncryptionAvailable()) throw new Error('Die sichere Passwort-Speicherung von Windows ist nicht verfügbar.');
  return ctx.safeStorage.encryptString(text).toString('base64');
}
function decrypt(b64) {
  if (!b64) return '';
  try { return ctx.safeStorage.decryptString(Buffer.from(b64, 'base64')); } catch { return ''; }
}

/** Öffentliche Sicht auf die Server (ohne Geheimnisse). */
function list() {
  return servers().map((s) => ({
    id: s.id, name: s.name, host: s.host, port: s.port, username: s.username, auth: s.auth,
    keyPath: s.keyPath || '', remoteDir: s.remoteDir || '', hasSecret: !!s.secret, hostKey: s.hostKey ? s.hostKey.slice(0, 16) : '',
  }));
}

function clean(s) {
  const host = String(s.host || '').trim().replace(/^sftp:\/\//i, '').replace(/\/.*$/, '');
  if (!host) throw new Error('Bitte die Server-Adresse (Host oder IP) eingeben.');
  const port = Number(s.port) || 22;
  if (port < 1 || port > 65535) throw new Error('Der Port muss zwischen 1 und 65535 liegen.');
  const username = String(s.username || '').trim();
  if (!username) throw new Error('Bitte den Benutzernamen eingeben.');
  const auth = s.auth === 'key' ? 'key' : 'password';
  if (auth === 'key' && !s.keyPath) throw new Error('Bitte die Schlüsseldatei (privater Schlüssel) wählen.');
  return { name: String(s.name || '').trim() || `${username}@${host}`, host, port, username, auth, keyPath: auth === 'key' ? String(s.keyPath) : '', remoteDir: String(s.remoteDir || '').trim() };
}

/** Speichern/Anlegen. secret: Passwort bzw. Passphrase; undefined = unverändert lassen. */
function save(server, secret) {
  const st = ctx.getSettings();
  const all = st.sftpServers || [];
  const c = clean(server);
  let s = server.id ? all.find((x) => x.id === server.id) : null;
  if (!s) { s = { id: crypto.randomUUID() }; all.push(s); }
  const hostChanged = s.host !== c.host || s.port !== c.port;
  Object.assign(s, c);
  if (hostChanged) delete s.hostKey;
  if (secret !== undefined && secret !== null) s.secret = encrypt(secret);
  st.sftpServers = all;
  ctx.saveSettings();
  closeConn(s.id);
  return s.id;
}

function remove(id) {
  const st = ctx.getSettings();
  st.sftpServers = (st.sftpServers || []).filter((s) => s.id !== id);
  ctx.saveSettings();
  closeConn(id);
  return true;
}

// ------------------------------------------------------------------ Verbindung

const conns = new Map(); // id -> { client, timer, ready: Promise }

function closeConn(id) {
  const c = conns.get(id);
  if (!c) return;
  conns.delete(id);
  clearTimeout(c.timer);
  c.client.end().catch(() => {});
}

function friendly(e, s) {
  const m = String((e && e.message) || e || '');
  if (/ENOTFOUND|getaddrinfo/i.test(m)) return `Server „${s.host}“ wurde nicht gefunden. Adresse prüfen.`;
  if (/ECONNREFUSED/i.test(m)) return `Verbindung zu ${s.host}:${s.port} abgelehnt. Läuft dort ein SFTP-Server (Port richtig)?`;
  if (/ETIMEDOUT|Timed out|timeout/i.test(m)) return `Zeitüberschreitung bei ${s.host}:${s.port}. Server erreichbar? Firewall?`;
  if (/authentication|All configured authentication methods failed/i.test(m)) return 'Anmeldung fehlgeschlagen – Benutzername, Passwort oder Schlüssel prüfen.';
  if (/Host key|HOSTKEY/i.test(m)) return m;
  if (/No such file|ENOENT|does not exist/i.test(m)) return 'Datei oder Ordner auf dem Server nicht gefunden.';
  if (/Permission denied|EACCES/i.test(m)) return 'Keine Berechtigung auf dem Server für diesen Ordner.';
  if (/passphrase|Encrypted private key/i.test(m)) return 'Die Schlüsseldatei ist verschlüsselt – bitte die richtige Passphrase angeben.';
  return m || 'Unbekannter SFTP-Fehler.';
}

/** Verbindet (oder nutzt eine offene Verbindung). draft: ungespeicherter Server zum Testen. */
async function connect(id, draft) {
  const s = draft ? { ...clean(draft), id: null, hostKey: null } : findServer(id);
  if (!s) throw new Error('Server nicht gefunden.');
  if (!draft && conns.has(id)) {
    const c = conns.get(id);
    try { await c.ready; touch(id); return c.client; } catch { closeConn(id); }
  }
  const secret = draft ? (draft.secret || '') : decrypt(s.secret);
  const client = new Client(`gta-tool-${s.id || 'test'}`);
  let seenKey = null;
  const opts = {
    host: s.host, port: s.port, username: s.username, readyTimeout: 15000, keepaliveInterval: 15000,
    hostHash: 'sha256',
    hostVerifier: (hash) => {
      seenKey = String(hash);
      if (s.hostKey && s.hostKey !== seenKey) return false;
      return true;
    },
  };
  if (s.auth === 'key') {
    try { opts.privateKey = fs.readFileSync(s.keyPath); } catch { throw new Error(`Schlüsseldatei nicht lesbar: ${s.keyPath}`); }
    if (secret) opts.passphrase = secret;
  } else {
    opts.password = secret;
    opts.tryKeyboard = true;
  }
  const ready = client.connect(opts).catch((e) => {
    if (s.hostKey && seenKey && s.hostKey !== seenKey) {
      throw new Error(`Achtung: Der Sicherheitsschlüssel von ${s.host} hat sich geändert. Falls der Server neu aufgesetzt wurde, den Server in der Verwaltung löschen und neu anlegen – sonst NICHT verbinden.`);
    }
    throw new Error(friendly(e, s));
  });
  if (!draft) {
    conns.set(id, { client, ready, timer: null });
    client.on('end', () => { if (conns.get(id) && conns.get(id).client === client) conns.delete(id); });
    client.on('error', () => closeConn(id));
  }
  await ready;
  if (!draft && !s.hostKey && seenKey) { s.hostKey = seenKey; ctx.saveSettings(); }
  if (!draft) touch(id);
  return client;
}

function touch(id) {
  const c = conns.get(id);
  if (!c) return;
  clearTimeout(c.timer);
  c.timer = setTimeout(() => closeConn(id), 90 * 1000);
}

const posix = path.posix;
const norm = (p) => posix.normalize(String(p || '.').replace(/\\/g, '/'));

async function wrap(id, fn) {
  const s = findServer(id);
  try {
    const c = await connect(id);
    const r = await fn(c);
    touch(id);
    return r;
  } catch (e) {
    throw new Error(friendly(e, s || {}));
  }
}

async function test(draft, secret) {
  const s = clean(draft);
  let c;
  try {
    c = await connect(null, { ...draft, secret });
    const home = await c.realPath('.');
    const dir = s.remoteDir ? await c.exists(s.remoteDir) : 'd';
    return { ok: true, home, remoteDirOk: dir === 'd' };
  } catch (e) {
    throw new Error(friendly(e, s));
  } finally { if (c) c.end().catch(() => {}); }
}

/** Ordnerinhalt. path leer → Startordner des Servers bzw. Home. */
async function listDir(id, p) {
  const s = findServer(id);
  return wrap(id, async (c) => {
    let dir = p || s.remoteDir || '.';
    dir = await c.realPath(norm(dir));
    const items = await c.list(dir);
    const entries = items
      .filter((x) => x.name !== '.' && x.name !== '..')
      .map((x) => ({ name: x.name, type: x.type === 'd' ? 'dir' : x.type === 'l' ? 'link' : 'file', size: x.size, time: x.modifyTime }))
      .sort((a, b) => (a.type === 'dir' ? 0 : 1) - (b.type === 'dir' ? 0 : 1) || a.name.localeCompare(b.name, 'de', { sensitivity: 'base' }));
    return { path: dir, entries };
  });
}

/**
 * Eine Datei übertragen – schonend und mit Wiederholung:
 *  1. fastGet/fastPut mit wenig parallelen Anfragen (viele Server/Hoster brechen bei 64 parallelen Anfragen ab)
 *  2. bei Fehler: neu verbinden und einfach (sequenziell, Stream) übertragen
 *  3. notfalls noch einmal nach kurzer Pause
 */
const GENTLE = { concurrency: 4, chunkSize: 32768 };
async function transfer(id, kind, from, to, label) {
  let lastErr = null;
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      if (attempt > 0) { closeConn(id); await new Promise((r) => setTimeout(r, 800 * attempt)); }
      const c = await connect(id);
      if (kind === 'get') {
        if (attempt === 0) await c.fastGet(from, to, GENTLE); else await c.get(from, to);
      } else {
        if (attempt === 0) await c.fastPut(from, to, GENTLE); else await c.put(from, to);
      }
      touch(id);
      return;
    } catch (e) {
      lastErr = e;
      ctx.log && ctx.log(`[sftp] ${kind} ${label} Versuch ${attempt + 1}: ${e.message}`);
      if (/No such file|ENOENT|Permission denied|EACCES/i.test(String(e.message))) break;
    }
  }
  throw new Error(`${posix.basename(String(label))}: ${friendly(lastErr, findServer(id) || {})}`);
}

/** Lädt Dateien/Ordner in einen neuen lokalen Ordner (Struktur und Ordnernamen bleiben erhalten). */
async function download(id, remotePaths, intoDir, verb) {
  if (!remotePaths || !remotePaths.length) throw new Error('Nichts ausgewählt.');
  const local = intoDir || path.join(ctx.tempRoot, `sftp-${Date.now()}`);
  fs.mkdirSync(local, { recursive: true });
  // Erst alles auflisten (Größen kommen gleich mit, für den Fortschritt)
  const files = [];
  const results = [];
  await wrap(id, async (c) => {
    const walk = async (rp, lp, typeHint, sizeHint, mtimeHint) => {
      const t = typeHint || await c.exists(rp);
      if (t === 'd') {
        fs.mkdirSync(lp, { recursive: true });
        ctx.send('progress', { text: `Ordner wird gelesen: ${posix.basename(rp)}`, percent: -1 });
        for (const e of await c.list(rp)) {
          if (e.name === '.' || e.name === '..') continue;
          if (e.type === 'd' && /^(\.git|node_modules|cache|\.vs)$/i.test(e.name)) continue;
          await walk(posix.join(rp, e.name), path.join(lp, safeLocal(e.name)), e.type === 'l' ? null : e.type, e.size, e.modifyTime);
        }
      } else if (t === '-' || t === 'l') files.push({ rp, lp, size: sizeHint || 0, mtime: mtimeHint || 0 });
    };
    for (const rp of remotePaths) {
      const r = norm(rp);
      const lp = path.join(local, safeLocal(posix.basename(r)));
      await walk(r, lp, null, 0);
      results.push(lp);
    }
  });
  if (!files.length) throw new Error('In der Auswahl wurden keine Dateien gefunden.');
  const total = files.reduce((a, f) => a + f.size, 0);
  let done = 0;
  for (let i = 0; i < files.length; i++) {
    const f = files[i];
    ctx.send('progress', { text: `${verb || 'Wird vom Server geladen'} (${i + 1}/${files.length}): ${posix.basename(f.rp)}`, percent: total ? Math.round((done / total) * 100) : Math.round((i / files.length) * 100) });
    fs.mkdirSync(path.dirname(f.lp), { recursive: true });
    await transfer(id, 'get', f.rp, f.lp, f.rp);
    try {
      const size = fs.statSync(f.lp).size;
      done += size;
      // Stand merken: beim späteren Hochladen werden nur geänderte Dateien übertragen
      if (f.mtime && size === f.size) stateLoad()[skey(id, f.rp)] = { size, mtime: f.mtime, md5: await md5File(f.lp) };
    } catch { /* egal */ }
  }
  stateSave();
  return { paths: results.filter((p) => fs.existsSync(p)), files: files.length, bytes: done, localRoot: local };
}

const safeLocal = (n) => String(n).replace(/[<>:"|?*\\]/g, '_').replace(/[. ]+$/, '_') || '_';

async function exists(id, remoteDir, name) {
  return wrap(id, async (c) => !!(await c.exists(posix.join(norm(remoteDir), name))));
}

// ---------------------------------------------------------------- Abgleich beim Hochladen
// Stand der Server-Dateien (Größe, Zeitstempel, MD5) aus Import und letztem Upload –
// damit beim Export nur wirklich geänderte Dateien übertragen werden.
let stateCache = null;
function stateLoad() {
  if (stateCache) return stateCache;
  try { stateCache = JSON.parse(fs.readFileSync(ctx.stateFile, 'utf8')) || {}; } catch { stateCache = {}; }
  return stateCache;
}
function stateSave() { if (!ctx.stateFile || !stateCache) return; try { fs.writeFileSync(ctx.stateFile, JSON.stringify(stateCache)); } catch { /* egal */ } }
const skey = (id, rp) => `${id}|${rp}`;
function md5File(f) {
  return new Promise((res, rej) => {
    const h = crypto.createHash('md5');
    fs.createReadStream(f).on('data', (d) => h.update(d)).on('end', () => res(h.digest('hex'))).on('error', rej);
  });
}
const localKey = (rel) => rel.split('/').map(safeLocal).join('/');

/** Alle Dateien unter root (relativer Pfad → Infos). */
async function remoteTree(c, root) {
  const out = new Map();
  const walk = async (rp, rel) => {
    for (const e of await c.list(rp)) {
      if (e.name === '.' || e.name === '..') continue;
      const r = rel ? `${rel}/${e.name}` : e.name;
      if (e.type === 'd') await walk(posix.join(rp, e.name), r);
      else out.set(r, { rp: posix.join(rp, e.name), size: e.size, mtime: e.modifyTime });
    }
  };
  await walk(root, '');
  return out;
}

/** MD5 direkt auf dem Server (nur wenn der Server Befehle erlaubt) – sonst null. */
async function remoteMd5(c, paths) {
  const ssh = c && c.client;
  if (!ssh || typeof ssh.exec !== 'function' || !paths.length) return null;
  const q = (s) => `'${String(s).replace(/'/g, "'\\''")}'`;
  const result = new Map();
  for (let i = 0; i < paths.length; i += 40) {
    const batch = paths.slice(i, i + 40);
    const out = await new Promise((res) => {
      const timer = setTimeout(() => res(null), 30000);
      try {
        ssh.exec(`md5sum -- ${batch.map(q).join(' ')}`, (err, stream) => {
          if (err) { clearTimeout(timer); return res(null); }
          let o = '';
          stream.on('data', (d) => { o += d; });
          stream.stderr.on('data', () => {});
          stream.on('close', () => { clearTimeout(timer); res(o); });
        });
      } catch { clearTimeout(timer); res(null); }
    });
    if (out === null) return result.size ? result : null;
    for (const line of out.split('\n')) {
      const m = /^\\?([0-9a-f]{32})\s+\*?(.+)$/.exec(line.trim());
      if (m) result.set(m[2], m[1]);
    }
    if (i === 0 && !result.size) return null;
  }
  return result;
}

/**
 * Lokalen Resource-Ordner nach remoteDir/<ordnername> hochladen – mit Abgleich:
 * nur neue und geänderte Dateien werden übertragen; Dateien, die es beim Import gab und die jetzt fehlen
 * (im Tool gelöscht), werden auf dem Server entfernt. Unbekannte Dateien auf dem Server bleiben unangetastet.
 * Alles, was überschrieben oder gelöscht wird, wird vorher lokal gesichert.
 */
async function upload(id, localFolder, remoteDir) {
  if (!fs.existsSync(localFolder)) throw new Error('Der exportierte Ordner fehlt.');
  const name = path.basename(localFolder);
  const state = stateLoad();

  // Lokale Dateien + Prüfsummen
  const local = [];
  const walkLocal = (lp, rel) => {
    for (const e of fs.readdirSync(lp, { withFileTypes: true })) {
      const l = path.join(lp, e.name), r = rel ? `${rel}/${e.name}` : e.name;
      if (e.isDirectory()) walkLocal(l, r); else local.push({ l, rel: r, size: fs.statSync(l).size });
    }
  };
  walkLocal(localFolder, '');
  for (let i = 0; i < local.length; i++) {
    if (i % 25 === 0) ctx.send('progress', { text: `Dateien werden verglichen (${i + 1}/${local.length}) …`, percent: Math.round((i / local.length) * 100) });
    local[i].md5 = await md5File(local[i].l);
  }

  // Server-Stand
  ctx.send('progress', { text: 'Server-Stand wird gelesen …', percent: -1 });
  const { t, tree } = await wrap(id, async (c) => {
    const base = await c.realPath(norm(remoteDir || '.'));
    if ((await c.exists(base)) !== 'd') await c.mkdir(base, true);
    const tt = posix.join(base, name);
    const ex = await c.exists(tt);
    if (ex && ex !== 'd') throw new Error(`Auf dem Server gibt es schon eine Datei namens „${name}“.`);
    return { t: tt, tree: ex === 'd' ? await remoteTree(c, tt) : new Map() };
  });
  const remoteByKey = new Map();
  for (const [rel, info] of tree) remoteByKey.set(localKey(rel), info);

  // Vergleichen
  const unsure = [];
  for (const f of local) {
    const r = remoteByKey.get(f.rel);
    f.rp = r ? r.rp : posix.join(t, f.rel);
    f.r = r;
    if (!r) { f.action = 'new'; continue; }
    if (r.size !== f.size) { f.action = 'changed'; continue; }
    const s = state[skey(id, r.rp)];
    if (s && s.md5 && s.size === r.size && s.mtime === r.mtime) { f.action = s.md5 === f.md5 ? 'same' : 'changed'; continue; }
    unsure.push(f);
  }
  if (unsure.length) {
    ctx.send('progress', { text: `${unsure.length} Dateien werden auf dem Server geprüft …`, percent: -1 });
    let sums = null;
    try { sums = await wrap(id, (c) => remoteMd5(c, unsure.map((f) => f.rp))); } catch { sums = null; }
    for (const f of unsure) {
      const h = sums && sums.get(f.rp);
      f.action = h ? (h === f.md5 ? 'same' : 'changed') : 'changed';
    }
  }
  const localKeys = new Set(local.map((f) => f.rel));
  const toDelete = [], untouched = [];
  for (const [rel, r] of tree) {
    if (localKeys.has(localKey(rel))) continue;
    if (state[skey(id, r.rp)]) toDelete.push({ ...r, rel }); else untouched.push(rel);
  }
  const toPut = local.filter((f) => f.action !== 'same');
  const unchanged = local.length - toPut.length;

  // Sicherung von allem, was überschrieben oder gelöscht wird
  let backup = null;
  const risky = [...toPut.filter((f) => f.r).map((f) => ({ rp: f.rp, rel: f.rel })), ...toDelete.map((d) => ({ rp: d.rp, rel: localKey(d.rel) }))];
  if (risky.length && ctx.backupRoot) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    const dir = path.join(ctx.backupRoot, `${name}_${stamp}`);
    try {
      for (let i = 0; i < risky.length; i++) {
        const b = risky[i];
        ctx.send('progress', { text: `Server-Version wird gesichert (${i + 1}/${risky.length}): ${posix.basename(b.rp)}`, percent: Math.round((i / risky.length) * 100) });
        const lp = path.join(dir, name, ...b.rel.split('/'));
        fs.mkdirSync(path.dirname(lp), { recursive: true });
        await transfer(id, 'get', b.rp, lp, b.rp);
      }
      backup = path.join(dir, name);
      pruneBackups(name);
    } catch (e) {
      try { fs.rmSync(dir, { recursive: true, force: true }); } catch { /* egal */ }
      throw new Error(`Die Server-Version konnte nicht gesichert werden – es wurde nichts verändert. (${e.message})`);
    }
  }

  // Hochladen
  if (toPut.length) {
    await wrap(id, async (c) => {
      const dirs = [...new Set(toPut.map((f) => posix.dirname(f.rp)))].sort();
      for (const d of dirs) if ((await c.exists(d)) !== 'd') await c.mkdir(d, true);
    });
  }
  const total = toPut.reduce((a, f) => a + f.size, 0) || 1;
  let done = 0;
  for (let i = 0; i < toPut.length; i++) {
    const f = toPut[i];
    ctx.send('progress', { text: `Wird hochgeladen (${i + 1}/${toPut.length}): ${path.basename(f.l)}`, percent: Math.round((done / total) * 100) });
    await transfer(id, 'put', f.l, f.rp, f.rp);
    done += f.size;
  }

  // Löschen + Stand merken
  await wrap(id, async (c) => {
    for (const d of toDelete) {
      ctx.send('progress', { text: `Auf dem Server gelöscht: ${posix.basename(d.rp)}`, percent: -1 });
      try { await c.delete(d.rp); } catch { /* schon weg */ }
      delete state[skey(id, d.rp)];
    }
    // dadurch leer gewordene Ordner entfernen (tiefste zuerst, nie den Resource-Ordner selbst)
    const emptied = new Set();
    for (const d of toDelete) for (let p = posix.dirname(d.rp); p.length > t.length && p.startsWith(t + '/'); p = posix.dirname(p)) emptied.add(p);
    for (const p of [...emptied].sort((a, b) => b.length - a.length)) {
      try { if (!(await c.list(p)).some((e) => e.name !== '.' && e.name !== '..')) await c.rmdir(p); } catch { /* egal */ }
    }
    for (const f of local) {
      let size = f.r ? f.r.size : f.size, mtime = f.r ? f.r.mtime : 0;
      if (f.action !== 'same') {
        try { const st = await c.stat(f.rp); size = st.size; mtime = st.modifyTime; } catch { continue; }
      }
      state[skey(id, f.rp)] = { size, mtime, md5: f.md5 };
    }
  });
  stateSave();
  return {
    remote: t, files: toPut.length, uploaded: toPut.length, newFiles: toPut.filter((f) => !f.r).length,
    unchanged, deleted: toDelete.length, untouched, bytes: done, backup,
  };
}

/** Pro Resource nur die letzten 5 Server-Sicherungen behalten. */
function pruneBackups(name) {
  try {
    const all = fs.readdirSync(ctx.backupRoot).filter((d) => d.startsWith(name + '_') && /_\d{4}-\d\d-\d\dT/.test(d.slice(name.length))).sort();
    for (const d of all.slice(0, Math.max(0, all.length - 5))) fs.rmSync(path.join(ctx.backupRoot, d), { recursive: true, force: true });
  } catch { /* egal */ }
}

/** Gespeichertes Geheimnis (nur im Hauptprozess, z. B. für „Verbindung testen“ beim Bearbeiten). */
function savedSecret(id) { const s = findServer(id); return s ? decrypt(s.secret) : ''; }

function closeAll() { for (const id of [...conns.keys()]) closeConn(id); }

module.exports = { init, list, save, remove, test, listDir, download, exists, upload, closeAll, savedSecret };
