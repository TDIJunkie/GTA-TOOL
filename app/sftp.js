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
async function download(id, remotePaths) {
  if (!remotePaths || !remotePaths.length) throw new Error('Nichts ausgewählt.');
  const local = path.join(ctx.tempRoot, `sftp-${Date.now()}`);
  fs.mkdirSync(local, { recursive: true });
  // Erst alles auflisten (Größen kommen gleich mit, für den Fortschritt)
  const files = [];
  const results = [];
  await wrap(id, async (c) => {
    const walk = async (rp, lp, typeHint, sizeHint) => {
      const t = typeHint || await c.exists(rp);
      if (t === 'd') {
        fs.mkdirSync(lp, { recursive: true });
        ctx.send('progress', { text: `Ordner wird gelesen: ${posix.basename(rp)}`, percent: -1 });
        for (const e of await c.list(rp)) {
          if (e.name === '.' || e.name === '..') continue;
          if (e.type === 'd' && /^(\.git|node_modules|cache|\.vs)$/i.test(e.name)) continue;
          await walk(posix.join(rp, e.name), path.join(lp, safeLocal(e.name)), e.type === 'l' ? null : e.type, e.size);
        }
      } else if (t === '-' || t === 'l') files.push({ rp, lp, size: sizeHint || 0 });
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
    ctx.send('progress', { text: `Wird vom Server geladen (${i + 1}/${files.length}): ${posix.basename(f.rp)}`, percent: total ? Math.round((done / total) * 100) : Math.round((i / files.length) * 100) });
    fs.mkdirSync(path.dirname(f.lp), { recursive: true });
    await transfer(id, 'get', f.rp, f.lp, f.rp);
    try { done += fs.statSync(f.lp).size; } catch { /* egal */ }
  }
  return { paths: results.filter((p) => fs.existsSync(p)), files: files.length, bytes: done, localRoot: local };
}

const safeLocal = (n) => String(n).replace(/[<>:"|?*\\]/g, '_').replace(/[. ]+$/, '_') || '_';

async function exists(id, remoteDir, name) {
  return wrap(id, async (c) => !!(await c.exists(posix.join(norm(remoteDir), name))));
}

/** Lokalen Resource-Ordner hochladen nach remoteDir/<ordnername> (vorhandener Ordner wird ersetzt). */
async function upload(id, localFolder, remoteDir) {
  if (!fs.existsSync(localFolder)) throw new Error('Der exportierte Ordner fehlt.');
  const name = path.basename(localFolder);
  const files = [];
  const target = await wrap(id, async (c) => {
    const base = await c.realPath(norm(remoteDir || '.'));
    const t = posix.join(base, name);
    if ((await c.exists(base)) !== 'd') await c.mkdir(base, true);
    if (await c.exists(t)) {
      ctx.send('progress', { text: 'Alte Version auf dem Server wird entfernt …', percent: -1 });
      await c.rmdir(t, true);
    }
    const walk = (lp, rp) => {
      for (const e of fs.readdirSync(lp, { withFileTypes: true })) {
        const l = path.join(lp, e.name), r = posix.join(rp, e.name);
        if (e.isDirectory()) walk(l, r); else files.push({ l, r });
      }
    };
    walk(localFolder, t);
    const dirs = [...new Set(files.map((f) => posix.dirname(f.r)))].sort();
    for (const d of dirs) await c.mkdir(d, true);
    return t;
  });
  const total = files.reduce((a, f) => a + fs.statSync(f.l).size, 0) || 1;
  let done = 0;
  for (let i = 0; i < files.length; i++) {
    const f = files[i];
    ctx.send('progress', { text: `Wird hochgeladen (${i + 1}/${files.length}): ${path.basename(f.l)}`, percent: Math.round((done / total) * 100) });
    await transfer(id, 'put', f.l, f.r, f.r);
    done += fs.statSync(f.l).size;
  }
  return { remote: target, files: files.length, bytes: done };
}

/** Gespeichertes Geheimnis (nur im Hauptprozess, z. B. für „Verbindung testen“ beim Bearbeiten). */
function savedSecret(id) { const s = findServer(id); return s ? decrypt(s.secret) : ''; }

function closeAll() { for (const id of [...conns.keys()]) closeConn(id); }

module.exports = { init, list, save, remove, test, listDir, download, exists, upload, closeAll, savedSecret };
