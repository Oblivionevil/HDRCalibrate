# HDR Calibrate 1.6.1

Portables WPF-Tool für Windows 11 und .NET Framework 4.8.1. Es passt bei
aktiviertem Windows-HDR ausschließlich die von DWM zusammengesetzten
SDR-Flächen an. Anwendungen werden nicht gehookt und Windows-Dateien werden
nicht verändert.

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

## Programme: Farbraum gezielt beim Start erzwingen

Über **Programme…** oben rechts öffnet sich die gespeicherte Programmliste.
Mit **EXE hinzufügen…** lassen sich Brave, Google Chrome, Microsoft Edge und
Chromium aufnehmen. Andere Anwendungen werden nicht als kompatibel ausgegeben.

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

Die Profile gelten **nur bei einem Start aus dieser Liste**, nicht bei normalen
Verknüpfungen, Links oder anderen Programmstarts. Es gibt keine Prozess-Hooks,
keine systemweite Startumleitung und keinen automatischen Browserstart. Die
Liste wird unter `%LOCALAPPDATA%\HDRCalibrate\program-profiles.xml` gespeichert;
beschädigte Listen werden nicht stillschweigend überschrieben. DWM-Korrektur
und Windows-Update-Sperre werden durch diese Startprofile nicht verändert.

Die Kombination „beliebiges SDR-Gamma erzwingen und HDR derselben gemischten
Browserausgabe garantiert unverändert lassen“ ist damit weiterhin nicht möglich.

## Bekannte Probleme

### HotS, VRR und niedrigste Latenz: noch nicht bestätigt

Die Programmliste dieses Releases unterstützt Chromium-Browser, **nicht Heroes
of the Storm oder beliebige Spiele**. Der experimentelle Ansatz zur gezielten
DWM-Komposition steht separat unter [tools/SelectiveDwm](tools/SelectiveDwm/README.md)
im Quellcode. Er ist nicht mit der Gamma-Korrektur integriert und wird weder
vom Installer noch von der portablen App automatisch geladen.

Funktionierendes VRR, unveränderte HDR-Ausgabe und niedrigste Latenz bei einer
erzwungenen HotS-Korrektur sind nicht nachgewiesen. Die Veröffentlichung von
v1.6.1 wartet nicht auf einen weiteren VRR-Praxistest; sie gibt diese Funktion
aber auch nicht als fertig oder geprüft aus. Der Nutzer übernimmt den
VRR-Praxistest. Bisherige Labormessungen und deren Grenzen stehen im
[Testbericht](tools/SelectiveDwm/TEST-RESULTS-20260906.md).

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

- Der per-user Installer benötigt für die Hauptanwendung keine
  Administratorrechte und installiert standardmäßig nach
  `%LOCALAPPDATA%\Programs\HDR Calibrate`.
- Desktop-Verknüpfung und **Mit Windows starten (im Tray)** sind im Installer
  optionale, standardmäßig abgewählte Aufgaben.
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

Geändert werden exakt vier FL9.1/FL9.3-SDR-Exporte und 16
Konstantenvektoren. ABI, Slotgrößen, Chunkfolge, `Aon9`, `STAT`, `RDEF`, `LFS0`,
die beiden FL10-Varianten sowie native HDR-Pfade bleiben unverändert. Der
Laufzeitschreiber überträgt nur die tatsächlich abweichenden Bytebereiche und
liest sie sofort zur Kontrolle zurück.

## Fail-closed-Kompatibilität

Der Livepfad ist ausschließlich für die exakt geprüfte Systemdatei
freigegeben:

- `dwmcore.dll` Version `10.0.26100.8875 (WinBuild.160101.0800)`
- SHA-256 `825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3`

Bei jedem anderen Windows-Build, Hash, Shaderlayout oder Exportmanifest bleibt
der Schreibpfad gesperrt. Ein Windows-Update kann daher eine neue, separat zu
prüfende Freigabe erfordern.

## Build und Tests

```powershell
dotnet build .\HDRCalibrate.sln -c Release -p:Platform=x64 -warnaserror
.\tests\HDRCalibrate.Tests\bin\x64\Release\net481\HDRCalibrate.Tests.exe
.\tools\Build-Installer.ps1 -Configuration Release
```

Das Installer-Buildskript verwendet einen fest gepinnten und per SHA-256 sowie
Authenticode geprüften Inno-Setup-Compiler. Falls kein vertrauenswürdiger
Compiler vorhanden ist, wird er ausschließlich in
`installer\artifacts\toolcache` bereitgestellt. Installer und Prüfsumme landen
unter `installer\artifacts\output`.

Die automatisierten Tests verändern DWM nicht. Der reale Hardwaretest läuft
ausschließlich über den separaten Watchdog-Pfad und erzeugt Screenshots sowie
einen JSON-Bericht unter `artifacts\supervised-live-test-*`.

## Herkunft

Die DWM-Analyse baut auf öffentlich dokumentierter Arbeit von `dwm_eotf_rs`,
`dwm_eotf` und `dwm_lut_is_back` auf. Die Lizenz- und Quellenhinweise stehen in
`THIRD-PARTY-NOTICES.md`.
