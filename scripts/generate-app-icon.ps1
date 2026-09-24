# Regenerate the committed PNG and multi-resolution ICO from the SVG on Windows.
# Uses only System.Drawing; no application build or external packages are needed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '../src/CodexHistorySync.Desktop/Assets'
[xml]$svg = Get-Content (Join-Path $assets 'agent-sync.svg') -Raw
$paths = @()
try {
    foreach ($element in $svg.svg.path) {
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        # This deliberately small SVG uses absolute M, L, C and Z commands only.
        $tokens = [regex]::Matches($element.d, '[MLCZ]|-?\d+(?:\.\d+)?')
        $i = 0
        [single]$x = 0
        [single]$y = 0
        while ($i -lt $tokens.Count) {
            $command = $tokens[$i++].Value
            switch ($command) {
                'M' { $x = [single]$tokens[$i++].Value; $y = [single]$tokens[$i++].Value; $path.StartFigure() }
                'L' {
                    $nextX = [single]$tokens[$i++].Value; $nextY = [single]$tokens[$i++].Value
                    $path.AddLine($x, $y, $nextX, $nextY); $x = $nextX; $y = $nextY
                }
                'C' {
                    $a = [single]$tokens[$i++].Value; $b = [single]$tokens[$i++].Value
                    $c = [single]$tokens[$i++].Value; $d = [single]$tokens[$i++].Value
                    $nextX = [single]$tokens[$i++].Value; $nextY = [single]$tokens[$i++].Value
                    $path.AddBezier($x, $y, $a, $b, $c, $d, $nextX, $nextY); $x = $nextX; $y = $nextY
                }
                'Z' { $path.CloseFigure() }
                default { throw "Unsupported SVG command: $command" }
            }
        }
        $paths += @{ Path = $path; Color = [System.Drawing.ColorTranslator]::FromHtml($element.fill) }
    }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = @()
    foreach ($size in $sizes) {
        $large = [System.Drawing.Bitmap]::new($size * 4, $size * 4)
        $graphics = [System.Drawing.Graphics]::FromImage($large)
        $small = [System.Drawing.Bitmap]::new($size, $size)
        $output = [System.Drawing.Graphics]::FromImage($small)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.ScaleTransform($size * 4 / 256.0, $size * 4 / 256.0)
            foreach ($item in $paths) {
                $brush = [System.Drawing.SolidBrush]::new($item.Color)
                try { $graphics.FillPath($brush, $item.Path) } finally { $brush.Dispose() }
            }
            $output.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $output.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $output.DrawImage($large, 0, 0, $size, $size)
            $small.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images += ,$stream.ToArray()
            if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $assets 'agent-sync.png'), $stream.ToArray()) }
        } finally {
            $stream.Dispose(); $output.Dispose(); $small.Dispose(); $graphics.Dispose(); $large.Dispose()
        }
    }
    $file = [System.IO.File]::Create((Join-Path $assets 'agent-sync.ico'))
    $writer = [System.IO.BinaryWriter]::new($file)
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
    } finally { $writer.Dispose() }
} finally {
    foreach ($item in $paths) { $item.Path.Dispose() }
}
