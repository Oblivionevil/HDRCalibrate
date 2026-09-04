param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [double]$WindowWidth = 1400,
    [double]$WindowHeight = 1000
)

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -Sta -ExecutionPolicy Bypass -File $PSCommandPath `
        -Executable $Executable -OutputPath $OutputPath `
        -WindowWidth $WindowWidth.ToString([Globalization.CultureInfo]::InvariantCulture) `
        -WindowHeight $WindowHeight.ToString([Globalization.CultureInfo]::InvariantCulture)
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$executablePath = [IO.Path]::GetFullPath($Executable)
$screenshotPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Programmdatei fehlt: $executablePath"
}
$screenshotDirectory = Split-Path -Parent $screenshotPath
if (-not (Test-Path -LiteralPath $screenshotDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $screenshotDirectory | Out-Null
}

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$buildDirectory = Split-Path -Parent $executablePath
$dwmAssemblyPath = Join-Path $buildDirectory 'HDRCalibrate.Dwm.dll'
if (-not (Test-Path -LiteralPath $dwmAssemblyPath -PathType Leaf)) {
    throw "Abhängigkeit fehlt: $dwmAssemblyPath"
}
[void][Reflection.Assembly]::LoadFrom($dwmAssemblyPath)
$assembly = [Reflection.Assembly]::LoadFrom($executablePath)
$applicationType = $assembly.GetType('HDRCalibrate.App', $true)
$windowType = $assembly.GetType('HDRCalibrate.MainWindow', $true)
$application = [Activator]::CreateInstance($applicationType)
$window = $null

try {
    $applicationType.GetMethod('InitializeComponent').Invoke($application, @())
    $window = [Windows.Window][Activator]::CreateInstance($windowType)
    $window.Width = $WindowWidth
    $window.Height = $WindowHeight
    $window.Show()

    $frame = [Windows.Threading.DispatcherFrame]::new()
    $timer = [Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromMilliseconds(2500)
    $timer.add_Tick({
        $timer.Stop()
        $frame.Continue = $false
    })
    $timer.Start()
    [Windows.Threading.Dispatcher]::PushFrame($frame)

    $window.UpdateLayout()
    $source = [System.Windows.PresentationSource]::FromVisual($window)
    $scaleX = 1.0
    $scaleY = 1.0
    if ($null -ne $source -and $null -ne $source.CompositionTarget) {
        $scaleX = $source.CompositionTarget.TransformToDevice.M11
        $scaleY = $source.CompositionTarget.TransformToDevice.M22
    }
    $pixelWidth = [Math]::Max(1, [int][Math]::Ceiling($window.ActualWidth * $scaleX))
    $pixelHeight = [Math]::Max(1, [int][Math]::Ceiling($window.ActualHeight * $scaleY))
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(
        $pixelWidth, $pixelHeight, 96.0 * $scaleX, 96.0 * $scaleY,
        [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($window)

    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($screenshotPath)
    try { $encoder.Save($stream) }
    finally { $stream.Dispose() }

    "SCREENSHOT=$screenshotPath"
    "WINDOW_SIZE=${pixelWidth}x${pixelHeight}"
}
finally {
    if ($null -ne $window) { $window.Close() }
    if ($null -ne $application) { $application.Shutdown() }
}
