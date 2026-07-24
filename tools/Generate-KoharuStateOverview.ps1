param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\docs\assets\koharu-states'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\assets\koharu-states\koharu-state-overview.png')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$states = @(
    @{ File = 'idle.png'; Label = '待機' },
    @{ File = 'walk.png'; Label = '右へ走る' },
    @{ File = 'run_left.png'; Label = '左へ走る' },
    @{ File = 'wave.png'; Label = '手を振る' },
    @{ File = 'jump.png'; Label = 'ジャンプ' },
    @{ File = 'fail.png'; Label = '失敗' },
    @{ File = 'sleep.png'; Label = '寝る' },
    @{ File = 'sprint.png'; Label = '作業中' },
    @{ File = 'sit.png'; Label = '座る' }
)

$fontCandidates = @('Yu Gothic UI Semibold', 'Yu Gothic UI', 'Meiryo UI', 'BIZ UDPGothic')
$availableFonts = [System.Drawing.FontFamily]::Families.Name
$fontName = $fontCandidates | Where-Object { $availableFonts -contains $_ } | Select-Object -First 1
if (-not $fontName) {
    throw "日本語フォントが見つかりません。候補: $($fontCandidates -join ', ')"
}

$canvasWidth = 660
$canvasHeight = 780
$cardWidth = 208
$cardHeight = 248
$gap = 12
$outerMargin = 6
$imageTop = 44
$imageAreaHeight = 194
$imageMaxWidth = 180
$imageMaxHeight = 166

$canvas = [System.Drawing.Bitmap]::new(
    $canvasWidth,
    $canvasHeight,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($canvas)
$font = [System.Drawing.Font]::new(
    $fontName,
    18,
    [System.Drawing.FontStyle]::Bold,
    [System.Drawing.GraphicsUnit]::Pixel)
$labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 248, 250))
$cardBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(37, 44, 54))
$borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(102, 113, 127), 1)

try {
    $graphics.Clear([System.Drawing.Color]::FromArgb(16, 22, 30))
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    for ($index = 0; $index -lt $states.Count; $index++) {
        $row = [Math]::Floor($index / 3)
        $column = $index % 3
        $x = $outerMargin + ($column * ($cardWidth + $gap))
        $y = $outerMargin + ($row * ($cardHeight + $gap))
        $card = [System.Drawing.Rectangle]::new($x, $y, $cardWidth, $cardHeight)

        $graphics.FillRectangle($cardBrush, $card)
        $graphics.DrawRectangle($borderPen, $card)
        $graphics.DrawString($states[$index].Label, $font, $labelBrush, $x + 8, $y + 7)

        $sourcePath = Join-Path $SourceDirectory $states[$index].File
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "状態画像が見つかりません: $sourcePath"
        }

        $source = [System.Drawing.Bitmap]::new($sourcePath)
        try {
            $scale = [Math]::Min($imageMaxWidth / $source.Width, $imageMaxHeight / $source.Height)
            $drawWidth = [Math]::Max(1, [int][Math]::Round($source.Width * $scale))
            $drawHeight = [Math]::Max(1, [int][Math]::Round($source.Height * $scale))
            $drawX = $x + [int][Math]::Round(($cardWidth - $drawWidth) / 2)
            $drawY = $y + $imageTop + [int][Math]::Round(($imageAreaHeight - $drawHeight) / 2)
            $destination = [System.Drawing.Rectangle]::new($drawX, $drawY, $drawWidth, $drawHeight)

            $graphics.DrawImage(
                $source,
                $destination,
                0,
                0,
                $source.Width,
                $source.Height,
                [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $source.Dispose()
        }
    }

    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $temporaryPath = "$resolvedOutput.tmp.png"
    $canvas.Save($temporaryPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Move-Item -LiteralPath $temporaryPath -Destination $resolvedOutput -Force
    Write-Output "Generated: $resolvedOutput ($fontName)"
}
finally {
    $borderPen.Dispose()
    $cardBrush.Dispose()
    $labelBrush.Dispose()
    $font.Dispose()
    $graphics.Dispose()
    $canvas.Dispose()
}
