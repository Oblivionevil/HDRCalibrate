param(
    [string]$TracePath,
    [long]$ShownQpc,
    [long]$HiddenQpc,
    [long]$QpcFrequency = 10000000,
    [int]$TargetProcessId,
    [string]$OutputPath,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Get-NumericStats {
    param([object[]]$Values)
    $numbers = @(
        foreach ($value in $Values) {
            $number = 0.0
            if ([double]::TryParse([string]$value, [Globalization.NumberStyles]::Float,
                    $invariant, [ref]$number) -and
                -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)) {
                $number
            }
        }
    ) | Sort-Object
    if (@($numbers).Count -eq 0) { return $null }
    $numbers = @($numbers)
    function Percentile([double]$fraction) {
        $position = ($numbers.Count - 1) * $fraction
        $left = [int][Math]::Floor($position)
        $right = [int][Math]::Ceiling($position)
        [Math]::Round($numbers[$left] + ($numbers[$right] - $numbers[$left]) * ($position - $left), 4)
    }
    [ordered]@{
        Count = $numbers.Count
        Mean = [Math]::Round(($numbers | Measure-Object -Average).Average, 4)
        Median = Percentile 0.5
        P95 = Percentile 0.95
        P99 = Percentile 0.99
        Min = $numbers[0]
        Max = $numbers[-1]
    }
}

function Get-Phase {
    param([object[]]$Frames, [string]$Name, [long]$StartQpc, [long]$EndQpc)
    $selected = @($Frames | Where-Object {
        [long]$_.QPCTime -ge $StartQpc -and [long]$_.QPCTime -lt $EndQpc
    })
    $displayed = @($selected | Where-Object { $_.Dropped -eq '0' })
    $modes = @($selected | Group-Object PresentMode | ForEach-Object {
        [ordered]@{ Mode = $_.Name; Count = $_.Count }
    })
    [ordered]@{
        Name = $Name
        StartQpc = $StartQpc
        EndQpc = $EndQpc
        Frames = $selected.Count
        DisplayedFrames = $displayed.Count
        DroppedFrames = @($selected | Where-Object { $_.Dropped -eq '1' }).Count
        PresentModes = $modes
        Swapchains = @($selected | Group-Object SwapChainAddress | ForEach-Object {
            [ordered]@{ Address = $_.Name; Count = $_.Count }
        })
        PresentIntervalMs = Get-NumericStats @($selected | ForEach-Object { $_.msBetweenPresents })
        PresentToDisplayMs = Get-NumericStats @($displayed | ForEach-Object { $_.msUntilDisplayed })
        DisplayChangeIntervalMs = Get-NumericStats @($displayed | ForEach-Object { $_.msBetweenDisplayChange })
    }
}

if ($SelfTest) {
    $stats = Get-NumericStats @('NA', 'NaN', 'Infinity', '1', '2', '3', '4', '5')
    if ($stats.Count -ne 5 -or $stats.Median -ne 3 -or $stats.P95 -ne 4.8 -or $stats.Mean -ne 3) {
        throw 'Numeric summary regression.'
    }
    if ($null -ne (Get-NumericStats @('NA'))) { throw 'Missing values must remain unavailable.' }
    $one = Get-NumericStats @('2.5')
    if ($one.P99 -ne 2.5) { throw 'Single-value percentile regression.' }
    $fixtures = @(
        [pscustomobject]@{ QPCTime = 100; Dropped = '0'; PresentMode = 'Composed: Flip'; SwapChainAddress = '0x1'; msBetweenPresents = 4; msUntilDisplayed = 2; msBetweenDisplayChange = 4 },
        [pscustomobject]@{ QPCTime = 101; Dropped = '1'; PresentMode = 'Composed: Flip'; SwapChainAddress = '0x1'; msBetweenPresents = 5; msUntilDisplayed = 0; msBetweenDisplayChange = 0 },
        [pscustomobject]@{ QPCTime = 200; Dropped = '0'; PresentMode = 'Hardware Composed: Independent Flip'; SwapChainAddress = '0x1'; msBetweenPresents = 4; msUntilDisplayed = 3; msBetweenDisplayChange = 4 }
    )
    $phase = Get-Phase $fixtures 'fixture' 100 200
    if ($phase.Frames -ne 2 -or $phase.DroppedFrames -ne 1 -or $phase.PresentToDisplayMs.Count -ne 1 -or $phase.PresentToDisplayMs.Median -ne 2) {
        throw 'Phase boundaries or dropped-frame exclusion regression.'
    }
    $empty = Get-Phase $fixtures 'empty' 300 400
    if ($empty.Frames -ne 0 -or $null -ne $empty.PresentIntervalMs) { throw 'Empty-phase regression.' }
    'PASS: numeric values, missing data, singleton, half-open boundaries, dropped exclusion, empty phase.'
    exit 0
}

if (-not $TracePath -or $TargetProcessId -le 0 -or $QpcFrequency -le 0 -or $ShownQpc -le 0 -or $HiddenQpc -le $ShownQpc) {
    throw 'TracePath, TargetProcessId, positive QpcFrequency and valid shown/hidden timestamps are required.'
}
$rows = @(Import-Csv -LiteralPath $TracePath)
if ($rows.Count -eq 0) { throw 'No frames captured.' }
foreach ($column in @('ProcessID', 'QPCTime', 'Dropped', 'PresentMode', 'SwapChainAddress', 'msBetweenPresents', 'msUntilDisplayed', 'msBetweenDisplayChange')) {
    if ($rows[0].PSObject.Properties.Name -notcontains $column) { throw "Missing PresentMon v1 column: $column" }
}
$rows = @($rows | Where-Object { [int]$_.ProcessID -eq $TargetProcessId })
if ($rows.Count -eq 0) { throw 'Trace has no frames from the selected process.' }
$margin = [long]($QpcFrequency / 4)
$span = [long]($QpcFrequency * 5)
if ($HiddenQpc - $ShownQpc -le 2 * $margin) { throw 'Visible interval too short for stable-phase analysis.' }
$result = [ordered]@{
    TargetProcessId = $TargetProcessId
    TracePath = [IO.Path]::GetFullPath($TracePath)
    QpcFrequency = $QpcFrequency
    VisibleDurationMs = [Math]::Round(($HiddenQpc - $ShownQpc) * 1000.0 / $QpcFrequency, 3)
    TransitionMarginMs = 250
    Phases = @(
        Get-Phase $rows 'baseline' ($ShownQpc - $span) ($ShownQpc - $margin)
        Get-Phase $rows 'overlay' ($ShownQpc + $margin) ($HiddenQpc - $margin)
        Get-Phase $rows 'recovery' ($HiddenQpc + $margin) ($HiddenQpc + $span)
    )
    Limitations = @(
        'PresentToDisplayMs is ETW-derived present-to-display timing, NOT input-to-photon latency.',
        'PresentMode and display intervals alone do NOT prove active monitor VRR.',
        'Foreground stability, source SDR state, scene stability and VRR status require separate checks.',
        'A mixed-mode phase or multiple swapchains must be inspected before drawing conclusions.'
    )
}
$json = $result | ConvertTo-Json -Depth 10
if ($OutputPath) {
    if (Test-Path -LiteralPath $OutputPath) { throw 'Output already exists; choose a new report path.' }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json, [Text.UTF8Encoding]::new($false))
}
$json
