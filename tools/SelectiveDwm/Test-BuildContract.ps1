[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$DwmCorePath = (Join-Path $env:SystemRoot 'System32\dwmcore.dll')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# File-only inspection of an exact PE and compiled-header probes. This script
# never loads dwmcore, reads another process, invokes preflight, or installs hooks.
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactPrefix = (Join-Path $repository 'artifacts').TrimEnd('\') + '\'
if (-not $OutputDirectory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.Substring($artifactPrefix.Length) -notmatch '^native-9278-candidate-[A-Za-z0-9_-]+(\\.*)?$') {
    throw 'File-test output must be below artifacts\native-9278-candidate-<name>.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'File tests require a fresh output directory.' }
for ($ancestor = [IO.DirectoryInfo]::new($OutputDirectory); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'File-test output may not traverse a reparse point.' }
}
$null = New-Item -ItemType Directory -Path $OutputDirectory
$script:passed = 0
$script:failed = 0
function Check([string]$Name, [bool]$Condition) {
    if ($Condition) { ++$script:passed; Write-Output "PASS $Name" }
    else { ++$script:failed; Write-Output "FAIL $Name" }
}
function Rejects([scriptblock]$Action) {
    try { & $Action | Out-Null; return $false } catch { return $true }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'MSVC x64 compiler not found.' }
$devCmd = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
$environmentLines = & $env:ComSpec /d /c ('call "' + $devCmd + '" -no_logo -host_arch=x64 -arch=x64 >nul && set')
if ($LASTEXITCODE -ne 0) { throw 'Failed to initialize MSVC.' }
foreach ($line in $environmentLines) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
$contracts = @{}
foreach ($build in @('default', '261008875', '261009278', 'unsupported')) {
    $directory = Join-Path $OutputDirectory $build
    $null = New-Item -ItemType Directory -Path $directory
    $arguments = @('/nologo', '/std:c++17', '/W4', '/WX', '/EHsc', '/MT', '/O2',
        (Join-Path $PSScriptRoot 'ContractFileProbe.cpp'), "/Fo$directory\ContractFileProbe.obj", "/Fe$directory\ContractFileProbe.exe")
    if ($build -ne 'default') {
        $define = if ($build -eq 'unsupported') { '1' } else { $build }
        $arguments += '/DHDR_SELECTIVE_DWMCORE_BUILD=' + $define
    }
    & cl.exe @arguments 2>&1 | Out-File -LiteralPath (Join-Path $directory 'compile.log') -Encoding utf8
    if ($build -eq 'unsupported') { Check 'unknown-compile-time-build-is-rejected' ($LASTEXITCODE -ne 0); continue }
    if ($LASTEXITCODE -ne 0) { throw "Compiled-header probe failed: $directory\compile.log" }
    $json = & (Join-Path $directory 'ContractFileProbe.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Compiled-header probe failed.' }
    $contracts[$build] = $json | ConvertFrom-Json
    $json | Out-File -LiteralPath (Join-Path $directory 'contract.json') -Encoding utf8
}
$default = $contracts['default']
$candidate = $contracts['261009278']
Check 'no-define-default-is-8875' ($default.build -ceq '10.0.26100.8875' -and $default.sha256 -ceq '825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3')
Check 'explicit-8875-equals-no-define-default' (($default | ConvertTo-Json -Depth 6 -Compress) -ceq ($contracts['261008875'] | ConvertTo-Json -Depth 6 -Compress))
Check 'candidate-is-exact-9278-contract' ($candidate.build -ceq '10.0.26100.9278' -and $candidate.sha256 -ceq '40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222')
foreach ($entry in @($default, $candidate)) {
    Check ($entry.build + '-preflight-hex-and-hook-byte-hash-identical') ($entry.sha256 -ceq $entry.sha256_bytes)
    Check ($entry.build + '-all-15-RVAs-and-12-guards-present') (@($entry.rvas.PSObject.Properties).Count -eq 15 -and $entry.guards.Count -eq 12)
}
$map = Get-Content -LiteralPath (Join-Path $repository 'artifacts\dwm-9278-native-analysis\contract-map.json') -Raw | ConvertFrom-Json
Check 'independent-symbol-map-has-all-15-RVAs' ($map.Count -eq 15)
foreach ($entry in $map) {
    Check ($entry.Constant + '-compiled-old-and-new-match-independent-PDB-map') ($candidate.rvas.($entry.Constant) -eq [uint32]$entry.NewRvaValue -and
        $default.rvas.($entry.Constant) -eq [Convert]::ToUInt32($entry.OldRva.Substring(2), 16))
}

function Get-Pe([byte[]]$Bytes) {
    if ($Bytes.Length -lt 64 -or [BitConverter]::ToUInt16($Bytes, 0) -ne 0x5A4D) { throw 'Invalid DOS header.' }
    $pe = [BitConverter]::ToInt32($Bytes, 0x3C)
    if ($pe -lt 64 -or $pe -gt $Bytes.Length - 24 -or [BitConverter]::ToUInt32($Bytes, $pe) -ne 0x4550 -or
        [BitConverter]::ToUInt16($Bytes, $pe + 4) -ne 0x8664) { throw 'Invalid x64 PE header.' }
    $count = [BitConverter]::ToUInt16($Bytes, $pe + 6)
    $optional = [BitConverter]::ToUInt16($Bytes, $pe + 20)
    $table = $pe + 24 + $optional
    if ($optional -lt 112 -or $count -lt 1 -or $count -gt 96 -or $table + 40 * $count -gt $Bytes.Length -or
        [BitConverter]::ToUInt16($Bytes, $pe + 24) -ne 0x20B) { throw 'Truncated PE headers.' }
    $sections = @()
    for ($index = 0; $index -lt $count; ++$index) {
        $offset = $table + 40 * $index
        $sections += [pscustomobject]@{ Rva = [BitConverter]::ToUInt32($Bytes, $offset + 12); Size = [BitConverter]::ToUInt32($Bytes, $offset + 16); Raw = [BitConverter]::ToUInt32($Bytes, $offset + 20) }
    }
    return [pscustomobject]@{ Sections = $sections; ImageBase = [BitConverter]::ToUInt64($Bytes, $pe + 48) }
}
function Get-Offset($Pe, [long]$Rva, [int]$Length, [int]$FileLength) {
    foreach ($section in $Pe.Sections) {
        $delta = $Rva - [long]$section.Rva
        if ($delta -ge 0 -and $Length -gt 0 -and $delta + $Length -le [long]$section.Size) {
            $offset = [long]$section.Raw + $delta
            if ($offset -lt 0 -or $offset + $Length -gt $FileLength) { throw 'Truncated PE section.' }
            return [int]$offset
        }
    }
    throw 'RVA has no complete file-backed range.'
}
function GuardMatches([byte[]]$Bytes, $Pe, $Guard) {
    try {
        $length = [int]($Guard.bytes.Length / 2)
        $offset = Get-Offset $Pe $Guard.rva $length $Bytes.Length
        return [BitConverter]::ToString($Bytes, $offset, $length).Replace('-', '') -ceq $Guard.bytes
    } catch { return $false }
}
function ContractMatches([byte[]]$Bytes, $Contract) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        if ([BitConverter]::ToString($hasher.ComputeHash($Bytes)).Replace('-', '') -cne $Contract.sha256) { return $false }
        $pe = Get-Pe $Bytes
        foreach ($guard in $Contract.guards) { if (-not (GuardMatches $Bytes $pe $guard)) { return $false } }
        return $true
    } catch { return $false } finally { $hasher.Dispose() }
}
$bytes = [IO.File]::ReadAllBytes($DwmCorePath)
$pe = Get-Pe $bytes
Check 'installed-file-exact-9278-hash-and-all-code-guards' (ContractMatches $bytes $candidate)
Check 'default-8875-rejects-current-9278-file' (-not (ContractMatches $bytes $default))
$byteEvidence = Get-Content -LiteralPath (Join-Path $repository 'artifacts\dwm-9278-native-analysis\byte-comparison.json') -Raw | ConvertFrom-Json
foreach ($guard in $candidate.guards) {
    $evidence = @($byteEvidence | Where-Object { $_.Name -ceq $guard.name })
    Check ($guard.name + '-matches-independent-byte-evidence-and-current-file') ($evidence.Count -eq 1 -and
        $guard.bytes -ceq $evidence[0].NewBytes.Replace(',', '') -and (GuardMatches $bytes $pe $guard))
    $offset = Get-Offset $pe $guard.rva ([int]($guard.bytes.Length / 2)) $bytes.Length
    $bytes[$offset] = $bytes[$offset] -bxor 1
    try {
        Check ($guard.name + '-single-byte-mutation-rejected-by-byte-guard') (-not (GuardMatches $bytes $pe $guard))
        Check ($guard.name + '-single-byte-mutation-rejected-by-whole-file-identity') (-not (ContractMatches $bytes $candidate))
    } finally { $bytes[$offset] = $bytes[$offset] -bxor 1 }
}
foreach ($slot in @(
    @{ Table = 'kSurfacePixelFormatVtableRva'; Offset = 0x18; Target = 'kSurfacePixelFormatGetterRva' },
    @{ Table = 'kD2DBitmapPixelFormatVtableRva'; Offset = 0x18; Target = 'kD2DBitmapPixelFormatGetterRva' },
    @{ Table = 'kGlobalCompositionSurfaceVtableRva'; Offset = 0x38; Target = 'kSurfaceGetRealizationRva' },
    @{ Table = 'kGlobalCompositionSurfaceVtableRva'; Offset = 0x40; Target = 'kSurfaceGetBindRva' }
)) {
    $offset = Get-Offset $pe ($candidate.rvas.($slot.Table) + $slot.Offset) 8 $bytes.Length
    Check ($slot.Table + '-slot-' + $slot.Offset + '-has-exact-getter') ([BitConverter]::ToUInt64($bytes, $offset) - $pe.ImageBase -eq $candidate.rvas.($slot.Target))
}
Check 'empty-file-rejected' (-not (ContractMatches ([byte[]]@()) $candidate))
Check 'truncated-file-rejected' (-not (ContractMatches ([byte[]]$bytes[0..1023]) $candidate))
Check 'truncated-file-fails-independent-byte-bounds' (-not (GuardMatches ([byte[]]$bytes[0..1023]) $pe $candidate.guards[0]))
Check 'PE-parser-rejects-truncated-header' (Rejects { Get-Pe ([byte[]]$bytes[0..63]) })

foreach ($builder in @('Build-SelectiveDwm.ps1', 'Hook\BuildHook.ps1', 'Controller\Build-ControllerTests.ps1')) {
    $path = Join-Path $PSScriptRoot $builder
    Check ($builder + '-candidate-requires-explicit-output') (Rejects { & $path -DwmCoreBuild '26100.9278' })
    Check ($builder + '-candidate-rejects-frozen-v3') (Rejects { & $path -DwmCoreBuild '26100.9278' -OutputDirectory (Join-Path $repository 'artifacts\selective-dwm-20260906\v3\bin') })
    Check ($builder + '-candidate-rejects-versioned-payload') (Rejects { & $path -DwmCoreBuild '26100.9278' -OutputDirectory (Join-Path $repository 'artifacts\program-gamma-native\1.7.3') })
    Check ($builder + '-candidate-rejects-existing-output') (Rejects { & $path -DwmCoreBuild '26100.9278' -OutputDirectory $OutputDirectory })
    Check ($builder + '-rejects-unknown-build') (Rejects { & $path -DwmCoreBuild '26100.9999' -OutputDirectory $OutputDirectory })
}
Write-Output "RESULT $script:passed passed; $script:failed failed (compiled headers and files only; no live DWM access)"
if ($script:failed -ne 0) { throw 'Offline build-contract verification failed.' }
