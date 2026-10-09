# Run with Windows PowerShell in STA mode: powershell.exe -NoProfile -STA -File scripts/Update-ApplicationIcon.ps1
# Render the actual sidebar artwork so Windows branding stays faithful to the app.
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this script with powershell.exe -STA.' }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$window = Get-Content -LiteralPath (Join-Path $projectRoot 'src/CommuteCast.Desktop/MainWindow.xaml') -Raw
$namespaces = New-Object Xml.XmlNamespaceManager($window.NameTable)
$namespaces.AddNamespace('w', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$artwork = $window.SelectSingleNode('//w:Border[@Width="42" and @Height="42" and @Background="#CF704A"]', $namespaces)
if ($null -eq $artwork) { throw 'The sidebar icon was not found. Update the artwork selector.' }
$artwork = $artwork.CloneNode($true)
$artwork.SetAttribute('xmlns', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$artwork.SetAttribute('Margin', '0')
$artwork.SetAttribute('HorizontalAlignment', 'Stretch')
$visual = [Windows.Markup.XamlReader]::Parse($artwork.OuterXml)
$visual.Measure([Windows.Size]::new(42, 42))
$visual.Arrange([Windows.Rect]::new(0, 0, 42, 42))
$visual.UpdateLayout()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $drawing = [Windows.Media.DrawingVisual]::new()
    $context = $drawing.RenderOpen()
    $context.DrawRectangle([Windows.Media.VisualBrush]::new($visual), $null, [Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($drawing)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); $frames += ,$stream.ToArray() } finally { $stream.Dispose() }
}
$assetRoot = Join-Path $projectRoot 'src/CommuteCast.Desktop/Assets'
New-Item -ItemType Directory -Force -Path $assetRoot | Out-Null
$output = Join-Path $assetRoot 'CommuteCast.ico'
$stream = [IO.File]::Create($output)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output "Generated $output ($($sizes -join ', ') pixels)."
