param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [double]$WindowWidth = 1400,
    [double]$WindowHeight = 1000,
    [ValidateRange(250, 15000)][int]$WaitMilliseconds = 5000,
    [switch]$ScrollToBottom,
    [switch]$SimulateWindowsUpdate
)

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $captureArguments = @()
    if ($ScrollToBottom) { $captureArguments += '-ScrollToBottom' }
    if ($SimulateWindowsUpdate) { $captureArguments += '-SimulateWindowsUpdate' }
    & $windowsPowerShell -NoProfile -Sta -ExecutionPolicy Bypass -File $PSCommandPath `
        -Executable $Executable -OutputPath $OutputPath `
        -WindowWidth $WindowWidth.ToString([Globalization.CultureInfo]::InvariantCulture) `
        -WindowHeight $WindowHeight.ToString([Globalization.CultureInfo]::InvariantCulture) `
        -WaitMilliseconds $WaitMilliseconds @captureArguments
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
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies @('System.Drawing', 'System') -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class HdrWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    public static void Save(IntPtr window, string path)
    {
        RECT rect;
        if (!GetWindowRect(window, out rect)) throw new InvalidOperationException("GetWindowRect failed.");
        using (var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(window, dc, 2)) throw new InvalidOperationException("PrintWindow failed.");
            }
            finally { graphics.ReleaseHdc(dc); }
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
'@
Add-Type -ReferencedAssemblies @('WindowsBase', 'System') -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;

public static class HdrCaptureWait
{
    public static void Complete(Task initialization, int minimumMilliseconds)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        timer.Interval = TimeSpan.FromMilliseconds(50);
        long completedAt = -1;
        timer.Tick += (sender, args) =>
        {
            if (initialization.IsCompleted && completedAt < 0) completedAt = clock.ElapsedMilliseconds;
            if (clock.ElapsedMilliseconds > 20000
                || (completedAt >= 0 && clock.ElapsedMilliseconds >= minimumMilliseconds
                    && clock.ElapsedMilliseconds >= completedAt + 250))
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (!initialization.IsCompleted) throw new TimeoutException("UI initialization timed out.");
        initialization.GetAwaiter().GetResult();
    }
}
'@

$buildDirectory = Split-Path -Parent $executablePath
$dwmAssemblyPath = Join-Path $buildDirectory 'HDRCalibrate.Dwm.dll'
if (-not (Test-Path -LiteralPath $dwmAssemblyPath -PathType Leaf)) {
    throw "Abhängigkeit fehlt: $dwmAssemblyPath"
}
[void][Reflection.Assembly]::LoadFrom($dwmAssemblyPath)
$assembly = [Reflection.Assembly]::LoadFrom($executablePath)
$windowType = $assembly.GetType('HDRCalibrate.MainWindow', $true)
$captureStoreSource = @'
using HDRCalibrate.Services;

public sealed class HdrCaptureRunStore : IRunValueStore
{
    public string Value;
    public string Read(string name) { return Value; }
    public void Write(string name, string value) { Value = value; }
    public void Delete(string name) { Value = null; }
}

public sealed class HdrCapturePolicyStore : IStartupActivationPolicyStore
{
    public PersistedStartupActivationPolicy Value;
    public PersistedStartupActivationPolicy Load() { return Value; }
    public void Save(PersistedStartupActivationPolicy policy) { Value = policy; }
    public void Clear() { Value = null; }
}
'@
$captureCompiler = [Microsoft.CSharp.CSharpCodeProvider]::new()
try {
    $captureCompilerOptions = [CodeDom.Compiler.CompilerParameters]::new()
    $captureCompilerOptions.GenerateInMemory = $true
    [void]$captureCompilerOptions.ReferencedAssemblies.Add('System.dll')
    [void]$captureCompilerOptions.ReferencedAssemblies.Add($executablePath)
    [void]$captureCompilerOptions.ReferencedAssemblies.Add($dwmAssemblyPath)
    $captureCompilation = $captureCompiler.CompileAssemblyFromSource($captureCompilerOptions, $captureStoreSource)
    if ($captureCompilation.Errors.HasErrors) {
        throw ('Screenshot store compilation failed: ' + ($captureCompilation.Errors -join ' | '))
    }
    [void]$captureCompilation.CompiledAssembly
}
finally { $captureCompiler.Dispose() }
$captureRunStore = [HdrCaptureRunStore]::new()
$capturePolicyStore = [HdrCapturePolicyStore]::new()
$captureAutostart = [HDRCalibrate.Services.AutostartService]::new($captureRunStore, $executablePath)
if ($SimulateWindowsUpdate) {
    # Synthetic old approval lives only in memory; no real test proof or startup state is written.
    $pastEnvironment = [HDRCalibrate.Dwm.WindowsEnvironmentIdentityProvider]::new().Capture()
    $pastEnvironment.UpdateBuildRevision++
    $capturePolicy = [HDRCalibrate.Services.PersistedStartupActivationPolicy]::new()
    $capturePolicy.SchemaVersion = [HDRCalibrate.Services.StartupActivationPolicy]::CurrentSchemaVersion
    $capturePolicy.Enabled = $true
    $capturePolicy.Settings.ApplySdrGammaCorrection = $true
    $capturePolicy.WindowsEnvironment = $pastEnvironment
    $capturePolicy.SettingsFingerprint = [HDRCalibrate.Services.CalibrationSettingsIdentity]::Compute($capturePolicy.Settings)
    $capturePolicy.DwmCoreVersion = [HDRCalibrate.Dwm.DwmCoreInspector]::SupportedDwmCoreVersion
    $capturePolicy.DwmCoreSha256 = $pastEnvironment.DwmCoreSha256
    $capturePolicy.PatchPlanFingerprint = 'A' * 64
    $capturePolicy.PatchImplementationId = [HDRCalibrate.Dwm.DwmPatchSafety]::PatchImplementationId
    $capturePolicy.AppVersion = $assembly.GetName().Version.ToString()
    $capturePolicy.HelperVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $buildDirectory 'HDRCalibrate.DwmHelper.exe')).FileVersion.Trim()
    $capturePolicy.TestedUtc = [DateTime]::UtcNow
    $capturePolicy.WatchdogResetConfirmed = $true
    $capturePolicyStore.Value = $capturePolicy
    $captureAutostart.SetEnabled($true)
}
$captureViewModel = [HDRCalibrate.ViewModels.MainViewModel]::new($captureAutostart, $capturePolicyStore, $null)
"STARTUP_SETTINGS=isolated-in-memory; SIMULATED_WINDOWS_UPDATE=$SimulateWindowsUpdate"
# A plain WPF host avoids the app's single-instance and startup lifecycle.
$application = [Windows.Application]::new()
$application.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$window = $null

try {
    $themeUri = [Uri]::new('/HDRCalibrate;component/Theme.xaml', [UriKind]::Relative)
    $application.Resources.MergedDictionaries.Add([Windows.Application]::LoadComponent($themeUri))
    $trayIconMethod = $assembly.GetType('HDRCalibrate.App').GetMethod(
        'LoadTrayIcon', [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic)
    $trayIcon = $trayIconMethod.Invoke($null, @())
    try {
        if ($trayIcon.Width -lt 16 -or $trayIcon.Height -lt 16) { throw 'Tray icon resolution is invalid.' }
        "TRAY_ICON=$($trayIcon.Width)x$($trayIcon.Height)"
    }
    finally { $trayIcon.Dispose() }
    $window = [Windows.Window][Activator]::CreateInstance($windowType, @($captureViewModel))
    $window.Width = $WindowWidth
    $window.Height = $WindowHeight
    $window.Show()
    $initialization = $window.DataContext.InitializeAsync()

    [HdrCaptureWait]::Complete($initialization, $WaitMilliseconds)

    $window.UpdateLayout()
    if ($null -eq $window.Icon) { throw 'The window icon is missing.' }
    if ($window.DataContext.IsBusy) { throw 'The screenshot would show an incomplete initialization.' }
    $headerActions = $window.Content.Children[0].Child.Children[1]
    $hdrSettingsButton = $headerActions.Children[1]
    $hdrButtonBounds = $hdrSettingsButton.TransformToAncestor($window).TransformBounds(
        [Windows.Rect]::new(0, 0, $hdrSettingsButton.ActualWidth, $hdrSettingsButton.ActualHeight))
    if ($hdrButtonBounds.Left -lt 0 -or $hdrButtonBounds.Right -gt $window.ActualWidth) {
        throw 'The Windows HDR settings button is clipped by the window.'
    }
    "HEADER_BUTTON_RIGHT=$($hdrButtonBounds.Right)/$($window.ActualWidth)"
    if ($SimulateWindowsUpdate) {
        if ($captureViewModel.IsStartupActivationEnabled -or $null -eq $capturePolicyStore.Value -or
            -not $capturePolicyStore.Value.ApprovalRevoked) {
            throw 'The simulated Windows update did not revoke startup approval.'
        }
        'SIMULATED_UPDATE_APPROVAL_REVOKED=True'
    }
    $contentScroll = $window.Content.Children |
        Where-Object { $_ -is [Windows.Controls.ScrollViewer] } | Select-Object -First 1
    if ($null -eq $contentScroll) { throw 'The main content scroll viewer is missing.' }
    if ($ScrollToBottom) {
        $contentScroll.ScrollToEnd()
        [HdrCaptureWait]::Complete($initialization, 300)
        $window.UpdateLayout()
    }
    "CONTENT_SCROLL=$($contentScroll.VerticalOffset)/$($contentScroll.ScrollableHeight)"
    $headerLogoField = $windowType.GetField(
        'HeaderLogo',
        [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Public)
    $headerLogo = if ($null -eq $headerLogoField) { $null } else { $headerLogoField.GetValue($window) }
    if ($null -ne $headerLogo -and $null -ne $headerLogo.Source) {
        "HEADER_LOGO_SOURCE=$($headerLogo.Source)"
        "HEADER_LOGO_PIXELS=$($headerLogo.Source.PixelWidth)x$($headerLogo.Source.PixelHeight)"
    }
    else {
        "HEADER_LOGO_SOURCE=$(if ($null -eq $headerLogo) { 'control-missing' } else { 'image-source-missing' })"
    }
    $windowHandle = [Windows.Interop.WindowInteropHelper]::new($window).Handle
    [HdrWindowCapture]::Save($windowHandle, $screenshotPath)

    "SCREENSHOT=$screenshotPath"
    "CAPTURE=PrintWindow"
    "INITIALIZATION=$($initialization.Status)"
    "UI_STATUS=$($window.DataContext.StatusMessage)"
}
finally {
    if ($null -ne $window) { $window.Close() }
    if ($null -ne $application) { $application.Shutdown() }
}
