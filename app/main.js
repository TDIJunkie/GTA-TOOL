'use strict';
// GTA Tool – Hauptprozess (Fenster, Dialoge, Backend, Blender, Dateiüberwachung)

const { app, BrowserWindow, ipcMain, dialog, shell, Menu, safeStorage } = require('electron');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { spawn } = require('child_process');
const detect = require('./detect');
const sftp = require('./sftp');

const isPacked = app.isPackaged;
const ROOT = isPacked ? process.resourcesPath : path.join(__dirname, '..');
const BACKEND_EXE = isPacked
  ? path.join(ROOT, 'backend', 'GtaBackend.exe')
  : path.join(ROOT, 'build', 'backend', 'GtaBackend.exe');
const TEXCONV = isPacked ? path.join(ROOT, 'tools', 'texconv.exe') : path.join(ROOT, 'tools', 'texconv.exe');
const BRIDGE = path.join(ROOT, 'blender', 'gta_tool_bridge.py');
const TEMP_ROOT = path.join(os.tmpdir(), 'GTATool');

const startView = process.argv.includes('--vehicle-studio') ? 'vehicles' : process.argv.includes('--clothing-studio') ? 'clothing' : process.argv.includes('--mlo-studio') ? 'mlo' : process.argv.includes('--anim-studio') ? 'anim' : process.argv.includes('--prop-studio') ? 'prop' : 'start';
const startFiles = process.argv.slice(1).filter((a) => !a.startsWith('--') && /\.(rpf|yft|ytd|meta)$/i.test(a) && fs.existsSync(a));

let win = null;
let forceQuit = false;   // Update-Neustart: keine Rückfrage beim Schließen
if (process.env.GTA_TOOL_HIDDEN) {
  app.commandLine.appendSwitch('disable-backgrounding-occluded-windows');
  app.commandLine.appendSwitch('disable-renderer-backgrounding');
}

// ------------------------------------------------------------------ Einstellungen

const settingsFile = () => path.join(app.getPath('userData'), 'settings.json');
let settings = {};
function loadSettings() {
  try { settings = JSON.parse(fs.readFileSync(settingsFile(), 'utf8')); } catch { settings = {}; }
  let changed = false;
  if (!settings.gtaPath) { settings.gtaPath = detectGta(); changed = true; }
  if (!settings.blenderPath) { settings.blenderPath = detectBlender(); changed = true; }
  if (!settings.audioEditor) { const ae = detectAudacity(); if (ae) { settings.audioEditor = ae; changed = true; } }
  if (!settings.imageEditor) { const pdn = detectPaintNet(); if (pdn) { settings.imageEditor = pdn; changed = true; } else if (settings.imageEditor === undefined) { settings.imageEditor = ''; changed = true; } }
  if (changed) saveSettings();
}
function saveSettings() {
  fs.mkdirSync(path.dirname(settingsFile()), { recursive: true });
  fs.writeFileSync(settingsFile(), JSON.stringify(settings, null, 2));
}

/** Ordnername einer Resource (wie im Backend: Session.SafeName). */
// Gleiche Regel wie ExportMerge.ResName im Backend: Name bleibt exakt, nur für Ordner unzulässige Zeichen werden ersetzt.
const safeResourceName = (n) => String(n || '').trim().replace(/[^\x21-\x7E]|[<>:"/\\|?*]/g, '_').replace(/\.+$/, '') || 'resource';

/** Letzten Export-Ordner merken – aber nicht die Zwischenablage für SFTP-Exporte. */
function rememberExportDir(dir) {
  if (!dir || path.resolve(dir).toLowerCase().startsWith(path.resolve(TEMP_ROOT).toLowerCase())) return;
  settings.lastResourceDir = dir; settings.lastExportDir = dir; saveSettings();
}

// ------------------------------------------------------------------ Einrichtung (GTA, Blender, Sollumz)

let setupStatus = null;
let setupRunning = null;

/**
 * Sucht GTA V, Blender und Sollumz – auch außerhalb der Standardpfade. Gespeicherte, gültige Pfade bleiben.
 * force: auch suchen, wenn alles gesetzt ist (z. B. Knopf „Erneut suchen“).
 */
function runSetupCheck({ force }) {
  if (setupRunning) return setupRunning;
  setupRunning = (async () => {
    const cur = force ? { gtaPath: settings.gtaPath, blenderPath: settings.blenderPath } : settings;
    const st = await detect.checkAll(cur);
    let changed = false;
    let gtaChanged = false;
    if (st.gta.ok && st.gta.path !== settings.gtaPath) { settings.gtaPath = st.gta.path; changed = true; gtaChanged = true; }
    if (st.blender.ok && st.blender.path !== settings.blenderPath) { settings.blenderPath = st.blender.path; changed = true; }
    if (changed) saveSettings();
    if (gtaChanged && backend) await loadKeys();
    setupStatus = { ...st, dismissed: !!settings.setupDismissed };
    send('setup', setupStatus);
    return setupStatus;
  })().finally(() => { setupRunning = null; });
  return setupRunning;
}

// ------------------------------------------------------------------ Updates (GitHub-Releases)

let autoUpdater = null;
const updateState = { available: null, downloaded: false, progress: 0, error: null, checking: false };
function initUpdater() {
  if (!isPacked || autoUpdater) return autoUpdater;
  try {
    ({ autoUpdater } = require('electron-updater'));
  } catch (e) { logLine(`[update] nicht verfügbar: ${e.message}`); return null; }
  autoUpdater.autoDownload = true;
  autoUpdater.autoInstallOnAppQuit = true;
  autoUpdater.logger = { info: (m) => logLine(`[update] ${m}`), warn: (m) => logLine(`[update] ${m}`), error: (m) => logLine(`[update] ${m}`), debug: () => {} };
  autoUpdater.on('update-available', (i) => { updateState.available = i.version; updateState.error = null; send('update', { state: 'available', version: i.version }); });
  autoUpdater.on('update-not-available', () => { updateState.available = null; send('update', { state: 'none' }); });
  autoUpdater.on('download-progress', (p) => { updateState.progress = Math.round(p.percent || 0); send('update', { state: 'progress', percent: updateState.progress }); });
  autoUpdater.on('update-downloaded', (i) => { updateState.downloaded = true; updateState.available = i.version; send('update', { state: 'downloaded', version: i.version, notes: typeof i.releaseNotes === 'string' ? i.releaseNotes : '' }); });
  autoUpdater.on('error', (e) => { updateState.error = e ? e.message : 'unbekannt'; logLine(`[update] Fehler: ${updateState.error}`); send('update', { state: 'error', error: updateState.error }); });
  return autoUpdater;
}

async function checkForUpdates(manual) {
  // Nur die per Installer installierte Version aktualisiert sich (win-unpacked = Testversion)
  const installed = isPacked && fs.existsSync(path.join(path.dirname(process.execPath), 'Uninstall GTA Tool.exe'));
  if (!installed) return { ok: false, reason: 'Automatische Updates gibt es nur in der installierten Version (Setup).' };
  const u = initUpdater();
  if (!u) return { ok: false, reason: 'Der Update-Dienst ist nicht verfügbar.' };
  if (updateState.downloaded) return { ok: true, downloaded: true, version: updateState.available };
  updateState.checking = true;
  try {
    const r = await u.checkForUpdates();
    const v = r && r.updateInfo ? r.updateInfo.version : null;
    const newer = v && v !== app.getVersion() && r.isUpdateAvailable !== false;
    return { ok: true, available: newer ? v : null, current: app.getVersion() };
  } catch (e) {
    if (manual) return { ok: false, reason: `Update-Prüfung fehlgeschlagen: ${e.message}` };
    return { ok: false };
  } finally { updateState.checking = false; }
}

function detectGta() {
  const candidates = [];
  for (const drive of ['C', 'D', 'E', 'F', 'G']) {
    candidates.push(
      `${drive}:\\GTA V\\Grand Theft Auto V Legacy`,
      `${drive}:\\GTA V`,
      `${drive}:\\Program Files\\Rockstar Games\\Grand Theft Auto V`,
      `${drive}:\\Program Files\\Rockstar Games\\Grand Theft Auto V Legacy`,
      `${drive}:\\Program Files (x86)\\Steam\\steamapps\\common\\Grand Theft Auto V`,
      `${drive}:\\Program Files (x86)\\Steam\\steamapps\\common\\Grand Theft Auto V Legacy`,
      `${drive}:\\SteamLibrary\\steamapps\\common\\Grand Theft Auto V`,
      `${drive}:\\SteamLibrary\\steamapps\\common\\Grand Theft Auto V Legacy`,
      `${drive}:\\Program Files\\Epic Games\\GTAV`,
      `${drive}:\\Games\\Grand Theft Auto V`,
    );
  }
  return candidates.find((c) => fs.existsSync(path.join(c, 'GTA5.exe'))) || '';
}

/** Letzten Import-Ordner merken: bei Dateien deren Ordner, bei gewählten Ordnern den Ordner darüber. */
function rememberImport(paths) {
  const first = (paths || [])[0];
  if (!first) return;
  try {
    const dir = path.dirname(first);
    if (dir && fs.existsSync(dir)) { settings.lastImportDir = dir; saveSettings(); }
  } catch { /* egal */ }
}

function detectPaintNet() {
  const c = [
    'C:\\Program Files\\paint.net\\paintdotnet.exe',
    'C:\\Program Files (x86)\\paint.net\\paintdotnet.exe',
    path.join(process.env.LOCALAPPDATA || '', 'Microsoft', 'WindowsApps', 'paintdotnet.exe'),
  ];
  return c.find((f) => { try { return fs.existsSync(f); } catch { return false; } }) || '';
}

function detectAudacity() {
  const c = ['C:\\Program Files\\Audacity\\Audacity.exe', 'C:\\Program Files (x86)\\Audacity\\Audacity.exe'];
  return c.find((f) => { try { return fs.existsSync(f); } catch { return false; } }) || '';
}

function openInAudioEditor(file) {
  const editor = settings.audioEditor && fs.existsSync(settings.audioEditor) ? settings.audioEditor : detectAudacity();
  if (editor) {
    spawn(editor, [file], { detached: true, stdio: 'ignore' }).unref();
    return Promise.resolve('');
  }
  return shell.openPath(file);
}

function openInEditor(file) {
  const editor = settings.imageEditor && fs.existsSync(settings.imageEditor) ? settings.imageEditor : detectPaintNet();
  if (editor) {
    spawn(editor, [file], { detached: true, stdio: 'ignore' }).unref();
    return Promise.resolve('');
  }
  return shell.openPath(file);
}

function detectBlender() {
  const base = 'C:\\Program Files\\Blender Foundation';
  try {
    const dirs = fs.readdirSync(base)
      .filter((d) => fs.existsSync(path.join(base, d, 'blender.exe')))
      .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
    if (dirs.length) return path.join(base, dirs[0], 'blender.exe');
  } catch { /* nicht installiert */ }
  return '';
}

// ------------------------------------------------------------------ Backend

let backend = null;
let backendBuf = '';
let nextId = 1;
const pending = new Map();

function startBackend() {
  backend = spawn(BACKEND_EXE, [], {
    env: { ...process.env, GTA_TOOL_TEXCONV: TEXCONV },
    windowsHide: true,
  });
  backend.stdout.setEncoding('utf8');
  backend.stdout.on('data', (chunk) => {
    backendBuf += chunk;
    let i;
    while ((i = backendBuf.indexOf('\n')) >= 0) {
      const line = backendBuf.slice(0, i).trim();
      backendBuf = backendBuf.slice(i + 1);
      if (!line) continue;
      let msg;
      try { msg = JSON.parse(line); } catch { continue; }
      if (msg.event) {
        if (msg.event === 'progress') send('progress', msg.data);
        continue;
      }
      const p = pending.get(msg.id);
      if (!p) continue;
      pending.delete(msg.id);
      if (msg.error) p.reject(new Error(msg.error)); else p.resolve(msg.result);
    }
  });
  backend.stderr.on('data', (d) => logLine('[backend] ' + d.toString()));
  backend.on('exit', (code) => {
    logLine(`[backend] beendet (${code})`);
    for (const p of pending.values()) p.reject(new Error('Das Backend wurde unerwartet beendet. Bitte die Aktion wiederholen.'));
    pending.clear();
    backend = null;
    if (!app.isQuitting) setTimeout(startBackend, 500);
  });
}

function call(method, params = {}) {
  return new Promise((resolve, reject) => {
    if (!backend) return reject(new Error('Das Backend läuft nicht.'));
    const id = nextId++;
    pending.set(id, { resolve, reject });
    backend.stdin.write(JSON.stringify({ id, method, params }) + '\n');
  });
}

const logFile = () => path.join(app.getPath('userData'), 'gta-tool.log');
function logLine(text) {
  try { fs.appendFileSync(logFile(), `${new Date().toISOString()} ${text.trim()}\n`); } catch { /* egal */ }
}

function send(channel, data) {
  if (win && !win.isDestroyed()) win.webContents.send(channel, data);
}

async function loadKeys() {
  try {
    const st = await call('keys.load', { gtaPath: settings.gtaPath || '', aesKey: settings.keyFor === settings.gtaPath ? settings.aesKey : null });
    if (st.loaded && st.aesKey) { settings.aesKey = st.aesKey; settings.keyFor = settings.gtaPath; saveSettings(); }
    send('keys', st);
    return st;
  } catch (e) {
    const st = { loaded: false, error: e.message };
    send('keys', st);
    return st;
  }
}

// ------------------------------------------------------------------ Sitzung (kein Projekt!)

// Jede Oberfläche (Fahrzeuge, Kleidung) hat ihre eigene Sitzung; „sessionDir“ zeigt auf die aktive.
const sessions = { vehicles: null, clothing: null, mlo: null, anim: null, prop: null };
let activeStudio = 'vehicles';
Object.defineProperty(globalThis, 'sessionDir', {
  get: () => sessions[activeStudio],
  set: (v) => { sessions[activeStudio] = v; },
  configurable: true,
});
function newSessionDir() {
  cleanupSession();
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  sessionDir = path.join(TEMP_ROOT, `sitzung-${stamp}`);
  fs.mkdirSync(sessionDir, { recursive: true });
  return sessionDir;
}
function cleanupAllSessions() {
  stopAllWatchers();
  for (const k of Object.keys(sessions)) {
    if (sessions[k]) { try { fs.rmSync(sessions[k], { recursive: true, force: true }); } catch { /* gesperrt */ } }
    sessions[k] = null;
  }
}
function cleanupSession() {
  if (sessionDir) { try { fs.rmSync(sessionDir, { recursive: true, force: true }); } catch { /* gesperrt */ } }
  sessionDir = null;
}
function cleanupOldSessions() {
  try {
    for (const d of fs.readdirSync(TEMP_ROOT)) {
      const full = path.join(TEMP_ROOT, d);
      const age = Date.now() - fs.statSync(full).mtimeMs;
      if (age > 24 * 3600 * 1000) fs.rmSync(full, { recursive: true, force: true });
    }
  } catch { /* gibt es noch nicht */ }
}
const backupDir = () => path.join(sessionDir, 'backup');
const abs = (rel) => (path.isAbsolute(rel) ? rel : path.join(sessionDir, rel));

// ------------------------------------------------------------------ Dateiüberwachung (Textur bearbeiten)

const watchers = new Map(); // key -> {stop}
function stopAllWatchers() {
  for (const w of watchers.values()) w.stop();
  watchers.clear();
}

function watchFile(key, file, onChange) {
  if (watchers.has(key)) watchers.get(key).stop();
  let last = fs.existsSync(file) ? fs.statSync(file).mtimeMs : 0;
  let timer = null;
  const listener = (cur) => {
    if (cur.mtimeMs > last) {
      last = cur.mtimeMs;
      clearTimeout(timer);
      timer = setTimeout(onChange, 1200); // Bildprogramme schreiben oft in mehreren Schritten
    }
  };
  fs.watchFile(file, { interval: 1000 }, listener);
  watchers.set(key, { stop: () => { fs.unwatchFile(file, listener); clearTimeout(timer); } });
}

// ------------------------------------------------------------------ Blender

const blenderSessions = new Map(); // model -> {proc}

/** Blender + Sollumz vorhanden? Sonst verständliche Meldung. */
function requireBlender() {
  if (!settings.blenderPath || !fs.existsSync(settings.blenderPath)) {
    throw new Error('Blender wurde nicht gefunden. Bitte Blender 4.2 oder neuer installieren (blender.org) oder in den Einstellungen den Pfad zu blender.exe angeben.');
  }
  const sz = detect.findSollumz(settings.blenderPath, null);
  if (!sz) throw new Error('Das Blender-Add-on Sollumz ist nicht installiert. Bitte Sollumz in Blender installieren (Bearbeiten → Einstellungen → Add-ons) – Download: github.com/Sollumz/Sollumz/releases');
}

async function openInBlender({ model, ytds }) {
  requireBlender();
  if (blenderSessions.has(model)) throw new Error(`${model} ist bereits in Blender geöffnet.`);
  const sd = sessionDir;
  const prep = await call('blender.prepare', { sessionDir: sd, model, ytds });
  const statusFile = path.join(prep.workDir, 'gta_tool_status.json');
  try { fs.rmSync(statusFile, { force: true }); } catch { /* egal */ }

  const proc = spawn(settings.blenderPath, ['--python', BRIDGE, '--', '--yft', prep.yft, '--export', prep.exportDir, '--status', statusFile], {
    detached: false,
    stdio: 'ignore',
  });
  const entry = { proc, lastExport: 0 };
  blenderSessions.set(model, entry);
  send('blender', { model, state: 'open' });

  let lastErr = null;
  const poll = setInterval(async () => {
    let st;
    try { st = JSON.parse(fs.readFileSync(statusFile, 'utf8')); } catch { return; }
    if (st.event === 'error' && st.error && st.error !== lastErr) {
      lastErr = st.error;
      send('blender', { model, state: 'error', error: st.error });
    }
    if (st.last && st.last > entry.lastExport) {
      entry.lastExport = st.last;
      try {
        const res = await call('blender.apply', { sessionDir: sd, exportDir: prep.exportDir, model, backupDir: path.join(sd, 'backup') });
        send('blender', { model, state: 'applied', files: res.applied });
      } catch (e) {
        send('blender', { model, state: 'error', error: e.message });
      }
    }
  }, 1000);

  proc.on('exit', () => {
    clearInterval(poll);
    blenderSessions.delete(model);
    send('blender', { model, state: 'closed' });
  });
  proc.on('error', (e) => {
    clearInterval(poll);
    blenderSessions.delete(model);
    send('blender', { model, state: 'error', error: `Blender konnte nicht gestartet werden: ${e.message}` });
  });
  return { ok: true, textures: prep.textures };
}

// ------------------------------------------------------------------ IPC

function handle(name, fn) {
  ipcMain.handle(name, async (_e, args) => {
    try { return { ok: true, data: await fn(args || {}) }; }
    catch (e) { logLine(`[${name}] ${e.stack || e.message}`); return { ok: false, error: e.message }; }
  });
}

function registerIpc() {
  handle('app:info', async () => ({ startView, startFiles, version: app.getVersion(), settings, packed: isPacked }));

  handle('settings:get', async () => settings);
  handle('settings:set', async (s) => {
    const gtaChanged = s.gtaPath !== undefined && s.gtaPath !== settings.gtaPath;
    settings = { ...settings, ...s };
    saveSettings();
    if (gtaChanged) await loadKeys();
    if (s.gtaPath !== undefined || s.blenderPath !== undefined) runSetupCheck({ force: false });
    return settings;
  });
  handle('keys:status', async () => call('keys.status'));

  handle('dialog:open', async ({ kind }) => {
    const opts = {
      folder: { title: 'Ordner importieren', properties: ['openDirectory', 'multiSelections'] },
      files: { title: 'Dateien öffnen', properties: ['openFile', 'multiSelections'], filters: [{ name: 'GTA-Dateien', extensions: ['yft', 'ytd', 'meta', 'xml', 'rpf', 'gxt2'] }, { name: 'Alle Dateien', extensions: ['*'] }] },
      propfiles: { title: 'Props öffnen', properties: ['openFile', 'multiSelections'], filters: [{ name: 'GTA-Props', extensions: ['ydr', 'yft', 'ytd', 'ytyp', 'ybn', 'rpf'] }, { name: 'Alle Dateien', extensions: ['*'] }] },
      animfiles: { title: 'Animationen öffnen', properties: ['openFile', 'multiSelections'], filters: [{ name: 'GTA-Animationen', extensions: ['ycd', 'xml', 'rpf'] }, { name: 'Alle Dateien', extensions: ['*'] }] },
      images: { title: 'Bild wählen', properties: ['openFile', 'multiSelections'], filters: [{ name: 'Bilder', extensions: ['png', 'jpg', 'jpeg', 'tga', 'bmp', 'dds', 'tif', 'tiff'] }] },
      image: { title: 'Bild wählen', properties: ['openFile'], filters: [{ name: 'Bilder', extensions: ['png', 'jpg', 'jpeg', 'tga', 'bmp', 'dds', 'tif', 'tiff'] }] },
      yft: { title: 'Modell wählen', properties: ['openFile'], filters: [{ name: 'GTA-Modell', extensions: ['yft'] }] },
      ytd: { title: 'Texturdatei wählen', properties: ['openFile'], filters: [{ name: 'GTA-Texturen', extensions: ['ytd'] }] },
      outdir: { title: 'Zielordner wählen', properties: ['openDirectory', 'createDirectory'] },
      ydr: { title: 'Modell wählen (.ydr)', properties: ['openFile'], filters: [{ name: 'GTA-Modell', extensions: ['ydr'] }] },
      wav: { title: 'WAV-Datei wählen', properties: ['openFile'], filters: [{ name: 'WAV-Audio', extensions: ['wav'] }] },
      exe: { title: 'Programm wählen', properties: ['openFile'], filters: [{ name: 'Programm', extensions: ['exe'] }] },
      gta: { title: 'GTA-V-Ordner wählen (mit GTA5.exe)', properties: ['openDirectory'] },
    }[kind];
    const importKinds = ['folder', 'files', 'animfiles', 'propfiles', 'images', 'image', 'yft', 'ytd', 'wav', 'ydr'];
    const isImport = importKinds.includes(kind);
    const start = isImport ? settings.lastImportDir : kind === 'outdir' ? (settings.lastResourceDir || settings.lastExportDir) : null;
    if (start && fs.existsSync(start)) opts.defaultPath = start;
    const r = await dialog.showOpenDialog(win, opts);
    if (r.canceled || !r.filePaths.length) return [];
    if (isImport) rememberImport(r.filePaths);
    if (kind === 'outdir') { settings.lastResourceDir = r.filePaths[0]; settings.lastExportDir = r.filePaths[0]; saveSettings(); }
    return r.filePaths;
  });

  handle('studio:set', async ({ studio }) => { if (sessions[studio] !== undefined) activeStudio = studio; return activeStudio; });

  // ------------------------------------------------ MLO
  const propsCache = () => path.join(app.getPath('userData'), 'gta-objekte.json');
  const propsDir = () => path.join(app.getPath('userData'), 'gta-objekte');
  handle('mlo:open', async ({ paths, add }) => {
    rememberImport(paths);
    if (!add || !sessionDir) newSessionDir();
    await call('session.open', { paths, sessionDir });
    return call('mlo.analyze', { sessionDir });
  });
  handle('mlo:analyze', async () => call('mlo.analyze', { sessionDir }));
  handle('mlo:scene', async ({ key }) => call('mlo.scene', { sessionDir, key, propsCache: propsCache() }));
  handle('mlo:archGlb', async ({ name, version }) => {
    const out = path.join(sessionDir, 'vorschau', 'mlo', `${name.replace(/[^\w.-]/g, '_')}-${version || 0}.glb`);
    if (!fs.existsSync(out)) {
      await call('mlo.archGlb', { sessionDir, name, gtaPath: settings.gtaPath || '', propsCache: propsCache(), propsDir: propsDir(), outFile: out });
    }
    return { glb: fs.readFileSync(out) };
  });
  handle('mlo:collision', async ({ ybn, pos, rot, clipMin, clipMax }) => {
    const out = path.join(sessionDir, 'vorschau', 'kollision', `${path.basename(ybn)}-${Date.now()}.glb`);
    const info = await call('mlo.collisionGlb', { ybn: abs(ybn), outFile: out, mloPos: pos || null, mloRot: rot || null, clipMin: clipMin || null, clipMax: clipMax || null });
    const glb = fs.readFileSync(out);
    fs.rm(out, { force: true }, () => {});
    return { ...info, glb };
  });
  handle('mlo:setEntity', async (a) => call('mlo.setEntity', { sessionDir, key: a.key, id: a.id, pos: a.pos || null, rot: a.rot || null, scale: a.scale || null, backupDir: backupDir() }));
  handle('mlo:addEntity', async (a) => call('mlo.addEntity', { sessionDir, key: a.key, name: a.name || '', pos: a.pos || null, rot: a.rot || null, room: a.room ?? -1, copyFrom: a.copyFrom || '', backupDir: backupDir() }));
  handle('mlo:deleteEntity', async (a) => call('mlo.deleteEntity', { sessionDir, key: a.key, id: a.id, backupDir: backupDir() }));
  handle('mlo:swapArchetype', async (a) => call('mlo.swapArchetype', { sessionDir, key: a.key, id: a.id || '', from: a.from || '', to: a.to, backupDir: backupDir() }));
  handle('mlo:setPlacement', async (a) => call('mlo.setPlacement', { sessionDir, ymap: a.ymap, index: a.index || 0, pos: a.pos, rot: a.rot, backupDir: backupDir() }));
  handle('mlo:importModel', async ({ key }) => {
    const r = await dialog.showOpenDialog(win, { title: 'Eigenes Modell wählen (.ydr)', properties: ['openFile'], defaultPath: settings.lastImportDir, filters: [{ name: 'GTA-Modell', extensions: ['ydr'] }] });
    if (r.canceled) return null;
    rememberImport(r.filePaths);
    const ydr = r.filePaths[0];
    const sameYtd = path.join(path.dirname(ydr), path.basename(ydr, '.ydr') + '.ytd');
    let ytd = fs.existsSync(sameYtd) ? sameYtd : null;
    if (!ytd) {
      const t = await dialog.showOpenDialog(win, { title: 'Passende Texturdatei (.ytd) wählen – optional, „Abbrechen“ wenn eingebettet', properties: ['openFile'], defaultPath: path.dirname(ydr), filters: [{ name: 'GTA-Texturen', extensions: ['ytd'] }] });
      if (!t.canceled) ytd = t.filePaths[0];
    }
    return call('mlo.importModel', { sessionDir, key, ydr, ytd: ytd || '', backupDir: backupDir() });
  });
  // GTA-Welt für die 3D-Platzierung
  const worldCache = () => path.join(app.getPath('userData'), 'gta-welt.json');
  handle('world:status', async () => call('world.status', { cacheFile: worldCache() }));
  handle('world:build', async () => call('world.build', { gtaPath: settings.gtaPath || '', cacheFile: worldCache() }));
  handle('world:area', async ({ x, y, radius }) => call('world.area', { gtaPath: settings.gtaPath || '', cacheFile: worldCache(), propsCache: propsCache(), x, y, radius: radius || 120, max: 1500 }));
  handle('world:archGlb', async ({ name }) => {
    const out = path.join(app.getPath('userData'), 'gta-objekte-glb', `${String(name).replace(/[^\w.-]/g, '_')}.glb`);
    if (!fs.existsSync(out)) await call('world.archGlb', { gtaPath: settings.gtaPath || '', propsCache: propsCache(), propsDir: propsDir(), name, outFile: out });
    return { glb: fs.readFileSync(out) };
  });

  // Neues Interior: Karte, Bodenhöhe, Blender
  handle('map:glb', async () => {
    const out = path.join(app.getPath('userData'), 'karte2.glb');
    await call('map.build', { gtaPath: settings.gtaPath || '', outFile: out });
    return { glb: fs.readFileSync(out) };
  });
  handle('map:height', async ({ x, y }) => call('map.height', { gtaPath: settings.gtaPath || '', x, y, flipY: 'false' }));
  const blendSource = (n) => path.join(app.getPath('userData'), 'blender-quellen', `${n}.blend`);
  handle('mlo:blendSource', async ({ name }) => ({ exists: fs.existsSync(blendSource(String(name || '').toLowerCase())) }));
  handle('mlo:newInBlender', async ({ name, pos, heading, size, reopen }) => {
    requireBlender();
    const n = String(name || '').trim().toLowerCase();
    if (!/^[a-z0-9_-]{1,50}$/.test(n)) throw new Error('Bitte einen gültigen Namen eingeben (a–z, 0–9, _ und -).');
    const key = `newmlo:${n}`;
    if (blenderSessions.has(key)) throw new Error('Dieses Interior ist bereits in Blender geöffnet.');
    if (!sessionDir) newSessionDir();
    const sd = sessionDir;
    const work = path.join(sd, 'blender', `neu_mlo_${n}`);
    const exportDir = path.join(work, 'out');
    const statusFile = path.join(work, 'status.json');
    fs.mkdirSync(work, { recursive: true });
    try { fs.rmSync(statusFile, { force: true }); } catch { /* egal */ }
    let doors = [];
    try { doors = await call('mlo.doorList', { propsCache: propsCache() }); } catch { doors = []; }
    const doorsFile = path.join(work, 'tueren.json');
    fs.writeFileSync(doorsFile, JSON.stringify(doors));
    const blend = blendSource(n);
    fs.mkdirSync(path.dirname(blend), { recursive: true });
    const p = (pos || [0, 0, 0]).map(Number);
    const s3 = (size || [10, 8, 3.5]).map(Number);
    const args = [];
    if (reopen && fs.existsSync(blend)) args.push(blend);
    args.push('--python', BRIDGE, '--', '--new-mlo', n, '--pos', p.join(','), '--heading', String(Number(heading) || 0), '--size', s3.join(','),
      '--doors', doorsFile, '--blend-save', blend, '--export', exportDir, '--status', statusFile);
    const proc = spawn(settings.blenderPath, args, { detached: false, stdio: 'ignore' });
    const entry = { proc, lastExport: 0 };
    blenderSessions.set(key, entry);
    let lastErr = null;
    const poll = setInterval(async () => {
      let st;
      try { st = JSON.parse(fs.readFileSync(statusFile, 'utf8')); } catch { return; }
      if (st.event === 'error' && st.error && st.error !== lastErr) { lastErr = st.error; send('blender', { model: key, state: 'error', error: st.error, studio: 'mlo' }); }
      if (st.last && st.last > entry.lastExport) {
        entry.lastExport = st.last;
        try {
          const res = await call('mlo.applyNew', { sessionDir: sd, exportDir, name: n, backupDir: path.join(sd, 'backup') });
          send('blender', { model: key, state: 'applied', name: n, warnings: res.warnings || [], files: [], studio: 'mlo' });
        } catch (e) { send('blender', { model: key, state: 'error', error: e.message, studio: 'mlo' }); }
      }
    }, 1000);
    proc.on('exit', () => { clearInterval(poll); blenderSessions.delete(key); send('blender', { model: key, state: 'closed', studio: 'mlo' }); });
    proc.on('error', (e) => { clearInterval(poll); blenderSessions.delete(key); send('blender', { model: key, state: 'error', error: e.message, studio: 'mlo' }); });
    return { ok: true, key, reopened: !!(reopen && fs.existsSync(blend)) };
  });
  handle('mlo:export', async ({ outDir, resourceName }) => {
    rememberExportDir(outDir);
    return call('mlo.export', { sessionDir, outDir, resourceName });
  });
  handle('props:status', async () => call('props.status', { cacheFile: propsCache() }));
  handle('props:build', async () => call('props.build', { gtaPath: settings.gtaPath || '', cacheFile: propsCache() }));
  handle('props:search', async ({ term }) => call('props.search', { cacheFile: propsCache(), term, max: 300 }));
  handle('mlo:blender', async ({ file, ytds, studio: st }) => {
    const studio = st || 'mlo';
    requireBlender();
    if (blenderSessions.has(file)) throw new Error('Diese Datei ist bereits in Blender geöffnet.');
    const sd = sessionDir;
    const work = path.join(sd, 'blender', path.basename(file).replace(/[^\w.-]/g, '_'));
    const prep = await call('blender.prepareFile', { sessionDir: sd, file, workDir: path.join(work, 'in'), ytds: ytds || [] });
    const exportDir = path.join(work, 'out');
    const statusFile = path.join(work, 'status.json');
    const proc = spawn(settings.blenderPath, ['--python', BRIDGE, '--', '--file', prep.file, '--export', exportDir, '--status', statusFile], { detached: false, stdio: 'ignore' });
    const entry = { proc, lastExport: 0 };
    blenderSessions.set(file, entry);
    let lastErr = null;
    const poll = setInterval(async () => {
      let st;
      try { st = JSON.parse(fs.readFileSync(statusFile, 'utf8')); } catch { return; }
      if (st.event === 'error' && st.error && st.error !== lastErr) { lastErr = st.error; send('blender', { model: file, state: 'error', error: st.error, studio }); }
      if (st.last && st.last > entry.lastExport) {
        entry.lastExport = st.last;
        try {
          const res = await call('blender.applyFile', { sessionDir: sd, file, exportDir, backupDir: path.join(sd, 'backup') });
          send('blender', { model: file, state: 'applied', files: [res.applied], studio });
        } catch (e) { send('blender', { model: file, state: 'error', error: e.message, studio }); }
      }
    }, 1000);
    proc.on('exit', () => { clearInterval(poll); blenderSessions.delete(file); send('blender', { model: file, state: 'closed', studio }); });
    proc.on('error', (e) => { clearInterval(poll); blenderSessions.delete(file); send('blender', { model: file, state: 'error', error: e.message, studio }); });
    return { ok: true };
  });

  // ------------------------------------------------ Kleidung
  const freemodeDir = () => path.join(app.getPath('userData'), 'freemode');
  const catalogFile = () => path.join(freemodeDir(), 'gta-kleidung.json');
  handle('clothing:open', async ({ paths, add }) => {
    rememberImport(paths);
    if (!add || !sessionDir) newSessionDir();
    await call('session.open', { paths, sessionDir });
    return call('clothing.analyze', { sessionDir });
  });
  handle('clothing:new', async () => { if (!sessionDir) newSessionDir(); return call('clothing.analyze', { sessionDir }); });
  handle('clothing:analyze', async () => call('clothing.analyze', { sessionDir }));
  handle('clothing:base', async () => call('freemode.base', { gtaPath: settings.gtaPath || '', cacheDir: path.join(freemodeDir(), 'koerper') }));
  handle('clothing:toAddon', async ({ ids, collection }) => call('clothing.toAddon', { sessionDir, ids, collection, backupDir: backupDir() }));
  handle('clothing:toReplace', async ({ id, index }) => call('clothing.toReplace', { sessionDir, id, index, backupDir: backupDir() }));
  handle('clothing:setGender', async ({ id, gender }) => call('clothing.setGender', { sessionDir, id, gender, backupDir: backupDir() }));
  handle('clothing:delete', async ({ id }) => call('clothing.delete', { sessionDir, id, backupDir: backupDir() }));
  handle('clothing:addVariant', async ({ id, images }) => call('clothing.addVariant', { sessionDir, id, images, backupDir: backupDir() }));
  handle('clothing:removeVariant', async ({ id, file }) => call('clothing.removeVariant', { sessionDir, id, file, backupDir: backupDir() }));
  handle('clothing:transform', async (a) => call('clothing.transform', { sessionDir, id: a.id, dx: a.dx, dy: a.dy, dz: a.dz, scale: a.scale, backupDir: backupDir() }));
  handle('clothing:uv', async ({ file, ytd, mode, size }) => {
    let outFile;
    const stem = path.basename(file, '.ydd').replace(/^.*\^/, '');
    if (mode === 'save') {
      const r = await dialog.showSaveDialog(win, {
        title: 'UV-Vorlage speichern',
        defaultPath: path.join(settings.lastExportDir || app.getPath('desktop'), `${stem}_uv.png`),
        filters: [{ name: 'PNG', extensions: ['png'] }],
      });
      if (r.canceled) return null;
      outFile = r.filePath;
      settings.lastExportDir = path.dirname(outFile); saveSettings();
    } else {
      outFile = path.join(sessionDir, 'bearbeiten', 'uv', `${stem}_uv.png`);
    }
    const res = await call('clothing.uv', { ydd: abs(file), size: size || 2048, outFile, bgYtd: ytd ? abs(ytd) : null });
    if (mode === 'edit') await openInEditor(outFile);
    return res;
  });
  handle('clothing:catalogStatus', async () => call('freemode.catalogStatus', { cacheFile: catalogFile() }));
  handle('clothing:catalogBuild', async () => call('freemode.catalogBuild', { gtaPath: settings.gtaPath || '', cacheFile: catalogFile() }));
  handle('clothing:browse', async ({ ped, comp, coll }) => call('freemode.browse', { cacheFile: catalogFile(), ped, comp, coll: coll === undefined ? null : coll }));
  handle('clothing:gtaPreview', async ({ id }) => call('freemode.preview', {
    gtaPath: settings.gtaPath || '', cacheFile: catalogFile(), id, outDir: path.join(TEMP_ROOT, 'gta-vorschau', id.replace(/[^\w.-]/g, '_')),
  }));
  handle('clothing:addFromGta', async ({ catalogId, collection, keepTextures }) => {
    if (!sessionDir) newSessionDir();
    return call('clothing.addFromGta', { sessionDir, gtaPath: settings.gtaPath || '', cacheFile: catalogFile(), catalogId, collection, keepTextures: keepTextures ? 'true' : 'false' });
  });
  handle('clothing:export', async ({ outDir, resourceName }) => {
    rememberExportDir(outDir);
    return call('clothing.export', { sessionDir, outDir, resourceName });
  });
  handle('clothing:gender', async ({ id }) => {
    requireBlender();
    const base = await call('freemode.base', { gtaPath: settings.gtaPath || '', cacheDir: path.join(freemodeDir(), 'koerper') });
    const work = path.join(sessionDir, 'umwandeln', String(Date.now()));
    const prep = await call('clothing.prepareWork', { sessionDir, id, workDir: path.join(work, 'in') });
    const src = prep.gender === 'f' ? 'mp_f_freemode_01' : 'mp_m_freemode_01';
    const dst = prep.gender === 'f' ? 'mp_m_freemode_01' : 'mp_f_freemode_01';
    const resultFile = path.join(work, 'ergebnis.json');
    send('progress', { text: 'Blender passt das Kleidungsstück an den anderen Körper an …', percent: 40 });
    await new Promise((resolve, reject) => {
      const p = spawn(settings.blenderPath, ['-b', '--python', path.join(ROOT, 'blender', 'clothing_convert.py'), '--',
        '--model', prep.file, '--stem', prep.stem,
        '--source-skel', base[src].skeleton, '--target-skel', base[dst].skeleton,
        '--source-base', base[src].dir, '--target-base', base[dst].dir,
        '--out', path.join(work, 'out'), '--result', resultFile], { stdio: 'ignore', windowsHide: true });
      p.on('exit', resolve);
      p.on('error', reject);
    });
    let r;
    try { r = JSON.parse(fs.readFileSync(resultFile, 'utf8')); } catch { throw new Error('Blender hat kein Ergebnis geliefert.'); }
    if (!r.ok) throw new Error(r.error || 'Die Umwandlung ist fehlgeschlagen.');
    const applied = await call('clothing.genderApply', { sessionDir, id, file: r.file });
    return { ...applied, report: r };
  });
  handle('clothing:blender', async ({ id, ytd }) => {
    requireBlender();
    if (blenderSessions.has(id)) throw new Error('Dieses Kleidungsstück ist bereits in Blender geöffnet.');
    const sd = sessionDir;
    const base = await call('freemode.base', { gtaPath: settings.gtaPath || '', cacheDir: path.join(freemodeDir(), 'koerper') });
    const work = path.join(sd, 'blender', id.replace(/[^\w.-]/g, '_'));
    const prep = await call('clothing.prepareWork', { sessionDir: sd, id, workDir: path.join(work, 'in'), ytd });
    const ped = prep.gender === 'f' ? 'mp_f_freemode_01' : 'mp_m_freemode_01';
    const body = base[ped].parts.filter((x) => x.comp !== prep.comp).map((x) => x.model).join(';');
    const exportDir = path.join(work, 'out');
    const statusFile = path.join(work, 'status.json');
    const proc = spawn(settings.blenderPath, ['--python', BRIDGE, '--', '--ydd', prep.file, '--skeleton', base[ped].skeleton,
      '--body', body, '--export', exportDir, '--status', statusFile], { detached: false, stdio: 'ignore' });
    const entry = { proc, lastExport: 0 };
    blenderSessions.set(id, entry);
    let lastErr = null;
    const poll = setInterval(async () => {
      let st;
      try { st = JSON.parse(fs.readFileSync(statusFile, 'utf8')); } catch { return; }
      if (st.event === 'error' && st.error && st.error !== lastErr) { lastErr = st.error; send('blender', { model: id, state: 'error', error: st.error }); }
      if (st.last && st.last > entry.lastExport) {
        entry.lastExport = st.last;
        try {
          const res = await call('clothing.applyWork', { sessionDir: sd, id, exportDir, backupDir: path.join(sd, 'backup') });
          send('blender', { model: id, state: 'applied', files: [res.applied], studio: 'clothing' });
        } catch (e) { send('blender', { model: id, state: 'error', error: e.message }); }
      }
    }, 1000);
    proc.on('exit', () => { clearInterval(poll); blenderSessions.delete(id); send('blender', { model: id, state: 'closed' }); });
    proc.on('error', (e) => { clearInterval(poll); blenderSessions.delete(id); send('blender', { model: id, state: 'error', error: e.message }); });
    return { ok: true };
  });

  // ------------------------------------------------ Props
  handle('prop:open', async ({ paths, add }) => {
    rememberImport(paths);
    if (!add || !sessionDir) newSessionDir();
    const r = await call('session.open', { paths, sessionDir });
    const model = await call('prop.analyze', { sessionDir });
    return { ...model, warnings: [...(r.warnings || []).filter((w) => !/keine verwendbaren/.test(w)), ...model.warnings] };
  });
  handle('prop:new', async () => { if (!sessionDir) newSessionDir(); return call('prop.analyze', { sessionDir }); });
  handle('prop:analyze', async () => call('prop.analyze', { sessionDir }));
  handle('prop:details', async ({ file }) => call('prop.details', { sessionDir, file }));
  handle('prop:ensureArchetypes', async ({ ytypName }) => call('prop.ensureArchetypes', { sessionDir, ytypName: ytypName || '', backupDir: backupDir() }));
  handle('prop:setLod', async ({ name, lodDist }) => call('prop.setLod', { sessionDir, name, lodDist, backupDir: backupDir() }));
  handle('prop:rename', async ({ name, newName }) => call('prop.rename', { sessionDir, name, newName, backupDir: backupDir() }));
  handle('prop:delete', async ({ name }) => call('prop.delete', { sessionDir, name, backupDir: backupDir() }));
  handle('prop:addFromGta', async ({ gtaName, newName }) => {
    if (!sessionDir) newSessionDir();
    return call('prop.addFromGta', { sessionDir, gtaPath: settings.gtaPath || '', propsCache: propsCache(), propsDir: propsDir(), gtaName, newName, ytypName: '', backupDir: backupDir() });
  });
  handle('prop:gtaPreview', async ({ name }) => {
    const out = path.join(TEMP_ROOT, 'gta-vorschau', 'props', `${String(name).replace(/[^\w.-]/g, '_')}.glb`);
    if (!sessionDir) newSessionDir();
    fs.mkdirSync(path.join(sessionDir, 'stream'), { recursive: true });
    if (!fs.existsSync(out)) await call('mlo.archGlb', { sessionDir, name, gtaPath: settings.gtaPath || '', propsCache: propsCache(), propsDir: propsDir(), outFile: out });
    return { glb: fs.readFileSync(out) };
  });
  handle('prop:newInBlender', async ({ name }) => {
    requireBlender();
    const n = String(name || '').trim().toLowerCase();
    if (!/^[a-z0-9_-]{1,60}$/.test(n)) throw new Error('Bitte einen gültigen Namen eingeben (a–z, 0–9, _ und -).');
    const key = `new:${n}`;
    if (blenderSessions.has(key)) throw new Error('Dieses Prop ist bereits in Blender geöffnet.');
    if (!sessionDir) newSessionDir();
    const sd = sessionDir;
    const work = path.join(sd, 'blender', `neu_${n}`);
    const exportDir = path.join(work, 'out');
    const statusFile = path.join(work, 'status.json');
    fs.mkdirSync(work, { recursive: true });
    try { fs.rmSync(statusFile, { force: true }); } catch { /* egal */ }
    const proc = spawn(settings.blenderPath, ['--python', BRIDGE, '--', '--new-prop', n, '--export', exportDir, '--status', statusFile], { detached: false, stdio: 'ignore' });
    const entry = { proc, lastExport: 0 };
    blenderSessions.set(key, entry);
    let lastErr = null;
    const poll = setInterval(async () => {
      let st;
      try { st = JSON.parse(fs.readFileSync(statusFile, 'utf8')); } catch { return; }
      if (st.event === 'error' && st.error && st.error !== lastErr) { lastErr = st.error; send('blender', { model: key, state: 'error', error: st.error, studio: 'prop' }); }
      if (st.last && st.last > entry.lastExport) {
        entry.lastExport = st.last;
        try {
          const res = await call('prop.applyNew', { sessionDir: sd, exportDir, name: n, backupDir: path.join(sd, 'backup') });
          send('blender', { model: key, state: 'applied', files: [res.file], name: n, studio: 'prop' });
        } catch (e) { send('blender', { model: key, state: 'error', error: e.message, studio: 'prop' }); }
      }
    }, 1000);
    proc.on('exit', () => { clearInterval(poll); blenderSessions.delete(key); send('blender', { model: key, state: 'closed', studio: 'prop' }); });
    proc.on('error', (e) => { clearInterval(poll); blenderSessions.delete(key); send('blender', { model: key, state: 'error', error: e.message, studio: 'prop' }); });
    return { ok: true, key };
  });
  handle('prop:export', async ({ outDir, resourceName, testCommand }) => {
    rememberExportDir(outDir);
    return call('prop.export', { sessionDir, outDir, resourceName, testCommand: testCommand ? 'true' : 'false', backupDir: backupDir() });
  });

  // ------------------------------------------------ Animationen
  handle('anim:open', async ({ paths, add }) => {
    rememberImport(paths);
    if (!add || !sessionDir) newSessionDir();
    const r = await call('session.open', { paths, sessionDir });
    const model = await call('anim.analyze', { sessionDir });
    return { ...model, warnings: [...(r.warnings || []).filter((w) => !/keine verwendbaren/.test(w)), ...model.warnings] };
  });
  handle('anim:analyze', async () => call('anim.analyze', { sessionDir }));
  handle('anim:sample', async ({ file, clip }) => call('anim.sample', { file: abs(file), clip, fps: 30 }));
  handle('anim:rig', async ({ ped }) => {
    const p = ped === 'f' ? 'mp_f_freemode_01' : 'mp_m_freemode_01';
    const out = path.join(freemodeDir(), `rig2-${p}.glb`);
    if (!fs.existsSync(out)) {
      const base = await call('freemode.base', { gtaPath: settings.gtaPath || '', cacheDir: path.join(freemodeDir(), 'koerper') });
      const b = base[p];
      const parts = b.parts.filter((x) => x.comp !== 'hair' && x.comp !== 'hand');
      await call('anim.rig', { skeleton: b.skeleton, models: parts.map((x) => x.model), ytds: parts.map((x) => x.ytd || ''), outFile: out });
    }
    return { glb: fs.readFileSync(out) };
  });
  const animOp = (name) => async (a) => call(`anim.${name}`, { sessionDir, file: a.file, clip: a.clip || '', name: a.name || '', target: a.target || '', rate: a.rate, start: a.start, end: a.end, backupDir: backupDir() });
  for (const op of ['renameClip', 'duplicateClip', 'deleteClip', 'setTiming', 'copyClip', 'newDictFromClip', 'renameDict', 'deleteDict']) handle(`anim:${op}`, animOp(op));
  handle('anim:exportFile', async ({ file, format }) => {
    const name = path.basename(file, '.ycd') + (format === 'xml' ? '.ycd.xml' : '.ycd');
    const r = await dialog.showSaveDialog(win, {
      title: format === 'xml' ? 'Als XML speichern (CodeWalker / Sollumz)' : 'Animations-Datei speichern',
      defaultPath: path.join(settings.lastExportDir || app.getPath('desktop'), name),
      filters: [format === 'xml' ? { name: 'CodeWalker-XML', extensions: ['xml'] } : { name: 'GTA-Animation', extensions: ['ycd'] }],
    });
    if (r.canceled) return null;
    settings.lastExportDir = path.dirname(r.filePath); saveSettings();
    return call('anim.exportFile', { sessionDir, file, outFile: r.filePath, format: format || 'ycd' });
  });
  handle('anim:export', async ({ outDir, resourceName, testCommand }) => {
    rememberExportDir(outDir);
    return call('anim.export', { sessionDir, outDir, resourceName, testCommand: testCommand ? 'true' : 'false' });
  });
  handle('anim:blender', async ({ file, clip, clipName, ped }) => {
    requireBlender();
    const key = `${file}|${clip}`;
    if (blenderSessions.has(key)) throw new Error('Dieser Clip ist bereits in Blender geöffnet.');
    const sd = sessionDir;
    const base = await call('freemode.base', { gtaPath: settings.gtaPath || '', cacheDir: path.join(freemodeDir(), 'koerper') });
    const p = ped === 'f' ? 'mp_f_freemode_01' : 'mp_m_freemode_01';
    const body = base[p].parts.filter((x) => x.comp !== 'hair' && x.comp !== 'hand').map((x) => x.model).join(';');
    const work = path.join(sd, 'blender', `anim_${path.basename(file, '.ycd').replace(/[^\w.-]/g, '_')}_${clip}`);
    const prep = await call('anim.prepareBlender', { sessionDir: sd, file, workDir: path.join(work, 'in') });
    const exportDir = path.join(work, 'out');
    const statusFile = path.join(work, 'status.json');
    try { fs.rmSync(statusFile, { force: true }); } catch { /* egal */ }
    const proc = spawn(settings.blenderPath, ['--python', BRIDGE, '--', '--ycd', prep.xml, '--clip', clipName || '', '--skeleton', base[p].skeleton,
      '--body', body, '--export', exportDir, '--status', statusFile], { detached: false, stdio: 'ignore' });
    const entry = { proc, lastExport: 0 };
    blenderSessions.set(key, entry);
    let lastErr = null;
    const poll = setInterval(async () => {
      let st;
      try { st = JSON.parse(fs.readFileSync(statusFile, 'utf8')); } catch { return; }
      if (st.event === 'error' && st.error && st.error !== lastErr) { lastErr = st.error; send('blender', { model: key, state: 'error', error: st.error, studio: 'anim' }); }
      if (st.last && st.last > entry.lastExport) {
        entry.lastExport = st.last;
        try {
          const res = await call('anim.applyBlender', { sessionDir: sd, file, exportDir, clip, backupDir: path.join(sd, 'backup') });
          send('blender', { model: key, state: 'applied', files: res.applied, studio: 'anim' });
        } catch (e) { send('blender', { model: key, state: 'error', error: e.message, studio: 'anim' }); }
      }
    }, 1000);
    proc.on('exit', () => { clearInterval(poll); blenderSessions.delete(key); send('blender', { model: key, state: 'closed', studio: 'anim' }); });
    proc.on('error', (e) => { clearInterval(poll); blenderSessions.delete(key); send('blender', { model: key, state: 'error', error: e.message, studio: 'anim' }); });
    return { ok: true };
  });

  // Sitzung
  handle('session:open', async ({ paths, add }) => {
    rememberImport(paths);
    if (!add || !sessionDir) newSessionDir();
    return call('session.open', { paths, sessionDir });
  });
  handle('session:analyze', async () => call('session.analyze', { sessionDir }));
  handle('session:close', async () => { cleanupSession(); return true; });

  // Texturen
  handle('tex:list', async ({ file }) => call('textures.list', { file: abs(file) }));
  handle('tex:preview', async ({ file, name }) => call('textures.preview', { file: abs(file), name, maxSize: 1024 }));
  handle('tex:export', async ({ file, names, format }) => {
    const dirs = await dialog.showOpenDialog(win, { title: 'Exportieren nach …', properties: ['openDirectory', 'createDirectory'], defaultPath: settings.lastExportDir });
    if (dirs.canceled) return null;
    settings.lastExportDir = dirs.filePaths[0]; saveSettings();
    const res = await call('textures.export', { file: abs(file), names, outDir: dirs.filePaths[0], format: format || 'png' });
    return { ...res, outDir: dirs.filePaths[0] };
  });
  handle('tex:replace', async ({ file, name, image }) => call('textures.replace', { file: abs(file), name, image, backupDir: backupDir() }));
  handle('tex:add', async ({ file, images }) => call('textures.add', { file: abs(file), images, backupDir: backupDir() }));
  handle('tex:remove', async ({ file, names }) => call('textures.remove', { file: abs(file), names, backupDir: backupDir() }));
  handle('tex:edit', async ({ file, name }) => {
    const editDir = path.join(sessionDir, 'bearbeiten', path.basename(file, path.extname(file)));
    const res = await call('textures.export', { file: abs(file), names: [name], outDir: editDir, format: 'png' });
    const png = res.files[0];
    if (!png) throw new Error('Die Textur konnte nicht zum Bearbeiten vorbereitet werden.');
    const err = await openInEditor(png);
    if (err) throw new Error(`Kein Bildprogramm für PNG gefunden (${err}). Bitte in den Einstellungen Paint.NET auswählen.`);
    const absFile = abs(file), bdir = backupDir();
    watchFile(`${absFile}|${name}`, png, async () => {
      try {
        await call('textures.replace', { file: absFile, name, image: png, backupDir: bdir });
        send('tex:updated', { file, name });
      } catch (e) {
        send('toast', { type: 'error', text: `${name}: ${e.message}` });
      }
    });
    return { png };
  });

  // 3D-Vorschau: YFT → GLB (im Backend), Inhalt geht als Bytes an die Oberfläche
  let previewCounter = 0;
  handle('model:preview', async ({ file, ytds, attach, bone, livery, hide, variant }) => {
    const outFile = path.join(sessionDir || TEMP_ROOT, 'vorschau', `${path.basename(file).replace(/[^\w.-]/g, '_')}-${++previewCounter}.glb`);
    const info = await call('model.glb', { file: abs(file), ytds: (ytds || []).map(abs), outFile, attachYft: attach ? abs(attach) : null, attachBone: bone || null, livery: livery || null, hideBones: hide || [], variantTexture: variant ? 'true' : 'false' });
    const glb = fs.readFileSync(outFile);
    fs.rm(outFile, { force: true }, () => {});
    return { ...info, glb };
  });

  // Replace → Addon / Umbenennen
  const vanillaCache = () => path.join(app.getPath('userData'), 'gta-fahrzeugdaten.json');
  handle('vanilla:status', async () => call('vanilla.status', { cacheFile: vanillaCache(), gtaPath: settings.gtaPath || '' }));
  handle('vanilla:build', async () => call('vanilla.build', { cacheFile: vanillaCache(), gtaPath: settings.gtaPath || '' }));
  handle('vanilla:lookup', async ({ models }) => call('vanilla.lookup', { cacheFile: vanillaCache(), models }));
  handle('vehicle:replaceToAddon', async (a) => call('vehicle.replaceToAddon', {
    sessionDir, cacheFile: vanillaCache(), oldModel: a.oldModel, newModel: a.newModel,
    displayName: a.displayName || '', makeName: a.makeName || '', ownHandling: a.ownHandling ? 'true' : 'false', backupDir: backupDir(),
  }));
  handle('vehicle:rename', async (a) => call('vehicle.rename', {
    sessionDir, oldModel: a.oldModel, newModel: a.newModel, displayName: a.displayName || '', makeName: a.makeName || '', backupDir: backupDir(),
  }));

  // Motorsounds
  handle('audio:info', async () => call('audio.info', { sessionDir }));
  handle('audio:setName', async ({ model, name }) => call('audio.setName', { sessionDir, model, audioName: name, backupDir: backupDir() }));
  handle('audio:wav', async ({ awc, index }) => {
    const r = await call('audio.wav', { awc: abs(awc), index });
    return { name: r.name, wav: Buffer.from(r.wav, 'base64') };
  });
  handle('audio:exportWav', async ({ awc, index, name }) => {
    const r = await dialog.showSaveDialog(win, {
      title: 'Sound als WAV speichern',
      defaultPath: path.join(settings.lastExportDir || app.getPath('desktop'), `${(name || 'sound').replace(/[^\w.-]/g, '_')}.wav`),
      filters: [{ name: 'WAV', extensions: ['wav'] }],
    });
    if (r.canceled) return null;
    settings.lastExportDir = path.dirname(r.filePath); saveSettings();
    return call('audio.wav', { awc: abs(awc), index, outFile: r.filePath });
  });
  handle('audio:exportAll', async ({ awc }) => {
    const d = await dialog.showOpenDialog(win, { title: 'Alle Sounds exportieren nach …', properties: ['openDirectory', 'createDirectory'], defaultPath: settings.lastExportDir });
    if (d.canceled) return null;
    settings.lastExportDir = d.filePaths[0]; saveSettings();
    const out = path.join(d.filePaths[0], path.basename(awc, '.awc'));
    const r = await call('audio.exportAll', { awc: abs(awc), outDir: out });
    shell.openPath(out);
    return r;
  });
  handle('audio:replace', async ({ awc, index, wav }) => call('audio.replace', { awc: abs(awc), index, wav, backupDir: backupDir() }));
  handle('audio:edit', async ({ awc, index, name }) => {
    const dir = path.join(sessionDir, 'bearbeiten', 'audio', path.basename(awc, '.awc'));
    const file = path.join(dir, `${String(name || index).replace(/[^\w.-]/g, '_')}.wav`);
    await call('audio.wav', { awc: abs(awc), index, outFile: file });
    const err = await openInAudioEditor(file);
    if (err) throw new Error(`Kein Programm für WAV gefunden (${err}). Bitte Audacity installieren oder in den Einstellungen wählen.`);
    watchFile(`${awc}|${index}`, file, async () => {
      try {
        const r = await call('audio.replace', { awc: abs(awc), index, wav: file, backupDir: backupDir() });
        send('audio:updated', { awc, index, name, note: r.note });
      } catch (e) {
        send('toast', { type: 'error', text: `${name}: ${e.message}` });
      }
    });
    return { file, editor: settings.audioEditor || '' };
  });
  handle('audio:exportPack', async ({ source }) => {
    const d = await dialog.showOpenDialog(win, { title: 'Sound-Pack exportieren nach …', properties: ['openDirectory', 'createDirectory'], defaultPath: settings.lastExportDir });
    if (d.canceled) return null;
    settings.lastExportDir = d.filePaths[0]; saveSettings();
    const src = path.join(sessionDir, 'audio', source);
    const out = path.join(d.filePaths[0], `${source}_sounds`);
    fs.rmSync(out, { recursive: true, force: true });
    fs.cpSync(src, out, { recursive: true });
    shell.openPath(out);
    return { folder: out };
  });
  handle('audio:relGet', async ({ file }) => call('audio.relGet', { file: abs(file) }));
  handle('audio:relSet', async ({ file, xml }) => call('audio.relSet', { file: abs(file), xml, backupDir: backupDir() }));

  // Folierungen (Liveries)
  const rel = (p) => path.relative(sessionDir, p).split(path.sep).join('/');
  handle('livery:info', async ({ yft, ytds }) => {
    const r = await call('livery.info', { yft: abs(yft), ytds: (ytds || []).map(abs) });
    return { ...r, targetYtd: r.targetYtd ? rel(r.targetYtd) : null, liveries: r.liveries.map((l) => ({ ...l, ytd: rel(l.ytd) })) };
  });
  handle('livery:template', async ({ yft, size, bgYtd, bgName, mode }) => {
    let outFile;
    if (mode === 'save') {
      const r = await dialog.showSaveDialog(win, {
        title: 'UV-Vorlage speichern',
        defaultPath: path.join(settings.lastExportDir || app.getPath('desktop'), `${path.basename(yft, '.yft').replace(/_hi$/, '')}_folien_vorlage${bgName ? '_' + bgName : ''}.png`),
        filters: [{ name: 'PNG', extensions: ['png'] }],
      });
      if (r.canceled) return null;
      outFile = r.filePath;
      settings.lastExportDir = path.dirname(outFile); saveSettings();
    } else {
      outFile = path.join(sessionDir, 'bearbeiten', 'vorlagen', `${path.basename(yft, '.yft')}_vorlage.png`);
    }
    const res = await call('livery.template', { yft: abs(yft), size: size || 2048, outFile, bgYtd: bgYtd ? abs(bgYtd) : null, bgName: bgName || null });
    if (mode === 'edit') await openInEditor(outFile);
    return res;
  });
  handle('livery:add', async ({ ytd, base, images }) => call('livery.add', { ytd: abs(ytd), base, images, backupDir: backupDir() }));
  handle('livery:create', async ({ ytd, base, size }) => call('livery.create', { ytd: abs(ytd), base, size: size || 2048, backupDir: backupDir() }));
  handle('livery:remove', async ({ ytd, base, name }) => call('livery.remove', { ytd: abs(ytd), base, name, backupDir: backupDir() }));

  // Modelle
  handle('model:blender', async ({ model, ytds }) => openInBlender({ model, ytds }));
  handle('file:export', async ({ file }) => {
    const r = await dialog.showSaveDialog(win, { title: 'Datei speichern', defaultPath: path.join(settings.lastExportDir || app.getPath('desktop'), path.basename(file)) });
    if (r.canceled) return null;
    settings.lastExportDir = path.dirname(r.filePath); saveSettings();
    fs.copyFileSync(abs(file), r.filePath);
    return { file: r.filePath };
  });
  handle('file:replace', async ({ file, source }) => call('file.replace', { file: abs(file), source, backupDir: backupDir() }));

  // FiveM-Export
  handle('fivem:export', async ({ outDir, resourceName }) => {
    rememberExportDir(outDir);
    return call('fivem.export', { sessionDir, outDir, resourceName });
  });
  handle('fivem:exists', async ({ outDir, resourceName }) => fs.existsSync(path.join(outDir, safeResourceName(resourceName))));

  handle('shell:open', async ({ target }) => { await shell.openPath(target); return true; });
  handle('shell:show', async ({ target }) => { shell.showItemInFolder(target); return true; });

  // Externe Links (nur bekannte Seiten)
  handle('shell:external', async ({ url }) => {
    const host = (() => { try { const u = new URL(String(url)); return u.protocol === 'https:' ? u.hostname : ''; } catch { return ''; } })();
    if (!['www.blender.org', 'github.com', 'www.rockstargames.com', 'store.steampowered.com', 'store.epicgames.com'].includes(host)) throw new Error('Link nicht erlaubt.');
    await shell.openExternal(url);
    return true;
  });

  // SFTP (Server)
  handle('sftp:servers', async () => sftp.list());
  handle('sftp:save', async ({ server, secret }) => ({ id: sftp.save(server, secret), servers: sftp.list() }));
  handle('sftp:delete', async ({ id }) => { sftp.remove(id); return sftp.list(); });
  handle('sftp:test', async ({ server, secret, useSaved }) => {
    let sec = secret;
    if (useSaved && server.id && (secret === undefined || secret === null)) sec = sftp.savedSecret(server.id);
    return sftp.test(server, sec);
  });
  handle('sftp:list', async ({ id, path: p }) => sftp.listDir(id, p));
  handle('sftp:download', async ({ id, paths }) => {
    const r = await sftp.download(id, paths);
    settings.sftpLast = { ...(settings.sftpLast || {}), id, importDir: path.posix.dirname(String(paths[0]).split('\\').join('/')) }; saveSettings();
    return r;
  });
  handle('sftp:last', async () => settings.sftpLast || {});
  handle('session:original', async () => (sessionDir ? call('session.original', { sessionDir }) : { name: null }));
  handle('sftp:exists', async ({ id, remoteDir, resourceName }) => sftp.exists(id, remoteDir, safeResourceName(resourceName)));
  handle('sftp:stage', async () => { const d = path.join(TEMP_ROOT, `sftp-export-${Date.now()}`); fs.mkdirSync(d, { recursive: true }); return d; });
  handle('sftp:upload', async ({ id, local, remoteDir }) => {
    const r = await sftp.upload(id, local, remoteDir);
    try { fs.rmSync(path.dirname(local), { recursive: true, force: true }); } catch { /* egal */ }
    settings.sftpLast = { ...(settings.sftpLast || {}), id, exportDir: remoteDir }; saveSettings();
    return r;
  });
  handle('sftp:pickKey', async () => {
    const r = await dialog.showOpenDialog(win, { title: 'Privaten Schlüssel wählen (z. B. id_ed25519 oder .ppk → vorher in OpenSSH umwandeln)', properties: ['openFile', 'showHiddenFiles'], defaultPath: path.join(os.homedir(), '.ssh') });
    return r.canceled ? null : r.filePaths[0];
  });

  // Einrichtung: GTA, Blender, Sollumz automatisch suchen
  handle('setup:check', async () => runSetupCheck({ force: true }));
  handle('setup:status', async () => setupStatus);

  // Updates
  handle('update:check', async () => checkForUpdates(true));
  handle('update:install', async () => { if (updateState.downloaded) { forceQuit = true; app.isQuitting = true; autoUpdater.quitAndInstall(false, true); } return true; });
  handle('update:state', async () => ({ ...updateState, version: app.getVersion() }));
}

// ------------------------------------------------------------------ Fenster

function createWindow() {
  win = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 1024,
    minHeight: 640,
    backgroundColor: '#16181d',
    title: 'GTA Tool',
    // Nur für automatische Tests: Fenster außerhalb des Bildschirms, ohne Fokus (stört kein laufendes Spiel)
    show: !process.env.GTA_TOOL_HIDDEN,
    skipTaskbar: !!process.env.GTA_TOOL_HIDDEN,
    focusable: !process.env.GTA_TOOL_HIDDEN,
    ...(process.env.GTA_TOOL_HIDDEN ? { x: -5000, y: 0 } : {}),
    icon: path.join(__dirname, 'renderer', 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  Menu.setApplicationMenu(null);
  if (process.env.GTA_TOOL_HIDDEN) {
    win.webContents.setBackgroundThrottling(false);
    win.showInactive();
  }
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));
  win.webContents.on('will-navigate', (e) => e.preventDefault());
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  win.webContents.on('before-input-event', (_e, input) => {
    if (input.type === 'keyDown' && input.key === 'F12' && !isPacked) win.webContents.toggleDevTools();
  });

  let confirmed = false;
  win.on('close', (e) => {
    if (confirmed || forceQuit) return;
    e.preventDefault();
    win.webContents.send('app:closing');
  });
  ipcMain.removeHandler('app:close-confirmed');
  ipcMain.handle('app:close-confirmed', () => { confirmed = true; win.close(); });
}

app.whenReady().then(() => {
  loadSettings();
  sftp.init({ getSettings: () => settings, saveSettings, safeStorage, send: (c, d) => send(c, d), tempRoot: TEMP_ROOT, backupRoot: path.join(app.getPath('userData'), 'server-sicherungen'), stateFile: path.join(app.getPath('userData'), 'sftp-stand.json'), log: logLine });
  cleanupOldSessions();
  startBackend();
  registerIpc();
  createWindow();
  win.webContents.once('did-finish-load', async () => {
    await runSetupCheck({ force: false });
    loadKeys();
    setTimeout(() => checkForUpdates(false), 4000);
  });
});

app.on('before-quit', () => { app.isQuitting = true; });
app.on('window-all-closed', () => {
  app.isQuitting = true;
  cleanupAllSessions();
  sftp.closeAll();
  for (const s of blenderSessions.values()) { /* Blender bleibt offen – Nutzer entscheidet */ void s; }
  try { backend && backend.stdin.end(); } catch { /* egal */ }
  app.quit();
});
