## Neu in dieser Version / What's new

**🇩🇪**
- **SFTP-Export: Zielordner wird geprüft** – wird versehentlich die Resource selbst als Ziel gewählt (z. B. `[mlo]/meine_resource`), lädt das Tool automatisch in den Ordner darüber hoch, statt eine verschachtelte Kopie anzulegen. Als Ziel wird der Ordner vorgeschlagen, aus dem importiert wurde.
- Leer gewordene Ordner auf dem Server werden nach dem Abgleich entfernt.
- Aus 0.3.1: Export behält die Original-Resource (Name, Ordner, Dateinamen, fxmanifest, Skripte); per SFTP werden nur geänderte Dateien hochgeladen.

**🇬🇧**
- **SFTP export checks the target folder** – if the resource itself is picked as target (e.g. `[mlo]/my_resource`), the tool uploads to the folder above instead of creating a nested copy. The folder you imported from is suggested as target.
- Folders on the server that became empty are removed after syncing.
- From 0.3.1: export keeps the original resource (name, folders, file names, fxmanifest, scripts); SFTP uploads only changed files.

---

## 🇩🇪 Download

👉 **`GTA-Tool-Setup-{version}.exe`** herunterladen, starten und installieren.

Bereits installiert? Das Tool aktualisiert sich **automatisch** – beim nächsten Start wird die neue Version geladen und nach einem Neustart installiert.

> `latest.yml` musst du nicht herunterladen – die Datei braucht nur die automatische Update-Funktion.

Voraussetzungen und alle Funktionen: siehe [README](https://github.com/TDIJunkie/GTA-TOOL#deutsch).

---

## 🇬🇧 Download

👉 Download **`GTA-Tool-Setup-{version}.exe`**, run it and install.

Already installed? The tool updates **automatically** – the new version is downloaded on the next start and installed after a restart.

> You don't need `latest.yml` – it is only used by the automatic update function.

Requirements and features: see the [README](https://github.com/TDIJunkie/GTA-TOOL#english).
