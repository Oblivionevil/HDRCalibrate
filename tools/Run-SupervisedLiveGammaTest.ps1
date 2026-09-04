param(
    [double]$Gamma = 2.4,
    [double]$BrightnessPercent = 0.0,
    [string]$BuildDirectory = "",
    [switch]$CanaryOnly
)

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $legacyArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-Gamma', $Gamma.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-BrightnessPercent', $BrightnessPercent.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    if (-not [string]::IsNullOrWhiteSpace($BuildDirectory)) {
        $legacyArguments += @('-BuildDirectory', $BuildDirectory)
    }
    if ($CanaryOnly) { $legacyArguments += '-CanaryOnly' }
    & $windowsPowerShell @legacyArguments
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$workspace = Split-Path -Parent $PSScriptRoot
$build = if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Join-Path $workspace 'src\HDRCalibrate\bin\x64\Release\net481'
} else {
    [IO.Path]::GetFullPath($BuildDirectory)
}
$appPath = Join-Path $build 'HDRCalibrate.exe'
$helperPath = Join-Path $build 'HDRCalibrate.DwmHelper.exe'
$dwmAssemblyPath = Join-Path $build 'HDRCalibrate.Dwm.dll'
foreach ($required in @($appPath, $helperPath, $dwmAssemblyPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Builddatei fehlt: $required" }
}
$ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
if ($null -eq $ffmpegCommand) {
    throw 'Für den visuellen Hardwaretest wird ffmpeg mit gdigrab benötigt.'
}
$ffmpegPath = $ffmpegCommand.Source

$artifactDirectory = Join-Path $workspace ('artifacts\supervised-live-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$requestRoot = Join-Path $env:LOCALAPPDATA 'HDRCalibrate\Requests'
New-Item -ItemType Directory -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Path $requestRoot -Force | Out-Null

[void][Reflection.Assembly]::LoadFrom($dwmAssemblyPath)
[void][Reflection.Assembly]::LoadFrom($appPath)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('HdrCalibrateProbe.ScreenProbe' -as [type])) {
    $probeAssemblies = @(
        [System.Drawing.Bitmap].Assembly.Location,
        [System.Drawing.Color].Assembly.Location,
        [System.Windows.Forms.Form].Assembly.Location
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    Add-Type -ReferencedAssemblies $probeAssemblies -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HdrCalibrateProbe
{
    public sealed class CanaryForm : Form
    {
        public CanaryForm()
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(18, 24, 33);
            DoubleBuffered = true;
            Text = "HDR Calibrate SDR Watchdog Canary";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.Clear(Color.FromArgb(18, 24, 33));
            using (var title = new Font("Segoe UI", 30, FontStyle.Bold))
            using (var body = new Font("Segoe UI", 16, FontStyle.Regular))
            using (var small = new Font("Consolas", 11, FontStyle.Regular))
            using (var white = new SolidBrush(Color.White))
            using (var muted = new SolidBrush(Color.FromArgb(170, 190, 215)))
            {
                graphics.DrawString("HDR CALIBRATE - STATIC SDR WATCHDOG CANARY", title, white, 48, 30);
                graphics.DrawString("Gamma / Helligkeit - Text - Kanten - Graustufen - Farben - Checkerboard", body, muted, 50, 82);

                int margin = 48, top = 130, gap = 10;
                int tileWidth = Math.Max(80, (ClientSize.Width - margin * 2 - gap * 7) / 8);
                int tileHeight = Math.Max(70, (ClientSize.Height - top - 250 - gap * 5) / 6);
                Color[] colors = {
                    Color.Black, Color.FromArgb(32,32,32), Color.FromArgb(96,96,96), Color.FromArgb(160,160,160),
                    Color.White, Color.Red, Color.Lime, Color.Blue,
                    Color.Cyan, Color.Magenta, Color.Yellow, Color.Orange,
                    Color.FromArgb(24,70,120), Color.FromArgb(20,115,75), Color.FromArgb(105,55,135), Color.FromArgb(145,75,25)
                };
                for (int row = 0; row < 6; row++)
                {
                    for (int column = 0; column < 8; column++)
                    {
                        int x = margin + column * (tileWidth + gap);
                        int y = top + row * (tileHeight + gap);
                        Color color = colors[(row * 8 + column) % colors.Length];
                        using (var brush = new SolidBrush(color)) graphics.FillRectangle(brush, x, y, tileWidth, tileHeight);
                        using (var pen = new Pen(Color.FromArgb(220, 230, 240), 2)) graphics.DrawRectangle(pen, x, y, tileWidth, tileHeight);
                        Color textColor = color.GetBrightness() > 0.55f ? Color.Black : Color.White;
                        using (var textBrush = new SolidBrush(textColor))
                            graphics.DrawString(String.Format("R{0} C{1}\n0123456789", row + 1, column + 1), small, textBrush, x + 8, y + 8);
                    }
                }

                int barTop = ClientSize.Height - 205;
                int barWidth = ClientSize.Width - margin * 2;
                for (int index = 0; index < 32; index++)
                {
                    int gray = (int)Math.Round(index * 255.0 / 31.0);
                    int left = margin + index * barWidth / 32;
                    int right = margin + (index + 1) * barWidth / 32;
                    using (var brush = new SolidBrush(Color.FromArgb(gray, gray, gray)))
                        graphics.FillRectangle(brush, left, barTop, right - left, 74);
                }
                using (var pen = new Pen(Color.White, 2)) graphics.DrawRectangle(pen, margin, barTop, barWidth, 74);

                int checkerTop = ClientSize.Height - 115;
                for (int column = 0; column < 64; column++)
                {
                    int left = margin + column * barWidth / 64;
                    int right = margin + (column + 1) * barWidth / 64;
                    Color color = (column & 1) == 0 ? Color.White : Color.Black;
                    using (var brush = new SolidBrush(color)) graphics.FillRectangle(brush, left, checkerTop, right - left, 38);
                }
                graphics.DrawString("Wenn Flaechen fehlen, schwarz werden oder fragmentieren: Test fehlgeschlagen.", body, white, margin, ClientSize.Height - 66);
            }
        }
    }

    public sealed class ImageMetrics
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double MeanLuma { get; set; }
        public double BlackFraction { get; set; }
        public double EdgeFraction { get; set; }
    }

    public sealed class ImageComparison
    {
        public double MeanAbsoluteDifference { get; set; }
        public double BlackFractionDelta { get; set; }
        public double MinimumTileEdgeRetention { get; set; }
        public int LowEdgeRetentionTileCount { get; set; }
        public int MaximumContiguousLowEdgeRetentionTilesInColumn { get; set; }
        public int EvaluatedTileCount { get; set; }
    }

    public static class ScreenProbe
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr handle, int command);

        public static ImageMetrics Analyze(string path)
        {
            using (var bitmap = new Bitmap(path))
            {
                long samples = 0, black = 0, edges = 0, edgeTests = 0;
                double luma = 0;
                const int step = 4;
                for (int y = 0; y < bitmap.Height; y += step)
                {
                    for (int x = 0; x < bitmap.Width; x += step)
                    {
                        Color value = bitmap.GetPixel(x, y);
                        double current = Luma(value);
                        luma += current;
                        if (value.R <= 5 && value.G <= 5 && value.B <= 5) black++;
                        samples++;
                        if (x + step < bitmap.Width)
                        {
                            if (Math.Abs(current - Luma(bitmap.GetPixel(x + step, y))) >= 20) edges++;
                            edgeTests++;
                        }
                    }
                }
                return new ImageMetrics
                {
                    Width = bitmap.Width,
                    Height = bitmap.Height,
                    MeanLuma = luma / Math.Max(1, samples),
                    BlackFraction = (double)black / Math.Max(1, samples),
                    EdgeFraction = (double)edges / Math.Max(1, edgeTests)
                };
            }
        }

        public static ImageComparison Compare(string baselinePath, string candidatePath)
        {
            using (var baseline = new Bitmap(baselinePath))
            using (var candidate = new Bitmap(candidatePath))
            {
                if (baseline.Width != candidate.Width || baseline.Height != candidate.Height)
                    throw new InvalidOperationException("Screenshot dimensions differ.");
                const int columns = 8, rows = 6, step = 4;
                long samples = 0, baselineBlack = 0, candidateBlack = 0;
                double difference = 0, minimumRetention = 1.0;
                int lowTiles = 0, evaluatedTiles = 0, maximumContiguousLowTiles = 0;
                int[] lowTileRuns = new int[columns];
                for (int tileY = 0; tileY < rows; tileY++)
                {
                    int top = tileY * baseline.Height / rows;
                    int bottom = (tileY + 1) * baseline.Height / rows;
                    for (int tileX = 0; tileX < columns; tileX++)
                    {
                        int left = tileX * baseline.Width / columns;
                        int right = (tileX + 1) * baseline.Width / columns;
                        long baselineEdges = 0, candidateEdges = 0;
                        for (int y = top; y < bottom; y += step)
                        {
                            for (int x = left; x < right; x += step)
                            {
                                Color first = baseline.GetPixel(x, y);
                                Color second = candidate.GetPixel(x, y);
                                double firstLuma = Luma(first), secondLuma = Luma(second);
                                difference += Math.Abs(firstLuma - secondLuma);
                                if (first.R <= 5 && first.G <= 5 && first.B <= 5) baselineBlack++;
                                if (second.R <= 5 && second.G <= 5 && second.B <= 5) candidateBlack++;
                                samples++;
                                if (x + step < right)
                                {
                                    if (Math.Abs(firstLuma - Luma(baseline.GetPixel(x + step, y))) >= 20) baselineEdges++;
                                    if (Math.Abs(secondLuma - Luma(candidate.GetPixel(x + step, y))) >= 20) candidateEdges++;
                                }
                            }
                        }
                        if (baselineEdges >= 20)
                        {
                            double retention = (double)candidateEdges / baselineEdges;
                            minimumRetention = Math.Min(minimumRetention, retention);
                            if (retention < 0.35)
                            {
                                lowTiles++;
                                lowTileRuns[tileX]++;
                                maximumContiguousLowTiles = Math.Max(maximumContiguousLowTiles, lowTileRuns[tileX]);
                            }
                            else
                            {
                                lowTileRuns[tileX] = 0;
                            }
                            evaluatedTiles++;
                        }
                        else
                        {
                            lowTileRuns[tileX] = 0;
                        }
                    }
                }
                return new ImageComparison
                {
                    MeanAbsoluteDifference = difference / Math.Max(1, samples),
                    BlackFractionDelta = (double)(candidateBlack - baselineBlack) / Math.Max(1, samples),
                    MinimumTileEdgeRetention = evaluatedTiles == 0 ? 1.0 : minimumRetention,
                    LowEdgeRetentionTileCount = lowTiles,
                    MaximumContiguousLowEdgeRetentionTilesInColumn = maximumContiguousLowTiles,
                    EvaluatedTileCount = evaluatedTiles
                };
            }
        }

        private static double Luma(Color value)
        {
            return 0.2126 * value.R + 0.7152 * value.G + 0.0722 * value.B;
        }
    }
}
'@
}

function Get-CurrentDwmIdentity {
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $process = Get-Process -Name dwm -ErrorAction Stop |
        Where-Object SessionId -eq $sessionId |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($null -eq $process) { throw "DWM in Sitzung $sessionId fehlt." }
    try {
        [pscustomobject]@{
            ProcessId = $process.Id
            StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
        }
    } finally {
        $process.Dispose()
    }
}

function Save-DesktopScreenshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $ffmpegOutput = & $ffmpegPath -hide_banner -loglevel error `
        -f gdigrab -framerate 30 -draw_mouse 0 -i desktop `
        -frames:v 1 -vf 'scale=1920:-2:flags=area' -update 1 -y $Path 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Desktopaufnahme mit ffmpeg/gdigrab fehlgeschlagen: $($ffmpegOutput -join ' ')"
    }
}

function Write-Request {
    param([string]$Path, [HDRCalibrate.Core.CalibrationSettings]$Settings)
    $request = [HDRCalibrate.Dwm.DwmPatchRequest]::new()
    $request.Command = [HDRCalibrate.Dwm.DwmPatchCommand]::TimedTest
    $request.Settings = $Settings
    $serializer = [Xml.Serialization.XmlSerializer]::new([type][HDRCalibrate.Dwm.DwmPatchRequest])
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $serializer.Serialize($stream, $request); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Read-Response {
    param([string]$Path)
    $serializer = [Xml.Serialization.XmlSerializer]::new([type][HDRCalibrate.Dwm.DwmPatchResponse])
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $serializer.Deserialize($stream) } finally { $stream.Dispose() }
}

$settings = [HDRCalibrate.Core.CalibrationSettings]::new()
$settings.Gamma = $Gamma
$settings.ApplySdrGammaCorrection = $true
$settings.Brightness = $BrightnessPercent / 100.0
$settings.Contrast = 1.0
$settings.Saturation = 1.0
$settings.RedGain = 1.0
$settings.GreenGain = 1.0
$settings.BlueGain = 1.0
$settings.Validate()

$token = [Guid]::NewGuid().ToString('N')
$requestPath = Join-Path $requestRoot ("request-$token.xml")
$responsePath = Join-Path $requestRoot ("response-$token.xml")
$canary = $null
$helper = $null
$previousForegroundWindow = [IntPtr]::Zero
$mutationStarted = $false
$resetConfirmed = $false
$report = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    Gamma = $Gamma
    BrightnessPercent = $BrightnessPercent
    Preflight = $null
    DwmBefore = $null
    DwmAfter = $null
    TimedTest = $null
    Screenshots = [ordered]@{}
    BaselineMetrics = $null
    ActiveMetrics = @()
    ActiveComparisons = @()
    AfterMetrics = $null
    AfterComparison = $null
    VisualCorruptionDetected = $false
    LeftoverHelperCount = $null
    Success = $false
    Error = $null
}

try {
    $preflight = [HDRCalibrate.Dwm.DwmPatchEngine]::new().Preflight($settings)
    $report.Preflight = $preflight
    if (-not $preflight.Success -or $preflight.PatchedShaderCount -ne 4 -or
        $preflight.PatchedConstantVectorCount -ne 16 -or $preflight.ChangedByteCount -ge 512) {
        throw "Read-only-Preflight fehlgeschlagen: $($preflight.Message)"
    }

    $display = @([HDRCalibrate.Services.DisplayService]::new().GetActiveDisplays() | Where-Object HdrActive)
    if ($display.Count -eq 0) { throw 'HDR ist auf keinem aktiven Monitor eingeschaltet.' }

    $previousForegroundWindow = [HdrCalibrateProbe.ScreenProbe]::GetForegroundWindow()
    if ($previousForegroundWindow -ne [IntPtr]::Zero) {
        [void][HdrCalibrateProbe.ScreenProbe]::ShowWindow($previousForegroundWindow, 6)
        Start-Sleep -Milliseconds 300
    }

    $canary = [HdrCalibrateProbe.CanaryForm]::new()
    $canary.Show()
    $canary.WindowState = [Windows.Forms.FormWindowState]::Maximized
    $canary.TopMost = $true
    $canary.BringToFront()
    $canary.Activate()
    [void][HdrCalibrateProbe.ScreenProbe]::ShowWindow($canary.Handle, 3)
    [void][HdrCalibrateProbe.ScreenProbe]::SetForegroundWindow($canary.Handle)
    for ($pump = 0; $pump -lt 12; $pump++) {
        [Windows.Forms.Application]::DoEvents()
        $canary.BringToFront()
        Start-Sleep -Milliseconds 50
    }

    $report.DwmBefore = Get-CurrentDwmIdentity
    $beforePath = Join-Path $artifactDirectory '01-before.png'
    Save-DesktopScreenshot -Path $beforePath
    $report.Screenshots.Before = $beforePath
    $report.BaselineMetrics = [HdrCalibrateProbe.ScreenProbe]::Analyze($beforePath)
    if ($CanaryOnly) {
        $report.Success = $true
        return
    }

    Write-Request -Path $requestPath -Settings $settings
    $helper = Start-Process -FilePath $helperPath -ArgumentList @(
        '--request', $requestPath, '--response', $responsePath
    ) -PassThru -WindowStyle Hidden
    $mutationStarted = $true

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $firstChangedDwmAt = $null
    $nextCapture = 0
    while (-not $helper.HasExited -and $clock.ElapsedMilliseconds -lt 30000) {
        [Windows.Forms.Application]::DoEvents()
        $canary.BringToFront()
        $current = Get-CurrentDwmIdentity
        if ($current.ProcessId -ne $report.DwmBefore.ProcessId -or
            $current.StartTimeUtcTicks -ne $report.DwmBefore.StartTimeUtcTicks) {
            if ($null -eq $firstChangedDwmAt) {
                $firstChangedDwmAt = $clock.ElapsedMilliseconds
                $nextCapture = $firstChangedDwmAt + 900
            }
            if ($clock.ElapsedMilliseconds -ge $nextCapture -and $report.ActiveMetrics.Count -lt 4) {
                $index = $report.ActiveMetrics.Count + 1
                $activePath = Join-Path $artifactDirectory ("02-active-$index.png")
                Save-DesktopScreenshot -Path $activePath
                $metrics = [HdrCalibrateProbe.ScreenProbe]::Analyze($activePath)
                $comparison = [HdrCalibrateProbe.ScreenProbe]::Compare($beforePath, $activePath)
                $report.Screenshots["Active$index"] = $activePath
                $report.ActiveMetrics += $metrics
                $report.ActiveComparisons += $comparison
                if ($comparison.MaximumContiguousLowEdgeRetentionTilesInColumn -ge 4) {
                    $report.VisualCorruptionDetected = $true
                }
                $nextCapture += 900
            }
        }
        Start-Sleep -Milliseconds 40
        $helper.Refresh()
    }
    if (-not $helper.HasExited) {
        try { $helper.Kill(); [void]$helper.WaitForExit(2000) } catch { }
        throw 'Der Test-Supervisor überschritt 30 Sekunden; sein unabhängiger Watchdog bleibt für den Reset zuständig.'
    }
    if (-not (Test-Path -LiteralPath $responsePath -PathType Leaf)) {
        throw "Der Test-Helfer erzeugte keine Antwort (Exitcode $($helper.ExitCode))."
    }
    $response = Read-Response -Path $responsePath
    $report.TimedTest = $response
    if (-not $response.Success -or -not $response.WatchdogArmed -or
        $response.TestDurationMilliseconds -lt 4300 -or $response.TestDurationMilliseconds -gt 5500) {
        throw "Überwachter Test fehlgeschlagen: $($response.Message)"
    }
    $resetConfirmed = $true

    Start-Sleep -Milliseconds 600
    $report.DwmAfter = Get-CurrentDwmIdentity
    if ($report.DwmAfter.ProcessId -ne $response.DwmProcessId -or
        $report.DwmAfter.StartTimeUtcTicks -ne $response.DwmStartTimeUtcTicks) {
        throw 'Die aktuelle DWM-Identität stimmt nicht mit dem verifizierten Watchdog-Reset überein.'
    }
    if ($report.DwmAfter.ProcessId -eq $response.TestedDwmProcessId) {
        throw 'Der getestete DWM-Prozess wurde nicht ersetzt.'
    }

    $afterPath = Join-Path $artifactDirectory '03-after-reset.png'
    Save-DesktopScreenshot -Path $afterPath
    $report.Screenshots.After = $afterPath
    $report.AfterMetrics = [HdrCalibrateProbe.ScreenProbe]::Analyze($afterPath)
    $report.AfterComparison = [HdrCalibrateProbe.ScreenProbe]::Compare($beforePath, $afterPath)
    if ($report.AfterComparison.MaximumContiguousLowEdgeRetentionTilesInColumn -ge 4) {
        throw 'Die Desktopstruktur ist nach dem Reset nicht stabil zur Baseline zurückgekehrt.'
    }
    if ($report.VisualCorruptionDetected) {
        throw 'Die regionsweise Screenshotanalyse hat während des Tests Schwarz-/Fragmentkorruption erkannt.'
    }
    if ($report.ActiveMetrics.Count -eq 0) { throw 'Während der aktiven Testphase wurde kein Screenshot erfasst.' }

    Start-Sleep -Milliseconds 500
    $report.LeftoverHelperCount = @(Get-Process -Name HDRCalibrate.DwmHelper -ErrorAction SilentlyContinue).Count
    if ($report.LeftoverHelperCount -ne 0) { throw 'Nach dem Test läuft noch ein DWM-Helferprozess.' }
    $report.Success = $true
}
catch {
    $report.Error = $_.Exception.ToString()
}
finally {
    if (-not $report.Success -and $mutationStarted -and -not $resetConfirmed) {
        try {
            Start-Sleep -Milliseconds 6000
            [void][HDRCalibrate.Dwm.DwmPatchEngine]::new().Reset()
        } catch {
            $report.Error = ($report.Error + [Environment]::NewLine + 'NOTFALL-RESET: ' + $_.Exception.ToString()).Trim()
        }
    }
    if ($null -ne $canary) {
        try { $canary.Close(); $canary.Dispose() } catch { }
    }
    if ($previousForegroundWindow -ne [IntPtr]::Zero) {
        try {
            [void][HdrCalibrateProbe.ScreenProbe]::ShowWindow($previousForegroundWindow, 9)
            [void][HdrCalibrateProbe.ScreenProbe]::SetForegroundWindow($previousForegroundWindow)
        } catch { }
    }
    if ($null -ne $helper) { $helper.Dispose() }
    foreach ($path in @($requestPath, $responsePath, $responsePath + '.tmp')) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $report.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    $reportPath = Join-Path $artifactDirectory 'supervised-live-test-report.json'
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    "ARTIFACT_DIR=$artifactDirectory"
    "REPORT=$reportPath"
    "SUCCESS=$($report.Success)"
    if ($null -ne $report.TimedTest) {
        "TESTED_DWM_PID=$($report.TimedTest.TestedDwmProcessId)"
        "RESET_DWM_PID=$($report.TimedTest.DwmProcessId)"
        "ACTIVE_MS=$($report.TimedTest.TestDurationMilliseconds)"
        "CHANGED_BYTES=$($report.TimedTest.ChangedByteCount)"
    }
    if (-not $report.Success) { Write-Error $report.Error }
}
