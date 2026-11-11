# Downloads real pictures for the benchmark (optional; the local set from make-testdata.ps1 works without them):
#   kodak  the 24 pictures of the Kodak Lossless True Color Image Suite (768x512 photographs, free for unrestricted use)
#   anime  freely licensed anime- and manga-style drawings from Wikimedia Commons (CC BY-SA and similar; used locally
#          for measurements only, never redistributed with the project)
# Raw downloads go to <Out>\dl; cleaned references (alpha flattened, large drawings downscaled) to <Out>\refs.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Magick = "magick"
)
$ErrorActionPreference = "Stop"
$agent = "unsquish-bench/0.8 (https://github.com/C1nderC0ated/unsquish; test image fetch)"
$dlKodak = Join-Path $Out 'dl\kodak'; $dlAnime = Join-Path $Out 'dl\commons'
$refKodak = Join-Path $Out 'refs\kodak'; $refAnime = Join-Path $Out 'refs\anime'
foreach ($dir in $dlKodak, $dlAnime, $refKodak, $refAnime) { New-Item -ItemType Directory -Force $dir | Out-Null }

function Get-File([string]$url, [string]$target) {
    if ((Test-Path $target) -and (Get-Item $target).Length -gt 1000) { return $true }
    & curl.exe -L -s -f -m 180 -A $agent -o $target $url
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $target)) { Write-Warning "download failed: $url"; return $false }
    $head = New-Object byte[] 8; $fs = [IO.File]::OpenRead($target); try { [void]$fs.Read($head, 0, 8) } finally { $fs.Close() }
    if ($head[0] -ne 0x89 -or $head[1] -ne 0x50 -or $head[2] -ne 0x4E -or $head[3] -ne 0x47) { Write-Warning "not a PNG: $url"; [IO.File]::Delete($target); return $false }
    return $true
}

# ---- Kodak
$okKodak = 0
foreach ($i in 1..24) {
    $name = "kodim{0:d2}.png" -f $i
    if (Get-File "https://r0k.us/graphics/kodak/kodak/$name" (Join-Path $dlKodak $name)) {
        & $Magick (Join-Path $dlKodak $name) -strip (Join-Path $refKodak $name); $okKodak++
    }
}

# ---- drawings from Wikimedia Commons, resolved through the API so the URLs stay valid
$titles = @(
    'Wikipe-tan and Wikipe-kun.png', 'Wikipe-tan con vestido venezolano.png', 'Wikipe-tan Avatar.png', 'Wikipe-tan flanderized.png',
    'Anime Girl.png', 'Slime girl.png', 'Orc Sorceress by ExpiredPopsicle.png', 'Wikipe-tan PSA by Cawfeecrow 1.png',
    'Go Go! Encyclopedia Girls - English 03.png', 'Wikipe-tan explains masking in manga.png', 'Manga page publicdomainq.png', 'Manga Boy.png'
)
$okAnime = 0; $n = 0
foreach ($title in $titles) {
    $n++
    $api = "https://commons.wikimedia.org/w/api.php?action=query&format=json&prop=imageinfo&iiprop=url%7Csize%7Cmime&titles=" + [uri]::EscapeDataString("File:$title")
    $json = & curl.exe -s -m 60 -A $agent $api | ConvertFrom-Json
    $info = $null
    if ($json.query.pages) { $info = ($json.query.pages.PSObject.Properties | Select-Object -First 1).Value.imageinfo | Select-Object -First 1 }
    if (-not $info -or $info.mime -ne 'image/png') { Write-Warning "not found on Commons: $title"; continue }
    $safe = ("a{0:d2}_" -f $n) + (($title -replace '\.png$', '') -replace '[^A-Za-z0-9]+', '_').Trim('_').ToLowerInvariant()
    if ($safe.Length -gt 40) { $safe = $safe.Substring(0, 40) }
    $raw = Join-Path $dlAnime "$safe.png"
    if (Get-File $info.url $raw) {
        # flatten transparency onto white; bring big drawings down to about 1 MP (this also removes any old artifacts)
        & $Magick $raw -background white -alpha remove -alpha off -filter Lanczos -resize '1200x1200>' -strip (Join-Path $refAnime "$safe.png")
        $okAnime++
    }
}
$mb = [math]::Round(((Get-ChildItem (Join-Path $Out 'dl') -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
"kodak: $okKodak of 24; drawings: $okAnime of $($titles.Count); downloaded $mb MB into $(Join-Path $Out 'dl')"
