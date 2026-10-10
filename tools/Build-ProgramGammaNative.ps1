[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.7.3',
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# MSBuild can inherit a PowerShell 7 module search path while starting Windows
# PowerShell 5.1. Load the utility module that belongs to this exact host.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
$nativeRepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$nativeRoot = Join-Path $nativeRepoRoot 'artifacts\program-gamma-native'
$nativeOutput = Join-Path $nativeRoot $Version
$nativeDependency = Join-Path $nativeRepoRoot 'artifacts\deps\minhook-v1.3.4'
$nativePin = 'c3fcafdc10146beb5919319d0683e44e3c30d537'
$nativeControllerBuild = Join-Path $PSScriptRoot 'SelectiveDwm\Build-SelectiveDwm.ps1'
$nativeHookBuild = Join-Path $PSScriptRoot 'SelectiveDwm\Hook\BuildHook.ps1'
$nativeManifest = Join-Path $nativeOutput 'ProgramGammaNativeManifest.xml'
$nativeStatePath = Join-Path $nativeOutput 'build-state.json'
$nativeGit = (Get-Command 'git.exe' -ErrorAction Stop).Source

function Get-TextSha256([string]$Text) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-', '')
    } finally { $hasher.Dispose() }
}

function Assert-PinnedMinHook {
    if (-not (Test-Path -LiteralPath $nativeDependency -PathType Container)) {
        throw "Missing MinHook source: $nativeDependency. Supply the official source at commit $nativePin."
    }
    $revision = @(& $nativeGit -C $nativeDependency rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $revision.Count -ne 1 -or $revision[0].Trim() -cne $nativePin) {
        throw "MinHook must be checked out at the exact reviewed commit $nativePin."
    }
    $changes = @(& $nativeGit -C $nativeDependency status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
        throw 'MinHook has modified or untracked files; native packaging was refused.'
    }
    # Do not trust a clean status that hides compiled inputs from worktree checks.
    $tracked = @(& $nativeGit -C $nativeDependency ls-files -v -- include src LICENSE.txt)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0 -or @($tracked | Where-Object { $_ -cmatch '^[a-zS] ' }).Count -ne 0) {
        throw 'MinHook input files must be tracked without assume-unchanged or skip-worktree flags.'
    }
}

function Get-NativeInputs {
    $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio vswhere is required for a verified native build.' }
    $installations = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) { throw 'A Visual Studio x64 C++ toolchain is required.' }
    $installation = $installations[0].Trim()
    $compilerVersion = (Get-Content -LiteralPath (Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
    $compilerDirectory = Join-Path $installation "VC\Tools\MSVC\$compilerVersion"
    $sdkRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10'
    $sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and
            (Test-Path -LiteralPath (Join-Path $sdkRoot "Lib\$($_.Name)\um\x64\kernel32.lib"))
        } | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
    if (-not $sdkVersion) { throw 'A complete Windows x64 SDK is required.' }

    $inputs = [Collections.Generic.List[string]]::new()
    $inputs.Add('Format=1')
    $inputs.Add("Version=$Version")
    $inputs.Add("MinHook=$nativePin")
    $inputs.Add("MSVC=$compilerVersion")
    $inputs.Add("WindowsSDK=$sdkVersion")
    foreach ($tool in @('cl.exe', 'link.exe', 'lib.exe')) {
        $toolPath = Join-Path $compilerDirectory "bin\Hostx64\x64\$tool"
        $inputs.Add("$tool=" + (Get-FileHash -LiteralPath $toolPath -Algorithm SHA256).Hash)
    }
    $sourcePaths = @(
        $PSCommandPath
        $nativeControllerBuild
        $nativeHookBuild
        (Join-Path $nativeDependency 'LICENSE.txt')
    )
    foreach ($directory in @(
        (Join-Path $PSScriptRoot 'SelectiveDwm\Controller'),
        (Join-Path $PSScriptRoot 'SelectiveDwm\Hook'),
        (Join-Path $PSScriptRoot 'SelectiveDwm\include'),
        (Join-Path $nativeDependency 'include'),
        (Join-Path $nativeDependency 'src')
    )) {
        $sourcePaths += Get-ChildItem -LiteralPath $directory -File -Recurse |
            Where-Object { $_.Extension -in @('.c', '.cpp', '.h', '.hpp') } |
            Select-Object -ExpandProperty FullName
    }
    foreach ($sourcePath in @($sourcePaths | Sort-Object -Unique)) {
        $relative = $sourcePath.Substring($nativeRepoRoot.Length + 1).Replace('\', '/')
        $inputs.Add($relative + '=' + (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash)
    }
    return Get-TextSha256 ($inputs -join [Environment]::NewLine)
}

function Assert-X64NativePe([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Not a PE file: $Path" }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 26) { throw "Invalid PE offset: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Expected an x64 PE image: $Path"
        }
        $stream.Position = $offset + 24
        if ($reader.ReadUInt16() -ne 0x20B) { throw "Expected a PE32+ image: $Path" }
    } finally { $stream.Dispose() }
}

function Get-NativeManifestText($State) {
    return '<ProgramGammaNative Version="' + $Version + '"><ControllerSha256>' +
        $State.ControllerSha256 + '</ControllerSha256><HookSha256>' +
        $State.HookSha256 + '</HookSha256></ProgramGammaNative>'
}

function Write-AtomicUtf8([string]$Path, [string]$Content) {
    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and [IO.File]::ReadAllText($Path) -ceq $Content) { return }
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, $Content, [Text.UTF8Encoding]::new($false))
        # Windows PowerShell 5.1 coerces $null to an empty string for this
        # overload; File.Replace rejects that as an invalid backup path.
        if (Test-Path -LiteralPath $Path -PathType Leaf) { [IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
    }
}

function Get-VerifiedCachedState([string]$InputSha256) {
    try {
        if (-not (Test-Path -LiteralPath $nativeStatePath -PathType Leaf)) { return $null }
        if ((Get-Item -LiteralPath $nativeStatePath).Length -gt 16384) { return $null }
        $state = Get-Content -LiteralPath $nativeStatePath -Raw | ConvertFrom-Json
        if ($state.Format -ne 1 -or $state.Version -cne $Version -or $state.MinHookCommit -cne $nativePin -or
            $state.InputSha256 -cne $InputSha256) { return $null }
        foreach ($item in @(
            @{ Name = 'HDRCalibrate.SelectiveController.exe'; Hash = $state.ControllerSha256; Pe = $true },
            @{ Name = 'HDRCalibrate.SelectiveDwmHook.dll'; Hash = $state.HookSha256; Pe = $true },
            @{ Name = 'MinHook-LICENSE.txt'; Hash = $state.LicenseSha256; Pe = $false }
        )) {
            $path = Join-Path $nativeOutput $item.Name
            if ($item.Hash -cnotmatch '^[0-9A-F]{64}$' -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $item.Hash) { return $null }
            if ($item.Pe) { Assert-X64NativePe $path }
        }
        if ($state.LicenseSha256 -cne (Get-FileHash -LiteralPath (Join-Path $nativeDependency 'LICENSE.txt') -Algorithm SHA256).Hash) {
            return $null
        }
        return $state
    } catch {
        Write-Verbose ("Cached native build is not verifiable: " + $_.Exception.Message)
        return $null
    }
}

# Serialize normal builds, including separate MSBuild processes, because the
# existing controller builder uses one repository-local object directory.
$mutexName = 'Local\HDRCalibrate.ProgramGammaNative.' + (Get-TextSha256 $nativeRepoRoot).Substring(0, 24)
$nativeMutex = [Threading.Mutex]::new($false, $mutexName)
$nativeMutexAcquired = $false
try {
    try { $nativeMutexAcquired = $nativeMutex.WaitOne(45000) }
    catch [Threading.AbandonedMutexException] { $nativeMutexAcquired = $true }
    if (-not $nativeMutexAcquired) { throw 'Another native build is still using the repository object directory.' }
    Assert-PinnedMinHook
    $inputHash = Get-NativeInputs
    $cached = Get-VerifiedCachedState $inputHash
    if ($null -ne $cached) {
        $manifestText = Get-NativeManifestText $cached
        if ($VerifyOnly) {
            if (-not (Test-Path -LiteralPath $nativeManifest -PathType Leaf) -or
                [IO.File]::ReadAllText($nativeManifest) -cne $manifestText) {
                throw 'The native resource manifest is missing or differs from the verified build.'
            }
        } else { Write-AtomicUtf8 $nativeManifest $manifestText }
        Write-Output "Program gamma native bundle verified; source and output hashes unchanged: $nativeOutput"
        return
    }
    if ($VerifyOnly) { throw 'No native bundle matches the current verified sources and toolchain. Run the native build first.' }

    # Every compiler output goes into a fresh directory under the new versioned
    # bundle root. The live experimental v3/bin directory is never a destination.
    $stage = [IO.Path]::GetFullPath((Join-Path $nativeOutput ('build-' + [Guid]::NewGuid().ToString('N'))))
    if (-not $stage.StartsWith($nativeRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native staging escaped its dedicated artifact directory.'
    }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        & $nativeControllerBuild -OutputDirectory $stage
        & $nativeHookBuild -RepositoryRoot $nativeRepoRoot -OutputDirectory $stage -MinHookDirectory $nativeDependency -MinHookLibrary (Join-Path $stage 'minhook-x64.lib')
        Assert-PinnedMinHook
        if ((Get-NativeInputs) -cne $inputHash) { throw 'Native sources or the toolchain changed during compilation; no bundle was published.' }

        $controller = Join-Path $stage 'HDRCalibrate.SelectiveController.exe'
        $hook = Join-Path $stage 'HDRCalibrate.SelectiveDwmHook.dll'
        $license = Join-Path $stage 'MinHook-LICENSE.txt'
        Assert-X64NativePe $controller
        Assert-X64NativePe $hook
        $state = [ordered]@{
            Format = 1
            Version = $Version
            MinHookCommit = $nativePin
            InputSha256 = $inputHash
            ControllerSha256 = (Get-FileHash -LiteralPath $controller -Algorithm SHA256).Hash
            HookSha256 = (Get-FileHash -LiteralPath $hook -Algorithm SHA256).Hash
            LicenseSha256 = (Get-FileHash -LiteralPath $license -Algorithm SHA256).Hash
        }
        if ($state.LicenseSha256 -cne (Get-FileHash -LiteralPath (Join-Path $nativeDependency 'LICENSE.txt') -Algorithm SHA256).Hash) {
            throw 'The packaged MinHook license does not match the pinned source.'
        }
        # Copy the potentially locked DLL first. Failure preserves the old manifest
        # and never triggers an unload, DWM restart, or system restart.
        Copy-Item -LiteralPath $hook -Destination (Join-Path $nativeOutput 'HDRCalibrate.SelectiveDwmHook.dll') -Force
        Copy-Item -LiteralPath $controller -Destination (Join-Path $nativeOutput 'HDRCalibrate.SelectiveController.exe') -Force
        Copy-Item -LiteralPath $license -Destination (Join-Path $nativeOutput 'MinHook-LICENSE.txt') -Force
        Write-AtomicUtf8 $nativeManifest (Get-NativeManifestText $state)
        Write-AtomicUtf8 $nativeStatePath ($state | ConvertTo-Json)
        if ($null -eq (Get-VerifiedCachedState $inputHash)) { throw 'The completed native bundle failed its hash readback.' }
        Write-Output "Program gamma native bundle built and verified: $nativeOutput"
    } finally {
        # Staging holds only intermediate copies; the published bundle lives in
        # $nativeOutput. Remove exactly the validated, freshly created directory.
        if ($stage -and $stage.StartsWith($nativeRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $stage -PathType Container)) {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
} finally {
    if ($nativeMutexAcquired) { $nativeMutex.ReleaseMutex() }
    $nativeMutex.Dispose()
}
