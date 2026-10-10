# HDR Calibrate 1.7.3

Portables WPF-Tool für Windows 11 und .NET Framework 4.8.1. Es passt bei
aktiviertem Windows-HDR ausschließlich die von DWM zusammengesetzten
SDR-Flächen an. Anwendungen werden nicht gehookt und Windows-Dateien werden
nicht verändert.

**Neu in 1.7.3:** Die globale SDR-Korrektur ändert zusätzlich die beiden
FL10-SDR-Exporte (jetzt sechs Exporte und 24 Konstantenvektoren). Diese
FL10-Erweiterung ist noch nicht live abgenommen. Nach einer rückwärts
gestellten Systemuhr gilt die Neustartsperre höchstens zehn Sekunden ab ihrer
ersten Erkennung statt bis zum Erreichen des alten Zeitstempels. In der
Programmliste sind Test und Erzwingen auf einem nicht freigegebenen DWM-Build
von vornherein gesperrt; über Junction, Symlink oder `subst` ausgewählte EXEs
werden erkannt. Der Installer ist jetzt ein eigenes WPF-Setup ohne Inno Setup;
er übernimmt bestehende Installationen und entfernt bei der Deinstallation auch
native Ordner früherer Versionen. Nach dem Versionswechsel ist ein neuer Test
erforderlich.

**Seit 1.7.2:** Nach dem 5-Sekunden-Test zeigt **Anwenden** die noch offene
10-Sekunden-Neustartsperre als Countdown. Sobald diese Schutzpause abläuft,
wird die Schaltfläche wieder freigegeben. Ein zu früher Versuch verwirft
den erfolgreichen Test nicht mehr; echte Aktivierungsfehler und Änderungen
der Windows-Identität sperren weiterhin sicher.

## Unterstützte Einstellungen

- SDR-Zielgamma von 1,8 bis 2,6, einschließlich der Presets 2,0, 2,2, 2,4
  und 2,6
- SDR-Helligkeit von -20 bis +20 Prozent
- Kontrast, Sättigung und RGB-Balance sind bewusst deaktiviert: Eine sichere
  Änderung dieser Werte ist mit dem ABI-erhaltenden Konstantenpatch nicht
  möglich.

Die DWM-Korrektur betrifft nur den Pfad `SDR -> lineares scRGB`. Die nativen
HDR-Shader und HDR-Inhalte bleiben durch diesen Patch unverändert. Independent Flip und bestimmte
MPO-Pfade können DWM umgehen; entsprechende SDR-Inhalte erhalten dann keine
Korrektur.

## Programme: SDR-Gamma für laufende Anwendungen erzwingen (experimentell)

**Windows-Update-Hinweis für 1.7.1:** Die globale SDR-Korrektur unterstützt
jetzt `dwmcore.dll 10.0.26100.9278`. Das zusätzliche Erzwingen in der
Programmliste ist auf diesem Build noch gesperrt: Seine privaten DWM-Adressen
sind unabhängig vom Shaderpfad und benötigen eine eigene Freigabe. Die App
zeigt den Grund vor dem Start eines Helfers an; eine erfolgreiche globale
Gamma-Aktivierung wird dadurch nicht als fehlgeschlagen behandelt. Die
nachfolgende Beschreibung gilt nur für einen separat freigegebenen nativen
Programm-Pfad. Browser-Startprofile bleiben davon unabhängig nutzbar.

Über **Programme…** oben rechts öffnet sich die gespeicherte Programmliste.
Mit **EXE hinzufügen…** lassen sich beliebige vorhandene lokale Programme
aufnehmen, beispielsweise die tatsächlich laufende Spiel-EXE von Heroes of
the Storm. Der Dateiname entscheidet nicht über die Kompatibilität: Nur vom
DWM-Helfer bestätigte SDR-Flächen sind für das Erzwingen zugelassen.

1. Im Hauptfenster die gewünschten SDR-Werte **5 Sekunden testen** und danach
   **Anwenden** wählen. Diese bestätigte globale Korrektur muss aktiv sein.
2. Das Zielprogramm selbst starten und sein Fenster nicht minimieren.
3. In der Programmliste **SDR-Gamma-Erzwingen für dieses Programm erlauben**
   einschalten und **Profil speichern** wählen.
4. **Gamma 5 s testen** prüft den selektiven SDR-Pfad. Nach fünf Sekunden wird
   nur das Erzwingen zurückgenommen; das Spiel und die globale Gamma-Korrektur
   bleiben bestehen. Die Vorprüfung und abschließende Rückmeldung dauern zusätzlich.
5. **Gamma erzwingen** erlaubt anschließend eine überwachte Sitzung für genau
   denselben Prozess, dasselbe Fenster und dieselben Gammawerte.
6. **Erzwingen stoppen** oder das Schließen der Programmliste beendet sie.

Es gilt das **bereits aktive Gamma des Hauptfensters**, nicht ein eigener
Gammawert pro Eintrag. Andere bereits von DWM komponierte SDR-Inhalte erhalten
weiterhin die globale Korrektur. Es kann jeweils **ein Programmfenster**
erzwungen werden. Bei mehreren passenden Fenstern wird deterministisch das
größte sichtbare Fenster gewählt; dessen Titel und EXE erscheinen im Status.

Die neue Funktion beeinflusst nur den DWM-Auswahlpfad für bestätigte
SDR-Bypass-Flächen. Sie injiziert nichts in Anwendungen und schreibt weder
Spiel- noch Windows-Dateien. PQ/HDR, lineares scRGB und unbekannte Metadaten
werden durchgelassen, nicht pauschal als SDR behandelt. Ein Browserfenster,
das SDR und HDR bereits in einer scRGB-Fläche mischt, kann damit nicht
SDR-selektiv korrigiert werden.

Gespeicherte Häkchen aktivieren **nichts automatisch**, auch nicht beim
Windows-Start. Programm-/Fensterwechsel, ein neuer DWM-Prozess, geänderte
Gammawerte oder Windows-Identität erfordern einen neuen Test. Die bestehende
globale Windows-Update-Sperre bleibt erhalten. Jede native Freigabe ist auf
höchstens fünf Sekunden begrenzt; nur ein lebender, bestätigter App-/Helferkanal
kann dieselbe Sitzung verlängern. Auch die Bedienoberfläche muss Heartbeats
beantworten; eine blockierte UI oder unbeantwortete UAC-Abfrage hält keine
unbegrenzte Sitzung offen. Ein bekannter Surface- oder Farbraumwechsel
widerruft sie; kein automatisches Wiederanlernen.

Die nativen Komponenten werden versionsgetrennt unter `Native/1.7.3` ausgeliefert
und vor jeder Sitzung gegen im App-Build eingebettete SHA-256-Werte geprüft.
Eine bereits geladene andere Hook-Version sperrt einen konkurrierenden Start.
Die DLL bleibt nach Stop passiv bis zum regulären Ende dieser DWM-Sitzung
geladen; das Programm erzwingt hierfür keinen Neustart. Das derzeitige native
Sicherheitsbudget erlaubt 16 Test-/Aktivierungssitzungen je DWM-Prozess.

Ein erfolgreicher Auswahltest bestätigt Identität, SDR-Veto und Rücknahme,
**nicht** die optisch korrekte Gammakurve, VRR oder niedrigste Latenz.
Diese Praxisprüfung bleibt ausdrücklich offen.

## Separate Browser-Startprofile

Für Brave, Google Chrome, Microsoft Edge und Chromium stehen zusätzlich
Farbraum-Startprofile bereit. Sie sind unabhängig von der neuen Gamma-Option;
andere Anwendungen erhalten keine Browserparameter und werden selbst gestartet.

Pro Eintrag stehen drei Startprofile zur Auswahl:

- **SDR / sRGB erzwingen:** setzt `--force-color-profile=srgb`. Das kann HDR-
  und Wide-Gamut-Inhalte in dieser Anwendung einschränken. Die DWM-Korrektur
  muss separat aktiv sein; Overlay-Pfade können sie weiterhin umgehen.
- **scRGB / HDR-fähig festlegen:** setzt `--force-color-profile=scrgb-linear`.
  Das kann den Farbraumwechsel vermeiden, aber die DWM-SDR-Gammakorrektur
  greift für diese Ausgabe nicht mehr. Die Helligkeitszuordnung kann sich ändern.
- **Standard:** startet ohne zusätzlichen Farbraum-Parameter.

Nach **Profil speichern** startet **Mit gespeichertem Profil starten** die
ausgewählte EXE. Bei einem erzwungenen Profil ist eine ausdrückliche Bestätigung
des Hinweises erforderlich. Laufende Browser müssen vorher selbst vollständig
beendet werden, da eine bestehende Instanz neue Startparameter ignorieren kann.
Das Tool beendet keine fremden Prozesse. Auch wenn HDR Calibrate als
Administrator läuft, ist der Programmstart möglich: Die App übergibt ihn an
den Windows-Desktop/Explorer. Bei normal laufendem Explorer startet der Browser
dadurch ebenfalls ohne Administratorrechte. Ist UAC deaktiviert und läuft auch
der Desktop erhöht, sind diese Starts ebenfalls erlaubt; der Browser übernimmt
dann die Rechte des Desktops. Das Tool ändert keine UAC- oder Sicherheitseinstellungen.
Scheitert die Übergabe, wird kein zweiter Startversuch ausgelöst.
Grundlage: [Microsofts Start über Explorer](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643).

Die Browser-Startprofile gelten **nur bei einem Start aus dieser Liste**, nicht bei normalen
Verknüpfungen, Links oder anderen Programmstarts. Es gibt keine Prozess-Hooks,
keine systemweite Startumleitung und keinen automatischen Browserstart. Die
Liste wird unter `%LOCALAPPDATA%\HDRCalibrate\program-profiles.xml` gespeichert;
beschädigte Listen werden nicht stillschweigend überschrieben. DWM-Korrektur
und Windows-Update-Sperre werden durch diese Startprofile nicht verändert.

Die Kombination „beliebiges SDR-Gamma erzwingen und HDR derselben gemischten
Browserausgabe garantiert unverändert lassen“ ist damit weiterhin nicht möglich.

## Bekannte Probleme

### HotS, andere Spiele, VRR und niedrigste Latenz: Praxisprüfung offen

Der selektive Ansatz ist jetzt mit der aktiven Gamma-Korrektur und der
Programmliste verbunden, bleibt aber experimentell. Ein erfolgreiches natives
SDR-Veto beweist allein noch nicht, dass die korrigierten Shader tatsächlich
für das sichtbare Spielbild verwendet werden. Ein bereits unabhängiger
Flip-Pfad lässt sich möglicherweise nicht allein durch das Candidate-Veto
zurückholen. Es gibt kein Capture-Overlay, kein globales MPO-Abschalten und
keine Änderung am Spiel zur Umgehung dieser Grenze.

Funktionierendes VRR, optisch unveränderte HDR-Ausgabe und niedrigste Latenz
sind für den neuen integrierten Pfad nicht nachgewiesen. Der Nutzer übernimmt
den VRR-Praxistest. Bisherige Labormessungen und deren Grenzen stehen im
[Testbericht](https://github.com/Oblivionevil/HDRCalibrate/blob/v1.6.1/tools/SelectiveDwm/TEST-RESULTS-20260906.md).

### Chromium-Browser: zeitweise wechselndes Gamma

In Chromium-basierten Browsern wie **Brave, Chrome und Edge** kann das Gamma
innerhalb des Fensters gelegentlich zwischen korrigiertem und ursprünglichem
Aussehen wechseln. HDR- oder Wide-Gamut-Inhalte können den Browser dazu bringen,
das gesamte Fenster bereits als lineares scRGB auszugeben; auch Inhalte in
anderen Tabs können daran beteiligt sein. Die sRGB-Linearisierung findet dann
schon im Browser statt. Der von HDR Calibrate geänderte DWM-Konverter
`SDR -> scRGB` wird für diese Ausgabe nicht mehr verwendet, sodass SDR-Inhalte
zeitweise unkorrigiert erscheinen können, obwohl der DWM-Patch weiterhin aktiv
ist.

Das ist eine bekannte Einschränkung des DWM-only-Ansatzes und derzeit nicht
behoben. In einer bereits vom Browser zusammengesetzten SDR/HDR-Fläche lassen
sich SDR-Anteile nicht zuverlässig allein anhand der Pixelwerte unterscheiden.
Eine pauschale Gamma-Nachkorrektur könnte deshalb auch HDR-Inhalte verändern.
HDR Calibrate wendet eine solche Nachkorrektur nicht an und hookt keine Browser.

Siehe die Hinweise der Ursprungsprojekte:
[dwm_eotf – Known issues](https://github.com/ledoge/dwm_eotf#known-issues) und
[dwm_eotf_rs – Known Issues](https://github.com/SERGEYDJUM/dwm_eotf_rs#known-issues).

## Bedienung und Sicherheit

1. Windows-HDR einschalten und die App starten.
2. Gamma und optional die SDR-Helligkeit einstellen.
3. **5 Sekunden testen** wählen. Vor jeder Änderung wird ein unabhängiger,
   erhöhter Watchdog gestartet.
4. Der Test setzt DWM nach fünf Sekunden immer ungepatcht neu auf und prüft
   Prozessidentität, Modulpfad und alle Originalshader.
5. Nur nach einem erfolgreichen Test wird **Anwenden** für exakt dieselben
   Reglerwerte in der laufenden App-Sitzung freigeschaltet.
6. **DWM-Korrektur entfernen** startet DWM mit den unveränderten
   Originalshadern neu.

Der Bildschirm kann beim Start und Ende eines Tests beziehungsweise beim
Anwenden kurz schwarz werden. Ein DWM-Neustart entfernt die laufende
Korrektur. Manuell muss sie danach erneut getestet und angewendet werden; die
separat freigegebene Startaktivierung darf ausschließlich die zuvor exakt
geprüften Werte erneut anwenden. Nach einer App-, Helfer- oder kompatibilitäts-
relevanten Windows-Aktualisierung ist eine neue Prüfung erforderlich. Die App
richtet keinen ungefragten Autostart ein und verwendet keine
herstellerspezifische Software.

## Installer, Tray und Autostart

- Der Installer ist ab 1.7.3 ein eigenes WPF-Programm (.NET Framework 4.8.1,
  x64, Deutsch/Englisch nach Windows-Anzeigesprache). Er benötigt keine
  Administratorrechte und installiert fest pro Benutzer nach
  `%LOCALAPPDATA%\Programs\HDR Calibrate`. Eine bestehende Installation des
  früheren Inno-Setup-Installers wird im bisherigen Ordner übernommen,
  einschließlich Autostart, Verknüpfungen und Eintrag unter „Apps“.
- Alle Anwendungsdateien sind im Setup eingebettet und werden vor dem Kopieren
  per SHA-256 geprüft. Eine laufende Instanz wird über ihren eigenen
  Beenden-Pfad geschlossen, nie erzwungen beendet. Schlägt das Ersetzen einer
  Datei fehl, stellt das Setup alle vorherigen Dateien wieder her.
- Desktop-Verknüpfung und **Mit Windows starten (im Tray)** sind optional und
  bei einer Erstinstallation abgewählt; bei einem Update spiegeln sie den
  aktuellen Zustand wider.
- Die Deinstallation („Apps“ oder `HDRCalibrate.Setup.exe --uninstall` im
  Programmordner) entfernt nur selbst installierte Dateien, den eigenen
  Autostart-Eintrag und die Verknüpfungen. Einstellungen unter
  `%LOCALAPPDATA%\HDRCalibrate` bleiben erhalten; eine noch von DWM geladene
  native Gamma-DLL bleibt ohne Neustartanforderung mit ihrer Lizenz liegen.
- Autostart lässt sich jederzeit oben rechts in der App oder über das
  Tray-Menü umschalten. Der eigene HKCU-Run-Eintrag enthält den vollständig
  gequoteten Programmpfad und `--tray --startup`. Ein alter eigener
  `--tray`-Eintrag wird erkannt und beim erneuten Aktivieren sicher migriert.
- `--tray` startet genau eine Instanz ohne sichtbares Hauptfenster. Ein normaler
  zweiter Start öffnet die bereits laufende Instanz.
- Das Fenster-X verbirgt die App im Infobereich. **Beenden** im Tray beendet
  sie vollständig; während eines sicherheitskritischen DWM-Vorgangs wird das
  Beenden bis zu dessen Abschluss aufgeschoben.

Die separate Option **SDR-Korrektur beim Windows-Start aktivieren (UAC)** ist
standardmäßig aus. Sie lässt sich erst einschalten, nachdem exakt dieselben
Werte erfolgreich fünf Sekunden getestet und anschließend manuell angewendet
wurden. Die Freigabe ist an Reglerwerte, App- und Helferversion, die exakte
`dwmcore.dll` sowie den geprüften Byte-Schreibplan gebunden. Ohne diese Freigabe
bleibt `--startup` ein read-only No-op und startet keinen erhöhten Helfer.

### Windows-Updates heben die Startfreigabe auf

Zusätzlich gehören der Windows-Build, die Update-Revision (UBR), BuildLabEx
und die tatsächlichen SHA-256-Dateihashes von `dwm.exe` und `dwmcore.dll` zum
Testnachweis. Ändert sich auch nur einer dieser Werte oder lässt sich die
Identität nicht vollständig lesen, wird nicht automatisch angewendet. Die
Freigabe wird dauerhaft widerrufen; auch ein späteres Windows-Rollback
aktiviert sie nicht wieder. Alte Testnachweise ohne diese Identität werden
nicht übernommen. Der gesperrte Zustand wird in der Oberfläche angezeigt.

Erst ein neuer erfolgreicher Fünf-Sekunden-Test, anschließendes manuelles
Anwenden und erneutes ausdrückliches Einschalten der Startaktivierung erzeugen
eine neue Freigabe. Die Identität wird auch nach der UAC-Abfrage im erhöhten
Helfer, vor dem Patch und vor dem Zustands-Commit erneut geprüft.
Zusätzlich wird die genaue freigegebene Richtliniendatei per SHA-256 bis zum
Schreibvorgang überwacht: Widerruf, Ersetzung oder Löschung während einer
offenen UAC-Abfrage brechen die Aktivierung ab.

Bei aktivierter Startkorrektur zeigt Windows nach der Anmeldung eine
UAC-Abfrage. Vor dem Patch startet ein unabhängiger erhöhter Watchdog. Erst wenn
App, Patch-Worker und Watchdog dieselbe DWM-Prozessidentität und denselben
Schreibplan bestätigt haben, bleibt die Korrektur aktiv. Fehlt der atomare
Zustands-Commit, startet der Watchdog DWM ungepatcht neu und die Startkorrektur
wird für weitere Anmeldungen deaktiviert. Native HDR-Inhalte bleiben auch in
diesem Pfad unverändert.

EXE, Fenster, Tray, Setup und Verknüpfungen verwenden dasselbe
Multi-Resolution-App-Symbol.

## Technischer Fix gegenüber 1.2.0

Version 1.2.0 ersetzte vier komplette Windows-Shaderfunktionen. Dabei gingen
Downlevel-Metadaten verloren und eine undurchsichtige Funktion erhielt eine
falsche `float3`- statt `float4`-ABI. Das konnte den Desktop fast vollständig
schwarz und fragmentiert darstellen.

Version 1.3.0 kompiliert oder ersetzt keine Shaderfunktion. Nach einer
vollständigen Hash-, Container-, Layout-, Linkage- und Reflection-Prüfung
werden nur vier vorhandene Konstantenvektoren je freigegebenem Shader geändert:

- Exponent `2,4` wird zum gewählten Zielgamma.
- sRGB-Knick `0,04045` und Offset `0,055` werden zu null.
- Die Skalierung wird aus der gewählten SDR-Helligkeit berechnet.

Ab Version 1.7.3 werden exakt sechs FL9.1/FL9.3/FL10-SDR-Exporte und 24
Konstantenvektoren geändert (bis 1.7.2: vier FL9.1/FL9.3-Exporte und 16
Vektoren). ABI, Slotgrößen, Chunkfolge, `Aon9`, `STAT`, `RDEF`, `LFS0` und
native HDR-Pfade bleiben unverändert. Die FL10-Erweiterung ist noch nicht live
abgenommen. Der
Laufzeitschreiber überträgt nur die tatsächlich abweichenden Bytebereiche und
liest sie sofort zur Kontrolle zurück.

## Fail-closed-Kompatibilität

Der globale SDR-Shaderpfad ist ausschließlich für die exakt geprüfte Systemdatei
freigegeben:

- `dwmcore.dll` Version `10.0.26100.9278 (WinBuild.160101.0800)`
- SHA-256 `40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222`

Die sechs SDR-Funktionscontainer sind unverändert. Die drei Bibliotheken
enthalten jetzt 133/133/101 Funktionen, das SDR-Exportpaar jeweils an Index
16/17. Neue Elternprüfsummen, diese Indizes und die vollständige D3D-Reflection
werden weiterhin exakt geprüft; es gibt keine automatische Hash-Übernahme.
Der separate native Programm-Hook bleibt auf seinem bisherigen privaten
`26100.8875`-Vertrag und wird auf `26100.9278` nicht gestartet.

Bei jedem anderen Windows-Build, Hash, Shaderlayout oder Exportmanifest bleibt
der Schreibpfad gesperrt. Ein Windows-Update kann daher eine neue, separat zu
prüfende Freigabe erfordern. Frühere Startfreigaben werden nicht übernommen:
Nach dem Update erneut fünf Sekunden testen und Startaktivierung ausdrücklich
einschalten. Ein Build bzw. ein lesender Preflight ersetzt keinen sichtbaren
Gamma-, HDR-Isolations- oder VRR-Test am tatsächlichen Monitor.

## Build und Tests

Zusätzlich zum .NET-SDK werden die Visual-Studio-C++-x64-Buildtools und ein
Windows-SDK benötigt. Der Build erzeugt den nativen Helfer und Hook aus dem
Quellcode, einschließlich des gepinnten MinHook-v1.3.4-Commits
`c3fcafdc10146beb5919319d0683e44e3c30d537`. Geänderte Abhängigkeitsquellen
werden abgewiesen; die nativen Dateien gehören zum vollständigen App-Paket.

Bei einem neuen Checkout die Quellabhängigkeit einmal bereitstellen:

```powershell
git clone --branch v1.3.4 --depth 1 https://github.com/TsudaKageyu/minhook.git artifacts/deps/minhook-v1.3.4
```

Der nachfolgende Build prüft zusätzlich den exakten Commit, nicht nur den Tag.

```powershell
dotnet build .\HDRCalibrate.sln -c Release -p:Platform=x64 -warnaserror
.\tests\HDRCalibrate.Tests\bin\x64\Release\net481\HDRCalibrate.Tests.exe
.\tools\Build-Installer.ps1 -Configuration Release
```

Das Installer-Buildskript benötigt keine fremde Installer-Toolchain: Es baut
die App, prüft x64-Format sowie native Hashes, bettet die zehn Paketdateien
samt SHA-256-Manifest in `HDRCalibrate.Setup.exe` ein und erzeugt aus derselben
Dateiliste das portable ZIP. Installer, ZIP und ihre `.sha256.txt`-Dateien
landen unter `installer\artifacts\output`.

Die automatisierten Tests verändern DWM nicht. Der reale Hardwaretest läuft
ausschließlich über den separaten Watchdog-Pfad und erzeugt Screenshots sowie
einen JSON-Bericht unter `artifacts\supervised-live-test-*`.

## Herkunft

Die DWM-Analyse baut auf öffentlich dokumentierter Arbeit von `dwm_eotf_rs`,
`dwm_eotf` und `dwm_lut_is_back` auf. Die Lizenz- und Quellenhinweise stehen in
`THIRD-PARTY-NOTICES.md`.
