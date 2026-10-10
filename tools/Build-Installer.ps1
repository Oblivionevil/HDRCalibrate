[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

# Builds the self-contained .NET Framework 4.8.1 WPF setup (no third-party
# installer toolchain) and the portable ZIP from the same verified payload.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerRoot = Join-Path $repoRoot 'installer'
$artifactRoot = Join-Path $installerRoot 'artifacts'
$stagingRoot = Join-Path $artifactRoot 'staging'
$payloadDir = Join-Path $stagingRoot 'app'
$setupPayloadDir = Join-Path $stagingRoot 'setup-payload'
$setupBuildDir = Join-Path $stagingRoot 'setup'
$outputDir = Join-Path $artifactRoot 'output'
$appProject = Join-Path $repoRoot 'src\HDRCalibrate\HDRCalibrate.csproj'
$dwmProject = Join-Path $repoRoot 'src\HDRCalibrate.Dwm\HDRCalibrate.Dwm.csproj'
$helperProject = Join-Path $repoRoot 'src\HDRCalibrate.DwmHelper\HDRCalibrate.DwmHelper.csproj'
$setupProject = Join-Path $installerRoot 'HDRCalibrate.Setup\HDRCalibrate.Setup.csproj'
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

function Read-EmbeddedXml {
    param(
        [Parameter(Mandatory)][Reflection.Assembly]$Assembly,
        [Parameter(Mandatory)][string]$ResourceName,
        [Parameter(Mandatory)][int]$MaximumCharacters
    )

    $resource = $Assembly.GetManifestResourceStream($ResourceName)
    if ($null -eq $resource) { throw "Embedded resource '$ResourceName' is missing." }
    try {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = $MaximumCharacters
        $reader = [Xml.XmlReader]::Create($resource, $settings)
        try {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
            return $document
        } finally { $reader.Dispose() }
    } finally { $resource.Dispose() }
}

function Assert-NativeGammaManifest {
    param(
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$NativeDirectory,
        [Parameter(Mandatory)][string]$NativeVersion
    )

    # Reading assembly resources does not invoke App.OnStartup or any native code.
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($AppPath))
    if ($assembly.GetName().Version.ToString(3) -cne $NativeVersion) {
        throw 'The application assembly and native directory versions differ.'
    }
    $manifest = Read-EmbeddedXml -Assembly $assembly -ResourceName 'HDRCalibrate.ProgramGammaNativeManifest.xml' -MaximumCharacters 4096
    if ($manifest.DocumentElement.Name -cne 'ProgramGammaNative' -or
        $manifest.DocumentElement.GetAttribute('Version') -cne $NativeVersion) {
        throw 'The embedded native gamma manifest has the wrong root or version.'
    }
    foreach ($entry in @(
        @{ Element = 'ControllerSha256'; File = 'HDRCalibrate.SelectiveController.exe' },
        @{ Element = 'HookSha256'; File = 'HDRCalibrate.SelectiveDwmHook.dll' }
    )) {
        $nodes = $manifest.SelectNodes('/ProgramGammaNative/' + $entry.Element)
        if ($nodes.Count -ne 1 -or $nodes[0].InnerText -cnotmatch '^[0-9A-Fa-f]{64}$') {
            throw "The native gamma manifest has no unique valid $($entry.Element) value."
        }
        $actual = (Get-FileHash -LiteralPath (Join-Path $NativeDirectory $entry.File) -Algorithm SHA256).Hash
        if (-not $actual.Equals($nodes[0].InnerText, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Native payload differs from the embedded hash manifest: $($entry.File)"
        }
    }
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

function New-PackageZip {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string[]]$RelativePaths,
        [Parameter(Mandatory)][string]$Destination
    )

    $stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($relative in ($RelativePaths | Sort-Object)) {
                $sourcePath = Join-Path $SourceDirectory $relative
                $entry = $archive.CreateEntry($relative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset](Get-Item -LiteralPath $sourcePath).LastWriteTime
                $source = [IO.File]::OpenRead($sourcePath)
                try {
                    $target = $entry.Open()
                    try { $source.CopyTo($target) } finally { $target.Dispose() }
                } finally { $source.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

function New-PayloadManifest {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string[]]$RelativePaths,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string]$Destination
    )

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($Destination, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('SetupPayload')
        $writer.WriteAttributeString('Version', $PackageVersion)
        foreach ($relative in ($RelativePaths | Sort-Object)) {
            $sourcePath = Join-Path $SourceDirectory $relative
            $writer.WriteStartElement('File')
            $writer.WriteAttributeString('Path', $relative.Replace('\', '/'))
            $writer.WriteAttributeString('Size', [string](Get-Item -LiteralPath $sourcePath).Length)
            $writer.WriteAttributeString('Sha256', (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash)
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    } finally { $writer.Dispose() }
}

function Write-Sha256File {
    param([Parameter(Mandatory)][string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $hashFile = "$Path.sha256.txt"
    Set-Content -LiteralPath $hashFile -Value "$hash  $([IO.Path]::GetFileName($Path))" -Encoding ascii
    return [pscustomobject]@{ File = $Path; Sha256File = $hashFile; Sha256 = $hash; SizeBytes = (Get-Item -LiteralPath $Path).Length }
}

$projectVersions = [ordered]@{
    App = Get-ProjectVersion -Path $appProject
    Dwm = Get-ProjectVersion -Path $dwmProject
    Helper = Get-ProjectVersion -Path $helperProject
    Setup = Get-ProjectVersion -Path $setupProject
}
if (-not $Version) {
    $Version = $projectVersions.App
}
foreach ($entry in $projectVersions.GetEnumerator()) {
    if ($entry.Value -cne $Version) {
        throw "Version mismatch: requested '$Version', but $($entry.Key) declares '$($entry.Value)'."
    }
}

$iconFrameSizes = Assert-MultiResolutionIcon -Path $appIconFile
Write-Host "Validated application icon frames: $($iconFrameSizes -join ', ')."
$nativePayloadDir = Join-Path $payloadDir "Native\$Version"

$safeStagingRoot = Assert-PathInsideInstaller $stagingRoot
if (Test-Path -LiteralPath $safeStagingRoot) {
    Remove-Item -LiteralPath $safeStagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadDir, $setupPayloadDir, $setupBuildDir, $outputDir -Force | Out-Null

$dotnet = (Get-Command 'dotnet.exe' -ErrorAction Stop).Source
$commonBuildArguments = @('--nologo', '--configuration', $Configuration, '-p:Platform=x64', '-p:ContinuousIntegrationBuild=true')
Invoke-CheckedNative -FilePath $dotnet -ArgumentList @(
    'build', $appProject
    $commonBuildArguments
    "-p:OutputPath=$payloadDir\"
) -Description "Building HDR Calibrate $Version for x64..."

# The single source of truth for both the setup payload and the portable ZIP.
$payloadFiles = @(
    'HDRCalibrate.exe',
    'HDRCalibrate.exe.config',
    'HDRCalibrate.Dwm.dll',
    'HDRCalibrate.DwmHelper.exe',
    'HDRCalibrate.DwmHelper.exe.config',
    'README.md',
    'THIRD-PARTY-NOTICES.md',
    "Native\$Version\HDRCalibrate.SelectiveController.exe",
    "Native\$Version\HDRCalibrate.SelectiveDwmHook.dll",
    "Native\$Version\MinHook-LICENSE.txt"
)
foreach ($relative in $payloadFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadDir $relative) -PathType Leaf)) {
        throw "Required payload file is missing: $relative"
    }
}
$unexpectedLibraries = @(Get-ChildItem -LiteralPath $payloadDir -File -Filter '*.dll' |
    Where-Object { $_.Name -cne 'HDRCalibrate.Dwm.dll' })
if ($unexpectedLibraries.Count -ne 0) {
    throw "The build produced libraries that are not part of the payload list: $($unexpectedLibraries.Name -join ', ')"
}

foreach ($relative in @(
    'HDRCalibrate.exe', 'HDRCalibrate.Dwm.dll', 'HDRCalibrate.DwmHelper.exe',
    "Native\$Version\HDRCalibrate.SelectiveController.exe", "Native\$Version\HDRCalibrate.SelectiveDwmHook.dll"
)) {
    $machine = Get-PeMachine -Path (Join-Path $payloadDir $relative)
    if ($machine -ne 0x8664) {
        throw ("Expected an x64 PE (machine 0x8664), but '{0}' uses machine 0x{1:X4}." -f $relative, $machine)
    }
}

& (Join-Path $PSScriptRoot 'Build-ProgramGammaNative.ps1') -Version $Version -VerifyOnly
Assert-NativeGammaManifest -AppPath (Join-Path $payloadDir 'HDRCalibrate.exe') -NativeDirectory $nativePayloadDir -NativeVersion $Version
$sourceLicense = Join-Path $repoRoot 'artifacts\deps\minhook-v1.3.4\LICENSE.txt'
if ((Get-FileHash -LiteralPath (Join-Path $nativePayloadDir 'MinHook-LICENSE.txt') -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $sourceLicense -Algorithm SHA256).Hash) {
    throw 'The packaged MinHook license differs from the pinned source license.'
}

$payloadZip = Join-Path $setupPayloadDir 'HDRCalibrate.Payload.zip'
$payloadManifest = Join-Path $setupPayloadDir 'HDRCalibrate.PayloadManifest.xml'
New-PackageZip -SourceDirectory $payloadDir -RelativePaths $payloadFiles -Destination $payloadZip
New-PayloadManifest -SourceDirectory $payloadDir -RelativePaths $payloadFiles -PackageVersion $Version -Destination $payloadManifest

Invoke-CheckedNative -FilePath $dotnet -ArgumentList @(
    'build', $setupProject
    $commonBuildArguments
    "-p:OutputPath=$setupBuildDir\"
    "-p:SetupPayloadDirectory=$setupPayloadDir"
) -Description "Building the HDR Calibrate $Version setup..."

$builtSetup = Join-Path $setupBuildDir 'HDRCalibrate.Setup.exe'
if ((Get-PeMachine -Path $builtSetup) -ne 0x8664) {
    throw 'The setup is not an x64 executable.'
}
$setupAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($builtSetup))
if ($setupAssembly.GetName().Version.ToString(3) -cne $Version) {
    throw 'The setup assembly version differs from the package version.'
}
foreach ($resource in @(
    @{ Name = 'HDRCalibrate.Setup.Payload.zip'; File = $payloadZip },
    @{ Name = 'HDRCalibrate.Setup.PayloadManifest.xml'; File = $payloadManifest }
)) {
    $stream = $setupAssembly.GetManifestResourceStream($resource.Name)
    if ($null -eq $stream) { throw "The setup does not embed '$($resource.Name)'." }
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $embeddedHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
    if ($embeddedHash -cne (Get-FileHash -LiteralPath $resource.File -Algorithm SHA256).Hash) {
        throw "The setup embeds a different '$($resource.Name)' than the verified payload."
    }
}

$installerPath = Join-Path $outputDir "HDRCalibrate-Setup-v$Version-x64.exe"
$portablePath = Join-Path $outputDir "HDRCalibrate-v$Version-win-x64.zip"
foreach ($path in @($installerPath, "$installerPath.sha256.txt", $portablePath, "$portablePath.sha256.txt")) {
    $safePath = Assert-PathInsideInstaller $path
    if (Test-Path -LiteralPath $safePath) { Remove-Item -LiteralPath $safePath -Force }
}
Copy-Item -LiteralPath $builtSetup -Destination $installerPath
New-PackageZip -SourceDirectory $payloadDir -RelativePaths $payloadFiles -Destination $portablePath

$installer = Write-Sha256File -Path $installerPath
$portable = Write-Sha256File -Path $portablePath
[pscustomobject]@{
    Version = $Version
    Installer = $installer.File
    InstallerSha256 = $installer.Sha256
    InstallerBytes = $installer.SizeBytes
    Portable = $portable.File
    PortableSha256 = $portable.Sha256
    PortableBytes = $portable.SizeBytes
    Authenticode = [string](Get-AuthenticodeSignature -FilePath $installerPath).Status
}
