param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\LiveQs.Windows\Assets\LiveQs.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function New-IconPng([int]$Size) {
    $scale = $Size / 64.0
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
        $background = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromRgb(0, 122, 255))
        $context.DrawEllipse($background, $null, [System.Windows.Point]::new(32, 32), 28, 28)

        $clockPen = [System.Windows.Media.Pen]::new([System.Windows.Media.Brushes]::White, 5)
        $clockPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $clockPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $clockPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        $context.DrawEllipse($null, $clockPen, [System.Windows.Point]::new(32, 32), 16, 16)
        $context.DrawLine($clockPen, [System.Windows.Point]::new(32, 32), [System.Windows.Point]::new(32, 22))
        $context.DrawLine($clockPen, [System.Windows.Point]::new(32, 32), [System.Windows.Point]::new(41, 37))
        $context.Pop()
    }
    finally {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$images = @($sizes | ForEach-Object { ,(New-IconPng $_) })
$headerSize = 6 + (16 * $sizes.Count)
$offset = $headerSize
$stream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $image = $images[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }

    $directory = Split-Path -Parent $OutputPath
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    [System.IO.File]::WriteAllBytes($OutputPath, $stream.ToArray())
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Output "Generated $OutputPath with sizes: $($sizes -join ', ')"
