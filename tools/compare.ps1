# Per-image comparison of benchmark variants at one quality: one row per picture, one column per variant (PSNR gain).
# Usage: compare.ps1 -Sets anime,art -Q 30 -Variants "a=spec", "b=spec" [-BenchDir out2] [-Ssim]
param(
    [string[]]$Sets = @('anime', 'art'),
    [string]$Q = '30',
    [Parameter(Mandatory = $true)][string[]]$Variants,
    [string]$BenchDir = 'out',
    [string]$Only,
    [string]$Save,
    [switch]$Ssim
)
$repo = Split-Path $PSScriptRoot -Parent
$bench = Join-Path $repo "tools\bench\$BenchDir\unsquish-bench.exe"
$inv = [Globalization.CultureInfo]::InvariantCulture
$labels = $Variants | ForEach-Object { $_.Split('=')[0] }
$rows = @{}; $order = New-Object System.Collections.ArrayList
$extra = @(); if ($Only) { $extra += '--only', $Only }; if ($Save) { $extra += '--save', $Save }
& $bench run (Join-Path $repo 'testdata\refs') (Join-Path $repo 'testdata\jpg') --sets ($Sets -join ',') --q $Q --detail @extra @Variants 2>&1 | ForEach-Object {
    if ($_ -match '^\s+(\S+)\s+q(\d+)\s+(\S+)\s+(\S+) dB\s+ssim\s+(\S+)') {
        $key = $Matches[1]; if (-not $rows.ContainsKey($key)) { $rows[$key] = @{}; [void]$order.Add($key) }
        $text = if ($Ssim) { $Matches[5] } else { $Matches[4] }
        $val = 0.0; [void][double]::TryParse(($text -replace '^-\+', '-'), [Globalization.NumberStyles]::Float, $inv, [ref]$val)
        $rows[$key][$Matches[3]] = $val
    }
}
$head = "{0,-24}" -f "q$Q"; foreach ($l in $labels) { $head += "{0,10}" -f $l.Substring(0, [Math]::Min(9, $l.Length)) }; $head
$sum = @{}; foreach ($l in $labels) { $sum[$l] = 0.0 }
foreach ($key in $order) {
    $line = "{0,-24}" -f $key.Substring(0, [Math]::Min(23, $key.Length))
    foreach ($l in $labels) { $v = $rows[$key][$l]; $sum[$l] += $v; $line += "{0,10}" -f $v.ToString($(if ($Ssim) { '+0.0000;-0.0000' } else { '+0.00;-0.00' }), $inv) }
    $line
}
$line = "{0,-24}" -f "MEAN ($($order.Count))"; foreach ($l in $labels) { $line += "{0,10}" -f ($sum[$l] / [Math]::Max(1, $order.Count)).ToString($(if ($Ssim) { '+0.0000;-0.0000' } else { '+0.00;-0.00' }), $inv) }; $line
