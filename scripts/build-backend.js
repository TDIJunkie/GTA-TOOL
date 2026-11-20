// Veröffentlicht das .NET-Backend nach build/backend (inkl. texconv.exe daneben)
const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const out = path.join(root, 'build', 'backend');
const exe = path.join(out, 'GtaBackend.exe');
const dev = process.argv.includes('--dev');

function newestMtime(dir) {
  let newest = 0;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj'].includes(e.name)) continue;
    const full = path.join(dir, e.name);
    newest = Math.max(newest, e.isDirectory() ? newestMtime(full) : fs.statSync(full).mtimeMs);
  }
  return newest;
}

if (dev && fs.existsSync(exe) && fs.statSync(exe).mtimeMs > newestMtime(path.join(root, 'backend'))) {
  console.log('Backend ist aktuell.');
  process.exit(0);
}

console.log('> Backend wird veröffentlicht …');
fs.rmSync(out, { recursive: true, force: true });
execSync(`dotnet publish "${path.join(root, 'backend', 'GtaBackend', 'GtaBackend.csproj')}" -c Release -o "${out}" -p:DebugType=none -p:GenerateDocumentationFile=false`, { stdio: 'inherit' });
fs.copyFileSync(path.join(root, 'tools', 'texconv.exe'), path.join(out, 'texconv.exe'));
console.log('> Backend fertig:', out);
