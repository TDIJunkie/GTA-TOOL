// npm run build            → dist/win-unpacked (immer!)
// npm run build:installer  → zusätzlich Installer-EXE (nur nach Freigabe durch den Nutzer)
// Veröffentlichen: Versions-Tag pushen → GitHub Actions (.github/workflows/release.yml)
const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const installer = process.argv.includes('--installer');
const run = (cmd) => execSync(cmd, { stdio: 'inherit', cwd: root });

run('node scripts/build-backend.js');

console.log(`> Electron-Paket wird gebaut (${installer ? 'win-unpacked + Installer' : 'win-unpacked'}) …`);
run(`npx electron-builder --win ${installer ? '' : '--dir'} --x64 --publish never`);

// Verknüpfung „Vehicle Studio“ (startet direkt ohne Startoberfläche)
const unpacked = path.join(root, 'dist', 'win-unpacked');
const exe = path.join(unpacked, 'GTA Tool.exe');
if (fs.existsSync(exe)) {
  const lnk = path.join(unpacked, 'Vehicle Studio.lnk');
  const ps = `$s=(New-Object -ComObject WScript.Shell).CreateShortcut('${lnk.replace(/'/g, "''")}');` +
    `$s.TargetPath='${exe.replace(/'/g, "''")}';$s.Arguments='--vehicle-studio';` +
    `$s.WorkingDirectory='${unpacked.replace(/'/g, "''")}';$s.IconLocation='${exe.replace(/'/g, "''")},0';$s.Description='GTA Tool – Vehicle Studio';$s.Save()`;
  execSync(`powershell -NoProfile -Command "${ps.replace(/"/g, '\\"')}"`, { stdio: 'inherit' });
  const lnk2 = path.join(unpacked, 'Clothing Studio.lnk');
  const ps2 = ps.replace(lnk.replace(/'/g, "''"), lnk2.replace(/'/g, "''")).replace("--vehicle-studio", "--clothing-studio").replace('GTA Tool – Vehicle Studio', 'GTA Tool – Clothing Studio');
  execSync(`powershell -NoProfile -Command "${ps2.replace(/"/g, '\\"')}"`, { stdio: 'inherit' });
  const lnk3 = path.join(unpacked, 'MLO Studio.lnk');
  const ps3 = ps.replace(lnk.replace(/'/g, "''"), lnk3.replace(/'/g, "''")).replace("--vehicle-studio", "--mlo-studio").replace('GTA Tool – Vehicle Studio', 'GTA Tool – MLO Studio');
  execSync(`powershell -NoProfile -Command "${ps3.replace(/"/g, '\\"')}"`, { stdio: 'inherit' });
  const lnk4 = path.join(unpacked, 'Animation Studio.lnk');
  const ps4 = ps.replace(lnk.replace(/'/g, "''"), lnk4.replace(/'/g, "''")).replace('--vehicle-studio', '--anim-studio').replace('GTA Tool – Vehicle Studio', 'GTA Tool – Animation Studio');
  execSync(`powershell -NoProfile -Command "${ps4.replace(/"/g, '\\"')}"`, { stdio: 'inherit' });
  const lnk5 = path.join(unpacked, 'Prop Studio.lnk');
  const ps5 = ps.replace(lnk.replace(/'/g, "''"), lnk5.replace(/'/g, "''")).replace('--vehicle-studio', '--prop-studio').replace('GTA Tool – Vehicle Studio', 'GTA Tool – Prop Studio');
  execSync(`powershell -NoProfile -Command "${ps5.replace(/"/g, '\\"')}"`, { stdio: 'inherit' });
  const lnk6 = path.join(unpacked, 'Weapon Studio.lnk');
  const ps6 = ps.replace(lnk.replace(/'/g, "''"), lnk6.replace(/'/g, "''")).replace('--vehicle-studio', '--weapon-studio').replace('GTA Tool – Vehicle Studio', 'GTA Tool – Weapon Studio');
  execSync(`powershell -NoProfile -Command "${ps6.replace(/"/g, '\\"')}"`, { stdio: 'inherit' });
  console.log('> Fertig:', exe);
  console.log('> Direktstart Vehicle Studio:', lnk);
}
