# Unsquish

Un-squishes compressed images: JPEG artifact restoration, pixel-art upscaling and verified
lossless re-saving, in one Windows exe with a window and a command line. Version 0.8.0.

`unsquish.exe` is the whole tool in a single Windows executable: content classification,
JPEG restoration, pixel-art upscaling, lossless re-saving, JPEG XL output, a before/after
viewer, and a window for using it without a terminal. Nothing has to be installed and
nothing sits beside it. Requires 64-bit Windows 10 or 11.

## The window

Double-click `unsquish.exe`, or drop files or a folder onto its icon. The window opens
in a dark theme and remembers your settings.

1. **Input**: `Add files...`, `Add folder...`, or drop files and folders anywhere on the
   window. Several inputs can be queued; folders include their subfolders unless you
   untick *Include subfolders*.
2. **Output folder**: leave it empty to get `<input>_restored` next to the input, or
   pick a folder. Existing outputs are left alone unless *Overwrite existing outputs* is
   ticked.
3. **Settings**: everything starts on *Auto*, which detects photo / art / pixel art per
   file and picks the right treatment. Each setting can be forced instead (the
   equivalent command-line switch is listed below); hover a control for an explanation.
   *Reset to Auto* puts everything back.
4. **Preview (writes nothing)** shows the class and the decision for every file.
   **Restore** runs it; the log fills in as files finish, *Cancel* stops after the current
   file, and *Open output folder* opens the result in Explorer.
5. **Compare before / after** opens a viewer on the files of the last run: the original
   left of a line that follows the mouse, the result right of it. Wheel zooms, dragging
   pans, `B` and `A` show one side only, `F` fits, `1` is 100 percent.
6. **Guide** lists what happens to each kind of file and which settings to force when.

| Window setting | Command line | Notes |
|---|---|---|
| Content: Auto-detect / Photo / Art / Pixel art / No detection | (default) / `/class photo` / `/class art` / `/class pixelart` / `/noauto` | *Art* is the one to force for anime or illustrations that were detected as photo |
| Restoration level: Auto / 0-6 | (default) / `/qs N` | photo 4, art 6 on Auto; 2 is the fast setting |
| Find earlier compression | (default) / `/nohistory` | re-saved JPEGs and bitmaps that were JPEGs once |
| Selective blur: Off / On | (default) / `/blur` | the blur of versions before 0.8; off because it lowers measured quality |
| Pixel-art upscale: Auto / Keep size / 2x-6x | (default) / `/xbrz 1` / `/xbrz N` | Auto is enough for about 1024 px, never below an undone enlargement |
| Pixel-art style: Crisp / Smooth | (default) / `/smooth` | Crisp keeps the blocks; Smooth is the xBRZ emulator-filter look |
| Palette snap | `/snap N` | pixel art from JPEG only; helps few-color sprites, hurts busy screens |
| Restore JPEGs below quality | `/qf N` | files at or above are re-saved losslessly; 1 = everything lossless |
| Output format: JPEG XL / PNG / JPEG quality 95 | `/jxl` / `/png` / `/jpg` | JPEG XL and PNG are lossless; JPEG is small and opens anywhere but compresses once more (`/jpgq N` sets its quality) |
| Threads | `/threads N` | |
| Include subfolders / Overwrite | `/norecurse` / `/overwrite` | |
| Preview | `/dry` | |

Settings are kept in `%LOCALAPPDATA%\unsquish\gui.ini`. `unsquish.exe /gui` opens the
window from a terminal.

## Command line

Started from a terminal with arguments, the exe is a command-line tool:

```
unsquish.exe "C:\Users\you\Pictures\holiday"            -> ...\holiday_restored
unsquish.exe "C:\Users\you\Pictures\holiday" "E:\out"   -> a last argument that does not exist yet, is empty or holds a previous
                                                           unsquish_log.csv is the output folder; a folder of images is another input
unsquish.exe a.jpg b.jpg "C:\more\pics" /out "E:\out"   -> several inputs into one folder (without /out: _restored next to the first file)
unsquish.exe "C:\Users\you\Pictures\holiday" /dry       -> decisions only, nothing written
unsquish.exe /view before.jpg after.jxl                 -> the before/after viewer on any two pictures
unsquish.exe /?
```

Inputs are never modified. Existing outputs are skipped unless `/overwrite`. Every output is
written under a temporary name and renamed only after it has been verified, so an interrupted
run never leaves a half-written file under a final name. Inputs that lie inside the output
folder are reported and skipped (compared by what the file system resolves, so a mapped or
subst drive naming the same folder is caught too). Two inputs with the same relative name
get distinct outputs (`name.jxl`, `name (2).jxl`).

## What it does per file

| Detected | Route | Output |
|---|---|---|
| JPEG below the quality threshold (default 90), classed **photo** | level 4: denoising in the DCT domain over all 64 block positions, then chroma smoothed along the luma edges | lossless JPEG XL (`/png` for PNG, `/jpg` for JPEG), ICC and EXIF carried over, the EXIF orientation baked into the pixels |
| same, classed **art** (flat color, hard lines; also photographed documents) | level 6: the same denoising, then everything that is flat is smoothed; screentone, dither and fine lines are detected and left alone | as above |
| same, between the two (painting-like; shown as `photo 0.50`) | the art method at level 4 | as above |
| **pixel art** (sprites, game screens, icons, nearest-neighbor enlargements; JPEG, PNG, GIF, BMP or WebP) | restoration if JPEG (the art method), undo the enlargement, optional `/snap N` palette, crisp nearest-neighbor upscale 2x-4x (never below the undone enlargement, so the output is never smaller than the input). `/smooth` uses xBRZ instead, in passes of at most 6x. A lossless input that is already an exact integer enlargement is left alone | PNG |
| **animated pixel art** (GIF, animated PNG or WebP whose first frame is pixel art) | the same steps on every frame | animated PNG with the original frame delays and number of plays |
| JPEG at or above the threshold | reversible JPEG XL transcode, verified by rebuilding the original bytes (with `/png` or `/jpg`: Huffman-optimized JPEG, verified coefficient by coefficient) | `.jxl` (about 20 percent smaller) or `.jpg` |
| **a JPEG that was saved again at a higher quality** | the levels of the file still sit on the comb of the first, harsher compression. When its steps are established and follow a standard table, the file is restored with the wide bins of that compression, whatever its own tables say | as for photo / art |
| **PNG, BMP or lossless WebP that was a JPEG once** (saved from a browser, screenshotted at 100 percent, converted) | the same search on the block DCT of the pixels, including where the block grid starts and how chroma was stored; if certain, restored like the JPEG it was | as for photo / art |
| 12-bit, CMYK, arithmetic-coded, lossless JPEG; MPF / Ultra HDR / Motion Photo (data after the end marker) | copied unchanged, hash-verified | `.jpg` |
| other PNG/GIF/BMP/WebP that are not pixel art (including animations of photos or drawings), files of an unrecognized type | skipped, reason logged: there is no JPEG damage in them to undo | |

File types are recognized from their content, not their extension. The CSV log in the
output folder records class, confidence, edge ratios, grid factor, quality factor, route,
action, sizes, timing and the quality of an earlier compression per file.

## How the restoration works

A JPEG stores every DCT coefficient of every 8x8 block as a whole multiple of a step. The
true value is lost, but it is known to lie within half a step of the stored one: in its
*bin*. Everything Unsquish does to a picture happens inside those bins, so the result is
always a picture that the same JPEG settings would have turned into the same file. Nothing
is added that the file does not allow. Three stages work inside the bins:

1. **Denoising over all block positions.** The picture is cut into 8x8 blocks 64 times,
   once for every possible position of the grid. Each block is denoised in the DCT domain
   by the Wiener rule, with the file's own steps as the noise level, and the 64 results are
   averaged. The seams of the original grid fall inside the shifted blocks and average
   away; real detail, which stands far above the noise in the coefficients that carry it,
   passes. This is the stage that suits photographs: it does not flatten texture.
2. **Chroma along the luma edges** (photos, level 4 and up). Color is stored at half
   resolution in most JPEGs. Instead of interpolating it, chroma is solved for at full
   resolution: total variation over all three channels jointly, luma held fixed, so that
   color edges settle where the luma edges are. Luma keeps every bit of its texture.
3. **Smoothing what is flat** (art). The same solver on all three channels makes flat
   color flat again and edges clean. That is right for drawings and wrong for texture, so
   two things hold it back. A *guard* measures, around every pixel, how much of the
   neighborhood carries gradient: one edge crossing a flat area occupies a few pixels,
   screentone, dither, hatching and grain occupy most of them, and there the smoothing
   weight goes to zero. And a *fidelity* term ties the result to the denoised picture,
   so that what the guard half protects is not worn down by running longer.

An **earlier compression** leaves traces. A picture compressed at quality 30 and saved
again at quality 92 says "92" in its tables, but its levels still cluster on multiples of
the old steps; a PNG saved from a JPEG has a block DCT that sits on that comb up to the
rounding of the pixels. Unsquish looks for the comb in the fifteen lowest frequencies and
claims an earlier compression only when the steps found fit one scaled standard table
(what cameras, messengers, social networks and most software write). A false alarm would
smooth a picture that was never damaged, a miss leaves things as they were, so every
threshold is set for "certain or nothing". In bitmaps, blocks that the decoder clipped at
white or black (every block with a line in it, in a drawing on a white page) are no longer
on the comb; their levels are recovered by assuming the clipped pixels went further.

## How the classifier decides

- **edge hardness** on a point-sampled proxy *and* on full-resolution crops; art requires
  hard edges in both (large photos look hard-edged on the proxy only). A picture that is
  nearly as hard-edged as a drawing is called a photo with confidence 0.50 and gets the
  art method at the photo level.
- **color density** (unique colors per pixel): palette images are below 2 percent
- **pixel grid**: a nearest-neighbor round trip that beats a box-filtered one by 3 dB
  reveals a 2x-8x nearest-neighbor enlargement, which is undone before upscaling. Every grid
  phase is tried, because a cropped or padded sprite rarely starts on a cell boundary.
- **palette texture**: a GIF has at most 256 colors and dithered edges, which makes any
  small GIF look like a sprite. A quantized soft picture gives itself away by its texture:
  many tiny neighbor differences (adjacent palette entries), hard edges that cross seven
  or more colors (gradients), or hardly any hard edges at all. Such files are kept out of
  the pixel-art route. JPEG sources are exempt, since JPEG noise would trigger this on
  genuine sprites.

Calibrated on 16 test images, the 47 game screens shipped with xBRZ, 37 dithered GIF
renditions of photos and drawings, and a library of 111 real images (phone photos,
screenshots, drawings, memes). The classes are the same as in 0.7.1 on every file of the
calibration sets.

## Options

| Option | Meaning |
|---|---|
| `/out X` | output folder (default: `<input>_restored` next to the input). Without it, a last argument that does not exist yet, is an empty folder or holds a previous `unsquish_log.csv` is the output; an existing folder of images is an input |
| `/noauto` | no content detection: every JPEG below `/qf` is treated as a photo, other formats ignored |
| `/class photo\|art\|pixelart` | force the class for every file (the classifier still measures the pixel grid for pixel art) |
| `/qf N` | quality-factor threshold, default 90 |
| `/qs N` | force the restoration level 0-6: 0-1 light smoothing (fastest), 2 denoising over 16 block positions, 3-6 over all 64 plus 0/30/60/100 chroma iterations (photo) or 30/60/100/150 smoothing iterations (art). `/fast` is `/qs 2` |
| `/nohistory` | do not look for an earlier compression |
| `/blur` | add the selective blur of versions before 0.8 after the restoration |
| `/xbrz N` | pixel-art upscale factor (1 = keep size, 2-6); default: enough for about 1024 px, at least the undone enlargement |
| `/smooth` | pixel art: xBRZ smoothing (stair-steps become curves) instead of the crisp nearest-neighbor upscale |
| `/snap N` | pixel art from JPEG only: median-cut to N colors before upscaling (off by default; helps few-color sprites, hurts rich screens) |
| `/jxl`, `/png`, `/jpg` | restored output format: lossless JPEG XL (default), PNG, or JPEG (4:4:4, optimized Huffman tables). `/png` and `/jpg` also make the lossless branch write Huffman-optimized JPEGs instead of JPEG XL |
| `/jpgq N` | quality of `/jpg` output, 50-100 (default 95); implies `/jpg` |
| `/threads N` | CPU threads for restoration and JPEG XL encoding |
| `/view A B` | open the before/after viewer on two pictures (anything the tool reads or writes) |
| `/cli` | command-line mode even when the process owns its console (scripts, Task Scheduler); the window opens only for an interactive double-click or `/gui` |
| `/norecurse`, `/overwrite`, `/dry`, `/features`, `/gui`, `/?` | as named; `/features` prints the classifier measurements per file |

Exit code 0 when everything succeeded, 1 if any file failed or an input does not exist,
2 on a usage error.

## Restoration quality

Measured against the uncompressed originals of 58 pictures, each compressed with
libjpeg-turbo (ImageMagick) at six qualities with 4:2:0 chroma. The numbers are the *gain
over a plain decode* of the same file, as PSNR in dB over RGB and, after the slash, as
SSIM x 1000 over luma; higher is better, negative is worse than doing nothing.

Photographs, default settings (level 4):

| Set | Version | q10 | q20 | q30 | q50 | q70 | q85 |
|---|---|---|---|---|---|---|---|
| 24 Kodak photographs (film, textured) | 0.7.1 | +0.52 / +2.0 | +0.29 / -5.7 | +0.16 / -6.6 | +0.04 / -7.0 | -0.05 / -6.9 | -0.39 / -8.3 |
| | **0.8** | **+0.99 / +20.9** | **+0.89 / +12.2** | **+0.84 / +9.3** | **+0.80 / +6.8** | **+0.81 / +5.4** | **+0.78 / +3.7** |
| 9 clean digital photographs (Windows wallpapers) | 0.7.1 | +0.90 / +19.4 | +0.94 / +11.4 | +0.87 / +7.2 | +0.77 / +3.9 | +0.70 / +2.2 | +0.48 / +0.5 |
| | **0.8** | **+1.69 / +26.8** | **+1.87 / +15.1** | **+1.81 / +10.0** | **+1.69 / +5.7** | **+1.61 / +3.6** | **+1.60 / +2.0** |
| 6 synthetic textures (leaves, clouds, grain, skin) | 0.7.1 | +0.54 / -1.6 | +0.37 / -6.0 | +0.26 / -5.6 | +0.18 / -4.4 | +0.15 / -3.8 | -0.02 / -5.3 |
| | **0.8** | **+1.06 / +16.5** | **+0.99 / +8.9** | **+0.95 / +6.4** | **+0.97 / +5.1** | **+1.03 / +4.7** | **+1.02 / +3.0** |

Drawings, default settings (level 6):

| Set | Version | q10 | q20 | q30 | q50 | q70 | q85 |
|---|---|---|---|---|---|---|---|
| 12 drawings from Wikimedia Commons (anime style, manga pages) | 0.7.1 | +0.47 / +16.1 | +0.43 / +11.1 | +0.47 / +8.6 | +0.48 / +5.5 | +0.49 / +3.9 | +0.48 / +1.9 |
| | **0.8** | **+2.07 / +25.4** | **+2.28 / +17.7** | **+2.66 / +13.9** | **+2.84 / +9.4** | **+2.96 / +7.0** | **+3.01 / +3.2** |
| 7 synthetic drawings (cel scene, line art, screentone page, interface text, soft shading) | 0.7.1 | +0.68 / +15.5 | +0.53 / +10.3 | +0.47 / +7.3 | +0.43 / +4.4 | +0.45 / +2.8 | +0.60 / +1.4 |
| | **0.8** | **+1.72 / +21.6** | **+2.08 / +14.5** | **+2.58 / +11.1** | **+2.54 / +7.3** | **+3.03 / +5.1** | **+3.50 / +2.6** |

The same pictures with 4:4:4 chroma at quality 30: drawings +2.86 dB (0.7.1: +0.70),
synthetic drawings +2.96 (+0.84), Kodak +0.95 (+0.23), clean photographs +2.26 (+1.19),
textures +1.31 (+0.44).

Levels, at quality 30 (time on a four-core laptop processor from 2019, restoration only):

| Level | Kodak photographs | s per megapixel | Drawings | s per megapixel |
|---|---|---|---|---|
| 2 | +0.74 dB | 0.1 | +0.89 dB | 0.1 |
| 3 | +0.77 dB | 0.1-0.2 | +1.80 dB | 0.4-0.7 |
| 4 | +0.84 dB (default) | 0.4-0.7 | +2.21 dB | 0.7-1.1 |
| 5 | +0.86 dB | 0.6-0.9 | +2.49 dB | 1.1-1.5 |
| 6 | +0.88 dB | 0.9-1.1 | +2.66 dB (default) | 1.6-1.9 |

The smaller time is for a 22-megapixel picture, the larger for pictures under one megapixel.

When the classifier is wrong: drawings sent down the photo route still gain +1.11 / +1.28 /
+1.64 dB at quality 10 / 30 / 70 (instead of +2.07 / +2.66 / +2.96). Kodak photographs sent
down the art route gain +0.99 / +0.82 / +0.81 dB, the same as on their own route, with
a smaller SSIM gain (+12.7 / +4.2 / +4.8 against +20.9 / +9.3 / +5.4); they stay above a
plain decode in both measures at every quality tested.

Pixel art saved as JPEG (8 sprite screens, 4:2:0): enlarged four times, +2.47 / +4.08 /
+7.32 dB at quality 50 / 75 / 90 (0.7.1: +0.56 / +0.43 / +0.27); at native size +0.46 /
+0.56 / +0.84 dB (0.7.1: +0.03).

Earlier compression, on a sample of 13 photographs and 10 drawings first compressed at
quality 20, 50 and 70:

| Case | Photographs found | Drawings found | Effect |
|---|---|---|---|
| JPEG saved again at quality 92 | 37 of 39 | 16 of 30 | Kodak: +0.59 / +0.63 / +0.94 dB over a plain decode, against +0.09 / +0.26 / +0.40 without the search |
| saved as PNG (the usual smooth chroma upsampling) | 36 of 39 | 24 of 30 | Kodak: +0.59 / +0.68 / +0.68 dB; drawings +0.80 / +1.35 / +1.49 dB |
| saved as PNG and cropped by 3 x 5 pixels | 31 of 39 | 17 of 30 | as above where found |
| never compressed (58 pictures); compressed once (406 JPEGs) | 0 false alarms | | |

The quality found was the true one or one off in most groups, two off on average in the
worst. Drawings on white
are the hard case: their edges ring past white and black, which the decoder clips, and
that disturbs the traces.

Every lossless output is verified before it is kept: JPEG XL transcodes are decoded back
and compared byte for byte with the input; restored JPEG XL and PNG files are decoded back
and compared pixel for pixel; Huffman re-encodes are compared coefficient by coefficient.

The benchmark and the regression suite are in `tools/` (see Building).

## Limitations

- Content detection is the same as before, and it matters more now: some drawings (soft
  shading, painterly styles) are taken for photographs and get the photo method, which
  gains about half as much on them. For a folder of drawings, force *Content = Art*.
- An earlier compression is recognized only when the picture was not resized in between
  (a crop is fine) and the first compression used a standard table. Resized pictures gain
  little. Flat-color drawings saved again as JPEG are recognized about half the time.
- Time: the default photo setting takes about 0.4 s per megapixel, the default art
  setting about 1.6 s per megapixel, plus writing the output. Level 2 takes a fifth of the
  time for photographs and keeps most of their gain.
- Memory: about 80 MB per megapixel for photographs and 110 MB for drawings (a
  22-megapixel photograph: 1.8 GB). When the smoothing stage would need more than half of
  the machine's memory it is skipped and the log says so.
- Grayscale JPEGs keep one channel in JPEG XL and PNG output; a color ICC profile on a gray
  picture (or a gray one on a color picture) is dropped. CMYK and 12-bit JPEGs are copied,
  not restored.
- Animated inputs are processed only when they are pixel art (every frame, animated PNG out,
  all frames held in memory, so the factor is lowered for very long animations). Other
  animations are skipped. Viewers without APNG support show the first frame.
- Dithered GIFs with 64 colors or fewer can still pass as pixel art; the texture test was
  tuned on 256-color GIFs.
- JPEG XL viewers: Chrome 155+, Firefox 158+, IrfanView/XnView with plugin, GIMP, darktable.
  Convert any output with a JPEG XL tool if your viewer lacks support, or choose PNG or JPEG.
- A console window flashes for a moment on double-click (the exe is a console program
  that hands over to the window); harmless.

## Building

Requires the .NET 10 SDK on 64-bit Windows. The six libjxl 0.12.0 DLLs are in `native/`.

```text
dotnet publish -c Release -o publish -p:PublishTrimmed=true -p:TrimMode=partial
```

The result is `publish\unsquish.exe`, one self-contained file of about 26 MB.

ImageSharp stays on the 3.1 line (3.2 and 4.x need a Six Labors license key at build
time). NuGet lists advisories against 3.1.12; they concern the TIFF decoder, a
histogram-equalization processor and the parsing of ICC profile entries, none of which
runs here: only the PNG, GIF, BMP and WebP decoders are registered, no processor is
called, and ICC profiles are carried as opaque bytes. The project file suppresses those
six advisories by name, so a new one still shows up.

`tools/` holds what the numbers above come from:

- `tools\make-testdata.ps1`, `tools\make-texture-refs.py`, `tools\get-testphotos.ps1`:
  build the reference sets under `testdata\` (synthetic drawings and textures, calibration
  pictures; the Kodak set and twelve drawings from Wikimedia Commons are downloaded, not
  redistributed). `testdata\` is not part of the repository.
- `tools\bench`: `unsquish-bench`, which compiles the engine sources and measures them
  against the references (`gen`, `run`, `history`, and a few diagnostics; see the header
  of `tools\bench\Program.cs`). Build it with `dotnet build -c Release -o out` in that folder.
- `tools\sweep.ps1`, `tools\compare.ps1`: tables per set and quality, or per picture.
- `tools\verify.ps1`: the regression suite. It runs the real exe over a folder that
  exercises every route, checks routes, verification and that inputs are untouched,
  holds the engines to quality floors, checks the earlier-compression search for false
  alarms and misses, the command-line behavior, and that the window and the viewer open.

## Licenses

- LibJpeg.NET by Bit Miracle, BSD-3-Clause (JPEG coefficient access, re-encoding)
- libjxl 0.12.0 by the JPEG XL Project Authors, BSD-3-Clause with patent grant (bundled DLLs, extracted at start-up)
- XbrzSharp by Ho Tzin Mein, a port of xBRZ by Zenju, GPL-3.0 (pixel-art upscaling)
- ImageSharp by Six Labors, Six Labors Split License (PNG/GIF/BMP/WebP decoding)
- .NET 10 runtime and Windows Forms by Microsoft, MIT

Because of xBRZ the combined executable is distributed under GPL-3.0; the tool's own source is this repository.
