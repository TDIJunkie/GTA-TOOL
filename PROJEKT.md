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
