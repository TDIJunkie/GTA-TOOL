'use strict';
// Navigation (Startoberfläche ⇄ Studios), Einstellungen, GTA-Schlüssel-Anzeige

const { h, call, toast, modal } = UI;

const App = {
  view: 'start',
  keys: { loaded: false },

  go(view) {
    document.querySelectorAll('.view').forEach((v) => v.classList.add('hidden'));
    document.getElementById(`view-${view}`).classList.remove('hidden');
    this.view = view;
    if (view === 'vehicles') { window.api.invoke('studio:set', { studio: 'vehicles' }); VehicleStudio.show(); }
    if (view === 'clothing') ClothingStudio.show();
    if (view === 'mlo') MloStudio.show();
    if (view === 'anim') AnimStudio.show();
    if (view === 'prop') PropStudio.show();
    if (view === 'weapon') WeaponStudio.show();
  },

  renderKeys() {
    for (const id of ['keys-badge', 'keys-badge-2', 'keys-badge-3', 'keys-badge-4', 'keys-badge-5', 'keys-badge-6', 'keys-badge-7']) {
      const b = document.getElementById(id);
      b.className = 'badge ' + (this.keys.loaded ? 'ok' : 'bad');
      b.textContent = this.keys.loaded ? '✓ GTA-Schlüssel geladen' : '⚠ GTA-Schlüssel fehlen';
      b.title = this.keys.loaded
        ? 'Verschlüsselte dlc.rpf-Archive können gelesen werden.'
        : (this.keys.error || 'GTA-V-Ordner in den Einstellungen angeben') + ' – Klicken für Einstellungen.';
      b.onclick = this.keys.loaded ? null : () => this.openSettings();
    }
  },

  setup: null,

  LINKS: {
    blender: 'https://www.blender.org/download/',
    sollumz: 'https://github.com/Sollumz/Sollumz/releases',
    sollumzHelp: 'https://github.com/Sollumz/Sollumz#installation',
  },

  /** Statusliste GTA / Blender / Sollumz mit Hilfe-Links */
  statusList(st) {
    const link = (label, url) => h('button.btn.small', { onclick: () => call('shell:external', { url }) }, label);
    const row = (ok, title, text, extra) => h('div.list-row', h('span', ok ? '✅' : '⚠️'), h('div.grow', h('div.name', title), h('div.muted.small', text)), extra || null);
    return h('div.list',
      row(st.gta.ok, 'GTA V (Legacy)', st.gta.ok ? st.gta.path : 'Nicht gefunden. Ohne GTA können verschlüsselte dlc.rpf-Archive, Original-Kleidung und GTA-Objekte nicht gelesen werden. Bitte unten den Ordner mit GTA5.exe wählen.'),
      row(st.blender.ok && !st.blender.tooOld, 'Blender', st.blender.ok
        ? `${st.blender.path}${st.blender.version ? ` (Version ${st.blender.version})` : ''}${st.blender.tooOld ? ' – zu alt, bitte Blender 4.2 oder neuer installieren' : ''}`
        : 'Nicht installiert oder nicht gefunden. Wird zum Bearbeiten von Modellen und Animationen gebraucht.',
        st.blender.ok && !st.blender.tooOld ? null : link('Blender herunterladen', this.LINKS.blender)),
      row(st.sollumz.ok, 'Sollumz (Blender-Add-on)', st.sollumz.ok
        ? `Version ${st.sollumz.version || '?'} · ${st.sollumz.path}`
        : st.blender.ok ? `Nicht in Blender ${st.sollumz.blenderVersion || ''} installiert. In Blender: Bearbeiten → Einstellungen → Add-ons → „Von Datei installieren“ (ZIP von GitHub).` : 'Wird nach Blender installiert.',
        st.sollumz.ok ? null : h('div.row.gap-s', link('Sollumz herunterladen', this.LINKS.sollumz), link('Anleitung', this.LINKS.sollumzHelp))));
  },

  /** Hinweis beim Start, wenn etwas fehlt */
  showSetupNotice(st) {
    const missing = !st.gta.ok || !st.blender.ok || st.blender.tooOld || !st.sollumz.ok;
    if (!missing || st.dismissed) return;
    const dont = h('input', { type: 'checkbox' });
    modal({
      title: 'Es fehlt noch etwas',
      wide: true,
      body: h('div',
        h('p', 'Das GTA Tool hat deinen PC durchsucht. Folgendes wurde nicht gefunden – die übrigen Funktionen kannst du trotzdem benutzen:'),
        this.statusList(st),
        h('p.muted.small', 'Nach der Installation einfach in den Einstellungen auf „Erneut suchen“ klicken oder das Tool neu starten. Liegt ein Programm an einem ungewöhnlichen Ort, kannst du den Pfad in den Einstellungen selbst wählen.'),
        h('label.check.small', dont, ' Diesen Hinweis nicht mehr anzeigen')),
      buttons: [
        { label: '⚙ Einstellungen öffnen', onClick: () => { if (dont.checked) call('settings:set', { setupDismissed: true }); setTimeout(() => this.openSettings(), 50); } },
        { label: 'OK', primary: true, onClick: () => { if (dont.checked) call('settings:set', { setupDismissed: true }); } },
      ],
    });
  },

  async openSettings() {
    const s = await call('settings:get');
    if (!s) return;
    const fields = {};
    const pathField = (key, label, hint, kind) => {
      const input = h('input', { type: 'text', value: s[key] || '', placeholder: 'nicht gesetzt' });
      fields[key] = input;
      return h('div.field',
        h('label', label),
        h('div.input-row', input, h('button.btn', {
          onclick: async () => {
            const r = await call('dialog:open', { kind });
            if (r && r[0]) input.value = r[0];
          },
        }, 'Durchsuchen …')),
        h('div.hint', hint));
    };
    const statusBox = h('div');
    const showStatus = (st) => {
      statusBox.innerHTML = '';
      if (!st) { statusBox.append(h('p.muted.small', 'Prüfung läuft …')); return; }
      statusBox.append(this.statusList(st));
    };
    showStatus(this.setup);
    const updBox = h('div.row.gap-s.wrap', { style: { alignItems: 'center' } });
    const showUpd = async () => {
      const u = await call('update:state', null, { quiet: true });
      updBox.innerHTML = '';
      updBox.append(h('span.muted.small', `Version ${u ? u.version : '?'}`),
        u && u.downloaded ? h('button.btn.small.primary', { onclick: () => call('update:install') }, `⟳ Update ${u.available} installieren (Neustart)`)
          : h('button.btn.small', {
            onclick: async () => {
              const r = await call('update:check', null, { busyText: 'Suche nach Updates …' });
              if (!r) return;
              if (!r.ok) toast(r.reason || 'Update-Prüfung nicht möglich.', 'warn');
              else if (r.downloaded) toast(`Update ${r.version} ist bereit.`);
              else if (r.available) toast(`Update ${r.available} gefunden – wird im Hintergrund geladen.`);
              else toast('Du hast die neueste Version.');
              showUpd();
            },
          }, 'Nach Updates suchen'));
    };
    showUpd();
    modal({
      title: 'Einstellungen',
      body: h('div',
        h('div.section', h('div.section-head', h('h3', 'Gefundene Programme')), statusBox,
          h('div.row.gap-s', { style: { marginTop: '6px' } }, h('button.btn.small', {
            onclick: async () => {
              showStatus(null);
              const st = await call('setup:check', null, { quiet: true });
              if (st) { this.setup = st; showStatus(st); for (const [k, el] of Object.entries(fields)) { const ns = await call('settings:get'); if (ns && ns[k] !== undefined) el.value = ns[k] || ''; } }
            },
          }, '🔍 Erneut suchen'))),
        pathField('gtaPath', 'GTA-V-Ordner', 'Ordner mit GTA5.exe (Legacy). Wird für verschlüsselte dlc.rpf-Archive gebraucht. Wird automatisch gesucht.', 'gta'),
        pathField('blenderPath', 'Blender (blender.exe)', 'Blender 4.2 oder neuer mit installiertem Sollumz-Add-on. Wird automatisch gesucht.', 'exe'),
        pathField('audioEditor', 'Audioprogramm (z. B. Audacity, optional)', 'Wird beim Bearbeiten von Sounds geöffnet. Leer = Windows-Standardprogramm für WAV.', 'exe'),
        pathField('imageEditor', 'Bildprogramm (Paint.NET)', 'Wird bei „Ersetzen“ und „Bearbeiten“ geöffnet. Paint.NET wird automatisch erkannt.', 'exe'),
        h('div.section', h('div.section-head', h('h3', 'SFTP-Server')), h('p.muted.small', 'Server für Import und Export per SFTP (z. B. dein FiveM-Server).'), h('button.btn.small', { onclick: () => Sftp.manage() }, '🌐 Server verwalten')),
        h('div.section', h('div.section-head', h('h3', 'Updates')), updBox,
          h('p.muted.small', 'Updates werden beim Start automatisch gesucht und im Hintergrund geladen.')),
      ),
      buttons: [
        { label: 'Abbrechen' },
        {
          label: 'Speichern', primary: true,
          onClick: async () => {
            const next = {};
            for (const [k, el] of Object.entries(fields)) next[k] = el.value.trim();
            const r = await call('settings:set', next, { busyText: 'Einstellungen werden gespeichert …' });
            if (r) toast('Einstellungen gespeichert.');
          },
        },
      ],
    });
  },
};

window.api.on('keys', (st) => {
  App.keys = st || { loaded: false };
  App.renderKeys();
  if (st && !st.loaded && st.error) console.warn('Schlüssel:', st.error);
});
window.api.on('progress', (d) => UI.progress(d));
let setupShown = false;
window.api.on('setup', (st) => {
  App.setup = st;
  if (!setupShown) { setupShown = true; App.showSetupNotice(st); }
});
window.api.on('update', (u) => {
  if (u.state === 'available') toast(`Neue Version ${u.version} gefunden – wird im Hintergrund geladen.`, 'info', 6000);
  if (u.state === 'downloaded') showUpdateReady(u.version);
});
function showUpdateReady(version) {
  {
    modal({
      title: `Update ${version} ist bereit`,
      body: h('div',
        h('p', 'Eine neue Version des GTA Tools wurde heruntergeladen.'),
        h('p.muted', 'Jetzt neu starten installiert das Update sofort. Bei „Später“ wird es beim nächsten Beenden installiert.'),
        (VehicleStudio.dirty || ClothingStudio.dirty || MloStudio.dirty || AnimStudio.dirty || PropStudio.dirty || WeaponStudio.dirty)
          ? h('div.notice.warn', 'Achtung: Du hast nicht exportierte Änderungen – vor dem Neustart bitte exportieren.') : null),
      buttons: [
        { label: 'Später' },
        { label: '⟳ Jetzt neu starten', primary: true, onClick: () => call('update:install') },
      ],
    });
  }
}
window.api.on('toast', (d) => toast(d.text, d.type || 'ok'));
window.api.on('app:closing', async () => {
  if (VehicleStudio.dirty || ClothingStudio.dirty || MloStudio.dirty || AnimStudio.dirty || PropStudio.dirty || WeaponStudio.dirty) {
    const ok = await UI.confirm('Beenden?', 'Du hast Änderungen, die noch nicht als FiveM-Resource exportiert wurden. Trotzdem beenden? Die Änderungen gehen verloren.', 'Beenden', { danger: true });
    if (!ok) return;
  }
  window.api.confirmClose();
});

document.addEventListener('click', (e) => {
  const go = e.target.closest('[data-goto]');
  if (go && !go.disabled) App.go(go.dataset.goto);
  const sf = e.target.closest('[data-sftp-open]');
  if (sf) Sftp.importTo({ vehicles: VehicleStudio, clothing: ClothingStudio, mlo: MloStudio, anim: AnimStudio, prop: PropStudio, weapon: WeaponStudio }[sf.dataset.sftpOpen], false);
  const act = e.target.closest('[data-action="settings"]');
  if (act) App.openSettings();
  if (e.target.closest('[data-action="update"]')) checkUpdateNow();
});

/** Knopf „Update prüfen“ in der Kopfleiste: sucht sofort; fertig geladenes Update kann direkt installiert werden. */
async function checkUpdateNow() {
  const u = await call('update:state', null, { quiet: true });
  if (u && u.downloaded) { showUpdateReady(u.available); return; }
  const r = await call('update:check', null, { busyText: 'Suche nach Updates …' });
  if (!r) return;
  if (!r.ok) toast(r.reason || 'Update-Prüfung nicht möglich.', 'warn');
  else if (r.downloaded) showUpdateReady(r.version);
  else if (r.available) toast(`Update ${r.available} gefunden – wird im Hintergrund geladen. Danach kannst du neu starten.`, 'info', 8000);
  else toast(`Du hast die neueste Version (${u ? u.version : ''}).`);
}

// Dateien, die nicht auf einer Ablagefläche landen, sollen das Fenster nicht umleiten
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => e.preventDefault());

(async () => {
  const info = await call('app:info');
  App.renderKeys();
  VehicleStudio.init();
  ClothingStudio.init();
  MloStudio.init();
  AnimStudio.init();
  PropStudio.init();
  WeaponStudio.init();
  App.go(info ? info.startView : 'start');
  if (info && info.startFiles && info.startFiles.length) {
    App.go('vehicles');
    VehicleStudio.open(info.startFiles, false);
  }
})();
