# Generates first-party icon assets from the same WPF vector used by Window.Icon.
# Run with Windows PowerShell (STA) from any working directory. No application window is created.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$projectRoot = Join-Path $PSScriptRoot '../TomatoTimerPro'
$vectorPath = Join-Path $projectRoot 'Resources/Icons/AppIcon.xaml'
$outputDirectory = Join-Path $projectRoot 'Resources/Images'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$stream = [IO.File]::OpenRead($vectorPath)
try { $dictionary = [Windows.Markup.XamlReader]::Load($stream) }
finally { $stream.Dispose() }
$image = $dictionary['AppIcon']
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try { $context.DrawImage($image, (New-Object Windows.Rect(0, 0, $size, $size))) }
    finally { $context.Close() }
    $target = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($target))
    $memory = New-Object IO.MemoryStream
    try { $encoder.Save($memory); $frames += ,$memory.ToArray() }
    finally { $memory.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $outputDirectory 'AppIcon.png'), $frames[$frames.Count - 1])
$iconStream = [IO.File]::Create((Join-Path $outputDirectory 'AppIcon.ico'))
$writer = New-Object IO.BinaryWriter($iconStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose(); $iconStream.Dispose() }
Write-Output ('Generated AppIcon.png and seven-resolution AppIcon.ico in ' + $outputDirectory)
