# Apply-Neustartsperre – 1.7.2

Prüfdatum: 2026-09-22. WPF / .NET Framework 4.8.1 / x64.

## Befund und Korrektur

Die ausgewerteten Zustandszeitpunkte lagen 9,9042353 Sekunden auseinander:
Der nächste Apply-Versuch traf damit etwa 96 ms vor Ablauf der unverändert
10 Sekunden langen DWM-Neustartsperre ein. Der bisherige generische
Fehlerpfad widerrief dabei den gerade erfolgreichen Test, obwohl noch kein
neuer Patchversuch stattgefunden hatte. Das Verhalten ist durch eine
isolierte Regression mit exakt dieser Zeitgrenze reproduziert.

- UI und Dienst lesen denselben Neustartmarker. Die UI zeigt einen Countdown
  und sperrt Test/Apply bis zum Ablauf; der Dienst prüft unabhängig erneut.
- Die spezifische Ausnahme `DwmRestartCooldownException` erhält die
  erfolgreiche Testfreigabe. Andere Apply-Fehler widerrufen sie weiterhin.
- Countdown-Abfragen verändern weder Marker noch Freigabe. Abgewiesene
  Versuche verlängern die Schutzpause nicht.
- Nicht lesbare Marker sperren sicher; beschädigte Marker verwenden den
  Änderungszeitpunkt, zukünftige Zeitstempel umgehen die Sperre nicht.
- Der UI-Timer läuft nur bei sichtbarem Fenster und aktiver Sperre. Er wird
  beim Schließen abgemeldet. Keine neue automatische Aktivierung.
- Windows-Update-/Versionssperren und der noch gesperrte private
  Programm-Gamma-Pfad auf `dwmcore.dll 10.0.26100.9278` bleiben erhalten.

## Geprüft

1. Release-Build mit `-warnaserror`: 0 Warnungen, 0 Fehler.
2. **91 Anwendungstests** bestanden, einschließlich sieben neuer
   Cooldown-/Apply-Regressionen. Erneut gegen eine hashidentische Kopie des
   tatsächlich verpackten Installer-Payloads ausgeführt.
3. Echte WPF-Komponenten isoliert gerendert: `Warten (1 s)`, gesperrte
   Test-/Apply-Befehle, Freigabe über den Fenster-Timerhandler, erhaltene
   Testfreigabe und kein laufender Timer bei unsichtbarem Fenster.
   Normal- und Mindestgröße geprüft; die unteren Bedienelemente wurden im
   vorhandenen Scrollbereich sichtbar gescrollt. Kein Fenster angezeigt.
4. **70 native CLI-Tests und 3 Managed-Pipe-Tests** gegen die nativen Dateien
   aus dem Installer bestanden; jeweils `LiveDwmAccess=false`.
5. Bestehende Shaderprüfung: 442 Nicht-Ziel-Shader bei Gamma 2,0 / 2,2 / 2,4
   unverändert. Aktuelle DWM-Datei read-only validiert.
6. App-/DWM-Prozessidentitäten und sieben Benutzerzustandsdateien beim
   abschließenden Vergleich unverändert; Requests-Verzeichnis leer.

Es fand **keine echte Gamma-Aktivierung, kein DWM-Neustart, kein Spielstart
und keine Installation** statt. Optische Gammawirkung und VRR werden durch
diese automatisierten Prüfungen nicht nachgewiesen.

## Artefakte

- Installer: `installer/artifacts/output/HDRCalibrate-Setup-v1.7.2-x64.exe`
- SHA-256: `96403E3B3CEB8CB2334B0155FB05B3AF48DC949EC685E86D2A93FCCE41D9E067`
- Größe: 3.163.604 Bytes; nicht signiert.
- Build-/Testlogs: `artifacts/apply-cooldown-1.7.2/`
- WPF-Renderings: `artifacts/apply-cooldown-1.7.2/staged-ui/`

Reproduzierbarer UI-Test nach dem Build:

```powershell
powershell.exe -NoProfile -STA -File .\tools\Test-ApplyCooldownUi.ps1 `
  -TestDirectory .\artifacts\apply-cooldown-1.7.2\staged-tests `
  -OutputDirectory .\artifacts\apply-cooldown-1.7.2\staged-ui
```

Nach Installation ist versionsbedingt ein neuer 5-Sekunden-Test nötig.
Danach den Countdown ablaufen lassen und manuell **Anwenden** wählen.
