# GTA Tool

**🇩🇪 [Deutsch](#deutsch) · 🇬🇧 [English](#english)**

---

## Deutsch

Das GTA Tool ist ein Windows-Programm, mit dem du GTA-V-Inhalte öffnen, ansehen, bearbeiten und als **fertige FiveM-Resource** exportieren kannst – ohne Projektdateien, ganz einfach: **öffnen → bearbeiten → exportieren**. Alle Bereiche sind auf Deutsch und für Einsteiger gemacht.

### Bereiche

| Bereich | Was du damit machen kannst |
| --- | --- |
| 🚗 **Fahrzeuge** | Addon- und Replace-Fahrzeuge aus `dlc.rpf`, Ordnern oder Einzeldateien öffnen · Tuningteile, Felgen und Texturen erkennen · 3D-Vorschau mit anbaubaren Tuningteilen · Folierungen (Liveries) erstellen und bearbeiten · Motorsounds abspielen, ersetzen, exportieren · Replace in Addon umwandeln, Namen ändern · Modelle in Blender bearbeiten |
| 👕 **Kleidung** | Freemode-Kleidung (männlich/weiblich) auf dem Charakter in 3D · Position und Größe anpassen · Texturvarianten · männlich ⇄ weiblich umwandeln · Addon ⇄ Replace · Original-GTA-Kleidung als Vorlage (mit Original-Namen wie `jbib_000_u`) · Textur per Regler verschieben/skalieren · Blender |
| 🏠 **MLO / Interiors** | Objekte in 3D verschieben, drehen, einfügen, löschen · GTA-Objektkatalog · Texturen · Kollisionen anzeigen · **neue Interiors erstellen**: Ort auf der GTA-Karte wählen, in der echten 3D-Umgebung platzieren, in Blender mit Räumen, Portalen und Türen bauen · Blender |
| 🪑 **Props / Objekte** | Eigene Props öffnen (.ydr/.yft) · 3D und Kollision · Texturen bearbeiten · Archetypen (.ytyp) automatisch · umbenennen, Sichtweite · GTA-Objekt als Vorlage · **komplett neue Props in Blender modellieren** · Export mit `/spawnprop` |
| 🔫 **Waffen** | Addon- und Replace-Waffen öffnen · Waffen, Modelle, Komponenten und Munition erkennen · 3D-Vorschau (normal und Hochdetail) · Namen im Spiel ändern · Texturen ersetzen, hinzufügen, löschen · **Original-GTA-Waffen als Vorlage** laden · Blender · Export mit Test-Befehl `/waffe` |
| 🕺 **Animationen** | `.ycd`-Animationen auf dem Freemode-Körper abspielen · umbenennen, duplizieren, Tempo und Länge ändern, zwischen Dateien kopieren · in Blender bearbeiten · Lua-Beispielcode · Export mit Test-Befehl `/animtest` |

Jeder Export ist eine fertige Resource (`fxmanifest.lua`, `stream/`, `data/` …), die direkt auf dem Server gestartet werden kann (`ensure <name>`).

### 🌐 Direkt mit dem Server arbeiten (SFTP)

In jedem Bereich kannst du Dateien und Ordner **direkt von deinem Server laden** („🌐 Vom Server (SFTP)“) und die fertige Resource **direkt auf den Server exportieren** (im Export-Dialog „Server (SFTP)“ wählen). Server legst du unter ⚙ Einstellungen → „SFTP-Server“ an – mit Passwort oder Schlüsseldatei. Zugangsdaten werden verschlüsselt mit Windows gespeichert, der Sicherheitsschlüssel des Servers wird geprüft.

**Gepackte Downloads direkt öffnen:** `.zip`, `.rar` (auch mehrteilig, `name.part1.rar`) und `.7z` lassen sich in jedem Bereich mit „🗜 ZIP / RAR öffnen“ oder per Drag & Drop laden – kein Entpacken nötig.

### Voraussetzungen

- **Windows 10/11 (64 Bit)**
- **GTA V (Legacy)** – optional, aber empfohlen: wird für verschlüsselte `dlc.rpf`-Archive, Original-Kleidung, GTA-Objekte und den Charakter in der 3D-Ansicht gebraucht.
- **Blender 4.2 oder neuer** mit dem Add-on **[Sollumz](https://github.com/Sollumz/Sollumz/releases)** – nur für „In Blender bearbeiten“.
- Optional: **Paint.NET** (Texturen bearbeiten), **Audacity** (Sounds bearbeiten).

Das Tool sucht GTA V, Blender und Sollumz beim Start **automatisch** – auch außerhalb der Standardordner (Steam, Epic, Rockstar Launcher, andere Laufwerke, portable Blender). Fehlt etwas, erscheint ein Hinweis mit Download-Links. Pfade lassen sich jederzeit in den **⚙ Einstellungen** ändern („Erneut suchen“).

### Installation

1. Unter **[Releases](https://github.com/TDIJunkie/GTA-TOOL/releases/latest)** die Datei `GTA-Tool-Setup-<version>.exe` herunterladen.
2. Installieren und starten. Es werden zusätzlich Verknüpfungen für die einzelnen Studios angelegt.

### Automatische Updates

Das Tool sucht bei jedem Start nach einer neuen Version, lädt sie im Hintergrund herunter und fragt dann, ob es neu starten soll. Wählst du „Später“, wird das Update beim nächsten Beenden installiert. Zusätzlich gibt es oben in jedem Bereich den Knopf **„⟳ Update prüfen“**.

### Hinweise

- Verschlüsselte FiveM-Dateien (Escrow) können nicht gelesen oder bearbeitet werden.
- Das Tool enthält **keine** Spieldateien von GTA V. Benötigte Daten werden nur lokal aus deiner eigenen Installation gelesen.
- Dieses Projekt steht in keiner Verbindung zu Rockstar Games, Take-Two oder Cfx.re.

---

## English

GTA Tool is a Windows application for opening, viewing, editing and exporting GTA V content as a **ready-to-run FiveM resource** – no project files, just **open → edit → export**. The user interface is in German and designed for beginners.

### Studios

| Studio | What you can do |
| --- | --- |
| 🚗 **Vehicles** | Open add-on and replace vehicles from `dlc.rpf`, folders or single files · detect tuning parts, wheels and textures · 3D preview with attachable tuning parts · create and edit liveries · play, replace and export engine sounds · convert replace → add-on, rename · edit models in Blender |
| 👕 **Clothing** | Freemode clothing (male/female) in 3D on the character · adjust position and scale · texture variants · convert male ⇄ female · add-on ⇄ replace · use original GTA clothing as a base (with original names like `jbib_000_u`) · move/scale textures with sliders · Blender |
| 🏠 **MLO / Interiors** | Move, rotate, insert and delete objects in 3D · GTA object catalog · textures · show collisions · **create new interiors**: pick a spot on the GTA map, place it in the real 3D surroundings, build it in Blender with rooms, portals and doors · Blender |
| 🪑 **Props / Objects** | Open custom props (.ydr/.yft) · 3D and collision · edit textures · archetypes (.ytyp) created automatically · rename, view distance · GTA object as a template · **model completely new props in Blender** · export with `/spawnprop` |
| 🔫 **Weapons** | Open add-on and replace weapons · detect weapons, models, components and ammo · 3D preview (normal and high detail) · change in-game names · replace, add and delete textures · **load original GTA weapons as a template** · Blender · export with test command `/waffe` |
| 🕺 **Animations** | Play `.ycd` animations on the freemode body · rename, duplicate, change speed and length, copy between files · edit in Blender · Lua sample code · export with test command `/animtest` |

Every export is a complete resource (`fxmanifest.lua`, `stream/`, `data/` …) that can be started on a server right away (`ensure <name>`).

### 🌐 Work directly with your server (SFTP)

In every studio you can **load files and folders straight from your server** (“🌐 Vom Server (SFTP)”) and **export the finished resource straight to the server** (choose “Server (SFTP)” in the export dialog). Add servers under ⚙ Einstellungen → “SFTP-Server” – with password or key file. Credentials are stored encrypted by Windows and the server’s host key is verified.

**Open packed downloads directly:** `.zip`, `.rar` (also multi-part, `name.part1.rar`) and `.7z` can be loaded in every studio via “🗜 ZIP / RAR öffnen” or drag & drop – no unpacking needed.

### Requirements

- **Windows 10/11 (64-bit)**
- **GTA V (Legacy)** – optional but recommended: needed for encrypted `dlc.rpf` archives, original clothing, GTA objects and the character in the 3D view.
- **Blender 4.2 or newer** with the **[Sollumz](https://github.com/Sollumz/Sollumz/releases)** add-on – only for “edit in Blender”.
- Optional: **Paint.NET** (texture editing), **Audacity** (sound editing).

On startup the tool **automatically** looks for GTA V, Blender and Sollumz – also outside the default folders (Steam, Epic, Rockstar Launcher, other drives, portable Blender). If something is missing, a notice with download links is shown. Paths can be changed at any time in **⚙ Einstellungen** (settings, “Erneut suchen” = search again).

### Installation

1. Download `GTA-Tool-Setup-<version>.exe` from **[Releases](https://github.com/TDIJunkie/GTA-TOOL/releases/latest)**.
2. Install and start it. Shortcuts for the individual studios are created as well.

### Automatic updates

On every start the tool checks for a new version, downloads it in the background and then asks whether to restart. If you choose “Später” (later), the update is installed the next time you close the tool. There is also an **“⟳ Update prüfen”** (check for updates) button at the top of every area.

### Notes

- Encrypted FiveM files (escrow) cannot be read or edited.
- The tool does **not** contain any GTA V game files. Required data is only read locally from your own installation.
- This project is not affiliated with Rockstar Games, Take-Two or Cfx.re.

---

## Entwicklung / Development

Voraussetzungen / requirements: **Node.js 22+**, **.NET 10 SDK**, Windows.

```bash
git clone --recursive https://github.com/TDIJunkie/GTA-TOOL.git
cd GTA-TOOL
npm install
npm start          # Entwicklungsstart / dev start
npm run build      # dist/win-unpacked
npm run build:installer   # zusätzlich Installer / additionally the installer
```

**Neue Version veröffentlichen / publishing a release:** Version in `package.json` erhöhen, committen und einen passenden Tag pushen – GitHub Actions baut den Installer und veröffentlicht das Release, das alle Nutzer automatisch als Update bekommen.
Bump the version in `package.json`, commit and push a matching tag – GitHub Actions builds the installer and publishes the release, which all users receive automatically.

```bash
git tag v0.2.0
git push origin v0.2.0
```

Aufbau / structure: `app/` Electron-Oberfläche (UI), `backend/` .NET-Backend (GTA-Dateiformate über CodeWalker), `blender/` Blender-Brücke (Sollumz), `tools/` texconv, `vendor/` CodeWalker-Quellcode. Details: [`PROJEKT.md`](PROJEKT.md).

### Drittanbieter / Third-party

- [CodeWalker](https://github.com/dexyfex/CodeWalker) (MIT) – GTA-V-Dateiformate
- [DirectXTex / texconv](https://github.com/microsoft/DirectXTex) (MIT) – Texturumwandlung
- [three.js](https://threejs.org) (MIT) – 3D-Ansicht
- [electron-updater](https://www.electron.build/auto-update) (MIT) – automatische Updates
- [ssh2-sftp-client](https://github.com/theophilusx/ssh2-sftp-client) / [ssh2](https://github.com/mscdex/ssh2) (Apache-2.0 / MIT) – SFTP
- [Sollumz](https://github.com/Sollumz/Sollumz) – Blender-Add-on (nicht enthalten, wird vom Nutzer installiert / not bundled)
