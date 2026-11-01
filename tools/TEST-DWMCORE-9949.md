# dwmcore.dll 9949 – Freigabe des globalen SDR-Pfads (1.7.4)

Prüfdatum: 2026-11-01. Read-only; kein DWM-Neustart, kein Schreibzugriff.

## Systemdatei

- Windows-Update KB5127690 (14.10.2026), UBR 9711.
- `C:\Windows\System32\dwmcore.dll`: SHA-256
  `32FAD20F0EFE38789E20130881E5F003AD4D8283926546BA22F4FE72E510E932`,
  4.476.928 Bytes.
- `FileVersionInfo` für den System32-Pfad meldet
  `10.0.26100.9949 (WinBuild.160101.0800)` (Ressource aus
  `SystemResources\dwmcore.dll.mun`); die Binärdatei selbst und eine Kopie
  außerhalb von System32 melden 10.0.26100.9711. Die App vergleicht wie bisher
  die für System32 gemeldete Version zusammen mit dem Datei-Hash.

## Vergleich mit 9278

Referenz: `WinSxS\…compositor_…_10.0.26100.9278_…\dwmcore.dll`, SHA-256
identisch mit der bisher freigegebenen 9278-Datei.

- Beide Dateien enthalten 458 DXBC-Container. Alle 458 gespeicherten
  Prüfsummen kommen in beiden vor (0 nur in einer Datei); nur die Offsets sind
  verschoben.
- Die sechs SDR→scRGB-Exporte (FL10, FL9.3, FL9.1; Alpha/Opaque) haben dieselben
  Prüfsummen, Slotgrößen und Chunkfolgen, liegen in denselben drei
  Profilbibliotheken (3191B088…, 97A90639…, 586540FE…) mit 133/133/101
  Funktionen an Index 16/17. Alle Container-Prüfsummen sind gültig.
- Die vollständige Produktions-Manifestprüfung (`ValidateShaderManifest`,
  inklusive D3D-Reflection) besteht für beide Dateien.

## Geprüft nach der Freigabe

- Release-Build mit `-warnaserror`: 0 Warnungen, 0 Fehler.
- 110 Anwendungstests bestanden auf dem 9949-System, darunter Preflight,
  Konstantenpatch-Dry-Run, 440 unveränderte Nicht-Ziel-Shader bei Gamma
  2,0/2,2/2,4, Datei- gegen gemappten Schreibplan und der neue Test, dass
  Version, Hash und Schreibplan-Fingerabdruck an denselben Build gebunden sind.
- `Verify-SafetyGate.ps1` gegen das laufende System: 6 Shader,
  24 Konstantenvektoren, 341 geplante Bytes, DWM-Identität unverändert.

Nicht geprüft: eine echte Aktivierung (5-Sekunden-Test) auf 9949, optische
Gammawirkung, VRR und Latenz. Der native Programm-Hook bleibt auf 26100.8875
beschränkt.
