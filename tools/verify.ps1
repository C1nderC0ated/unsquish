# Regression suite: runs the real unsquish.exe on a folder that exercises every route, checks what it wrote, and holds
# the engines to the quality the benchmark measured. Needs the test data (tools\make-testdata.ps1, tools\get-testphotos.ps1)
# and the benchmark tool (dotnet build -c Release -o tools\bench\out, in tools\bench).
# Usage: verify.ps1 [-Exe path\unsquish.exe] [-BenchDir out] [-Old path\older-unsquish.exe] [-NoGui]
param(
    [string]$Exe,
    [string]$Magick = 'C:\Program Files\ImageMagick-7.1.2-Q16-HDRI\magick.exe',
    [string]$BenchDir = 'out',
    [string]$Old,
    [switch]$NoGui
)
$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$td = Join-Path $repo 'testdata'
if (-not $Exe) { $Exe = @((Join-Path $repo 'publish\unsquish.exe'), (Join-Path $repo 'bin\Release\net10.0-windows\win-x64\unsquish.exe')) | Where-Object { Test-Path $_ } | Select-Object -First 1 }
$bench = Join-Path $repo "tools\bench\$BenchDir\unsquish-bench.exe"
foreach ($need in $Exe, $bench, $Magick, (Join-Path $td 'refs\kodak'), (Join-Path $td 'jpg\kodak')) { if (-not $need -or -not (Test-Path $need)) { "missing: $need"; exit 2 } }
$inv = [Globalization.CultureInfo]::InvariantCulture
$work = Join-Path $td 'verify'
if (Test-Path $work) { [IO.Directory]::Delete($work, $true) }
$in = Join-Path $work 'in'; [void][IO.Directory]::CreateDirectory($in)
$script:pass = 0; $script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') { if ($ok) { $script:pass++; "PASS  $name" } else { $script:fail++; "FAIL  $name   $detail" } }
function Unsquish([string[]]$a) { $o = & $Exe @a 2>&1 | ForEach-Object { "$_" }; [pscustomobject]@{ Code = $LASTEXITCODE; Out = @($o) } }
function Bench([string[]]$a) { @(& $bench @a 2>&1 | ForEach-Object { "$_" }) }
function Sha([string]$p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }
function Num([string]$s) { [double]::Parse($s, $inv) }

"unsquish: $Exe"
"version:  " + ((Unsquish @('/?')).Out | Select-Object -First 1)

# ---------------------------------------------------------------- fixtures: one file per route
$j = Join-Path $td 'jpg'; $r = Join-Path $td 'refs'
$art = (Get-ChildItem (Join-Path $j 'anime') -Filter 'a03*.q30.420.jpg')[0].FullName
Copy-Item (Join-Path $j 'kodak\kodim05.q30.420.jpg') (Join-Path $in 'photo_q30.jpg')
Copy-Item (Join-Path $j 'kodak\kodim23.q70.420.jpg') (Join-Path $in 'photo_q70.jpg')
Copy-Item $art (Join-Path $in 'art_q30.jpg')
Copy-Item (Join-Path $j 'art\manga_page.q30.420.jpg') (Join-Path $in 'manga_q30.jpg')
Copy-Item (Join-Path $r 'kodak\kodim01.png') (Join-Path $in 'clean.png')
& $Magick (Join-Path $r 'kodak\kodim08.png') -quality 95 (Join-Path $in 'photo_q95.jpg')
& $Magick (Join-Path $j 'kodak\kodim05.q30.420.jpg') (Join-Path $in 'was_jpeg_photo.png')
& $Magick $art (Join-Path $in 'was_jpeg_art.png')
& $Magick (Join-Path $j 'kodak\kodim23.q30.420.jpg') -sampling-factor 4:2:0 -quality 92 (Join-Path $in 'resaved_q30_as_q92.jpg')
& $Magick (Join-Path $j 'kodak\kodim05.q30.420.jpg') -colorspace Gray -quality 40 (Join-Path $in 'gray_q40.jpg')
# a JPEG that says "turn me a quarter clockwise": minimal Exif segment with Orientation = 6 right after the start marker
$srcBytes = [IO.File]::ReadAllBytes((Join-Path $j 'kodak\kodim19.q50.420.jpg'))
$exifSeg = [byte[]](0xFF, 0xE1, 0x00, 0x22, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
[IO.File]::WriteAllBytes((Join-Path $in 'rotated_q50.jpg'), [byte[]]($srcBytes[0..1] + $exifSeg + $srcBytes[2..($srcBytes.Length - 1)]))
& $Magick (Join-Path $in 'rotated_q50.jpg') (Join-Path $in 'was_jpeg_rotated.png')
$sprite = Get-ChildItem (Join-Path $td 'xbrz\Samples') -Filter *.png | Sort-Object Length -Descending | Select-Object -First 3 | Select-Object -Last 1
& $Magick $sprite.FullName -background white -alpha remove -alpha off -strip (Join-Path $in 'sprite.png')
& $Magick (Join-Path $in 'sprite.png') -filter point -resize 400% -quality 75 (Join-Path $in 'sprite_x4_q75.jpg')
$anim = Get-ChildItem (Join-Path $td 'anim') | Select-Object -First 1
if ($anim) { Copy-Item $anim.FullName (Join-Path $in ('anim' + $anim.Extension)) }
$before = @{}; Get-ChildItem $in | ForEach-Object { $before[$_.Name] = Sha $_.FullName }

# ---------------------------------------------------------------- default run: routes, verification, inputs untouched
""; "== default run (JPEG XL output)"
$out = Join-Path $work 'out_jxl'
$res = Unsquish @($in, '/out', $out, '/cli')
Check 'exit code 0' ($res.Code -eq 0) "exit $($res.Code)"
Check 'no FAIL line' (-not ($res.Out -match '  FAIL  ')) (($res.Out -match '  FAIL  ') -join ' | ')
$csv = Import-Csv (Join-Path $out 'unsquish_log.csv')
function Row([string]$name) { $csv | Where-Object { (Split-Path $_.file -Leaf) -eq $name } | Select-Object -First 1 }
$expect = [ordered]@{ 'photo_q30.jpg' = 'restore'; 'photo_q70.jpg' = 'restore'; 'photo_q95.jpg' = 'lossless'; 'art_q30.jpg' = 'restore'; 'manga_q30.jpg' = 'restore'; 'gray_q40.jpg' = 'restore';
    'rotated_q50.jpg' = 'restore'; 'was_jpeg_rotated.png' = 'restore'; 'was_jpeg_photo.png' = 'restore'; 'was_jpeg_art.png' = 'restore'; 'resaved_q30_as_q92.jpg' = 'restore'; 'clean.png' = 'skip'; 'sprite.png' = 'pixelart'; 'sprite_x4_q75.jpg' = 'pixelart' }
foreach ($k in $expect.Keys) { $row = Row $k; Check "route of $k is $($expect[$k])" ($row -and $row.route -eq $expect[$k]) "got '$($row.route)' ($($row.reason))" }
foreach ($k in 'was_jpeg_photo.png', 'was_jpeg_art.png', 'resaved_q30_as_q92.jpg') { $q = [int](Row $k).earlier_quality; Check "earlier quality of $k is about 30" ($q -ge 28 -and $q -le 32) "got $q" }
Check 'no earlier compression claimed for clean.png' ([int](Row 'clean.png').earlier_quality -eq 0)
Check 'every restored or re-saved file was verified' (@($res.Out | Where-Object { $_ -match '  (RESTORE|LOSSLESS|PIXELART)  ' -and $_ -notmatch 'verified' }).Count -eq 0)
Check 'art picture got the art preset' ((($res.Out | Where-Object { $_ -match 'manga_q30' }) -join '') -match 'guarded smoothing')
Check 'gray picture: no chroma stage claimed' ((($res.Out | Where-Object { $_ -match 'gray_q40' }) -join '') -match 'gray: no chroma stage')
Check 'orientation applied' ((($res.Out | Where-Object { $_ -match 'rotated_q50' }) -join '') -match 'orientation 6 applied')
Check 'orientation of a bitmap source applied (Exif carried in a PNG)' ((($res.Out | Where-Object { $_ -match 'was_jpeg_rotated' }) -join '') -match 'orientation 6 applied')
$changed = @(Get-ChildItem $in | Where-Object { $before[$_.Name] -ne (Sha $_.FullName) })
Check 'inputs unmodified' ($changed.Count -eq 0 -and @(Get-ChildItem $in).Count -eq $before.Count) (($changed | ForEach-Object Name) -join ', ')
$again = Unsquish @($in, '/out', $out, '/cli')
Check 'second run leaves existing outputs alone' (@($again.Out | Where-Object { $_ -match '  EXISTS  ' }).Count -ge 10 -and -not ($again.Out -match '  RESTORE  '))

# ---------------------------------------------------------------- PNG and JPEG output; the exe and the benchmark agree
""; "== output formats"
$three = @((Join-Path $in 'photo_q30.jpg'), (Join-Path $in 'art_q30.jpg'), (Join-Path $in 'rotated_q50.jpg'))
$png = Join-Path $work 'out_png'; $resPng = Unsquish ($three + @('/out', $png, '/png', '/cli'))
Check '/png run ok' ($resPng.Code -eq 0 -and (Test-Path (Join-Path $png 'photo_q30.png')) -and (Test-Path (Join-Path $png 'art_q30.png')))
$jpg = Join-Path $work 'out_jpg'; $resJpg = Unsquish ($three + @('/out', $jpg, '/jpgq', '92', '/cli'))
Check '/jpg run ok' ($resJpg.Code -eq 0 -and (Test-Path (Join-Path $jpg 'photo_q30.jpg')))
$p = (Bench @('psnr', (Join-Path $png 'photo_q30.png'), (Join-Path $jpg 'photo_q30.jpg'))) -join ''
Check 'JPEG output is close to the lossless one (> 38 dB)' ($p -match 'psnr ([\d.]+)' -and (Num $Matches[1]) -gt 38) $p
$p = (Bench @('psnr', (Join-Path $png 'rotated_q50.png'), (Join-Path $jpg 'rotated_q50.jpg'))) -join ''
Check 'rotated picture: both outputs upright and alike' ($p -match 'psnr ([\d.]+)' -and (Num $Matches[1]) -gt 38) $p
$stored = (& $Magick identify -format '%hx%w' (Join-Path $j 'kodak\kodim19.q50.420.jpg')) -join ''   # height x width: what a quarter turn makes of it
$size = (& $Magick identify -format '%wx%h' (Join-Path $png 'rotated_q50.png')) -join ''
Check "rotated picture: written turned by a quarter ($size)" ($size -eq $stored -and $size -notmatch '^(\d+)x\1$') "expected $stored"
$save = Join-Path $work 'bench'
[void](Bench @('run', $r, $j, '--sets', 'kodak', '--only', 'kodim05', '--q', '30', '--save', $save, 'p4=preset:photo:4'))
$p = (Bench @('psnr', (Join-Path $png 'photo_q30.png'), (Join-Path $save 'kodak_kodim05.q30.p4.png'))) -join ''
Check 'exe output equals the benchmark output for the same preset' ($p -eq 'identical') $p

$busy = Join-Path $work 'busy'; [void][IO.Directory]::CreateDirectory($busy)
Copy-Item (Join-Path $j 'kodak\kodim13.q85.420.jpg') (Join-Path $busy 'busy.jpg'); Copy-Item (Join-Path $j 'texture\tx_foliage.q85.420.jpg') (Join-Path $busy 'foliage.jpg')
$resLow = Unsquish @($busy, '/out', (Join-Path $work 'out_jpg50'), '/jpgq', '50', '/nohistory', '/cli')
Check 'the lowest JPEG quality on offer passes verification on busy pictures' ($resLow.Code -eq 0 -and (Test-Path (Join-Path $work 'out_jpg50\busy.jpg')) -and (Test-Path (Join-Path $work 'out_jpg50\foliage.jpg'))) (($resLow.Out -match 'FAIL') -join ' | ')

# ---------------------------------------------------------------- quality gates (gain over a plain decode, dB)
""; "== quality gates"
function Gain([string]$set, [string]$q, [string]$spec, [string[]]$more) {
    $lines = Bench (@('run', $r, $j, '--sets', $set, '--q', $q) + $more + @("x=$spec"))
    $line = $lines | Where-Object { $_ -match '^x\s' } | Select-Object -First 1
    if ($line -match '^x\s+([+-][\d.]+)/([+-][\d.]+)') { [pscustomobject]@{ Psnr = Num $Matches[1]; Ssim = Num $Matches[2] } } else { [pscustomobject]@{ Psnr = -99; Ssim = -99 } }
}
$g = Gain 'kodak' '30' 'preset:photo:4' @('--every', '6'); Check "photographs, quality 30, level 4: $($g.Psnr) dB (floor +0.70), SSIM $($g.Ssim)" ($g.Psnr -ge 0.70 -and $g.Ssim -gt 5)
$g = Gain 'kodak' '70' 'preset:photo:4' @('--every', '6'); Check "photographs, quality 70, level 4: $($g.Psnr) dB (floor +0.70), SSIM $($g.Ssim)" ($g.Psnr -ge 0.70 -and $g.Ssim -gt 3)
$g = Gain 'anime' '30' 'preset:art:6' @('--every', '3'); Check "drawings, quality 30, level 6: $($g.Psnr) dB (floor +2.0), SSIM $($g.Ssim)" ($g.Psnr -ge 2.0 -and $g.Ssim -gt 10)
$g = Gain 'art' '30' 'preset:art:6' @('--only', 'manga_page'); Check "screentone page, quality 30, level 6: $($g.Psnr) dB (floor +3.0)" ($g.Psnr -ge 3.0)
$g = Gain 'kodak' '30' 'preset:art:6' @('--every', '6'); Check "photographs under the art preset stay above a plain decode: $($g.Psnr) dB, SSIM $($g.Ssim)" ($g.Psnr -ge 0.5 -and $g.Ssim -gt 0)
$g = Gain 'kodak' '30' 'preset:photo:2' @('--every', '6'); Check "photographs, level 2 (fast): $($g.Psnr) dB (floor +0.5)" ($g.Psnr -ge 0.5)
Check 'DCT self-test (round trip and SIMD against scalar below 0.001)' ((Bench @('dct')) -join ' ' -match 'max error 0\.000\d+, SIMD vs scalar 0\.000')

# ---------------------------------------------------------------- earlier compression: nothing invented, the real cases found
""; "== earlier compression"
$h = Bench @('history', $r, $j, $Magick, (Join-Path $work 'tmp'), '--mode', 'clean,single')
Check 'no clean reference picture reported as a former JPEG' (($h -join ' ') -match 'former JPEGs: 0 of')
Check 'no once-compressed JPEG reported as re-saved' (($h -join ' ') -match 're-saved: 0 of')
$h = Bench @('history', $r, $j, $Magick, (Join-Path $work 'tmp'), '--mode', 'bitmap,resave', '--sets', 'kodak', '--every', '6', '--q', '20,50')
$found = @($h | Where-Object { $_ -match '^kodak' } | ForEach-Object { [regex]::Matches($_, 'q\d+: (\d+)/(\d+)') | ForEach-Object { $_.Groups[1].Value + '/' + $_.Groups[2].Value } })
Check "former JPEGs and re-saved JPEGs found: $($found -join ' ')" ($found.Count -eq 8 -and @($found | Where-Object { $_ -ne '4/4' }).Count -eq 0)

# ---------------------------------------------------------------- command-line behaviour
""; "== command line"
Check '/threads 0 is refused' ((Unsquish @($in, '/threads', '0', '/cli')).Code -eq 2)
Check '/qs 9 is refused' ((Unsquish @($in, '/qs', '9', '/cli')).Code -eq 2)
Check 'unknown option is refused' ((Unsquish @($in, '/nonsense', '/cli')).Code -eq 2)
Check '/view without files is refused' ((Unsquish @('/view', 'nothing-here.jpg', 'nor-here.jpg')).Code -eq 2)
$dry = Join-Path $work 'out_dry'; $resDry = Unsquish @($in, '/out', $dry, '/dry', '/cli')
Check '/dry writes nothing' ($resDry.Code -eq 0 -and -not (Test-Path $dry))
$gone = Join-Path $work 'out_missing'; $resGone = Unsquish @((Join-Path $work 'no-such-file.jpg'), '/out', $gone, '/cli')
Check 'an input that does not exist fails the run and creates nothing' ($resGone.Code -eq 1 -and -not (Test-Path $gone)) "exit $($resGone.Code)"
# how often an animation plays: a GIF without a loop block plays once, with loop count 0 forever
$gifDir = Join-Path $work 'gif'; [void][IO.Directory]::CreateDirectory($gifDir)
& $Magick -size 16x16 xc:red xc:blue -loop 1 (Join-Path $gifDir 'once.gif'); & $Magick -size 16x16 xc:red xc:blue -loop 0 (Join-Path $gifDir 'forever.gif')
[void](Unsquish @($gifDir, '/out', (Join-Path $work 'out_gif'), '/class', 'pixelart', '/xbrz', '2', '/cli'))
function Plays([string]$png) { if (-not (Test-Path $png)) { return -1 }; $b = [IO.File]::ReadAllBytes($png); for ($i = 8; $i -lt $b.Length - 16; $i++) { if ($b[$i] -eq 0x61 -and $b[$i + 1] -eq 0x63 -and $b[$i + 2] -eq 0x54 -and $b[$i + 3] -eq 0x4C) { return ($b[$i + 8] -shl 24) -bor ($b[$i + 9] -shl 16) -bor ($b[$i + 10] -shl 8) -bor $b[$i + 11] } }; -1 }
$once = Plays (Join-Path $work 'out_gif\once.png'); $forever = Plays (Join-Path $work 'out_gif\forever.png')
Check "a GIF that plays once stays a one-play animation (plays: $once), an endless one stays endless (plays: $forever)" ($once -eq 1 -and $forever -eq 0)
$a = Join-Path $work 'dup\2019'; $b = Join-Path $work 'dup\2020'; [void][IO.Directory]::CreateDirectory($a); [void][IO.Directory]::CreateDirectory($b)
Copy-Item (Join-Path $in 'photo_q30.jpg') (Join-Path $a 'same.jpg'); Copy-Item (Join-Path $in 'photo_q70.jpg') (Join-Path $b 'same.jpg')
$dupOut = Join-Path $work 'out_dup'; $resDup = Unsquish @((Join-Path $a 'same.jpg'), (Join-Path $b 'same.jpg'), '/out', $dupOut, '/qs', '1', '/cli')
Check 'two inputs with the same name get two outputs' ((Test-Path (Join-Path $dupOut 'same.jxl')) -and (Test-Path (Join-Path $dupOut 'same (2).jxl')))
$bad = Join-Path $work 'bad'; [void][IO.Directory]::CreateDirectory($bad)
$bytes = [IO.File]::ReadAllBytes((Join-Path $in 'photo_q30.jpg')); [IO.File]::WriteAllBytes((Join-Path $bad 'truncated.jpg'), $bytes[0..3000])
Copy-Item (Join-Path $in 'photo_q70.jpg') (Join-Path $bad 'fine.jpg')
$badOut = Join-Path $work 'out_bad'; $resBad = Unsquish @($bad, '/out', $badOut, '/qs', '1', '/cli')
Check 'a damaged file does not stop the run' ((Test-Path (Join-Path $badOut 'fine.jxl')) -and -not (Get-ChildItem $badOut -Filter '*.partial'))
$inside = Unsquish @($in, '/out', (Join-Path $in 'sub'), '/dry', '/cli')
Check 'output folder inside the input folder: run still lists the inputs' ($inside.Code -eq 0 -and ($inside.Out -join ' ') -match "$(@(Get-ChildItem $in -File).Count) image file")

# ---------------------------------------------------------------- classifier parity with an earlier version
if ($Old -and (Test-Path $Old)) {
    ""; "== classifier parity with $Old"
    function Classes([string]$exe, [string]$dir) {
        $m = @{}; & $exe $dir '/out' (Join-Path $work 'parity') '/dry' '/cli' 2>&1 | ForEach-Object { "$_" } | ForEach-Object { if ($_ -match '^\[\d+/\d+\] (.+?)  \[(\w+) [\d.]+\]') { $m[$Matches[1]] = $Matches[2] } }; $m
    }
    foreach ($set in 'calib', 'dither', 'xbrz', 'anim', 'jpg\anime', 'jpg\kodak') {
        $dir = Join-Path $td $set; if (-not (Test-Path $dir)) { continue }
        $o = Classes $Old $dir; $n = Classes $Exe $dir
        $diff = @($o.Keys | Where-Object { $n[$_] -ne $o[$_] })
        Check "same classes on $set ($($o.Count) files)" ($diff.Count -eq 0 -and $o.Count -gt 0) (($diff | Select-Object -First 5 | ForEach-Object { "$_ $($o[$_])->$($n[$_])" }) -join '; ')
    }
}

# ---------------------------------------------------------------- the window and the viewer open and close
if (-not $NoGui) {
    ""; "== window (opens for a moment)"
    function Smoke([string[]]$a, [string]$title) {
        $proc = Start-Process -FilePath $Exe -ArgumentList $a -PassThru
        $ok = $false
        for ($i = 0; $i -lt 60 -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 250; $proc.Refresh(); if ($proc.MainWindowTitle -like $title) { $ok = $true; break } }
        if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); if (-not $proc.WaitForExit(8000)) { $proc.Kill(); $ok = $false } }
        $ok
    }
    Check 'main window opens and closes' (Smoke @('/gui') 'Unsquish 0.*')
    Check 'viewer opens and closes' (Smoke @('/view', ('"' + (Join-Path $in 'photo_q30.jpg') + '"'), ('"' + (Join-Path $png 'photo_q30.png') + '"')) 'Unsquish: before*')
}

""; "$($script:pass) passed, $($script:fail) failed"
exit ([Math]::Min(1, $script:fail))
