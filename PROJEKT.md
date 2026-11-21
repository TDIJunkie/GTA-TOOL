# GTA Tool – FiveM Converter

Tool zum Umwandeln von GTA-V-Addons/Replaces in fertige FiveM-Resources.

## Feste Regeln (vom Nutzer vorgegeben)

1. **Immer `win-unpacked` mitbauen.** Nach jeder Änderung wird `npm run build` ausgeführt
   (erzeugt `dist\win-unpacked\GTA Tool.exe`).
2. **Keine Installer-EXE**, bevor der Nutzer `win-unpacked` ausdrücklich freigibt.
   Erst dann: `npm run build:installer`.
3. **Keine Projekte.** Dateien öffnen → bearbeiten → exportieren. Kein Projekt-Anlegen, keine Projektdateien.
4. **Startoberfläche** mit Auswahl (Fahrzeuge, Kleidung, …). Aus jedem Studio muss man
   **immer zurück zur Startoberfläche** kommen.
5. **Vehicle Studio direkt startbar** ohne Startoberfläche: `GTA Tool.exe --vehicle-studio`
   (Verknüpfung `Vehicle Studio.lnk` liegt in `win-unpacked`).
6. **Design:** sehr schlicht, sehr übersichtlich, leicht bedienbar, für Anfänger geeignet. Alles auf Deutsch.
7. Export immer in einen **Ordner nach Wahl** als **komplett fertiges FiveM-Script** (direkt auf dem Server startbar).

## Vehicle Studio – Anforderungen

- Lesen: `dlc.rpf` (auch verschachtelte RPFs), Ordner (FiveM-Resources, lose Dateien), einzelne `.yft/.ytd/.meta`
- Alles erkennen: Fahrzeuge, Tuningteile (über carvariations → carcols-Kits + Namensschema), Felgen, Texturen
- Texturen: exportieren, bearbeiten (externes Bildprogramm, automatische Übernahme), ersetzen, importieren
- Modelle (Fahrzeug, Tuningteile, Felgen): nach Blender exportieren, bearbeiten, direkt zurück importieren
- Export: fertige FiveM-Resource (`fxmanifest.lua`, `stream/`, `data/`, Namen über `AddTextEntry`)

## Architektur

```
app/                    Electron-Oberfläche (HTML/CSS/JS, kein Framework)
  main.js               Fenster, Dialoge, Startargumente, Backend-Prozess, Blender-Start
  preload.js            sichere Brücke zur Oberfläche
  renderer/             Startoberfläche + Vehicle Studio
backend/GtaBackend/     .NET 10 Konsolenprogramm, JSON-Zeilen über stdin/stdout
                        nutzt vendor/CodeWalker/CodeWalker.Core (MIT) für RPF/YTD/YFT/Meta
blender/gta_tool_bridge.py  läuft in Blender: Import via Sollumz, Button „Speichern & zurück zum GTA Tool“
tools/texconv.exe       DirectXTex (MIT) – DDS ⇄ PNG, BC-Kompression, Mipmaps
vendor/CodeWalker/      CodeWalker-Quellcode (MIT)
dist/win-unpacked/      Build-Ergebnis
```

- **Arbeitsordner statt Projekt:** Beim Öffnen wird alles in einen temporären Sitzungsordner
  (`%TEMP%\GTATool\sitzung-*`) entpackt (`stream\`, `data\`). Alle Bearbeitungen passieren dort.
  Export kopiert daraus die Resource.
- **GTA-Schlüssel:** Werden aus `GTA5.exe` (GTA-Ordner in den Einstellungen) erzeugt – nötig für verschlüsselte RPFs.
- **Blender:** Blender 4.x + Sollumz 2.9 (`bpy.ops.sollumz.import_assets/export_assets`, NATIVE/GEN8).
  Externe Texturen sucht Sollumz als DDS in `<modellordner>/<modellname>/`.
  Tuningteile: bei IndexError mit `split_by_group=False` neu importieren.

## Befehle

| Befehl | Zweck |
| --- | --- |
| `npm run build` | Backend veröffentlichen + `dist\win-unpacked` bauen (**immer nach Änderungen**) |
| `npm run build:installer` | Installer-EXE – **nur nach Freigabe durch den Nutzer** |
| `npm start` | Entwicklungsstart |

## Status

Siehe Abschnitt „Fortschritt“ unten (wird laufend gepflegt).

## Fortschritt

### Stand 18.11.2026 – Version 0.1.0 (nur win-unpacked, Installer noch NICHT freigegeben)

- [x] Startoberfläche (Fahrzeuge aktiv; Kleidung/Waffen/Props „bald“), „← Startseite“ in jedem Studio
- [x] Direktstart `--vehicle-studio` + `Vehicle Studio.lnk` in win-unpacked
- [x] Öffnen: dlc.rpf (auch NG-verschlüsselt, verschachtelte RPFs), Ordner, Einzeldateien, Drag & Drop, „Hinzufügen“
- [x] Erkennung: Fahrzeuge (vehicles.meta, Namen aus GXT2), Tuningteile über Modkits + Namensschema, Felgen, YTDs,
      Replace ohne Meta, Escrow-Erkennung, 16-MB-Warnung, fehlerhafte Metas
- [x] Texturen: Liste mit Vorschau, Export PNG/DDS, Bearbeiten im Bildprogramm mit Auto-Übernahme,
      Ersetzen, Importieren, Löschen; Mipmaps bis 4×4, Format bleibt erhalten; eingebettete Texturen in YFT
- [x] Blender: Karosserie/Tuningteile/Felgen öffnen (Sollumz), Knopf „Speichern & zurück zum GTA Tool“ → automatische Übernahme
- [x] FiveM-Export: fxmanifest.lua (data_file in richtiger Reihenfolge), stream, data, client/names.lua (AddTextEntry), LIESMICH.txt
- [x] Backups jeder geänderten Datei im Sitzungsordner (`backup\`)

Getestet mit: Skyline R34 (dlc.rpf, 9 Tuningteile), Diablo GTR (145 Dateien), Audi RS4 (fehlerhafte carcols.meta).
Blender-Rundweg (Fahrzeug + Tuningteil) headless und über die App geprüft. **Noch nicht im FiveM-Client getestet.**

### Stand 18.11.2026 (2) – Nutzerwünsche umgesetzt

- [x] Öffnen nur noch über **„Ordner importieren“** und **„Dateien öffnen“** (kein dlc.rpf-Knopf mehr).
      Der Ordner wird automatisch durchsucht (RPFs, lose Dateien, Metas).
- [x] **3D-Ansicht** (three.js 0.187, `app/renderer/vendor/three`) im Fahrzeug-, Tuningteil- und Texturbereich.
      Backend wandelt YFT → GLB (`Model3D.cs`, `model.glb`): _hi-Modell, Räder an allen 4 Positionen
      (Frag-Matrizen, rechte Seite gespiegelt), Texturen aus YTD/eingebettet, Ersatzfarben für GTA-Standardtexturen.
      Tuningteile werden am Bone aus `carcols.meta` (`<bone>`) ins durchsichtige Fahrzeug gesetzt (grün markiert).
      **Aktualisiert sich automatisch** nach Blender-Übernahme, Textur bearbeiten/ersetzen/importieren/löschen und Datei ersetzen
      (Kamera bleibt stehen). Lackfarben-Auswahl nur für die Vorschau.
- Wissen: Frag-Matrizen sind 3×4 (M44 = 0 → auf 1 setzen). Die Grundpose `BoneTransforms` kann leer sein → Skelett-Kette nutzen.
- Testmodus: `GTA_TOOL_HIDDEN=1` → Fenster außerhalb des Bildschirms ohne Fokus (für CDP-Tests).

### Stand 18.11.2026 (3) – Paint.NET + Folierungen

- [x] Textur **„Ersetzen“ öffnet direkt Paint.NET** (automatisch erkannt: `C:\Program Files\paint.net\paintdotnet.exe`),
      Speichern übernimmt automatisch. „Datei …“ = Ersetzen durch Bilddatei.
- [x] **Folierungen (Livery)** je Fahrzeug im Baum: Folien-Slot erkennen (Shader-Parameter `DiffuseSampler2`,
      Texturen `<basis>_1, _2 …`), Liste mit Vorschau, 3D-Anzeige je Folie (Folie liegt transparent über dem Lack,
      zweiter UV-Kanal), neue leere Folie erstellen (+ UV-Vorlage, beides in Paint.NET), Bild als Folie importieren,
      bearbeiten (Paint.NET), aus Datei ersetzen, exportieren (PNG), UV-Vorlage speichern (auch mit Folie als Hintergrund),
      löschen mit Nachrücken der Nummern. Livery-Tuningteile (VMT_LIVERY_MOD) werden mit aufgeführt.
- Backend: `Livery.cs` (`livery.info/template/add/create/remove`), `model.glb` mit Parameter `livery` ("-" = ohne).
- Fix: GLTFLoader teilt ein Material über viele Meshes → Folie am Material merken (`userData.liveryMap`), sonst nur auf
  dem ersten Karosserieteil sichtbar. Leere (durchsichtige) Folien werden als „leer“ markiert; in 3D wird die erste
  Folie mit Inhalt gezeigt. Getestet: Audi RS4 (Camouflage), Diablo GTR (Drache), Skyline.

### Stand 18.11.2026 (4) – Folien auf Anbauteilen

- [x] **Tuningteile in der Vorschau anbauen** (Fahrzeug-, Folien- und Texturansicht, Bereich „🔧 Tuningteile anbauen“):
      je Kategorie ein Teil wie im Spiel, Teile übernehmen die gewählte Folie, Serienteile aus `carcols.meta`
      `<turnOffBones>` werden ausgeblendet (auch Kind-Bones; bei Skin-Meshes per Vertex-Haupt-Bone). Nur Vorschau.
- Teil-Ansicht zeigt das Teil ebenfalls mit der gewählten Folie.
- Hinweis: Teile mit Shader ohne Folien-Slot (z. B. `vehicle_paint2`) zeigen auch im Spiel keine Folie.

### Stand 18.11.2026 (5) – Mitgelieferte Motorsounds

- [x] Neuer Bereich **„🔊 Motorsound“** je Fahrzeug (Nutzerwunsch: Sounds, die beim Auto dabei sind):
      Sound-Packs erkennen (`audio\<quelle>\sfx\<pack>\*.awc`, `audio\<quelle>\audioconfig\*.rel`), alle Sounds auflisten,
      **abspielen**, als WAV **exportieren** (einzeln/alle), **ersetzen** (WAV), **bearbeiten** (Audacity bzw. WAV-Standardprogramm,
      Speichern übernimmt automatisch), ganzen Pack exportieren, Pack importieren (Hinzufügen), Sound-Einstellungen (.rel)
      als XML bearbeiten, Verknüpfung `audioNameHash` in vehicles.meta setzen.
- WAV-Import: beliebige WAV (Stereo/24 Bit/Float/Zusatz-Chunks) → Mono 16 Bit, Original-Samplerate und **Original-Länge**
  (sonst ungültig, v. a. granulare Motorsounds). AWC-Neuaufbau über CodeWalker-XML (ADPCM-Kodierung automatisch).
- FiveM-Export: `audioconfig/*.rel` + `sfx/<pack>/*.awc`, `data_file AUDIO_GAMEDATA/SOUNDDATA/SYNTHDATA/DYNAMIXDATA/…`
  ('audioconfig/x_game.dat' für x_game.dat151.rel) und `AUDIO_WAVEPACK 'sfx/<pack>'`.
- Getestet mit ox_doorlock-/patoche-Audiodateien (Kopie): Ersetzen mit Testton, REL-XML-Rundweg byte-genau.
  **Mit echtem Fahrzeug-Soundpack noch nicht getestet** (keiner auf dem Rechner).

### Stand 19.11.2026 – Replace → Addon, Namen ändern

- [x] **Replace → Addon** („🔁 In Addon umwandeln“ bei Fahrzeugen ohne vehicles.meta): Original-Fahrzeugdaten werden
      einmalig aus der GTA-Installation gelesen (`Vanilla.cs`: update.rpf, update2.rpf, 92 DLC-Packs; 935 Fahrzeuge,
      ~2 s; Cache `%APPDATA%\GTA Tool\gta-fahrzeugdaten.json`). Umwandeln: Dateien auf neuen Spawn-Namen umbenennen,
      vehicles.meta (Original-Eintrag + modelName/txdName/gameName/Marke, txdRelationships), carvariations.meta,
      eigene handling.meta (Kopie, optional), Motorsound des Originals bleibt (audioNameHash).
- Basisspiel-Variationen kommen aus `x64/data/carvariations.ymt` (PSO) → Namen per JenkIndex auflösen
  (`<id>_<modell>_modkit`), Format an .meta angleichen (char_array/bool), ungelöste Kits → `0_default_modkit`
  (51 Fahrzeuge), ungelöste Kennzeichen → „Standard White“.
- [x] **Namen ändern** („✏ Namen ändern“ bei Addons): Spawn-Name, Anzeigename, Marke – Dateien und Metas werden angepasst.
- Anzeigenamen eigener Fahrzeuge in `labels.json` der Sitzung → `client/names.lua` (AddTextEntry).
- Reine Sound-Packs werden als „Sound-Pack“ erkannt (keine falsche Replace-Meldung).
- Getestet: Test-Replace `elegy2` → `myelegy` (Export geprüft), Skyline umbenannt → `r34gtr`. **Im Spiel noch ungetestet.**

### Stand 19.11.2026 (2) – Clothing Studio

Start: Kachel „Kleidung“ oder `GTA Tool.exe --clothing-studio` (`Clothing Studio.lnk` in win-unpacked).
Eigene Sitzung je Studio (`sessions.vehicles/clothing` in main.js, `globalThis.sessionDir` = aktive Sitzung, `studio:set`).

- [x] Öffnen (Ordner/Dateien/Drag & Drop): erkennt Freemode-Kleidung (Komponenten + Requisiten), Geschlecht und Sammlung
      aus Dateinamen oder Ordnernamen (DLC-Ordner `mp_m_freemode_01_<sammlung>`); Dateien werden kanonisch benannt
      (`<ped>[_<sammlung>]^<teil>…`), damit m/w sich nicht überschreiben.
- [x] **3D auf dem Charakter**: Freemode-Körper (head/uppr/lowr/feet/hand _000 + Skelett) einmalig aus x64v.rpf
      (`%APPDATA%\GTA Tool\freemode\koerper`), Kleidung mit gewählter Texturvariante; Requisiten am Bone (SKEL_Head …).
- [x] **Position/Größe**: Regler X/Y/Z (±5 cm) + Größe (90–110 %), Live-Vorschau, „Übernehmen“ schreibt die YDD.
- [x] **Texturen**: Varianten anzeigen (auf Modell + Karten), Ersetzen (Paint.NET, Auto-Übernahme), Datei …, Export,
      neue Variante importieren (neue YTD mit nächstem Buchstaben), Variante löschen (Buchstaben rücken nach).
- [x] **UV-Vorlage** (UV0) in Paint.NET öffnen oder speichern.
- [x] **Blender**: Sollumz-Import mit Freemode-Skelett (`dwd_import_external_skeleton`), Körper als gesperrte Referenz,
      Export nur des Kleidungsstücks → automatische Übernahme (Bridge `--ydd --skeleton --body`).
- [x] **Männlich ⇄ weiblich**: `blender/clothing_convert.py` (headless, aus FiveM Asset Studio): Skelett-Retarget,
      Körperform-Anpassung, Haut-Abstand; legt neues Teil für das andere Geschlecht an (Texturen kopiert). ~9 s.
- [x] **Addon ⇄ Replace**: Addon → Replace mit wählbarer GTA-Nummer; Replace → Addon in Sammlung `mp_m_/mp_f_<name>`.
- [x] **Texturen zu Original-GTA-Kleidung**: Katalog aller 5540 Original-Teile (Basisspiel + DLCs, Patches beachtet),
      Browser mit 3D-Vorschau, Übernahme als Addon (mit/ohne Original-Texturen) → eigene Varianten hinzufügen.
- [x] **FiveM-Export**: stream (Modelle/Texturen), YMT je Sammlung (CPedVariationInfo per CodeWalker-XML, Rundweg
      geprüft), Shop-Meta (`SHOP_PED_APPAREL_META_FILE`), fxmanifest, LIESMICH; Nummern werden lückenlos gemacht.
- Getestet mit grzyClothTool-Westen (m/w) und GTA jbib 005. **Im Spiel noch ungetestet.** YMT-Neuaufbau verwirft
  Sonderflags der Original-YMT (Audio, Kleidungsphysik, Requisiten-Flags) – bei Bedarf später übernehmen.

### Stand 19.11.2026 (3) – Letzte Ordner merken

- [x] Alle Dialoge starten im zuletzt benutzten Ordner: `lastImportDir` (Ordner/Dateien/Bilder/WAV/YFT/YTD öffnen,
      auch Drag & Drop; bei gewählten Ordnern der Ordner darüber) und `lastExportDir`/`lastResourceDir` (Texturen, Sounds,
      UV-Vorlagen, Datei speichern, FiveM-Resources). Gespeichert in `%APPDATA%\GTA Tool\settings.json`.
- Testhinweis: Automatische Tests dürfen keine Temp-Pfade in den Einstellungen hinterlassen.

### Stand 19.11.2026 (4) – MLO Studio

Start: Kachel „MLO / Interiors“ oder `GTA Tool.exe --mlo-studio` (`MLO Studio.lnk`). Eigene Sitzung `sessions.mlo`.

- [x] Öffnen (Ordner/Dateien/dlc.rpf/Drag & Drop): erkennt MLO-Archetypen in .ytyp (Räume, Portale, Objekt-Sets),
      Platzierung in .ymap (CMloInstanceDef), Modelle, Texturdateien, Kollisionen.
- [x] **GTA-Objektkatalog** (`Props.cs`): 149 466 Archetypen aus allen Archiven (~4 s, `%APPDATA%\GTA Tool\gta-objekte.json`);
      Original-Objekte werden für die 3D-Ansicht bei Bedarf nach `gta-objekte\` geholt. Namen über Katalog-Hash aufgelöst.
- [x] **3D-Szene** (viewer3d `scene*`): Archetyp-GLB einmal laden, je Objekt klonen; fehlende Modelle = gelbe Boxen;
      Klick wählt Objekt, TransformControls (Verschieben/Drehen) speichern beim Loslassen sofort in die YTYP.
- [x] **Objekte**: Zahlenfelder Position/Drehung (Grad)/Größe, Duplizieren, Löschen (Raum-/Portal-Indizes nachgezogen),
      Einfügen (GTA-Objekte suchen, Paket-Modelle, eigenes .ydr + .ytd importieren → neuer Archetyp im YTYP),
      Raumfilter, Suche, Liste.
- [x] **Kollision anzeigen** (YBN → GLB, in MLO-Raum umgerechnet, auf Raumgrenzen ±1 m begrenzt), eigene Ansicht je .ybn.
- [x] **Texturen** (eingebettet in YDR und YTD): Vorschau, Ersetzen (Paint.NET), Datei …, Export PNG/DDS, Import.
- [x] **Blender** für .ydr und .ybn (Bridge `--file`, Export über Wurzelobjekt), automatische Übernahme.
- [x] **Platzierung in der Welt** (ymap): Position + Drehung, Extents werden neu berechnet.
- [x] **FiveM-Export**: stream + fxmanifest (`this_is_a_map 'yes'`).
- Drehungen: MLO-Objekte speichern sie invertiert, die ymap-Platzierung direkt (wie CodeWalker).
- Getestet mit ace_realestateagency (50 Objekte): verschieben/drehen per Zahlenfeld, Einfügen (GTA-Stuhl), Löschen,
  Export neu eingelesen (51 Objekte, Werte korrekt). Blender-Rundweg YDR/YBN headless geprüft. **Im Spiel ungetestet.**
- Bekannt: Kollisionen (.ybn) werden beim Verschieben des Interiors in der Welt nicht mitbewegt (Hinweis im Tool).

### Stand 19.11.2026 (5) – Fehlende Modelle (z. B. Türen) im MLO

- Nutzer-Meldung „Türen werden nicht angezeigt“ (mlo_cafecute): Die Innentüren (0x67F12F1D ×4, 0x4592D9BE) u. a.
  verweisen auf Modelle, die weder im Paket noch in GTA Legacy (alle 92 DLCs) noch auf der Platte liegen →
  stammen aus einer fremden Resource, im Spiel ebenfalls unsichtbar. Alle 1662 GTA-Türen laden korrekt.
- Neu: deutliche orange Platzhalter, Hinweis-Box mit Liste der fehlenden Modelle (Klick springt hin),
  **„Modell ersetzen …“** im Objekt-Panel (`mlo.swapArchetype`, optional alle gleichen Objekte, auch Objekt-Sets).
- Diagnose-Befehle: `debug.hashes` (Hashes in allen GTA-Archiven suchen), `debug.ymap`, `debug.ydr`.

### Stand 20.11.2026 – Animation Studio

Start: Kachel „Animationen“ oder `GTA Tool.exe --anim-studio` (`Animation Studio.lnk`). Eigene Sitzung `sessions.anim`.
Backend `Animation.cs` (Klasse `Animations`), Oberfläche `anim.js`, Rig-Export `Model3D.ExportRig`.

- [x] **Import**: Ordner/Dateien/Drag & Drop/RPF – `.ycd` und CodeWalker-/Sollumz-XML (`.ycd.xml` → wird zu .ycd).
      Erkennung je Clip: Personen-, Gesichts-, Objekt-, UV- oder Kamera-Animation, Dauer, Frames, Tempo, Laufweg.
- [x] **3D-Vorschau**: Freemode-Körper (m/w) als Skin-GLB mit Skelett (`rig2-<ped>.glb` in `%APPDATA%\GTA Tool\freemode`,
      Bones mit `extras.tag`, Wurzelknoten „GTA“ dreht Z→Y). Backend tastet Clips mit 30 fps ab (`anim.sample`, Float32 Base64),
      three.js `AnimationMixer`. Abspielleiste (Start/Pause, Zeitleiste, 0,1–2×, Wiederholen), Laufweg (Mover) mit Kamera-Folgen.
      Oberschenkel-Rollbones folgen wie in CodeWalker. Bind-Matrizen = CodeWalker (`TransformationsInverted` geprüft, identisch).
- [x] **Bearbeiten** (XML-Rundweg über CodeWalker): Clip umbenennen, duplizieren, löschen (unbenutzte Animationen fliegen raus),
      Tempo & Ausschnitt (Rate/Start/Ende), Clip in andere Datei kopieren oder neue Datei daraus, Datei umbenennen/löschen.
      Lua-Beispielcode je Clip zum Kopieren.
- [x] **Blender**: Bridge `--ycd <xml> --clip <name> --skeleton --body` – Körper mit Skelett, Sollumz-Import der YCD-XML,
      Ziel-Armature gesetzt, Clip-Action auf das Skelett gelegt (30 fps). „Speichern & zurück“ exportiert CWXML; übernommen wird
      **nur der bearbeitete Clip + seine Animation** (Rest der Datei bleibt unverändert). Länge wird bei Ganz-Clips mitgenommen.
      Headless geprüft (`--auto-export --test-edit`): nur der geänderte Kopf-Bone weicht ab.
- [x] **Export**: einzelne Datei als .ycd oder .ycd.xml; FiveM-Resource (stream/*.ycd, fxmanifest, LIESMICH mit allen
      Datei-/Clip-Namen, optional `client.lua` mit `/animtest <datei> <clip>` und `/animstop`).
- Getestet: cwalk (FiveM), GTA `move_m@generic` (66 Clips, Gang sieht korrekt aus), hug_dog, UV-Animation (MLO).
  **Im Spiel noch ungetestet.**
- Hinweis Sollumz: YCD nur als XML (kein Binär-Import/-Export).

### Stand 20.11.2026 (2) – GitHub, Erkennung, Updates

- Repo: https://github.com/TDIJunkie/GTA-TOOL (Branch `main`, CodeWalker als Submodul `vendor/CodeWalker`).
- [x] **Automatische Suche** (`app/detect.js`): GTA V (Rockstar-/Steam-/Epic-Registry, libraryfolders.vdf, Epic-Manifeste,
      Uninstall-Einträge, typische Ordner, begrenzte Laufwerkssuche), Blender (Program Files, Uninstall, .blend-Zuordnung,
      Steam, PATH, Ordner `*blender*` auf allen Laufwerken, Laufwerkssuche), Sollumz (Benutzer-/portable Add-on- und
      Extension-Ordner, Version aus `blender_manifest.toml`). Bevorzugt Blender ≥ 4.2 mit Sollumz. ~2–3 s im Hintergrund.
- [x] **Hinweis beim Start**, wenn etwas fehlt (mit Download-Links, „nicht mehr anzeigen“ = `setupDismissed`);
      Einstellungen zeigen „Gefundene Programme“ + „Erneut suchen“. Blender-Funktionen prüfen Blender **und** Sollumz (`requireBlender`).
- [x] **Updates** (`electron-updater`, GitHub-Releases): Prüfung beim Start, Download im Hintergrund, Dialog „Jetzt neu starten“;
      sonst Installation beim Beenden. Nur in der installierten Version (nicht win-unpacked).
- **Release veröffentlichen:** Version in `package.json` erhöhen → commit → `git tag vX.Y.Z` → `git push origin vX.Y.Z`.
  GitHub Actions (`.github/workflows/release.yml`) baut den Installer und lädt `GTA-Tool-Setup-X.Y.Z.exe` + `latest.yml` hoch.
  Der Workflow legt genau ein Release an (Setup-EXE + `latest.yml`, Text aus `.github/release-notes.md`), keine Blockmap
  (`differentialPackage: false` → Updates laden immer das ganze Setup). **Release nur nach Freigabe durch den Nutzer.**
- Wichtig: Für Updates bei anderen Nutzern muss das Repo (bzw. die Releases) **öffentlich** sein.

### Stand 20.11.2026 (3) – SFTP (Version 0.2.0, vom Nutzer getestet und veröffentlicht)

- [x] **Server verwalten** (`app/sftp.js`, `app/renderer/sftp.js`, Einstellungen → „SFTP-Server“): Host, Port, Benutzer,
      Passwort oder Schlüsseldatei (+ Passphrase), Startordner, „Verbindung testen“. Geheimnisse mit `safeStorage` (Windows-DPAPI)
      verschlüsselt in settings.json (`sftpServers`). Server-Schlüssel wird beim ersten Verbinden gemerkt; Änderung → Warnung, kein Verbinden.
- [x] **Import**: Knopf „🌐 Vom Server (SFTP)“ in allen vier Studios (Startfläche + „Hinzufügen“). Server-Browser mit Ordnern,
      Mehrfachauswahl (nichts gewählt = ganzer Ordner). Download in `%TEMP%\GTATool\sftp-*` (Ordnernamen bleiben erhalten), dann normal öffnen.
- [x] **Export**: im Export-Dialog „Ordner auf dem PC“ / „Server (SFTP)“. Export in Zwischenordner → Upload nach `<zielordner>/<resource>`;
      vorhandene Resource auf dem Server wird nach Rückfrage komplett ersetzt. Letzter Server/Ordner wird gemerkt (`sftpLast`).
- Bibliothek `ssh2-sftp-client` 11.0.0 (reines JS, `npmRebuild: false`).
- Getestet mit eigenem lokalen Test-SFTP-Server (ssh2): falsches Passwort, Auflisten, Download/Upload byte-genau, neue Zielordner,
  Überschreiben, Host-Key-Wechsel, Verbindungsabbruch mitten im Download (Neuversuch); kompletter Ablauf über die Oberfläche.
  Übertragung schonend (4 parallele Anfragen), bei Fehler Neuverbindung + sequenziell (Fix für „No response from server“ beim Nutzer).
  Schlüsseldatei-Anmeldung noch ungetestet.
- [x] **Fix 21.11.2026 – Original-Resource bleibt erhalten** (Nutzer: MLO vom Server, Textur geändert, hochgeladen → kaputt).
      Ursache: MLO-/Prop-Export baute eine neue Minimal-Resource (nur flaches `stream/` + eigenes fxmanifest) und der Upload löschte
      die Server-Version → `fxmanifest.lua`, `client.lua`, Metas, Unterordner (`stream/ytyp/…`) weg. Jetzt: `Session.Open` sichert beim
      Öffnen eines Ordners alle Nicht-stream-Dateien nach `original/<quelle>` (+ `originals.json`); `Session.ExportPreserving` stellt die
      Resource 1:1 wieder her und legt Sitzungsdateien an ihren Ursprungspfad (Index-Origin), neue nach `stream/`, gelöschte fehlen.
- [x] **Alle Studios: Original erhalten + Abgleich-Upload** (Wunsch Nutzer 21.11.2026):
      `ExportMerge.Run` (Program.cs) umschließt alle fünf Exporte: Studio exportiert in Zwischenordner, danach Einsortieren in die
      Original-Resource (Sitzungsdatei → Index-Origin, sonst gleicher Dateiname, sonst neu; Original-Dateinamen exakt). fxmanifest bleibt,
      nur Einträge für wirklich neue Dateien werden ergänzt; erzeugte .lua/LIESMICH werden nicht übernommen. Resources mit .rpf: alter Weg.
      Name exakt (`ExportMerge.ResName` = `safeResourceName` in main.js: nur ordner-unzulässige Zeichen ersetzt, Groß/klein bleibt);
      Dialoge schlagen den Original-Namen vor (`session:original`, `Sftp.originalName/keepNotice`).
      SFTP-Upload (`sftp.upload`): Stand je Server-Datei (Größe, mtime, MD5) aus Import/Upload in `%APPDATA%\GTA Tool\sftp-stand.json`;
      nur neue/geänderte Dateien hochladen (unklar → `md5sum` per SSH, sonst hochladen), beim Import bekannte und jetzt fehlende Dateien
      löschen, unbekannte Server-Dateien nicht anfassen (werden gemeldet). Vorher Sicherung nur der überschriebenen/gelöschten Dateien
      nach `%APPDATA%\GTA Tool\server-sicherungen` (letzte 5 je Resource); scheitert sie, wird nichts verändert.
      Getestet: Import per Test-SFTP → Änderung/Löschen/Neu → Upload 2 Dateien + 1 gelöscht, 209 übersprungen; zweiter Upload 0;
      unveränderter Rundlauf aller Studios (diabli, jobwesten, cwalk, mlo_cafecute) ohne Verluste. Kleidung erzeugt .ymt/.meta immer neu.

### Stand 20.11.2026 (4) – Prop Studio (Version 0.3.0)

Start: Kachel „Props / Objekte“ oder `GTA Tool.exe --prop-studio` (`Prop Studio.lnk`). Sitzung `sessions.prop`.
Backend `PropPack.cs`, Oberfläche `props.js`.

- [x] **Import**: Ordner/Dateien/Drag & Drop/RPF/SFTP – .ydr, .yft (+ _hi), .ytd, .ybn, .ytyp. Zuordnung Modell ↔ Archetyp
      (name/assetName) ↔ Texturdatei (txd bzw. gleichnamige .ytd) ↔ eigene .ybn. Hinweise: Archetypen ohne Modell, MLO im Paket.
- [x] **Ansicht**: 3D (Modell / Kollision umschaltbar; Kollision auch eingebettet aus .ydr/.yft – `Mlo.LoadBounds`),
      Größe, Kollision ja/nein, Sichtweite, Texturdateien; Lua-Spawn-Code zum Kopieren.
- [x] **Archetypen**: fehlende automatisch anlegen (vorhandene Nicht-MLO-.ytyp oder neue `<name>.ytyp`; BBox aus dem Modell,
      Fragment/Drawable, Sichtweite 150 m), beim Export immer automatisch. Sichtweite ändern.
- [x] **Bearbeiten**: Umbenennen (Modell, _hi, .ybn, eigene .ytd, Archetyp), Löschen (Textur nur wenn ungenutzt), Modell ersetzen.
- [x] **Texturen**: eingebettet + .ytd – Bearbeiten (Paint.NET, Auto-Übernahme), Datei …, Export PNG/DDS, Importieren, Löschen.
- [x] **Blender** (.ydr/.yft/.ybn) über `mlo:blender` mit Parameter `studio: 'prop'`.
- [x] **Aus GTA-Objekt erstellen**: Katalogsuche + 3D-Vorschau, Kopie unter eigenem Namen (Modell + .ytd) + Archetyp.
- [x] **Export**: stream + `data_file 'DLC_ITYP_REQUEST' 'stream/x.ytyp'`, LIESMICH, optional `/spawnprop <name>` und `/delprops`; SFTP-Ziel.
- Getestet: Backend (Archetyp anlegen in vorhandener und neuer .ytyp, umbenennen, Sichtweite, löschen, Export),
  Oberfläche (GTA-Stuhl übernehmen, 3D, Kollision, eigenes Modell ohne Archetyp, Export). **Im Spiel noch ungetestet.**
  Blender für Props nicht erneut getestet (gleicher Weg wie MLO-.ydr). Archetyp-Flags = 0.
- [x] **Neues Prop in Blender** („✨ Neu in Blender“): Bridge `--new-prop <name>` startet mit Würfel „Form“ (Ursprung unten),
      Anleitung im Panel. „Speichern & zurück“: sichtbare Meshes werden kopiert (Modifikatoren angewendet, Weltlage eingebacken,
      Materialien kopiert, Farbe ohne Bild → 16×16-Farbtextur, fehlende UVs → Smart UV, „UVMap 0“ + weiße „Color 1“),
      Sollumz `autoconvertmaterials` + `converttodrawable` mit eingebetteter Kollision (Material CONCRETE), Export NATIVE .ydr;
      Kopien werden danach wieder gelöscht → beliebig oft zurückschicken. Sollumz nimmt den Texturnamen aus dem Bild-Dateinamen
      und bettet nur DDS ein → Bilder werden nach `out\texturen\<name>.png` gelegt, Nodes bekommen Bild-Kopien mit diesem Pfad.
      Backend `prop.applyNew`: .ydr ins Paket, Bilder → `<name>.ytd` (texconv), Archetyp anlegen/aktualisieren (BBox, txd).
      Headless getestet (`--auto-export --test-model`): Bevel-Würfel rot + Zylinder → 2 Texturen, Kollision, Archetyp, 3D korrekt.

### Stand 20.11.2026 (5) – Neues Interior (MLO) mit Karte und Blender (Version 0.3.0)

- [x] **Karte** (`MapView.cs`, `map.build` → `%APPDATA%\GTA Tool\karte2.glb`): Radar-Geometrie aus
      `x64e.rpf\levels\gta5\minimap.rpf\*.ydd` (65 Kacheln, ~145 000 Dreiecke, echte Weltkoordinaten, Vertexfarben, unbeleuchtet).
      Höhe der Geometrie bleibt als Ebenen-Reihenfolge erhalten (sonst überdecken Grundflächen die Straßen).
      Viewer-Kartenmodus (`mapShow/mapHide/mapSetMarker/mapOn`): Draufsicht, Norden oben, Ziehen = verschieben, Klick = Position,
      Marker = Grundriss (Breite × Tiefe, Drehung) + Nadel.
- [x] **Bodenhöhe** (`map.height`): heightmap.dat (update.rpf, sonst common.rpf), CodeWalker `HeightmapFile`, Zeile von BBMin.Y
      aus (geprüft: Chiliad 800 m, Flughafen 16 m, Vinewood-Schild 349 m). Vorschlag = Min-Höhe der Zelle (Max = Dächer).
- [x] **Dialog „✨ Neues Interior“** (MLO Studio: Startfläche, Übersicht, Hinzufügen): Name, Karte, X/Y/Z, Drehung, Größe,
      Koordinaten einfügen (`vector3(...)`).
- [x] **Blender-Modus** `--new-mlo <name> --pos x,y,z --heading h --size w,d,h --doors tueren.json --blend-save …`:
      Vorlage (Boden, 4 Wände mit Türöffnung, Decke) in Sammlungen „Gebäude“, „Räume“ (Draht-Boxen), „Portale“ (Rahmen),
      „Türen“ (Platzhalter in Originalgröße, Drehpunkt = Scharnier, Unterkante auf Boden; Liste aus dem Objektkatalog `mlo.doorList`).
      Panel-Knöpfe: Raum / Portal hinzufügen, Tür einfügen. Export: Hülle → `<name>_shell.ydr` (+ Texturen), Kollision →
      `<name>_col.ybn` **in Weltkoordinaten** (Platzierung + Drehung eingebacken), `mlo.json` (Räume = Welt-BBox, Portale = 4 Ecken in
      GTA-Reihenfolge, Raum-Zuordnung über Normale ±0,3 m, Türen an nächstes Portal). Blend wird unter
      `%APPDATA%\GTA Tool\blender-quellen\<name>.blend` gespeichert → „In Blender weiterbearbeiten“ (öffnet die .blend).
- [x] **Backend `MloCreate.ApplyNew`**: YTYP (Hülle-Archetyp + MLO mit limbo + Räumen, Portalen, Türen, Timecycles wie
      funktionierendes Custom-MLO) und YMAP (CMloInstanceDef) per CodeWalker-XML → binär; Drehung + Extents über `Mlo.SetPlacement`.
      Vom Tool erzeugte Objekte haben GUIDs `0x6A7A0000+i`; alle anderen (im MLO Studio eingefügten) bleiben beim erneuten Zurückschicken erhalten.
- Getestet headless: Vorlage + Anbau (2 Räume, 2 Portale, 1 Tür), Kollision liegt am Flughafen in Weltkoordinaten,
  MLO Studio liest es ein (3D, Räume, Platzierung), eingefügter Stuhl bleibt beim Neuaufbau erhalten; Karte/Klick/Höhe/Einfügen über die Oberfläche.
  **Blender-Fenster selbst (Panel, Knöpfe) und das Interior im Spiel sind ungetestet.** Wird die Platzierung im MLO Studio geändert,
  muss die Kollision über „In Blender weiterbearbeiten“ neu erzeugt werden.

### Stand 20.11.2026 (6) – 3D-Platzieren in der echten GTA-Welt (Version 0.3.0)

- [x] **Welt-Index** (`World.cs`, `world.build` → `%APPDATA%\GTA Tool\gta-welt.json`): alle YMAPs mit HD-/ORPHANHD-Objekten
      (x64*.rpf, dlcpacks, update.rpf – spätere ersetzen gleichnamige), Bereich je YMAP. 6 222 YMAPs, ~15 s einmalig.
- [x] **Umgebung** (`world.area`): HD-Objekte im Umkreis (60–300 m, max. 1 500, nächste zuerst), tatsächliche Ausrichtung über
      CodeWalker (`YmapEntityDef.Orientation`), Duplikate (gleiche Stelle aus mehreren YMAPs) gefiltert. Modelle über den Objektkatalog
      als GLB mit 128-px-Texturen (`Model3D.TextureLimit`), Cache `%APPDATA%\GTA Tool\gta-objekte-glb\`; .ydd-Pakete übersprungen.
- [x] **Viewer-Weltmodus** (`worldShow/worldAdd/worldSetObject/worldModeSet/worldSnap/worldSetValues/worldHide`): alles relativ zum
      Ursprung, Objekt = rote Box (neues Interior, Pfeil = Eingang) oder Klon der geladenen MLO-Szene; TransformControls
      (Verschieben; Drehen nur um die Hochachse), „Auf den Boden setzen“ per Strahl nach unten.
- [x] **Dialog** `placement.js` (`WorldPlace.open`): Umkreis, Modus, Boden, Zahlenfelder in beide Richtungen.
      Eingebunden in „Neues Interior“ („🎯 In 3D genau platzieren“) und im Platzierungsbereich bestehender Interiors
      („🎯 In der Welt platzieren (3D)“ → speichert YMAP; bei Tool-Interiors Hinweis, die Kollision über Blender neu zu erzeugen).
- Getestet (unsichtbar): Index + Umgebung am Flughafen in 16 s (643 Objekte), Boden-Einrasten, Drehung 75° gespeichert.
  Viele Umgebungsobjekte erscheinen ohne Textur (gemeinsame GTA-Texturdateien werden nicht geladen) – reicht zur Orientierung.

### Offen / nächste Schritte

- [ ] Animation Studio: neue Animation in Blender von Grund auf erstellen; Clip-Tags/Eigenschaften bearbeiten;
      Objekt-/UV-Animationen in 3D (am passenden Modell)

- [ ] Spieltest der exportierten Resource im FiveM-Client (mit dem Nutzer)
- [ ] Test mit einem echten Fahrzeug-Soundpack
- [ ] Meta-Editor (Handling-Werte, Namen) und Reparatur fehlerhafter Metas
- [ ] 3D-Vorschau im Tool
- [ ] Textur-Verkleinerung (für >16-MB-Dateien)
- [ ] Kleidung, Waffen, Props & Maps

### Testhinweis

App mit `--remote-debugging-port=9333` starten und per DevTools-Protokoll steuern – **niemals Fenster in den
Vordergrund holen** (der Nutzer spielt evtl. gerade).
Im versteckten Fenster (`GTA_TOOL_HIDDEN=1`) zeichnet requestAnimationFrame kaum neu → vor jedem CDP-Screenshot
`Viewer3D.renderNow()` aufrufen, sonst zeigt das Bild einen veralteten Stand. Nach Tests `settings.json` (lastImportDir) zurücksetzen.
