# Regenerate the multi-resolution icon using Windows drawing APIs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/RazerBookRgb/Assets/App.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $scale = $size / 256.0
        $graphics.ScaleTransform($scale, $scale)
        $background = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#101A20'))
        $graphics.FillRectangle($background, 8, 32, 240, 192)
        $background.Dispose()
        $colors = @('#44D62C', '#00C8FF', '#7866FF', '#FF4D91')
        for ($row = 0; $row -lt 3; $row++) {
            for ($col = 0; $col -lt 4; $col++) {
                $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($colors[$col]))
                $graphics.FillRectangle($brush, (28 + $col * 52), (52 + $row * 40), 40, 28)
                $brush.Dispose()
            }
        }
        $space = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#44D62C'))
        $graphics.FillRectangle($space, 64, 176, 128, 20)
        $space.Dispose()
        $stream = New-Object System.IO.MemoryStream
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            ,$stream.ToArray()
        } finally { $stream.Dispose() }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$file = [System.IO.File]::Create($target)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Generated $target"
