[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\HDRCalibrate\Assets\HDRCalibrate.ico'),
    [string]$PreviewPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\HDRCalibrate\Assets\HDRCalibrate.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [Parameter(Mandatory)][System.Drawing.RectangleF]$Bounds,
        [Parameter(Mandatory)][float]$Radius
    )

    $diameter = $Radius * 2.0
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconFrame {
    param([Parameter(Mandatory)][int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $inset = [Math]::Max(1.0, $Size * 0.055)
        $outer = [System.Drawing.RectangleF]::new(
            [single]$inset,
            [single]$inset,
            [single]($Size - 2.0 * $inset),
            [single]($Size - 2.0 * $inset))
        $radius = [single]($Size * 0.22)

        $shadowBounds = [System.Drawing.RectangleF]::new(
            [single]($outer.X + $Size * 0.02),
            [single]($outer.Y + $Size * 0.035),
            $outer.Width,
            $outer.Height)
        $shadowPath = New-RoundedRectanglePath -Bounds $shadowBounds -Radius $radius
        try {
            $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(95, 0, 0, 0))
            try { $graphics.FillPath($shadowBrush, $shadowPath) } finally { $shadowBrush.Dispose() }
        }
        finally { $shadowPath.Dispose() }

        $outerPath = New-RoundedRectanglePath -Bounds $outer -Radius $radius
        try {
            $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $outer,
                [System.Drawing.Color]::FromArgb(255, 24, 63, 57),
                [System.Drawing.Color]::FromArgb(255, 12, 31, 43),
                48.0)
            try { $graphics.FillPath($background, $outerPath) } finally { $background.Dispose() }

            $borderWidth = [single][Math]::Max(1.0, $Size * 0.025)
            $border = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 49, 135, 116), $borderWidth)
            try { $graphics.DrawPath($border, $outerPath) } finally { $border.Dispose() }
        }
        finally { $outerPath.Dispose() }

        $circleSize = [single]($Size * 0.52)
        $circle = [System.Drawing.RectangleF]::new(
            [single](($Size - $circleSize) / 2.0),
            [single]($Size * 0.22),
            $circleSize,
            $circleSize)

        $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 79, 224, 177))
        try {
            $oldClip = $graphics.Clip
            try {
                $graphics.SetClip([System.Drawing.RectangleF]::new($circle.X, $circle.Y, $circle.Width / 2.0, $circle.Height))
                $graphics.FillEllipse($fill, $circle)
            }
            finally {
                $graphics.Clip = $oldClip
                $oldClip.Dispose()
            }
        }
        finally { $fill.Dispose() }

        $ringWidth = [single][Math]::Max(1.25, $Size * 0.055)
        $ring = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 85, 239, 190), $ringWidth)
        try { $graphics.DrawEllipse($ring, $circle) } finally { $ring.Dispose() }

        $divider = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230, 85, 239, 190), [single][Math]::Max(1.0, $Size * 0.027))
        try {
            $x = [single]($circle.X + $circle.Width / 2.0)
            $graphics.DrawLine($divider, $x, [single]($circle.Y + $ringWidth / 2.0), $x, [single]($circle.Bottom - $ringWidth / 2.0))
        }
        finally { $divider.Dispose() }

        if ($Size -ge 24) {
            $accent = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 83, 165, 255), [single][Math]::Max(1.25, $Size * 0.035))
            $accent.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            try {
                $y = [single]($Size * 0.805)
                $graphics.DrawLine($accent, [single]($Size * 0.30), $y, [single]($Size * 0.70), $y)
            }
            finally { $accent.Dispose() }
        }
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
$preview = $null
try {
    foreach ($size in $sizes) {
        $bitmap = New-IconFrame -Size $size
        try {
            $stream = [IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($stream.ToArray())
            }
            finally { $stream.Dispose() }

            if ($size -eq 256) {
                $preview = [System.Drawing.Bitmap]$bitmap.Clone()
            }
        }
        finally { $bitmap.Dispose() }
    }

    $outputDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $file = [IO.File]::Open($OutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.BinaryWriter]::new($file)
        try {
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$sizes.Count)
            $offset = 6 + 16 * $sizes.Count
            for ($index = 0; $index -lt $sizes.Count; $index++) {
                $size = $sizes[$index]
                $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$frames[$index].Length)
                $writer.Write([UInt32]$offset)
                $offset += $frames[$index].Length
            }
            foreach ($frame in $frames) { $writer.Write($frame) }
        }
        finally { $writer.Dispose() }
    }
    finally { $file.Dispose() }

    if ($PreviewPath) {
        $previewDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PreviewPath))
        [IO.Directory]::CreateDirectory($previewDirectory) | Out-Null
        $preview.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
}
finally {
    if ($preview) { $preview.Dispose() }
}

[pscustomobject]@{
    Icon = [IO.Path]::GetFullPath($OutputPath)
    Preview = if ($PreviewPath) { [IO.Path]::GetFullPath($PreviewPath) } else { $null }
    Frames = $sizes -join ', '
}
