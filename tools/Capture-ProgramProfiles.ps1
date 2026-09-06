[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppExe,
    [Parameter(Mandatory)][string]$OutputPath,
    [int]$WindowWidth = 960,
    [int]$WindowHeight = 760,
    [switch]$Empty,
    [switch]$Corrupt
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run in Windows PowerShell with -STA.' }
$executable = (Resolve-Path -LiteralPath $AppExe).Path
$screenshot = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($screenshot)
$repoPrefix = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')).TrimEnd('\') + '\'
if (-not $screenshot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'UI test output must stay inside repository artifacts.' }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$fixtureDirectory = Join-Path $outputDirectory ('ui-profile-fixture-' + [Guid]::NewGuid().ToString('N'))

Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
[void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $executable -Parent) 'HDRCalibrate.Dwm.dll'))
[void][Reflection.Assembly]::LoadFrom($executable)
$store = [HDRCalibrate.Services.ProgramProfileStore]::new($fixtureDirectory)
$profiles = [Collections.Generic.List[HDRCalibrate.Services.ProgramLaunchProfile]]::new()
if (-not $Empty -and -not $Corrupt) {
    $profile = [HDRCalibrate.Services.ProgramLaunchProfile]::new()
    $profile.ExecutablePath = 'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe'
    $profile.ColorMode = [HDRCalibrate.Services.ProgramColorMode]::Srgb
    $profiles.Add($profile)
    $missing = [HDRCalibrate.Services.ProgramLaunchProfile]::new()
    $missing.ExecutablePath = Join-Path $fixtureDirectory 'missing-browser\chrome.exe'
    $missing.ColorMode = [HDRCalibrate.Services.ProgramColorMode]::ScrgbLinear
    $profiles.Add($missing)
}
$store.Save($profiles)
if ($Corrupt) { [IO.File]::WriteAllText((Join-Path $fixtureDirectory 'program-profiles.xml'), '<broken>') }

# Plain WPF host: never run App.OnStartup, touch live startup approval, or launch a browser.
$rejectStart = [Action[Diagnostics.ProcessStartInfo]] { throw 'A browser launch is forbidden in this UI test.' }
$launcher = [HDRCalibrate.Services.ProgramProfileLauncher]::new($null, $null, $rejectStart, $null, $rejectStart)
$application = [Windows.Application]::new()
$application.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$application.Resources.MergedDictionaries.Add([Windows.Application]::LoadComponent([Uri]::new('/HDRCalibrate;component/Theme.xaml', [UriKind]::Relative)))
$window = $null
try {
    $window = [HDRCalibrate.ProgramProfilesWindow]::new($store, $launcher)
    $window.Width = $WindowWidth
    $window.Height = $WindowHeight
    $window.Show()
    $window.UpdateLayout()
    $list = $window.FindName('ProgramList')
    $mode = $window.FindName('ModeSelector')
    $save = $window.FindName('SaveButton')
    $launch = $window.FindName('LaunchButton')
    if ($Corrupt) {
        if ($window.FindName('AddButton').IsEnabled -or $launch.IsEnabled -or $save.IsEnabled) { throw 'Corrupt-store UI did not fail closed.' }
        if ($window.FindName('EmptyLabel').Text -notlike '*nicht geladen*') { throw 'Corrupt-store UI incorrectly suggests adding a profile.' }
        if ([IO.File]::ReadAllText((Join-Path $fixtureDirectory 'program-profiles.xml')) -ne '<broken>') { throw 'Corrupt store was modified.' }
        'UI_CHECK=corrupt configuration blocks changes and starts'
    } elseif ($Empty) {
        if ($list.Items.Count -ne 0 -or $launch.IsEnabled -or $save.IsEnabled) { throw 'Empty-list UI permits an invalid operation.' }
        'UI_CHECK=empty list is safe'
    } else {
        if ($list.Items.Count -ne 2) { throw 'Profile list did not load.' }
        $list.SelectedIndex = 1
        if ($launch.IsEnabled) { throw 'Missing executable is launchable.' }
        $list.SelectedIndex = 0
        $mode.SelectedValue = [HDRCalibrate.Services.ProgramColorMode]::ScrgbLinear
        if (-not $save.IsEnabled -or $launch.IsEnabled) { throw "Unsaved mode change is not guarded: Save=$($save.IsEnabled), Launch=$($launch.IsEnabled), Value=$($mode.SelectedValue), Item=$($mode.SelectedItem.Mode)." }
        $save.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        if ($store.Load()[0].ColorMode -ne [HDRCalibrate.Services.ProgramColorMode]::ScrgbLinear -or $save.IsEnabled) { throw 'UI Save did not persist the mode.' }
        $mode.SelectedValue = [HDRCalibrate.Services.ProgramColorMode]::Default
        if (-not $save.IsEnabled -or $launch.IsEnabled) { throw 'Unsaved default-mode change is not guarded.' }
        $save.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        if ($store.Load()[0].ColorMode -ne [HDRCalibrate.Services.ProgramColorMode]::Default) { throw 'Default mode was not persisted.' }
        $mode.SelectedValue = [HDRCalibrate.Services.ProgramColorMode]::Srgb
        $save.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        $list.SelectedIndex = 1
        $window.FindName('RemoveButton').RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        if ($store.Load().Count -ne 1) { throw 'UI Remove did not persist.' }
        $store.Save($profiles)
        $window.GetType().GetMethod('Reload', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($window, @()) | Out-Null
        'UI_CHECK=selection, unsaved-mode guard, persistence, removal, reload and missing-file block passed'
    }
    $window.UpdateLayout()
    foreach ($name in @('ProgramList','ModeSelector','LaunchButton','StatusLabel')) {
        $control = $window.FindName($name)
        $bounds = $control.TransformToAncestor($window).TransformBounds([Windows.Rect]::new(0,0,$control.ActualWidth,$control.ActualHeight))
        if ($bounds.Right -gt $window.ActualWidth -or $bounds.Bottom -gt $window.ActualHeight -or $bounds.Top -lt 0) { throw "Clipped control: $name" }
    }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$window.ActualWidth, [int]$window.ActualHeight, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($window)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($screenshot)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    "SCREENSHOT=$screenshot"
    "LIVE_STATE=untouched; FIXTURE=$fixtureDirectory"
} finally {
    if ($null -ne $window) { $window.Close() }
    $application.Shutdown()
}
