# Builds the local test data for the benchmark and the regression scripts. Nothing is downloaded:
#   photos    Windows lock-screen and wallpaper images, downscaled so their own JPEG artifacts vanish
#   art       ImageMagick's built-in images and synthetic cel-shaded / line-art / UI scenes
#   pixel art crops of the xBRZ sample screens (optional: -XbrzSamples <folder of ScalerTest_1.2.zip\Samples>)
# Output layout: <Out>\refs\{photo,art}\*.png (clean references), <Out>\calib (classifier calibration set),
# <Out>\dither (GIF renditions that must not be taken for pixel art), <Out>\anim (animated GIFs).
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Magick = "magick",
    [string]$XbrzSamples = ""
)
$ErrorActionPreference = "Stop"
function M { param([string[]]$a) & $Magick @a; if ($LASTEXITCODE -ne 0) { throw "magick failed: $($a -join ' ')" } }
foreach ($d in 'refs\photo', 'refs\art', 'calib', 'dither', 'anim') { New-Item -ItemType Directory -Force (Join-Path $Out $d) | Out-Null }
$dirPhoto = Join-Path $Out 'refs\photo'; $dirArt = Join-Path $Out 'refs\art'; $dirCalib = Join-Path $Out 'calib'; $dirDither = Join-Path $Out 'dither'; $dirAnim = Join-Path $Out 'anim'

# ---- photographs (about 1 MP each)
$web = 'C:\Windows\Web'
$photos = @(
    @("$web\Screen\img100.jpg", '960x540'), @("$web\Screen\img101.jpg", '960x600'), @("$web\Screen\img102.jpg", '960x600'),
    @("$web\Screen\img103.jpg", '960x600!'), @("$web\Screen\img104.jpg", '960x600'),
    @("$web\4K\Wallpaper\Windows\img0_1920x1200.jpg", '960x600'), @("$web\4K\Wallpaper\Windows\img19_1920x1200.jpg", '960x600'),
    @("$web\touchkeyboard\TouchKeyboardThemeDark000.jpg", '958x539!'), @("$web\touchkeyboard\TouchKeyboardThemeLight002.jpg", '958x539!')
)
foreach ($ph in $photos) {
    if (-not (Test-Path $ph[0])) { Write-Warning "missing $($ph[0])"; continue }
    $name = [IO.Path]::GetFileNameWithoutExtension($ph[0]) -replace '_1920x1200', '' -replace 'TouchKeyboardTheme', 'kbd'
    M @($ph[0], '-filter', 'Lanczos', '-resize', $ph[1], '-strip', "$dirPhoto\$name.png")
}

# ---- art
M @('logo:', '-strip', "$dirArt\im_logo.png")
M @('wizard:', '-strip', "$dirArt\im_wizard.png")
M @('-size', '1024x768', 'gradient:#7ec8ff-#ffe9c4',
    '-fill', '#3b7d4f', '-stroke', '#1c3b26', '-strokewidth', '3', '-draw', "path 'M 0,560 Q 256,420 512,540 T 1024,500 L 1024,768 L 0,768 Z'",
    '-fill', '#2f6a41', '-stroke', 'none', '-draw', "path 'M 0,640 Q 300,560 600,650 T 1024,600 L 1024,768 L 0,768 Z'",
    '-fill', '#ffd23f', '-stroke', '#a86b00', '-strokewidth', '4', '-draw', 'circle 820,160 820,90',
    '-fill', '#fff3b0', '-stroke', 'none', '-draw', 'circle 800,140 800,115',
    '-fill', '#f4c9a8', '-stroke', '#3a2418', '-strokewidth', '4', '-draw', 'ellipse 380,380 110,130 0,360',
    '-fill', '#e3a983', '-stroke', 'none', '-draw', "path 'M 300,440 Q 380,520 460,440 Q 380,500 300,440 Z'",
    '-fill', '#5a3d8a', '-stroke', '#221436', '-strokewidth', '4', '-draw', "path 'M 262,380 Q 250,200 380,215 Q 510,200 498,380 Q 470,290 380,300 Q 290,290 262,380 Z'",
    '-fill', '#7a58b5', '-stroke', 'none', '-draw', "path 'M 300,300 Q 330,235 400,238 Q 350,250 300,300 Z'",
    '-fill', 'white', '-stroke', '#222222', '-strokewidth', '3', '-draw', 'ellipse 340,390 22,30 0,360', '-draw', 'ellipse 420,390 22,30 0,360',
    '-fill', '#2d6cdf', '-stroke', 'none', '-draw', 'circle 340,395 340,410', '-draw', 'circle 420,395 420,410',
    '-fill', '#0b1a3a', '-draw', 'circle 340,395 340,402', '-draw', 'circle 420,395 420,402',
    '-fill', 'white', '-draw', 'circle 346,388 346,392', '-draw', 'circle 426,388 426,392',
    '-fill', 'none', '-stroke', '#b03a48', '-strokewidth', '4', '-draw', "path 'M 350,455 Q 380,475 410,455'",
    '-stroke', '#3a2418', '-strokewidth', '2', '-draw', "path 'M 372,425 Q 380,432 388,425'",
    '-fill', '#e94f64', '-stroke', '#5e1220', '-strokewidth', '4', '-draw', "path 'M 250,768 Q 260,540 380,520 Q 500,540 510,768 Z'",
    '-fill', '#ff8fa0', '-stroke', 'none', '-draw', "path 'M 300,768 Q 310,600 380,560 Q 340,640 345,768 Z'",
    '-font', 'Arial', '-pointsize', '54', '-fill', '#d81b60', '-stroke', '#4a0a22', '-strokewidth', '2', '-annotate', '+570+720', 'Unsquish',
    '-font', 'Segoe-UI', '-pointsize', '20', '-fill', '#1c3b26', '-stroke', 'none', '-annotate', '+572+750', 'flat colour, line work, gradients',
    '-strip', "$dirArt\cel_scene.png")
M @('-size', '800x1100', 'xc:white',
    '-fill', 'none', '-stroke', 'black', '-strokewidth', '6', '-draw', 'rectangle 30,30 770,520', '-draw', 'rectangle 30,550 380,1070', '-draw', 'rectangle 410,550 770,1070',
    '-stroke', 'none', '-tile', 'pattern:gray25', '-draw', 'rectangle 36,36 764,200', '-tile', 'pattern:gray50', '-draw', "path 'M 420,900 Q 590,760 760,900 L 760,1060 L 420,1060 Z'", '+tile',
    '-fill', 'white', '-stroke', 'black', '-strokewidth', '5', '-draw', 'ellipse 250,330 120,150 0,360',
    '-fill', 'black', '-stroke', 'none', '-draw', "path 'M 130,300 Q 150,150 250,160 Q 360,150 372,310 Q 330,220 250,230 Q 170,220 130,300 Z'",
    '-fill', 'none', '-stroke', 'black', '-strokewidth', '3', '-draw', "path 'M 200,330 Q 215,315 230,330'", '-draw', "path 'M 270,330 Q 285,315 300,330'", '-draw', "path 'M 225,400 Q 250,420 275,400'",
    '-strokewidth', '1', '-draw', 'line 60,700 350,640', '-draw', 'line 60,720 350,680', '-draw', 'line 60,740 350,720', '-draw', 'line 60,760 350,760', '-draw', 'line 60,780 350,800',
    '-strokewidth', '2', '-draw', "path 'M 80,1000 Q 200,820 340,1000'",
    '-fill', 'white', '-stroke', 'black', '-strokewidth', '3', '-draw', 'ellipse 590,650 150,70 0,360',
    '-font', 'Comic-Sans-MS', '-pointsize', '30', '-fill', 'black', '-stroke', 'none', '-annotate', '+470+645', 'Is the restored one', '-annotate', '+490+680', 'actually better?',
    '-font', 'Impact', '-pointsize', '90', '-annotate', '+70+960', 'BAM!',
    '-strip', "$dirArt\manga_page.png")
M @('-size', '1000x700', 'xc:#f3f4f6',
    '-fill', '#1e3a8a', '-draw', 'rectangle 0,0 1000,90',
    '-font', 'Segoe-UI', '-pointsize', '40', '-fill', '#ff3b30', '-annotate', '+30+60', 'Red on blue, the 4:2:0 torture',
    '-fill', '#c2185b', '-draw', 'rectangle 0,90 1000,150', '-fill', '#00e676', '-pointsize', '28', '-annotate', '+30+130', 'green text on magenta, thin strokes',
    '-fill', 'white', '-stroke', '#9ca3af', '-strokewidth', '1', '-draw', 'roundrectangle 30,180 480,420 12,12', '-draw', 'roundrectangle 520,180 970,420 12,12',
    '-stroke', 'none', '-fill', '#111827', '-font', 'Consolas', '-pointsize', '16', '-annotate', '+50+215', 'int main(void) { return 0; }  // 16 px',
    '-pointsize', '12', '-annotate', '+50+240', 'The quick brown fox jumps over the lazy dog 0123456789',
    '-pointsize', '10', '-annotate', '+50+260', 'tiny ten pixel text: still legible after quality 10?',
    '-font', 'Times-New-Roman', '-pointsize', '22', '-fill', '#b91c1c', '-annotate', '+50+300', 'Serif red', '-fill', '#1d4ed8', '-annotate', '+170+300', 'blue', '-fill', '#047857', '-annotate', '+230+300', 'green', '-fill', '#7c3aed', '-annotate', '+310+300', 'violet',
    '-fill', '#2563eb', '-draw', 'roundrectangle 540,200 700,250 8,8', '-fill', 'white', '-font', 'Segoe-UI', '-pointsize', '20', '-annotate', '+575+233', 'Restore',
    '-fill', '#e5e7eb', '-draw', 'roundrectangle 720,200 950,250 8,8', '-fill', '#111827', '-annotate', '+740+233', 'Preview (dry run)',
    '-fill', 'none', '-stroke', '#ef4444', '-strokewidth', '1', '-draw', 'line 540,290 950,290', '-stroke', '#3b82f6', '-draw', 'line 540,300 950,300', '-stroke', '#10b981', '-draw', 'line 540,310 950,330', '-stroke', '#f59e0b', '-strokewidth', '2', '-draw', 'line 540,340 950,400',
    '-stroke', 'none', '-fill', '#fde68a', '-draw', 'rectangle 30,450 970,670',
    '-fill', '#92400e', '-font', 'Arial', '-pointsize', '64', '-annotate', '+60+540', 'Sharp edges', '-fill', '#065f46', '-pointsize', '36', '-annotate', '+60+610', 'and flat colour everywhere',
    '-strip', "$dirArt\ui_text.png")
M @('-size', '900x900', 'radial-gradient:#ffe0f0-#6a4c93',
    '(', '-size', '900x900', 'xc:none', '-fill', '#ff9aa2cc', '-draw', 'circle 300,330 300,150', '-fill', '#b5ead7cc', '-draw', 'circle 620,560 620,340', '-fill', '#c7ceeacc', '-draw', 'ellipse 450,760 330,90 0,360', '-blur', '0x14', ')', '-composite',
    '-fill', 'none', '-stroke', '#4a2c6b', '-strokewidth', '5', '-draw', 'circle 300,330 300,150', '-draw', 'circle 620,560 620,340',
    '-stroke', '#ffffff', '-strokewidth', '3', '-draw', "path 'M 200,250 Q 260,190 340,200'", '-draw', "path 'M 500,470 Q 580,400 680,410'",
    '-stroke', '#2b1840', '-strokewidth', '2', '-draw', "path 'M 60,860 C 200,700 420,880 560,720 S 800,700 850,560'",
    '-alpha', 'off', '-strip', "$dirArt\soft_shade.png")
M @('-size', '900x600', 'xc:#fafafa',
    '-fill', '#ffd6d6', '-draw', 'rectangle 0,0 300,600', '-fill', '#d6e4ff', '-draw', 'rectangle 300,0 600,600', '-fill', '#d9f7d9', '-draw', 'rectangle 600,0 900,600',
    '-fill', 'none', '-stroke', '#e00000', '-strokewidth', '1', '-draw', "path 'M 20,40 C 200,10 400,140 880,60'", '-stroke', '#0030d0', '-draw', "path 'M 20,120 C 300,220 500,20 880,160'",
    '-stroke', '#008a20', '-strokewidth', '2', '-draw', "path 'M 20,220 C 250,120 550,330 880,240'", '-stroke', '#d000b0', '-strokewidth', '3', '-draw', "path 'M 20,320 C 320,420 520,250 880,350'",
    '-stroke', '#000000', '-strokewidth', '1', '-draw', 'line 20,400 880,430', '-draw', 'line 20,460 880,460', '-draw', 'line 450,380 451,500',
    '-stroke', 'none', '-fill', '#e00000', '-font', 'Arial', '-pointsize', '18', '-annotate', '+30+520', 'red 18 px text on pink', '-fill', '#0030d0', '-annotate', '+330+520', 'blue 18 px text on pale blue', '-fill', '#008a20', '-annotate', '+630+520', 'green text on mint',
    '-fill', '#ff0000', '-draw', 'rectangle 60,540 120,580', '-fill', '#0000ff', '-draw', 'rectangle 120,540 180,580', '-fill', '#00c000', '-draw', 'rectangle 180,540 240,580', '-fill', '#ff00ff', '-draw', 'rectangle 240,540 300,580', '-fill', '#00ffff', '-draw', 'rectangle 300,540 360,580', '-fill', '#ffff00', '-draw', 'rectangle 360,540 420,580',
    '-strip', "$dirArt\line_color.png")

# ---- classifier calibration set (classes must stay stable between versions)
Copy-Item "$dirArt\cel_scene.png" "$dirCalib\cel_scene.png" -Force
M @("$dirArt\cel_scene.png", '-sampling-factor', '4:2:0', '-quality', '35', "$dirCalib\cel_scene_q35.jpg")
M @("$dirArt\cel_scene.png", '-sampling-factor', '4:2:0', '-quality', '85', "$dirCalib\cel_scene_q85.jpg")
M @("$dirArt\cel_scene.png", '-resize', '320x240', '-sampling-factor', '4:2:0', '-quality', '75', "$dirCalib\cel_scene_small_q75.jpg")
Copy-Item "$dirArt\im_logo.png" "$dirCalib\logo_flat_graphic.png" -Force
M @("$dirArt\im_logo.png", '-quality', '85', "$dirCalib\logo_flat_q85.jpg")
M @("$dirArt\im_wizard.png", '-quality', '85', "$dirCalib\wizard_painting_q85.jpg")
M @('rose:', "$dirCalib\rose_tiny_photo.png")
if (Test-Path "$web\Screen\img101.jpg") { Copy-Item "$web\Screen\img101.jpg" "$dirCalib\photo_img101_orig.jpg" -Force; Copy-Item "$web\Screen\img103.jpg" "$dirCalib\photo_img103_orig.jpg" -Force }
M @("$dirPhoto\img102.png", '-resize', '640x400', '-sampling-factor', '4:2:0', '-quality', '30', "$dirCalib\photo_img102_small_q30.jpg")
M @("$dirPhoto\img104.png", '-sampling-factor', '4:2:0', '-quality', '60', "$dirCalib\photo_img104_q60.jpg")
# a small sprite drawn pixel by pixel (hard edges, 7 colours), its JPEG, and nearest-neighbour enlargements
M @('-size', '48x48', 'xc:#3a86ff', '+antialias', '-fill', '#ffbe0b', '-draw', 'rectangle 8,8 39,39', '-fill', '#fb5607', '-draw', 'rectangle 14,14 33,33', '-fill', '#ff006e', '-draw', 'rectangle 20,20 27,27',
    '-fill', '#8338ec', '-draw', 'rectangle 0,44 47,47', '-fill', 'black', '-draw', 'point 16,16', '-draw', 'point 31,16', '-draw', 'line 18,30 29,30', '-fill', 'white', '-draw', 'rectangle 2,2 5,5', '-draw', 'rectangle 42,2 45,5', "$dirCalib\pix_native_48.png")
M @("$dirCalib\pix_native_48.png", '-quality', '85', "$dirCalib\pix_native_q85.jpg")
M @("$dirCalib\pix_native_48.png", '-scale', '400%', "$dirCalib\pix_nn4x_192.png")
M @("$dirCalib\pix_nn4x_192.png", '-quality', '80', "$dirCalib\pix_nn4x_q80.jpg")
M @("$dirCalib\pix_native_48.png", '-scale', '800%', "$dirCalib\pix_nn8x_384.png")
M @("$dirCalib\pix_native_48.png", '-scale', '300%', '-bordercolor', 'black', '-border', '2', "$dirCalib\pix_nn3x_border.png")

# ---- dithered GIF renditions of soft pictures: must not be classified as pixel art
$i = 0
foreach ($s in @("$dirPhoto\img101.png", "$dirPhoto\img103.png", "$dirPhoto\img104.png", "$dirArt\im_wizard.png", "$dirArt\cel_scene.png", "$dirArt\soft_shade.png")) {
    $i++; $b = [IO.Path]::GetFileNameWithoutExtension($s)
    M @($s, '-resize', '480x480>', '-dither', 'FloydSteinberg', '-colors', '256', "$dirDither\${i}_${b}_fs256.gif")
    M @($s, '-resize', '240x240>', '-dither', 'FloydSteinberg', '-colors', '256', "$dirDither\${i}_${b}_fs256_240.gif")
}

# ---- pixel-art screens and animations
if ($XbrzSamples -and (Test-Path $XbrzSamples)) {
    $s = $XbrzSamples
    M @('-delay', '10', "$s\Bio Menace.png", '-delay', '25', "$s\Commander Keen.png", '-delay', '7', "$s\Doom.png", '-loop', '0', "$dirAnim\three_screens.gif")
}
M @("$dirCalib\pix_native_48.png", '(', '+clone', '-flop', ')', '(', '-clone', '0', '-flip', ')', '-set', 'delay', '12', '-loop', '3', "$dirAnim\sprite_loop3.gif")
M @("$dirPhoto\img101.png", '-resize', '320x200!', '(', '+clone', '-modulate', '110', ')', '(', '-clone', '0', '-modulate', '90', ')', '-set', 'delay', '8', '-loop', '0', "$dirAnim\photo_anim.gif")

"refs: $((Get-ChildItem $dirPhoto -File).Count) photos, $((Get-ChildItem $dirArt -File).Count) art; calib $((Get-ChildItem $dirCalib -File).Count); dither $((Get-ChildItem $dirDither -File).Count); anim $((Get-ChildItem $dirAnim -File).Count)"
