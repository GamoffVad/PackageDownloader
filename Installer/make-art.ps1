<#
    Рисует графику мастера установки: art/banner.bmp (493x58) и art/dialog.bmp (493x312).
    Размеры и 24-битный BMP — требование WixUI. Фон светлый: поверх него
    мастер рисует чёрный текст, поэтому тёмной оставляем только левую колонку.

    Запуск:  pwsh -File Installer/make-art.ps1
#>
Add-Type -AssemblyName System.Drawing

$art = Join-Path $PSScriptRoot 'art'
New-Item -ItemType Directory -Force $art | Out-Null

$violet    = [System.Drawing.Color]::FromArgb(139, 79, 245)
$blue      = [System.Drawing.Color]::FromArgb(124, 156, 255)
$mint      = [System.Drawing.Color]::FromArgb(74, 216, 149)
$lightBg   = [System.Drawing.Color]::FromArgb(247, 246, 251)
$darkTop   = [System.Drawing.Color]::FromArgb(40, 28, 78)
$darkBot   = [System.Drawing.Color]::FromArgb(16, 12, 32)

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear($lightBg)
    return @($bmp, $g)
}

# Изометрический кубик-«пакет»: верхняя грань светлее, боковые темнее.
function Draw-Cube($g, [double]$cx, [double]$cy, [double]$s, $color, [int]$alpha = 255) {
    $hw = $s; $hh = $s * 0.5
    $top = @(
        (New-Object System.Drawing.PointF(([float]$cx),        ([float]($cy - $hh)))),
        (New-Object System.Drawing.PointF(([float]($cx + $hw)), ([float]$cy))),
        (New-Object System.Drawing.PointF(([float]$cx),        ([float]($cy + $hh)))),
        (New-Object System.Drawing.PointF(([float]($cx - $hw)), ([float]$cy)))
    )
    $left = @(
        (New-Object System.Drawing.PointF(([float]($cx - $hw)), ([float]$cy))),
        (New-Object System.Drawing.PointF(([float]$cx),        ([float]($cy + $hh)))),
        (New-Object System.Drawing.PointF(([float]$cx),        ([float]($cy + $hh + $s)))),
        (New-Object System.Drawing.PointF(([float]($cx - $hw)), ([float]($cy + $s))))
    )
    $right = @(
        (New-Object System.Drawing.PointF(([float]($cx + $hw)), ([float]$cy))),
        (New-Object System.Drawing.PointF(([float]$cx),        ([float]($cy + $hh)))),
        (New-Object System.Drawing.PointF(([float]$cx),        ([float]($cy + $hh + $s)))),
        (New-Object System.Drawing.PointF(([float]($cx + $hw)), ([float]($cy + $s))))
    )
    $mk = { param($c, $k) New-Object System.Drawing.SolidBrush (
        [System.Drawing.Color]::FromArgb($alpha, [int]($c.R * $k), [int]($c.G * $k), [int]($c.B * $k))) }
    $bTop = & $mk $color 1.0; $bLeft = & $mk $color 0.62; $bRight = & $mk $color 0.82
    $g.FillPolygon($bTop, [System.Drawing.PointF[]]$top)
    $g.FillPolygon($bLeft, [System.Drawing.PointF[]]$left)
    $g.FillPolygon($bRight, [System.Drawing.PointF[]]$right)
    $bTop.Dispose(); $bLeft.Dispose(); $bRight.Dispose()
}

# ── dialog.bmp: тёмная колонка слева, светлое поле под текст справа ──────────
$pair = New-Canvas 493 312
$bmp = $pair[0]; $g = $pair[1]

$column = New-Object System.Drawing.Rectangle 0, 0, 164, 312
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $column, $darkTop, $darkBot, 70.0
$g.FillRectangle($grad, $column)
$grad.Dispose()

# Поток пакетов, уходящий вниз в «хранилище».
Draw-Cube $g 82 60  26 $violet
Draw-Cube $g 54 112 20 $blue
Draw-Cube $g 110 126 20 $violet
Draw-Cube $g 82 170 24 $mint 230
Draw-Cube $g 82 236 34 $violet 215

# Светящиеся «трассы» между пакетами.
$penGlow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 139, 79, 245)), 2.0
for ($y = 20; $y -lt 300; $y += 26) { $g.DrawLine($penGlow, 12, $y, 32, ($y + 12)) }
$penGlow.Dispose()

# Тонкая акцентная граница светлой части.
$penEdge = New-Object System.Drawing.Pen $violet, 3.0
$g.DrawLine($penEdge, 164, 0, 164, 312)
$penEdge.Dispose()

# Деликатный водяной знак на светлом поле.
Draw-Cube $g 437 246 26 ([System.Drawing.Color]::FromArgb(239, 236, 250)) 255
$bmp.Save((Join-Path $art 'dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose()

# ── banner.bmp: светлая полоса, акцент и кубики справа ──────────────────────
$pair = New-Canvas 493 58
$bmp = $pair[0]; $g = $pair[1]

Draw-Cube $g 404 20 11 $blue
Draw-Cube $g 428 30 11 $violet
Draw-Cube $g 452 20 11 $mint
Draw-Cube $g 466 36 9  $violet 180

$penBar = New-Object System.Drawing.Pen $violet, 3.0
$g.DrawLine($penBar, 0, 56, 493, 56)
$penBar.Dispose()

$bmp.Save((Join-Path $art 'banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose()

Get-ChildItem $art | ForEach-Object { "$($_.Name) — $([int]($_.Length / 1024)) КБ" }
