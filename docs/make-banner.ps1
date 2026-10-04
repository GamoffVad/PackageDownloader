<#
    Рисует docs/hero.png — заглавное изображение README (1280x420).
    Палитра совпадает с тёмной темой приложения.

    Запуск:  pwsh -File docs/make-banner.ps1
#>
Add-Type -AssemblyName System.Drawing

$W = 1280; $H = 420
$bmp = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

$violet = [System.Drawing.Color]::FromArgb(139, 79, 245)
$blue   = [System.Drawing.Color]::FromArgb(124, 156, 255)
$mint   = [System.Drawing.Color]::FromArgb(74, 216, 149)

# Фон: диагональный градиент тёмной темы.
$rect = New-Object System.Drawing.Rectangle 0, 0, $W, $H
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(22, 22, 42)),
        ([System.Drawing.Color]::FromArgb(34, 26, 64)), 20.0
$g.FillRectangle($bg, $rect); $bg.Dispose()

# Сетка-подложка.
$grid = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(16, 255, 255, 255)), 1.0
for ($x = 0; $x -lt $W; $x += 40) { $g.DrawLine($grid, $x, 0, $x, $H) }
for ($y = 0; $y -lt $H; $y += 40) { $g.DrawLine($grid, 0, $y, $W, $y) }
$grid.Dispose()

# Фиолетовое свечение справа — там, где поток пакетов.
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(760, 10, 520, 400)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
$glow.CenterColor    = [System.Drawing.Color]::FromArgb(110, 139, 79, 245)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 139, 79, 245))
$g.FillPath($glow, $glowPath)
$glow.Dispose(); $glowPath.Dispose()

function Draw-Cube($g, [double]$cx, [double]$cy, [double]$s, $color, [int]$alpha = 255) {
    $hw = $s; $hh = $s * 0.5
    $pt = { param($x, $y) New-Object System.Drawing.PointF(([float]$x), ([float]$y)) }
    $top   = @((& $pt $cx ($cy - $hh)), (& $pt ($cx + $hw) $cy), (& $pt $cx ($cy + $hh)), (& $pt ($cx - $hw) $cy))
    $left  = @((& $pt ($cx - $hw) $cy), (& $pt $cx ($cy + $hh)), (& $pt $cx ($cy + $hh + $s)), (& $pt ($cx - $hw) ($cy + $s)))
    $right = @((& $pt ($cx + $hw) $cy), (& $pt $cx ($cy + $hh)), (& $pt $cx ($cy + $hh + $s)), (& $pt ($cx + $hw) ($cy + $s)))
    $mk = { param($c, $k) New-Object System.Drawing.SolidBrush (
        [System.Drawing.Color]::FromArgb($alpha, [int]($c.R * $k), [int]($c.G * $k), [int]($c.B * $k))) }
    $bT = & $mk $color 1.0; $bL = & $mk $color 0.55; $bR = & $mk $color 0.78
    $g.FillPolygon($bT, [System.Drawing.PointF[]]$top)
    $g.FillPolygon($bL, [System.Drawing.PointF[]]$left)
    $g.FillPolygon($bR, [System.Drawing.PointF[]]$right)
    $bT.Dispose(); $bL.Dispose(); $bR.Dispose()
}

# Поток пакетов: из «облака» слева-сверху в защищённое хранилище справа-внизу.
$flow = New-Object System.Drawing.Drawing2D.GraphicsPath
$flow.AddBezier(820, 95, 900, 150, 980, 170, 1090, 250)
$penFlow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 139, 79, 245)), 26.0
$penFlow.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penFlow.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawPath($penFlow, $flow); $penFlow.Dispose(); $flow.Dispose()

Draw-Cube $g 830  90 26 $blue
Draw-Cube $g 905 140 22 $violet
Draw-Cube $g 975 165 20 $mint
Draw-Cube $g 1040 205 24 $violet
Draw-Cube $g 1105 262 40 $violet 240

# Щит вокруг хранилища — изоляция контура сети.
$shield = New-Object System.Drawing.Drawing2D.GraphicsPath
$shield.AddEllipse(1020, 180, 180, 180)
$penShield = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 74, 216, 149)), 2.0
$penShield.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
$g.DrawPath($penShield, $shield); $penShield.Dispose(); $shield.Dispose()

# Текст.
$fTitle = New-Object System.Drawing.Font 'Segoe UI', 54, ([System.Drawing.FontStyle]::Bold)
$fSub   = New-Object System.Drawing.Font 'Segoe UI', 19, ([System.Drawing.FontStyle]::Regular)
$fTag   = New-Object System.Drawing.Font 'Segoe UI Semibold', 14, ([System.Drawing.FontStyle]::Bold)
$white  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$muted  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(176, 176, 208))

$g.DrawString('Package', $fTitle, $white, 72, 112)
$brushAccent = New-Object System.Drawing.SolidBrush $violet
$g.DrawString('Downloader', $fTitle, $brushAccent, 72, 180)
$g.DrawString('Пакеты и модели вместе со всеми зависимостями —', $fSub, $muted, 78, 262)
$g.DrawString('для изолированных и корпоративных сетей', $fSub, $muted, 78, 290)

# Плашки экосистем.
$x = 78
foreach ($t in @('NuGet', 'NPM', 'PyPI', 'MCP', 'HuggingFace')) {
    $size = $g.MeasureString($t, $fTag)
    $w = [int]$size.Width + 26
    $chip = New-Object System.Drawing.Drawing2D.GraphicsPath
    $chip.AddArc($x, 338, 28, 28, 90, 180)
    $chip.AddArc(($x + $w - 28), 338, 28, 28, 270, 180)
    $chip.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(38, 139, 79, 245))
    $pen  = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 139, 79, 245)), 1.0
    $g.FillPath($fill, $chip); $g.DrawPath($pen, $chip)
    $g.DrawString($t, $fTag, $white, ($x + 13), 344)
    $fill.Dispose(); $pen.Dispose(); $chip.Dispose()
    $x += $w + 12
}

# Акцентная полоса снизу.
$barRect = New-Object System.Drawing.Rectangle 0, ($H - 5), $W, 5
$bar = New-Object System.Drawing.Drawing2D.LinearGradientBrush $barRect, $violet, $mint, 0.0
$g.FillRectangle($bar, $barRect); $bar.Dispose()

$bmp.Save((Join-Path $PSScriptRoot 'hero.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"hero.png готов"
