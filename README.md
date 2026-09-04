# HDR Calibrate 1.3.0

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

Die Korrektur betrifft nur den DWM-Pfad `SDR -> lineares scRGB`. Die nativen
HDR-Shader und HDR-Inhalte bleiben unverändert. Independent Flip und bestimmte
MPO-Pfade können DWM umgehen; entsprechende SDR-Inhalte erhalten dann keine
Korrektur.

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
Anwenden kurz schwarz werden. Nach einem DWM- oder Windows-Neustart muss die
Korrektur erneut getestet und angewendet werden. Die App richtet keinen
Autostart ein und verwendet keine herstellerspezifische Software.

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
```

Die automatisierten Tests verändern DWM nicht. Der reale Hardwaretest läuft
ausschließlich über den separaten Watchdog-Pfad und erzeugt Screenshots sowie
einen JSON-Bericht unter `artifacts\supervised-live-test-*`.

## Herkunft

Die DWM-Analyse baut auf öffentlich dokumentierter Arbeit von `dwm_eotf_rs`,
`dwm_eotf` und `dwm_lut_is_back` auf. Die Lizenz- und Quellenhinweise stehen in
`THIRD-PARTY-NOTICES.md`.
