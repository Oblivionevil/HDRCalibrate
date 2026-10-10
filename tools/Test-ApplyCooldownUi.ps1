[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TestDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this isolated WPF test with Windows PowerShell -STA.'
}
$testRoot = (Resolve-Path -LiteralPath $TestDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
# The application resolves its helper relative to the entry process. Make this
# isolated host use the tested payload, not PowerShell's installation directory.
[AppDomain]::CurrentDomain.SetData('APPBASE', $testRoot.TrimEnd('\') + '\')
[void][Reflection.Assembly]::LoadFrom((Join-Path $testRoot 'HDRCalibrate.Dwm.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $testRoot 'HDRCalibrate.exe'))
$tests = [Reflection.Assembly]::LoadFrom((Join-Path $testRoot 'HDRCalibrate.Tests.exe'))
$program = $tests.GetType('HDRCalibrate.Tests.Program', $true)
$privateStatic = [Reflection.BindingFlags]'Static,NonPublic'
$storeType = $program.GetNestedType('MemoryStartupActivationPolicyStore', [Reflection.BindingFlags]::NonPublic)
$store = [Activator]::CreateInstance($storeType, $true)
$environment = $program.GetMethod('CreateTestWindowsEnvironment', $privateStatic).Invoke($null, @())
$script:cooldownRemaining = [TimeSpan]::FromMilliseconds(95.7647)
$remainingProvider = [Func[TimeSpan]] { return $script:cooldownRemaining }
$viewModel = $program.GetMethod('CreateCooldownViewModel', $privateStatic).Invoke(
    $null, @($store, $environment, $remainingProvider))
if (-not $viewModel.IsCurrentSettingsTested -or $store.Policy.ApprovalRevoked) {
    throw 'The isolated fixture did not begin with a successful test.'
}
$viewModel.Displays.Add($viewModel.SelectedDisplay)
$viewModel.GetType().GetField('_statusMessage', [Reflection.BindingFlags]'Instance,NonPublic').SetValue(
    $viewModel, 'Isolierter UI-Test: keine Aktivierung, keine Benutzerkonfiguration geaendert.')
$application = [Windows.Application]::new()
$application.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$application.Resources.MergedDictionaries.Add([Windows.Application]::LoadComponent(
    [Uri]::new('/HDRCalibrate;component/Theme.xaml', [UriKind]::Relative)))
$window = $null

function Save-Layout {
    param([string]$Label, [int]$Width, [int]$Height)
    # Render only the disconnected content tree; never Show, initialize the app,
    # invoke a patch command, or interact with the real desktop.
    $content = [Windows.FrameworkElement]$window.Content
    $content.Measure([Windows.Size]::new($Width, $Height))
    $content.Arrange([Windows.Rect]::new(0, 0, $Width, $Height))
    $content.UpdateLayout()
    $scroll = @($content.Children | Where-Object { $_ -is [Windows.Controls.ScrollViewer] })[0]
    $scroll.ScrollToEnd()
    $content.UpdateLayout()
    foreach ($name in @('TestButton', 'ApplyButton', 'RestartCooldownLabel')) {
        $control = $window.FindName($name)
        $bounds = $control.TransformToAncestor($content).TransformBounds(
            [Windows.Rect]::new(0, 0, $control.ActualWidth, $control.ActualHeight))
        if ($bounds.Left -lt 0 -or $bounds.Top -lt 0 -or $bounds.Right -gt ($Width + 1) -or $bounds.Bottom -gt ($Height + 1)) {
            throw "Clipped control in ${Label}: $name at $bounds, viewport ${Width}x${Height}"
        }
    }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Width, $Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $background = [Windows.Media.DrawingVisual]::new()
    $drawing = $background.RenderOpen()
    try { $drawing.DrawRectangle($window.FindResource('WindowBrush'), $null, [Windows.Rect]::new(0, 0, $Width, $Height)) }
    finally { $drawing.Dispose() }
    $bitmap.Render($background)
    $bitmap.Render($content)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $file = [IO.File]::Create((Join-Path $outputRoot ($Label + '.png')))
    try { $encoder.Save($file) } finally { $file.Dispose() }
}

try {
    $window = [HDRCalibrate.MainWindow]::new($viewModel)
    Save-Layout 'waiting' 1144 781
    $apply = $window.FindName('ApplyButton')
    $test = $window.FindName('TestButton')
    $label = $window.FindName('RestartCooldownLabel')
    if ($apply.Content -cne 'Warten (1 s)' -or $apply.IsEnabled -or $test.IsEnabled -or [string]::IsNullOrEmpty($label.Text)) {
        throw 'The actual WPF controls do not show and enforce the remaining cooldown.'
    }
    Save-Layout 'waiting-minimum' 944 661
    $script:cooldownRemaining = [TimeSpan]::Zero
    $window.GetType().GetMethod('OnCooldownTick', [Reflection.BindingFlags]'Instance,NonPublic').Invoke(
        $window, @($null, [EventArgs]::Empty))
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::DataBind)
    Save-Layout 'ready' 1144 781
    if ($apply.Content -cne 'Anwenden' -or -not $apply.IsEnabled -or -not $test.IsEnabled -or $label.Text -cne '') {
        throw 'The actual WPF controls did not become available after cooldown expiry.'
    }
    if (-not $viewModel.IsCurrentSettingsTested -or $store.Policy.ApprovalRevoked -or $store.Policy.Enabled) {
        throw 'Countdown rendering changed the successful test or silently enabled startup.'
    }
    if ($window.IsVisible -or $window.IsLoaded) { throw 'Offscreen test unexpectedly loaded a real window.' }
    $timer = $window.GetType().GetField('_cooldownTimer', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($window)
    if ($timer.IsEnabled) { throw 'Hidden windows must not keep polling the restart marker.' }
    Write-Output 'PASS: Apply countdown, command enablement, proof preservation, minimum layout and hidden timer; LiveDwmAccess=false'
}
finally {
    if ($null -ne $window) { $window.Close() }
    $application.Shutdown()
}
