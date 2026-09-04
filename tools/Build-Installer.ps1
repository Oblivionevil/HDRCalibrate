[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$IsccPath,

    [switch]$NoBootstrap
)

$ErrorActionPreference = 'Stop'
$expectedPublisher = 'CN=Pyrsys B.V., O=Pyrsys B.V., S=Noord-Holland, C=NL'
$bootstrapVersion = '7.1.0'
$bootstrapUri = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
$bootstrapSha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerRoot = Join-Path $repoRoot 'installer'
$artifactRoot = Join-Path $installerRoot 'artifacts'
$stagingRoot = Join-Path $artifactRoot 'staging'
$payloadDir = Join-Path $stagingRoot 'app'
$supportDir = Join-Path $stagingRoot 'support'
$outputDir = Join-Path $artifactRoot 'output'
$appProject = Join-Path $repoRoot 'src\HDRCalibrate\HDRCalibrate.csproj'
$dwmProject = Join-Path $repoRoot 'src\HDRCalibrate.Dwm\HDRCalibrate.Dwm.csproj'
$helperProject = Join-Path $repoRoot 'src\HDRCalibrate.DwmHelper\HDRCalibrate.DwmHelper.csproj'
$supportProject = Join-Path $installerRoot 'HDRCalibrate.SetupSupport\HDRCalibrate.SetupSupport.csproj'
$installerScript = Join-Path $installerRoot 'HDRCalibrate.iss'
$appIconFile = Join-Path $repoRoot 'src\HDRCalibrate\Assets\HDRCalibrate.ico'

function Assert-PathInsideInstaller {
    param([Parameter(Mandatory)][string]$Path)

    $installerPrefix = [IO.Path]::GetFullPath($installerRoot).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($installerPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a destructive operation outside the installer directory: $fullPath"
    }

    return $fullPath
}

function Assert-TrustedInnoFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Inno Setup file does not exist: $Path"
    }

    if ($ExpectedSha256) {
        $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        if (-not $actualHash.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Inno Setup SHA-256 mismatch. Expected $ExpectedSha256, got $actualHash."
        }
    }

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "Inno Setup Authenticode validation failed for '$Path': $($signature.Status)"
    }

    if ($signature.SignerCertificate.Subject -cne $expectedPublisher) {
        throw "Unexpected Inno Setup publisher '$($signature.SignerCertificate.Subject)'. Expected '$expectedPublisher'."
    }
}

function Get-InnoCompilerVersion {
    param([Parameter(Mandatory)][string]$Path)

    $versionOutput = (& $Path --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $versionOutput -match '^\d+(?:\.\d+)+$') {
        return [Version]$versionOutput
    }

    $helpOutput = (& $Path '/?' 2>&1 | Out-String)
    if ($helpOutput -notmatch 'Inno Setup (?<version>\d+(?:\.\d+)+) Command-Line Compiler') {
        throw "Could not determine Inno Setup compiler version from '$Path'."
    }

    return [Version]$Matches.version
}

function Find-InnoCompiler {
    param([string]$ExplicitPath)

    $candidates = [Collections.Generic.List[string]]::new()
    if ($ExplicitPath) {
        $candidates.Add($ExplicitPath)
    }
    if ($env:INNO_SETUP_COMPILER) {
        $candidates.Add($env:INNO_SETUP_COMPILER)
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        $candidates.Add($command.Source)
    }

    foreach ($base in @($env:LOCALAPPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $base) {
            continue
        }

        $programsBase = if ($base -eq $env:LOCALAPPDATA) { Join-Path $base 'Programs' } else { $base }
        foreach ($major in 7, 6) {
            $candidates.Add((Join-Path $programsBase "Inno Setup $major\ISCC.exe"))
        }
    }

    $candidates.Add((Join-Path $artifactRoot "toolcache\Inno Setup $bootstrapVersion\ISCC.exe"))

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $resolved = (Resolve-Path -LiteralPath $candidate).Path
            Assert-TrustedInnoFile -Path $resolved
            if ((Get-InnoCompilerVersion -Path $resolved).Major -ge 7) {
                return $resolved
            }
            Write-Verbose "Skipping trusted but too old Inno Setup compiler '$resolved'; x64 Setup requires Inno Setup 7 or later."
        }
    }

    return $null
}

function Install-PortableInnoCompiler {
    $toolDir = Assert-PathInsideInstaller (Join-Path $artifactRoot "toolcache\Inno Setup $bootstrapVersion")
    $compiler = Join-Path $toolDir 'ISCC.exe'
    if (Test-Path -LiteralPath $compiler -PathType Leaf) {
        Assert-TrustedInnoFile -Path $compiler
        return $compiler
    }

    New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
    $downloadPath = Join-Path ([IO.Path]::GetTempPath()) ("HDRCalibrate-InnoSetup-{0}.exe" -f [Guid]::NewGuid().ToString('N'))
    try {
        Write-Host "Downloading pinned Inno Setup $bootstrapVersion bootstrapper..."
        Invoke-WebRequest -Uri $bootstrapUri -OutFile $downloadPath
        Assert-TrustedInnoFile -Path $downloadPath -ExpectedSha256 $bootstrapSha256

        Write-Host "Extracting the verified compiler in portable mode to '$toolDir'..."
        & $downloadPath '/VERYSILENT' '/SUPPRESSMSGBOXES' '/NORESTART' '/PORTABLE=1' "/DIR=$toolDir"
        if ($LASTEXITCODE -ne 0) {
            throw "Portable Inno Setup bootstrap failed with exit code $LASTEXITCODE."
        }

        Assert-TrustedInnoFile -Path $compiler
        return $compiler
    }
    finally {
        if (Test-Path -LiteralPath $downloadPath) {
            Remove-Item -LiteralPath $downloadPath -Force
        }
    }
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$Description
    )

    Write-Host $Description
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "Not a PE file: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Invalid PE signature: $Path"
        }
        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-ProjectVersion {
    param([Parameter(Mandatory)][string]$Path)

    [xml]$projectXml = Get-Content -LiteralPath $Path -Raw
    $versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "No Version property was found in '$Path'."
    }
    return $versionNode.InnerText.Trim()
}

function Assert-MultiResolutionIcon {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Application icon does not exist: $Path"
    }

    $requiredSizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) {
            throw "Invalid ICO header: $Path"
        }
        $count = $reader.ReadUInt16()
        if ($count -lt $requiredSizes.Count) {
            throw "The application icon has only $count frames; at least $($requiredSizes.Count) are required."
        }

        $sizes = [Collections.Generic.List[int]]::new()
        for ($index = 0; $index -lt $count; $index++) {
            $widthByte = $reader.ReadByte()
            $heightByte = $reader.ReadByte()
            [void]$reader.ReadByte()
            [void]$reader.ReadByte()
            $planes = $reader.ReadUInt16()
            $bitsPerPixel = $reader.ReadUInt16()
            $dataLength = $reader.ReadUInt32()
            $dataOffset = $reader.ReadUInt32()
            $width = if ($widthByte -eq 0) { 256 } else { [int]$widthByte }
            $height = if ($heightByte -eq 0) { 256 } else { [int]$heightByte }
            if ($width -ne $height -or $planes -ne 1 -or $bitsPerPixel -ne 32) {
                throw "Invalid $($width)x$height ICO frame metadata in '$Path'."
            }
            if ($dataLength -le 8 -or ([UInt64]$dataOffset + [UInt64]$dataLength) -gt [UInt64]$stream.Length) {
                throw "ICO frame $width has an invalid data range in '$Path'."
            }

            $nextEntry = $stream.Position
            $stream.Position = $dataOffset
            $signature = $reader.ReadBytes(8)
            if (($signature -join ',') -ne '137,80,78,71,13,10,26,10') {
                throw "ICO frame $width is not a lossless PNG frame in '$Path'."
            }
            $stream.Position = $nextEntry
            $sizes.Add($width)
        }

        foreach ($requiredSize in $requiredSizes) {
            if (-not $sizes.Contains($requiredSize)) {
                throw "Application icon is missing the ${requiredSize}x${requiredSize} frame."
            }
        }
        return $sizes
    }
    finally {
        $stream.Dispose()
    }
}

$projectVersions = [ordered]@{
    App = Get-ProjectVersion -Path $appProject
    Dwm = Get-ProjectVersion -Path $dwmProject
    Helper = Get-ProjectVersion -Path $helperProject
    SetupSupport = Get-ProjectVersion -Path $supportProject
}
$appProjectVersion = $projectVersions.App
if (-not $Version) {
    $Version = $appProjectVersion
}
foreach ($entry in $projectVersions.GetEnumerator()) {
    if ($entry.Value -cne $Version) {
        throw "Version mismatch: requested '$Version', but $($entry.Key) declares '$($entry.Value)'."
    }
}

$iconFrameSizes = Assert-MultiResolutionIcon -Path $appIconFile
Write-Host "Validated application icon frames: $($iconFrameSizes -join ', ')."

$numericVersionMatch = [regex]::Match($Version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(?:\.(?<revision>\d+))?')
if (-not $numericVersionMatch.Success) {
    throw "Version '$Version' cannot be represented in Windows version metadata."
}
$revision = if ($numericVersionMatch.Groups['revision'].Success) { $numericVersionMatch.Groups['revision'].Value } else { '0' }
$versionInfoVersion = '{0}.{1}.{2}.{3}' -f `
    $numericVersionMatch.Groups['major'].Value,
    $numericVersionMatch.Groups['minor'].Value,
    $numericVersionMatch.Groups['build'].Value,
    $revision

$compilerPath = Find-InnoCompiler -ExplicitPath $IsccPath
if (-not $compilerPath) {
    if ($NoBootstrap) {
        throw 'No trusted Inno Setup compiler was found and bootstrap was disabled.'
    }
    $compilerPath = Install-PortableInnoCompiler
}

$compilerVersion = Get-InnoCompilerVersion -Path $compilerPath
if ($compilerVersion.Major -lt 7) {
    throw "Inno Setup 7 or later is required to produce a native x64 Setup. Found $compilerVersion."
}
Write-Host "Using trusted Inno Setup compiler $compilerVersion at '$compilerPath'."

$safeStagingRoot = Assert-PathInsideInstaller $stagingRoot
if (Test-Path -LiteralPath $safeStagingRoot) {
    Remove-Item -LiteralPath $safeStagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadDir, $supportDir, $outputDir -Force | Out-Null

$dotnet = (Get-Command 'dotnet.exe' -ErrorAction Stop).Source
$commonBuildArguments = @('--nologo', '--configuration', $Configuration, '-p:Platform=x64', '-p:ContinuousIntegrationBuild=true')
Invoke-CheckedNative -FilePath $dotnet -ArgumentList @(
    'build', $appProject
    $commonBuildArguments
    "-p:OutputPath=$payloadDir\"
) -Description "Building HDR Calibrate $Version for x64..."

Invoke-CheckedNative -FilePath $dotnet -ArgumentList @(
    'build', $supportProject
    $commonBuildArguments
    "-p:OutputPath=$supportDir\"
) -Description 'Building the x64 setup support helper...'

$requiredPayload = @(
    (Join-Path $payloadDir 'HDRCalibrate.exe'),
    (Join-Path $payloadDir 'HDRCalibrate.exe.config'),
    (Join-Path $payloadDir 'HDRCalibrate.Dwm.dll'),
    (Join-Path $payloadDir 'HDRCalibrate.DwmHelper.exe'),
    (Join-Path $payloadDir 'HDRCalibrate.DwmHelper.exe.config'),
    (Join-Path $payloadDir 'README.md'),
    (Join-Path $payloadDir 'THIRD-PARTY-NOTICES.md'),
    (Join-Path $supportDir 'HDRCalibrate.SetupSupport.exe'),
    (Join-Path $supportDir 'HDRCalibrate.SetupSupport.exe.config')
)
foreach ($file in $requiredPayload) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required installer payload is missing: $file"
    }
}

foreach ($peFile in @(
    (Join-Path $payloadDir 'HDRCalibrate.exe'),
    (Join-Path $payloadDir 'HDRCalibrate.Dwm.dll'),
    (Join-Path $payloadDir 'HDRCalibrate.DwmHelper.exe'),
    (Join-Path $supportDir 'HDRCalibrate.SetupSupport.exe')
)) {
    $machine = Get-PeMachine -Path $peFile
    if ($machine -ne 0x8664) {
        throw ("Expected an x64 PE (machine 0x8664), but '{0}' uses machine 0x{1:X4}." -f $peFile, $machine)
    }
}

$outputBaseName = "HDRCalibrate-Setup-v$Version-x64"
$compilerArguments = if ($compilerVersion.Major -ge 7) {
    @(
        "--output-dir=$outputDir",
        "--output-filename=$outputBaseName",
        "--define=AppVersion=$Version",
        "--define=VersionInfoVersion=$versionInfoVersion",
        "--define=PayloadDir=$payloadDir",
        "--define=SupportDir=$supportDir",
        "--define=AppIconFile=$appIconFile",
        '--no-ide-signtools',
        $installerScript
    )
}
else {
    @(
        "/O$outputDir",
        "/F$outputBaseName",
        "/DAppVersion=$Version",
        "/DVersionInfoVersion=$versionInfoVersion",
        "/DPayloadDir=$payloadDir",
        "/DSupportDir=$supportDir",
        "/DAppIconFile=$appIconFile",
        $installerScript
    )
}

Invoke-CheckedNative -FilePath $compilerPath -ArgumentList $compilerArguments -Description 'Compiling the per-user installer...'

$installerPath = Join-Path $outputDir "$outputBaseName.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup reported success but did not create '$installerPath'."
}

$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
$hashFile = "$installerPath.sha256.txt"
Set-Content -LiteralPath $hashFile -Value "$installerHash  $([IO.Path]::GetFileName($installerPath))" -Encoding ascii
$installerSignature = Get-AuthenticodeSignature -FilePath $installerPath

[pscustomobject]@{
    Installer = $installerPath
    Sha256File = $hashFile
    Sha256 = $installerHash
    SizeBytes = (Get-Item -LiteralPath $installerPath).Length
    Authenticode = [string]$installerSignature.Status
    Compiler = $compilerPath
    CompilerVersion = [string]$compilerVersion
}
