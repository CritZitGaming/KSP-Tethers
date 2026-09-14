<#
.SYNOPSIS
    Draws placeholder toolbar icons (KSPTethers/Icons/tether_38.png for the stock launcher and
    tether_24.png for Blizzy's toolbar): a looping white umbilical with a clip ring.

.DESCRIPTION
    The shipped icons are hand-made artwork; this script only exists to regenerate simple placeholders and
    will not overwrite existing icons unless -Force is given.
#>
param([switch]$Force)

Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "..\KSPTethers\Icons"
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-TetherIcon([int]$size, [string]$path) {
    if ((Test-Path $path) -and -not $Force) {
        Write-Host "Keeping existing $path (use -Force to replace it with a placeholder)"
        return
    }
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 38.0

    # The umbilical: from the capsule hatch (lower left), one lazy loop, up to the kerbal (upper right).
    $rope = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(2.0, 3.2 * $s))
    $rope.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $rope.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pts = @(
        (New-Object System.Drawing.PointF (5 * $s), (33 * $s)),
        (New-Object System.Drawing.PointF (4 * $s), (18 * $s)),
        (New-Object System.Drawing.PointF (22 * $s), (14 * $s)),
        (New-Object System.Drawing.PointF (19 * $s), (25 * $s)),
        (New-Object System.Drawing.PointF (13 * $s), (31 * $s)),
        (New-Object System.Drawing.PointF (10 * $s), (20 * $s)),
        (New-Object System.Drawing.PointF (29 * $s), (9 * $s))
    )
    $g.DrawCurve($rope, [System.Drawing.PointF[]]$pts, 0.6)

    # Clip plate at the hatch end and the backpack ring at the kerbal end.
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillRectangle($brush, [single](1 * $s), [single](32 * $s), [single](9 * $s), [single](3.5 * $s))
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 2.4 * $s))
    $g.DrawEllipse($ring, [single](27 * $s), [single](3 * $s), [single](8 * $s), [single](8 * $s))

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "Wrote $path"
}

New-TetherIcon 38 (Join-Path $outDir "tether_38.png")
New-TetherIcon 24 (Join-Path $outDir "tether_24.png")
