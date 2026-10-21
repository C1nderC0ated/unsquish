# Unsquish

Un-squishes compressed images: JPEG artifact restoration, pixel-art upscaling and verified
lossless re-saving, in one Windows exe with a window and a command line. Version 0.6.0.

`unsquish.exe` is the whole tool in a single Windows executable: content classification,
JPEG restoration, pixel-art upscaling, lossless re-saving, JPEG XL output, and a window
for using it without a terminal. Nothing has to be installed and nothing sits beside it.
Requires 64-bit Windows 10 or 11.

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
5. **Guide** lists what happens to each kind of file and which settings to force when.

| Window setting | Command line | Notes |
|---|---|---|
| Content: Auto-detect / Photo / Art / Pixel art / No detection | (default) / `/class photo` / `/class art` / `/class pixelart` / `/noauto` | *Art* is the one to force for anime or illustrations that were detected as photo |
| Restoration level: Auto / 0-6 | (default) / `/qs N` | photo 4, art 6 on Auto; 5-6 for heavily crushed files |
| Selective blur: Auto / On / Off | (default) / `/blur` / `/noblur` | photo on, art off on Auto |
| Pixel-art upscale: Auto / Keep size / 2x-6x | (default) / `/xbrz 1` / `/xbrz N` | |
| Palette snap | `/snap N` | pixel art from JPEG only; helps few-colour sprites, hurts busy screens |
| Restore JPEGs below quality | `/qf N` | files at or above are re-saved losslessly; 1 = everything lossless |
| Output format: JPEG XL / PNG | `/jxl` / `/png` | |
| Threads | `/threads N` | |
| Include subfolders / Overwrite | `/norecurse` / `/overwrite` | |
| Preview | `/dry` | |

Settings are kept in `%LOCALAPPDATA%\unsquish\gui.ini`. `unsquish.exe /gui` opens the
window from a terminal.

## Command line

Started from a terminal with arguments, the exe behaves as before:

```
unsquish.exe "C:\Users\you\Pictures\holiday"            -> ...\holiday_restored
unsquish.exe "C:\Users\you\Pictures\holiday" "E:\out"
unsquish.exe a.jpg b.jpg "C:\more\pics" /out "E:\out"   -> several inputs into one folder (without /out: _restored next to the first file)
unsquish.exe "C:\Users\you\Pictures\holiday" /dry       -> decisions only, nothing written
unsquish.exe /?
```

Inputs are never modified. Existing outputs are skipped unless `/overwrite`.

## What it does per file

| Detected | Route | Output |
|---|---|---|
| JPEG below the quality threshold (default 90), classed **photo** | smoothing across block seams with every DCT coefficient projected back into the interval the file allows (3 iterations), then a mild selective blur | lossless JPEG XL (`/png` for PNG), ICC and EXIF carried over |
| same, classed **art** (flat colour, hard lines; also photographed documents) | 4 iterations, no blur | lossless JPEG XL or PNG, ICC and EXIF carried over |
| **pixel art** (sprites, game screens, icons, nearest-neighbour enlargements; JPEG, PNG, GIF, BMP or WebP) | restoration if JPEG, undo the enlargement, optional `/snap N` palette, xBRZ 2x-4x (more after undoing a larger enlargement, so the output is never smaller than the input; 8x runs as 4x then 2x) | PNG |
| **animated pixel art** (GIF, animated PNG or WebP whose first frame is pixel art) | the same steps on every frame | animated PNG with the original frame delays and loop count |
| JPEG at or above the threshold | reversible JPEG XL transcode, verified by rebuilding the original bytes (with `/png`: Huffman-optimized JPEG, verified coefficient by coefficient) | `.jxl` (about 20 percent smaller) or `.jpg` |
| 12-bit, CMYK, arithmetic-coded, lossless JPEG; MPF / Ultra HDR / Motion Photo (data after the end marker) | copied unchanged, hash-verified | `.jpg` |
| PNG/GIF/BMP/WebP that are not pixel art (including animations of photos or drawings), files of an unrecognized type | skipped, reason logged: there is no JPEG damage in them to undo | |

File types are recognized from their content, not their extension. The CSV log in the
output folder records class, confidence, edge ratios, grid factor, quality factor, route,
action, sizes and timing per file.

## How the classifier decides

- **edge hardness** on a point-sampled proxy *and* on full-resolution crops; art requires
  hard edges in both (large photos look hard-edged on the proxy only)
- **colour density** (unique colours per pixel): palette images are below 2 percent
- **pixel grid**: a nearest-neighbour round trip that beats a box-filtered one by 3 dB
  reveals a 2x-8x nearest-neighbour enlargement, which is undone before xBRZ. Every grid
  phase is tried, because a cropped or padded sprite rarely starts on a cell boundary.
- **palette texture**: a GIF has at most 256 colours and dithered edges, which makes any
  small GIF look like a sprite. A quantized soft picture gives itself away by its texture:
  many tiny neighbour differences (adjacent palette entries), hard edges that cross seven
  or more colours (gradients), or hardly any hard edges at all. Such files are kept out of
  the pixel-art route. JPEG sources are exempt, since JPEG noise would trigger this on
  genuine sprites.

Calibrated on 16 test images, the 47 game screens shipped with xBRZ, 37 dithered GIF
renditions of photos and drawings, and a library of 111 real images (phone photos,
screenshots, drawings, memes). Against 0.5.1 the library decisions changed for two files:
a soft 256-px WebP is no longer treated as pixel art, and a sprite enlarged 8x with an
offset grid is now recognized as pixel art.

## Options

| Option | Meaning |
|---|---|
| `/out X` | output folder (default: `<input>_restored` next to the input; a last argument that does not exist yet also works) |
| `/noauto` | no content detection: every JPEG below `/qf` is treated as a photo, other formats ignored |
| `/class photo|art|pixelart` | force the class for every file (the classifier still measures the pixel grid for pixel art) |
| `/qf N` | quality-factor threshold, default 90 |
| `/qs N` | force the restoration level 0-6 (0-2: 2 iterations, 3-4: 3, 5-6: 4 with a wider smoothing scale) |
| `/blur`, `/noblur` | force the selective-blur stage on or off |
| `/xbrz N` | force the xBRZ factor for pixel art (1 = keep size, 2-6); default: enough for about 1024 px, at least the undone enlargement |
| `/snap N` | pixel art from JPEG only: median-cut to N colours before xBRZ (off by default; helps few-colour sprites, hurts rich screens) |
| `/jxl`, `/png` | restored output format (JPEG XL default); `/png` also makes the lossless branch write Huffman-optimized JPEGs |
| `/threads N` | CPU threads for restoration and JPEG XL encoding |
| `/norecurse`, `/overwrite`, `/dry`, `/features`, `/gui`, `/?` | as named; `/features` prints the classifier measurements per file |

Exit code 0 when everything succeeded, 1 if any file failed, 2 on a usage error.

## Restoration quality

Benchmark against uncompressed originals, PSNR in dB (the jpegqs-based bundle in brackets):

| Image | q10 | q20 | q35 |
|---|---|---|---|
| wallpaper render, plain decode | 28.9 | 31.6 | 33.3 |
| wallpaper render, restored | 29.5 (29.5) | 32.3 (32.5) | 34.0 (34.5) |
| landscape photo, plain decode | 32.5 | 36.7 | 39.7 |
| landscape photo, restored | 33.7 (33.5) | 38.2 (38.0) | 41.2 (41.2) |

Every lossless output is verified before it is kept: JPEG XL transcodes are decoded back
and compared byte for byte with the input; restored JPEG XL files are decoded back and
compared pixel for pixel; Huffman re-encodes are compared coefficient by coefficient.

## Limitations

- Restoration undoes the last compression generation only; re-saved or resized images gain little.
- Grayscale JPEGs are written as RGB. CMYK and 12-bit JPEGs are copied, not restored.
- Animated inputs are processed only when they are pixel art (every frame, animated PNG out,
  all frames held in memory, so the factor is lowered for very long animations). Other
  animations are skipped. Viewers without APNG support show the first frame.
- Dithered GIFs with 64 colours or fewer can still pass as pixel art; the texture test was
  tuned on 256-colour GIFs.
- JPEG XL viewers: Chrome 155+, Firefox 158+, IrfanView/XnView with plugin, GIMP, darktable.
  Convert any output with a JPEG XL tool if your viewer lacks support, or choose PNG.
- Memory: about 150 MB for a 12-megapixel restoration.
- A console window flashes for a moment on double-click (the exe is a console program
  that hands over to the window); harmless.
- The exe grew from 13 MB to 26 MB because the window needs the Windows Forms runtime.

## Building

Requires the .NET 10 SDK on 64-bit Windows. The six libjxl 0.12.0 DLLs are in `native/`.

```text
dotnet publish -c Release -o publish -p:PublishTrimmed=true -p:TrimMode=partial
```

The result is `publish\unsquish.exe`, one self-contained file of about 26 MB.

## Licenses

- LibJpeg.NET by Bit Miracle, BSD-3-Clause (JPEG decoding, coefficient access, re-encoding)
- libjxl 0.12.0 by the JPEG XL Project Authors, BSD-3-Clause with patent grant (bundled DLLs, extracted at start-up)
- XbrzSharp by Ho Tzin Mein, a port of xBRZ by Zenju, GPL-3.0 (pixel-art upscaling)
- ImageSharp by Six Labors, Six Labors Split License (PNG/GIF/BMP/WebP decoding)
- .NET 10 runtime and Windows Forms by Microsoft, MIT

Because of xBRZ the combined executable is distributed under GPL-3.0; the tool's own source is this repository.
