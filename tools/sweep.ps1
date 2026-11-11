# Runs a named group of benchmark variants on the tuning subset and writes the tables to testdata\results\<name>.txt.
# Usage: sweep.ps1 -Name a1 -Sets anime,art -Variants "tv100=pd,tgv=false,iterations=100", ...
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string[]]$Sets = @('anime', 'art'),
    [string]$Q = '10,30,70',
    [Parameter(Mandatory = $true)][string[]]$Variants,
    [switch]$Full,
    [string]$Ss = '420',
    [string]$BenchDir = 'out'
)
$repo = Split-Path $PSScriptRoot -Parent
$bench = Join-Path $repo "tools\bench\$BenchDir\unsquish-bench.exe"
$every = @{ anime = 3; art = 2; kodak = 6; texture = 2; photo = 3 }
$outFile = Join-Path $repo "testdata\results\$Name.txt"
New-Item -ItemType Directory -Force (Split-Path $outFile) | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
$lines = foreach ($set in $Sets) {
    $a = @('run', (Join-Path $repo 'testdata\refs'), (Join-Path $repo 'testdata\jpg'), '--sets', $set, '--q', $Q, '--ss', $Ss)
    if (-not $Full) { $a += @('--every', [string]$every[$set]) }
    & $bench @a @Variants 2>&1
}
$lines | Set-Content -Path $outFile -Encoding utf8
"sweep $Name finished in $([int]$sw.Elapsed.TotalSeconds) s -> $outFile"
