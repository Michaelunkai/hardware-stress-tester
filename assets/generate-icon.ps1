# Generates assets/app.ico — the StressLab brand icon:
# a rounded gradient square (blue #4DA3FF -> purple #B57BFF) with a white lightning bolt.
# Sizes 16-64 are embedded as 32bpp DIB entries; 256 as a PNG entry.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File assets\generate-icon.ps1

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outPath = Join-Path $PSScriptRoot 'app.ico'
$sizes = 16, 24, 32, 48, 64, 256

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $s = $size / 256.0
    $margin = [int](10 * $s)
    $diameter = $size - 2 * $margin
    $radius = [int](58 * $s)

    $rect = New-Object System.Drawing.Rectangle($margin, $margin, $diameter, $diameter)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $radius, $radius, 180, 90)
    $path.AddArc($rect.Right - $radius, $rect.Y, $radius, $radius, 270, 90)
    $path.AddArc($rect.Right - $radius, $rect.Bottom - $radius, $radius, $radius, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $radius, $radius, $radius, 90, 90)
    $path.CloseFigure()

    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 0x4D, 0xA3, 0xFF),
        [System.Drawing.Color]::FromArgb(255, 0xB5, 0x7B, 0xFF),
        45.0)
    $g.FillPath($gradient, $path)

    # lightning bolt polygon (256-space coordinates, scaled)
    $points256 = @(
        @(150, 18), @(66, 142), @(118, 142), @(102, 238), @(190, 108), @(134, 108), @(168, 18)
    )
    $pts = New-Object 'System.Drawing.PointF[]' $points256.Count
    for ($i = 0; $i -lt $points256.Count; $i++) {
        $pts[$i] = New-Object System.Drawing.PointF(
            [float]($points256[$i][0] * $s),
            [float]($points256[$i][1] * $s))
    }
    $g.FillPolygon([System.Drawing.Brushes]::White, $pts)

    $gradient.Dispose()
    $path.Dispose()
    $g.Dispose()
    return $bmp
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

function ConvertTo-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $xor = New-Object byte[] ($w * $h * 4)
        for ($y = 0; $y -lt $h; $y++) {
            $src = $data.Scan0.ToInt64() + $y * $stride
            $row = New-Object byte[] ($w * 4)
            [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]$src, $row, 0, $w * 4)
            # ICO DIB rows are bottom-up
            $dst = ($h - 1 - $y) * $w * 4
            [Array]::Copy($row, 0, $xor, $dst, $w * 4)
        }
    }
    finally {
        $bmp.UnlockBits($data)
    }

    # BITMAPINFOHEADER (40 bytes); biHeight = 2*h (XOR + AND mask)
    $header = New-Object byte[] 40
    [BitConverter]::GetBytes([uint32]40).CopyTo($header, 0)
    [BitConverter]::GetBytes([int32]$w).CopyTo($header, 4)
    [BitConverter]::GetBytes([int32]($h * 2)).CopyTo($header, 8)
    [BitConverter]::GetBytes([uint16]1).CopyTo($header, 12)
    [BitConverter]::GetBytes([uint16]32).CopyTo($header, 14)

    # AND mask: 1bpp, rows padded to 32 bits, all zero (alpha handles transparency)
    $maskStride = [Math]::Ceiling($w / 32.0) * 4
    $mask = New-Object byte[] ($maskStride * $h)

    $ms = New-Object System.IO.MemoryStream
    $ms.Write($header, 0, 40)
    $ms.Write($xor, 0, $xor.Length)
    $ms.Write($mask, 0, $mask.Length)
    return $ms.ToArray()
}

$frames = @{}
foreach ($size in $sizes) {
    $bmp = New-Frame $size
    if ($size -eq 256) { $frames[$size] = @{ Png = (ConvertTo-PngBytes $bmp) } }
    else { $frames[$size] = @{ Dib = (ConvertTo-DibBytes $bmp) } }

    if ($size -eq 256) {
        $preview = Join-Path $env:TEMP 'stresslab-icon-preview.png'
        $bmp.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "preview: $preview"
    }
    $bmp.Dispose()
}

# compose the ICO container
$count = $sizes.Count
$header = New-Object byte[] 6
[BitConverter]::GetBytes([uint16]0).CopyTo($header, 0)
[BitConverter]::GetBytes([uint16]1).CopyTo($header, 2)
[BitConverter]::GetBytes([uint16]$count).CopyTo($header, 4)

$entries = New-Object byte[] (16 * $count)
$blobs = New-Object System.IO.MemoryStream
$offset = 6 + 16 * $count
$i = 0
foreach ($size in ($sizes | Sort-Object)) {
    $bytes = if ($frames[$size].ContainsKey('Png')) { $frames[$size]['Png'] } else { $frames[$size]['Dib'] }
    $e = $i * 16
    $entries[$e] = if ($size -ge 256) { 0 } else { $size }
    $entries[$e + 1] = if ($size -ge 256) { 0 } else { $size }
    $entries[$e + 2] = 0
    $entries[$e + 3] = 0
    [BitConverter]::GetBytes([uint16]1).CopyTo($entries, $e + 4)
    [BitConverter]::GetBytes([uint16]32).CopyTo($entries, $e + 6)
    [BitConverter]::GetBytes([uint32]$bytes.Length).CopyTo($entries, $e + 8)
    [BitConverter]::GetBytes([uint32]$offset).CopyTo($entries, $e + 12)
    $blobs.Write($bytes, 0, $bytes.Length)
    $offset += $bytes.Length
    $i++
}

$out = New-Object System.IO.MemoryStream
$out.Write($header, 0, 6)
$out.Write($entries, 0, $entries.Length)
$blobs.WriteTo($out)
[System.IO.File]::WriteAllBytes($outPath, $out.ToArray())
Write-Host "icon written: $outPath ($($out.Length) bytes, $count sizes)"
