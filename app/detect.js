'use strict';
// Automatische Suche nach GTA V (Legacy), Blender und dem Sollumz-Add-on – auch außerhalb der Standardpfade.
// Reihenfolge: Registry (Rockstar, Steam, Epic, Uninstall-Einträge, Dateizuordnung) → bekannte Ordner → Laufwerkssuche.

const fs = require('fs');
const path = require('path');
const os = require('os');
const { execFile } = require('child_process');

const exists = (p) => { try { return !!p && fs.existsSync(p); } catch { return false; } };
const isDir = (p) => { try { return fs.statSync(p).isDirectory(); } catch { return false; } };
const readdir = (p) => { try { return fs.readdirSync(p, { withFileTypes: true }); } catch { return []; } };

/** reg query → Liste von { key, name, value } */
function regQuery(key, { recursive = false, value = null } = {}) {
  return new Promise((resolve) => {
    const args = ['query', key];
    if (value) args.push('/v', value);
    if (recursive) args.push('/s');
    execFile('reg.exe', args, { windowsHide: true, maxBuffer: 32 * 1024 * 1024, encoding: 'utf8' }, (err, out) => {
      if (err && !out) return resolve([]);
      const res = [];
      let cur = null;
      for (const line of String(out).split(/\r?\n/)) {
        if (/^HKEY_/i.test(line)) { cur = line.trim(); continue; }
        const m = line.match(/^\s{2,}(.+?)\s{2,}(REG_\w+)\s{2,}(.*)$/);
        if (m && cur) res.push({ key: cur, name: m[1] === '(Standard)' || m[1] === '(Default)' ? '' : m[1], value: m[3].trim() });
      }
      resolve(res);
    });
  });
}

function drives() {
  const list = [];
  for (let c = 67; c <= 90; c++) { const d = `${String.fromCharCode(c)}:\\`; if (exists(d)) list.push(d); }
  return list;
}

/** Steam-Bibliotheken (alle Laufwerke) aus libraryfolders.vdf */
async function steamLibraries() {
  const roots = new Set();
  for (const k of ['HKCU\\Software\\Valve\\Steam', 'HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam', 'HKLM\\SOFTWARE\\Valve\\Steam']) {
    for (const v of await regQuery(k)) if (/^(SteamPath|InstallPath)$/i.test(v.name)) roots.add(path.normalize(v.value));
  }
  const libs = new Set();
  for (const r of roots) {
    libs.add(r);
    try {
      const vdf = fs.readFileSync(path.join(r, 'steamapps', 'libraryfolders.vdf'), 'utf8');
      for (const m of vdf.matchAll(/"path"\s+"([^"]+)"/g)) libs.add(path.normalize(m[1].replace(/\\\\/g, '\\')));
    } catch { /* kein Steam */ }
  }
  return [...libs];
}

/** Epic-Manifeste → InstallLocation je Spiel */
function epicInstalls() {
  const dir = path.join(process.env.ProgramData || 'C:\\ProgramData', 'Epic', 'EpicGamesLauncher', 'Data', 'Manifests');
  const res = [];
  for (const e of readdir(dir)) {
    if (!e.name.endsWith('.item')) continue;
    try { const j = JSON.parse(fs.readFileSync(path.join(dir, e.name), 'utf8')); res.push({ name: j.DisplayName || '', dir: j.InstallLocation || '' }); } catch { /* egal */ }
  }
  return res;
}

/** Programme aus den Uninstall-Einträgen */
let uninstallCache = null;
async function uninstallEntries(filter) {
  if (!uninstallCache) {
    uninstallCache = (async () => {
      const bases = ['HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall', 'HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall', 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall'];
      const all = await Promise.all(bases.map((b) => regQuery(b, { recursive: true })));
      const byKey = new Map();
      for (const r of all.flat()) { if (!byKey.has(r.key)) byKey.set(r.key, {}); byKey.get(r.key)[r.name] = r.value; }
      return [...byKey.values()];
    })();
  }
  return (await uninstallCache).filter((v) => v.DisplayName && filter.test(v.DisplayName));
}

/** Begrenzte Laufwerkssuche nach einer Datei (Ordnernamen-Filter für Tempo) */
function scanDrives(fileName, { maxDepth = 4, dirFilter = null, timeoutMs = 20000 } = {}) {
  const skip = /^(windows|\$recycle\.bin|system volume information|programdata\\microsoft|recovery|perflogs|msocache|node_modules|\.git|appdata|winsxs|config\.msi)$/i;
  const start = Date.now();
  const found = [];
  const walk = (dir, depth) => {
    if (Date.now() - start > timeoutMs || found.length > 5) return;
    const entries = readdir(dir);
    for (const e of entries) if (e.isFile() && e.name.toLowerCase() === fileName.toLowerCase()) found.push(path.join(dir, e.name));
    if (depth >= maxDepth) return;
    for (const e of entries) {
      if (!e.isDirectory() || skip.test(e.name)) continue;
      if (dirFilter && depth >= 1 && !dirFilter(e.name, depth)) continue;
      walk(path.join(dir, e.name), depth + 1);
    }
  };
  for (const d of drives()) walk(d, 0);
  return found;
}

// ------------------------------------------------------------------ GTA V

const isGta = (dir) => exists(path.join(dir, 'GTA5.exe')) && exists(path.join(dir, 'x64a.rpf'));

async function findGta() {
  const cands = [];
  // Rockstar Launcher / Steam / Epic (Registry)
  for (const k of ['HKLM\\SOFTWARE\\WOW6432Node\\Rockstar Games\\Grand Theft Auto V', 'HKLM\\SOFTWARE\\WOW6432Node\\Rockstar Games\\GTAV',
    'HKLM\\SOFTWARE\\Rockstar Games\\Grand Theft Auto V', 'HKLM\\SOFTWARE\\WOW6432Node\\Rockstar Games\\Grand Theft Auto V Legacy']) {
    for (const v of await regQuery(k)) if (/InstallFolder/i.test(v.name) && v.value) cands.push(v.value);
  }
  for (const lib of await steamLibraries()) {
    cands.push(path.join(lib, 'steamapps', 'common', 'Grand Theft Auto V'), path.join(lib, 'steamapps', 'common', 'Grand Theft Auto V Legacy'));
  }
  for (const e of epicInstalls()) if (/grand theft auto v|gtav/i.test(e.name + e.dir)) cands.push(e.dir);
  for (const u of await uninstallEntries(/grand theft auto v/i)) if (u.InstallLocation) cands.push(u.InstallLocation.replace(/"/g, ''));
  for (const d of drives()) {
    for (const sub of ['GTA V\\Grand Theft Auto V Legacy', 'GTA V', 'GTAV', 'Grand Theft Auto V', 'Grand Theft Auto V Legacy',
      'Program Files\\Rockstar Games\\Grand Theft Auto V', 'Program Files\\Rockstar Games\\Grand Theft Auto V Legacy',
      'Program Files\\Epic Games\\GTAV', 'Games\\Grand Theft Auto V', 'Games\\GTA V', 'Games\\GTAV', 'SteamLibrary\\steamapps\\common\\Grand Theft Auto V',
      'SteamLibrary\\steamapps\\common\\Grand Theft Auto V Legacy', 'Rockstar Games\\Grand Theft Auto V']) cands.push(path.join(d, sub));
  }
  const hit = cands.find((c) => c && isGta(c));
  if (hit) return path.normalize(hit);
  // Laufwerkssuche (nur Ordner, die nach Spielen aussehen)
  const looksLikeGames = (n, depth) => depth > 2 || /gta|grand|rockstar|game|spiel|steam|epic|program|library|x86/i.test(n);
  for (const exe of scanDrives('GTA5.exe', { maxDepth: 5, dirFilter: looksLikeGames })) {
    const dir = path.dirname(exe);
    if (isGta(dir)) return dir;
  }
  return '';
}

// ------------------------------------------------------------------ Blender

function blenderVersionFromDir(exe) {
  // Installationsordner enthält einen Unterordner „4.5“ o. ä.
  const dir = path.dirname(exe);
  const vs = readdir(dir).filter((e) => e.isDirectory() && /^\d+\.\d+$/.test(e.name)).map((e) => e.name);
  vs.sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
  return vs[0] || null;
}

const vnum = (v) => { const [a, b] = String(v || '0.0').split('.').map(Number); return a * 100 + (b || 0); };

async function findBlenders() {
  const cands = new Set();
  const pf = [process.env.ProgramFiles, process.env['ProgramFiles(x86)'], 'C:\\Program Files'].filter(Boolean);
  for (const p of pf) {
    const bf = path.join(p, 'Blender Foundation');
    for (const e of readdir(bf)) if (e.isDirectory()) cands.add(path.join(bf, e.name, 'blender.exe'));
  }
  for (const u of await uninstallEntries(/blender/i)) {
    if (u.InstallLocation) cands.add(path.join(u.InstallLocation.replace(/"/g, ''), 'blender.exe'));
    if (u.DisplayIcon) cands.add(u.DisplayIcon.replace(/"/g, '').replace(/,\d+$/, ''));
  }
  // Dateizuordnung .blend
  for (const k of ['HKCR\\blendfile\\shell\\open\\command', 'HKCU\\Software\\Classes\\blendfile\\shell\\open\\command', 'HKLM\\SOFTWARE\\Classes\\blendfile\\shell\\open\\command']) {
    for (const v of await regQuery(k)) { const m = v.value.match(/"([^"]+blender(?:-launcher)?\.exe)"/i) || v.value.match(/^(\S+blender(?:-launcher)?\.exe)/i); if (m) cands.add(m[1].replace(/blender-launcher\.exe$/i, 'blender.exe')); }
  }
  for (const lib of await steamLibraries()) cands.add(path.join(lib, 'steamapps', 'common', 'Blender', 'blender.exe'));
  for (const p of (process.env.PATH || '').split(';')) if (p) cands.add(path.join(p, 'blender.exe'));
  for (const d of drives()) {
    for (const e of readdir(d)) if (e.isDirectory() && /blender/i.test(e.name)) {
      cands.add(path.join(d, e.name, 'blender.exe'));
      for (const s of readdir(path.join(d, e.name))) if (s.isDirectory()) cands.add(path.join(d, e.name, s.name, 'blender.exe'));
    }
  }
  let list = [...cands].filter((c) => exists(c) && /blender\.exe$/i.test(c));
  if (!list.length) {
    // Laufwerkssuche (z. B. portable Blender in eigenem Ordner)
    list = scanDrives('blender.exe', { maxDepth: 4, dirFilter: (n, depth) => depth > 1 || /blender|program|tools|software|apps|3d|portable|games/i.test(n) });
  }
  const uniq = [...new Map(list.map((f) => [path.normalize(f).toLowerCase(), path.normalize(f)])).values()];
  return uniq.map((exe) => ({ exe, version: blenderVersionFromDir(exe) }))
    .sort((a, b) => vnum(b.version) - vnum(a.version));
}

/** Sollumz-Add-on für eine Blender-Installation suchen (Benutzerordner, portable, mitgelieferte Add-ons). */
function findSollumz(blenderExe, version) {
  const roots = [];
  const appdata = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
  const userBase = path.join(appdata, 'Blender Foundation', 'Blender');
  const versions = version ? [version] : readdir(userBase).filter((e) => e.isDirectory()).map((e) => e.name);
  for (const v of versions) roots.push(path.join(userBase, v));
  if (blenderExe) {
    const dir = path.dirname(blenderExe);
    roots.push(path.join(dir, 'portable'));
    if (version) roots.push(path.join(dir, version));
  }
  for (const r of roots) {
    const places = [];
    for (const e of readdir(path.join(r, 'extensions'))) if (e.isDirectory()) places.push(path.join(r, 'extensions', e.name));
    places.push(path.join(r, 'scripts', 'addons'));
    for (const p of places) {
      for (const e of readdir(p)) {
        if (!e.isDirectory() || !/^sollumz/i.test(e.name)) continue;
        const full = path.join(p, e.name);
        if (!exists(path.join(full, '__init__.py'))) continue;
        let ver = null;
        try { const t = fs.readFileSync(path.join(full, 'blender_manifest.toml'), 'utf8'); const m = t.match(/^version\s*=\s*"([^"]+)"/m); if (m) ver = m[1]; } catch { /* älteres Add-on */ }
        if (!ver) { try { const t = fs.readFileSync(path.join(full, '__init__.py'), 'utf8'); const m = t.match(/"version"\s*:\s*\((\d+)\s*,\s*(\d+)\s*,\s*(\d+)/); if (m) ver = `${m[1]}.${m[2]}.${m[3]}`; } catch { /* egal */ } }
        return { path: full, version: ver };
      }
    }
  }
  return null;
}

/**
 * Prüft alles. current: gespeicherte Pfade (werden nur ersetzt, wenn ungültig).
 * Ergebnis: { gta: {ok, path}, blender: {ok, path, version}, sollumz: {ok, path, version, blenderVersion} }
 */
async function checkAll(current = {}) {
  const gtaPath = current.gtaPath && isGta(current.gtaPath) ? current.gtaPath : await findGta();
  let blenderPath = current.blenderPath && exists(current.blenderPath) ? current.blenderPath : '';
  let blenderVersion = blenderPath ? blenderVersionFromDir(blenderPath) : null;
  let sollumz = blenderPath ? findSollumz(blenderPath, blenderVersion) : null;
  if (!blenderPath || !sollumz) {
    // Bevorzugt eine Blender-Version, für die Sollumz installiert ist
    const all = await findBlenders();
    const withS = all.map((b) => ({ ...b, s: findSollumz(b.exe, b.version) })).find((b) => b.s && vnum(b.version) >= 402);
    const pick = withS || (blenderPath ? null : all.find((b) => vnum(b.version) >= 402) || all[0]);
    if (pick) { blenderPath = pick.exe; blenderVersion = pick.version; sollumz = pick.s || findSollumz(pick.exe, pick.version); }
  }
  return {
    gta: { ok: !!gtaPath, path: gtaPath || '' },
    blender: { ok: !!blenderPath, path: blenderPath || '', version: blenderVersion, tooOld: !!blenderVersion && vnum(blenderVersion) < 402 },
    sollumz: { ok: !!sollumz, path: sollumz ? sollumz.path : '', version: sollumz ? sollumz.version : null, blenderVersion },
  };
}

module.exports = { checkAll, findGta, findBlenders, findSollumz, isGta };
