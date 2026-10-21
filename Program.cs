// unsquish.exe - standalone image restoration: no helper programs, everything managed.
// Started from a terminal it is a command-line tool; started by double-click (or with files dropped on its icon)
// it opens a small window. Both drive the same Engine.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Unsquish;

public sealed class Options
{
    public List<string> Inputs = new(); public string? Output;
    public bool Blur = true, Dry, Overwrite, Recurse = true, Auto = true;
    public int Qf = 90, Qs = 4, Threads = Environment.ProcessorCount, Xbrz, Snap;
    public bool ExplicitQs, ExplicitBlur, Features;
    public bool Smooth;                  // pixel art: xBRZ smoothing instead of the crisp nearest-neighbour upscale
    public string Mode = "jxl";          // restored output: jxl or png
    public string? ForceClass;           // photo | art | pixelart: skip the classifier's verdict
}

public sealed class RunResult { public Dictionary<string, int> Counts = new(); public string OutDir = ""; public double Seconds; public int Files; }

/// <summary>Everything the tool does, driven by Options; reports through callbacks so the CLI and the window share it.</summary>
public static class Engine
{
    public const string Version = "0.7.1";

    public static RunResult Run(Options o, Action<string, ConsoleColor?> log, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var result = new RunResult();
        if (o.Mode == "jxl" && !Jxl.Available) { log("JPEG XL library could not be loaded; writing PNG instead.", ConsoleColor.Yellow); o.Mode = "png"; }
        Jpeg.Threads = Math.Max(1, o.Threads);
        // inputs: folders (recursed) and single files; relative output paths are kept per folder
        var extRe = new Regex(o.Auto ? @"^\.(jpe?g|jpe|jfif|png|gif|bmp|webp)$" : @"^\.(jpe?g|jpe|jfif)$", RegexOptions.IgnoreCase);
        var files = new List<(string path, string baseDir)>();
        string? firstFolder = null, firstFileDir = null;
        foreach (var inp in o.Inputs)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inp));   // keeps a drive root such as E:\ intact
            if (Directory.Exists(full))
            {
                firstFolder ??= full;
                var opts = new EnumerationOptions { RecurseSubdirectories = o.Recurse, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(full, "*", opts).Where(f => extRe.IsMatch(Path.GetExtension(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) files.Add((f, full));
            }
            else if (File.Exists(full)) { firstFileDir ??= Path.GetDirectoryName(full)!; files.Add((full, Path.GetDirectoryName(full)!)); }
            else log("Input not found: " + inp, ConsoleColor.Red);
        }
        string outDir = Path.GetFullPath(o.Output ?? (firstFolder != null ? DefaultOut(firstFolder) : Path.Combine(firstFileDir ?? ".", "_restored")));
        if (!o.Dry) Directory.CreateDirectory(outDir);
        // The same file given twice, and anything living inside the output folder, are not inputs. Compared by resolved
        // identity, so a mapped drive, a subst drive or a junction that names the output folder is caught as well.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string outCanon = Paths.Canon(outDir).TrimEnd('\\');
        int dropped = 0; var kept = new List<(string path, string baseDir)>(files.Count);
        foreach (var fb in files)
        {
            string dir = Path.GetDirectoryName(fb.path) ?? fb.path;
            if (!dirCanon.TryGetValue(dir, out var dc)) { dc = Paths.Canon(dir).TrimEnd('\\'); dirCanon[dir] = dc; }
            if (!seenPaths.Add(Path.Combine(dc, Path.GetFileName(fb.path)))) continue;
            if (dc.Equals(outCanon, StringComparison.OrdinalIgnoreCase) || dc.StartsWith(outCanon + "\\", StringComparison.OrdinalIgnoreCase)) { dropped++; continue; }
            kept.Add(fb);
        }
        files = kept;
        result.OutDir = outDir; result.Files = files.Count;

        log($"unsquish {Version}: {files.Count} image file(s); output -> {outDir}", ConsoleColor.White);
        if (dropped > 0) log($"  note: {dropped} input file(s) lie inside the output folder and are skipped; an output folder cannot also be an input", ConsoleColor.Yellow);
        var forced = new List<string>();
        if (o.ExplicitQs) forced.Add("level " + o.Qs); if (o.ExplicitBlur) forced.Add("blur " + (o.Blur ? "on" : "off")); if (o.Xbrz > 0) forced.Add("scale " + o.Xbrz + "x"); if (o.Smooth) forced.Add("smooth xBRZ"); if (o.Snap > 0) forced.Add("snap " + o.Snap + " colours"); if (o.ForceClass != null) forced.Add("class " + o.ForceClass);
        log(o.Auto ? "  auto:   photo -> level 4 + blur; art -> level 6, no blur; pixel art -> crisp upscale" + (forced.Count > 0 ? "  (forced: " + string.Join(", ", forced) + ")" : "")
                   : $"  auto:   off (every JPEG below qf {o.Qf} is treated as a photo)", null);
        log($"  jxl:    {(Jxl.Available ? "libjxl " + Jxl.VersionString + " (bundled)" : "not available")}", null);
        log($"  mode: {o.Mode}, level {o.Qs}, blur {(o.Blur ? "on" : "off")}, threshold qf {o.Qf}, threads {o.Threads}{(o.Dry ? ", DRY RUN" : "")}", null);
        log("", null);

        var csv = new List<string> { "\"file\",\"class\",\"confidence\",\"edge_ratio\",\"grid\",\"colors\",\"route\",\"reason\",\"qf\",\"exact_tables\",\"progressive\",\"comps\",\"action\",\"in_bytes\",\"out_bytes\",\"ms\",\"status\",\"message\"" };
        var counts = new Dictionary<string, int> { ["restore"] = 0, ["lossless"] = 0, ["pixelart"] = 0, ["copy"] = 0, ["skip"] = 0, ["exists"] = 0, ["fail"] = 0 };
        var swAll = System.Diagnostics.Stopwatch.StartNew(); int n = 0;
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // output stems written by this run
        foreach (var (f, baseDir) in files)
        {
            if (ct.IsCancellationRequested) { log("Cancelled.", ConsoleColor.Yellow); break; }
            n++; progress?.Invoke(n, files.Count); var sw = System.Diagnostics.Stopwatch.StartNew();
            string rel = f.Substring(baseDir.Length).TrimStart('\\');
            string relBase = Path.Combine(Path.GetDirectoryName(rel) ?? "", Path.GetFileNameWithoutExtension(rel));
            string outBase = Path.Combine(outDir, relBase);
            string status = "ok", msg = "", action = ""; long outBytes = 0, inBytes = 0; string fmt;
            try { inBytes = new FileInfo(f).Length; fmt = Loader.Sniff(f); }
            catch (Exception ex)
            {
                counts["fail"]++; log($"[{n}/{files.Count}] {rel}  FAIL  read: {ex.Message}", ConsoleColor.Red);
                csv.Add(string.Join(",", Csv(f), "-", "0", "-1", "1", "0", "-", Csv(""), "0", "False", "False", "0", Csv("read"), 0, 0, sw.ElapsedMilliseconds, "fail", Csv(ex.Message)));
                continue;
            }
            JpegInfo info;
            if (fmt == "jpeg") { try { info = JpegProbe.Probe(f); } catch (Exception ex) { info = new JpegInfo { IsJpeg = true, Error = "probe failed: " + ex.Message }; } }
            else info = new JpegInfo { Error = fmt == "unknown" ? "unrecognized file format" : "not a JPEG" };
            RgbImage? decoded = null; int frames = 1;
            var cls = new ClassResult { Class = "-" };
            if (o.Auto && (info.IsJpeg || fmt != "unknown"))
            {
                try
                {
                    decoded = info.IsJpeg ? Jpeg.DecodePlain(f) : Loader.LoadNonJpeg(f, out frames);
                    if (decoded != null) cls = Classifier.Classify(decoded, info.IsJpeg); else cls.Note = "decode failed";   // animations: first frame
                }
                catch (Exception ex) { cls.Note = "classify failed: " + ex.Message; }
                if (o.ForceClass != null && decoded != null) { cls.Class = o.ForceClass; cls.Confidence = 1; cls.Note = "forced"; }
            }
            var (route, reason) = Route(info, o, cls, fmt, frames);
            // two inputs with the same relative name (D:\2019\IMG_0001.jpg and D:\2020\IMG_0001.jpg) must not share an output;
            // a skipped file writes nothing and claims nothing
            string wanted = outBase; int dup = 1;
            if (route != "skip") while (!claimed.Add(outBase)) outBase = $"{wanted} ({++dup})";
            int effQs = o.Qs; bool effBlur = o.Blur;
            if (o.Auto && cls.Class == "art") { if (!o.ExplicitQs) effQs = 6; if (!o.ExplicitBlur) effBlur = false; }
            string tag = $"[{n}/{files.Count}] {rel}" + (o.Auto && cls.Class != "-" ? $"  [{cls.Class} {cls.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}]" : "");
            if (dup > 1) log($"{tag}  note: same name as an earlier input, written as {Path.GetFileName(outBase)}.*", ConsoleColor.Yellow);
            if (route == "skip") { status = "skip"; msg = reason; counts["skip"]++; log($"{tag}  SKIP  {msg}", ConsoleColor.Yellow); }
            else if (o.Dry)
            {
                status = "dry"; msg = reason; counts[route]++;
                if (o.Features) log($"{rel}\t{cls.Class}\thr_proxy={cls.HardRatio.ToString(CultureInfo.InvariantCulture)}\thr_native={cls.HardRatioNative.ToString(CultureInfo.InvariantCulture)}\tdensity={cls.Density.ToString(CultureInfo.InvariantCulture)}\tsoft={cls.Soft.ToString(CultureInfo.InvariantCulture)}\tmulti={cls.Multi.ToString(CultureInfo.InvariantCulture)}\tdithered={(cls.Dithered ? 1 : 0)}\tgrid={cls.GridK}\t{cls.Width}x{cls.Height}\tqf={info.Qf}\t{route}", ConsoleColor.Cyan);
                else log($"{tag}  would {route.ToUpperInvariant()}  ({reason})", ConsoleColor.Cyan);
            }
            else
            {
                // every name this route can produce (the lossless route may fall back from .jxl to .jpg)
                string[] exts = route switch { "copy" => new[] { ".jpg" }, "lossless" => o.Mode == "jxl" ? new[] { ".jxl", ".jpg" } : new[] { ".jpg" }, "pixelart" => new[] { ".png" }, _ => new[] { o.Mode == "jxl" ? ".jxl" : ".png" } };
                string? existing = exts.Select(e => outBase + e).FirstOrDefault(File.Exists);
                string? self = exts.Select(e => outBase + e).FirstOrDefault(p => File.Exists(p) && Paths.SameFile(f, p));
                if (self != null) { status = "fail"; msg = "the output path is the input file itself"; counts["fail"]++; log($"{tag}  FAIL  {msg}: {self}", ConsoleColor.Red); }
                else if (existing != null && !o.Overwrite) { status = "exists"; msg = "output exists (use /overwrite)"; counts["exists"]++; log($"{tag}  EXISTS  {existing}", ConsoleColor.DarkGray); }
                else
                {
                    (bool ok, string act, string? outPath, string m) res;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outBase)!);
                        res = route switch
                        {
                            "restore" => DoRestore(f, outBase, effQs, effBlur, decoded, info, o),
                            "lossless" => DoLossless(f, outBase, o),
                            "pixelart" => frames > 1 ? DoPixelArtAnimated(f, outBase, fmt, cls, o) : DoPixelArt(f, outBase, info, cls, o, decoded),
                            _ => DoCopy(f, outBase, reason),
                        };
                    }
                    catch (Exception ex) { res = (false, route, null, ex.Message); }
                    action = res.act; msg = res.m;
                    if (res.ok && res.outPath != null)
                    {
                        outBytes = new FileInfo(res.outPath).Length; counts[route]++;
                        log($"{tag}  {route.ToUpperInvariant()}  qf={info.Qf}  {action}  {inBytes / 1024}KB -> {outBytes / 1024}KB  {sw.ElapsedMilliseconds}ms", route == "restore" || route == "pixelart" ? ConsoleColor.Green : ConsoleColor.Gray);
                    }
                    else { status = "fail"; counts["fail"]++; log($"{tag}  FAIL  {action}: {msg}", ConsoleColor.Red); }
                }
            }
            sw.Stop();
            csv.Add(string.Join(",", Csv(f), cls.Class, cls.Confidence.ToString("0.00", CultureInfo.InvariantCulture), cls.HardRatio.ToString(CultureInfo.InvariantCulture), cls.GridK, cls.Colors, route, Csv(reason), info.Qf, info.Exact, info.Progressive, info.Comps, Csv(action), inBytes, outBytes, sw.ElapsedMilliseconds, status, Csv(msg)));
        }
        swAll.Stop(); result.Seconds = swAll.Elapsed.TotalSeconds; result.Counts = counts;
        string? csvPath = null;
        if (!o.Dry && files.Count > 0)
        {
            csvPath = Path.Combine(outDir, "unsquish_log.csv");
            try { File.WriteAllLines(csvPath, csv, new UTF8Encoding(true)); }
            catch (Exception ex)
            {
                // the previous log is open in a spreadsheet, or the folder turned read-only: never lose the run over it
                string alt = Path.Combine(outDir, $"unsquish_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                try { File.WriteAllLines(alt, csv, new UTF8Encoding(true)); log($"  note: unsquish_log.csv could not be written ({ex.Message}); the log is {Path.GetFileName(alt)}", ConsoleColor.Yellow); csvPath = alt; }
                catch (Exception ex2) { log("  note: the CSV log could not be written: " + ex2.Message, ConsoleColor.Yellow); csvPath = null; }
            }
        }
        log("", null);
        string summary = o.Dry
            ? $"DRY RUN, nothing written. Without the preview this would: restore {counts["restore"]}, re-save {counts["lossless"]} losslessly, upscale {counts["pixelart"]} pixel-art, copy {counts["copy"]}, skip {counts["skip"]}. Output folder would be {outDir}"
            : $"Done in {swAll.Elapsed.TotalSeconds.ToString("n1", CultureInfo.InvariantCulture)} s: {counts["restore"]} restored, {counts["lossless"]} lossless, {counts["pixelart"]} pixel-art, {counts["copy"]} copied, {counts["skip"]} skipped, {counts["exists"]} already existed, {counts["fail"]} failed";
        log(summary, counts["fail"] > 0 ? ConsoleColor.Red : ConsoleColor.White);
        if (csvPath != null) log("Log: " + csvPath, null);
        return result;
    }

    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    /// <summary>Default output next to the input folder; a drive root gets a folder inside it.</summary>
    static string DefaultOut(string folder) => Path.GetPathRoot(folder) == folder ? Path.Combine(folder, "_restored") : folder + "_restored";

    /// <summary>Writes through a temporary name in the same folder and renames once the write (and its verification)
    /// succeeded, so a failure or a Ctrl+C never leaves a truncated file under the final name.</summary>
    static void Atomic(string outp, Action<string> write)
    {
        string tmp = outp + ".partial";
        try { write(tmp); File.Move(tmp, outp, true); }
        finally { if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } } }
    }

    /// <summary>Decodes a PNG just written and compares every pixel (and alpha) with what was meant to be written.</summary>
    static void VerifyPng(string path, RgbImage img)
    {
        var back = Loader.LoadNonJpeg(path, out _) ?? throw new IOException("PNG verification failed: the file cannot be decoded");
        if (back.W != img.W || back.H != img.H || !back.Rgb.AsSpan().SequenceEqual(img.Rgb)) throw new IOException("PNG verification failed: pixels differ");
        if (img.Alpha != null && (back.Alpha == null ? img.Alpha.Any(a => a != 255) : !back.Alpha.AsSpan().SequenceEqual(img.Alpha))) throw new IOException("PNG verification failed: alpha differs");
    }

    /// <summary>An ICC profile for a grey colour space cannot describe an RGB image: libjxl refuses it and the PNG
    /// specification forbids it. Grayscale JPEGs are decoded to three equal channels, so their profile is dropped.</summary>
    static byte[]? IccForRgb(byte[]? icc, out string? note)
    {
        note = null;
        if (icc != null && icc.Length >= 20 && icc[16] == (byte)'G' && icc[17] == (byte)'R' && icc[18] == (byte)'A' && icc[19] == (byte)'Y') { note = "grey ICC profile dropped (RGB output)"; return null; }
        return icc;
    }

    static (string route, string reason) Route(JpegInfo info, Options o, ClassResult cls, string fmt, int frames)
    {
        if (frames > 1)
        {
            if (o.Auto && cls.Class == "pixelart")
                return CleanScaled(cls, o) ? ("skip", $"clean pixel art at {cls.GridK}x integer scale ({frames} frames), nothing to do") : ("pixelart", $"animated pixel art ({frames} frames): {cls.Note}");
            return ("skip", $"animated image ({frames} frames), not pixel art" + (cls.Class != "-" ? $" ({cls.Class})" : ""));
        }
        if (!info.IsJpeg)
        {
            if (fmt == "unknown") return ("skip", "unrecognized file format");
            if (o.Auto && cls.Class == "pixelart")
                return CleanScaled(cls, o) ? ("skip", $"clean pixel art at {cls.GridK}x integer scale, nothing to do") : ("pixelart", "pixel art: " + cls.Note);
            return ("skip", $"{fmt} is not a JPEG, nothing to restore ({cls.Class})");
        }
        if (!info.Ok) return ("skip", info.Error);
        if (info.Prec != 8) return ("copy", $"{info.Prec}-bit JPEG");
        if (info.Lossless) return ("copy", "lossless JPEG (SOF3/7/11/15)");
        if (info.Arith) return ("copy", "arithmetic-coded JPEG");
        if (info.Comps == 4) return ("copy", "CMYK/4-component JPEG");
        if (info.Mpf || info.Tail > 0 || info.Hdrgm) return ("copy", $"MPF/Ultra HDR or data after EOI ({info.Tail} bytes)");
        if (info.Tables == 0) return ("copy", "no quantization tables found");
        if (o.Auto && cls.Class == "pixelart") return ("pixelart", $"pixel art (quality {info.Qf}): {cls.Note}");
        if (info.Qf >= o.Qf) return ("lossless", $"quality {info.Qf} >= {o.Qf}");
        if (o.Auto && cls.Class != "-" && cls.Class != "unknown") return ("restore", $"quality {info.Qf} < {o.Qf}, {cls.Class}");
        return ("restore", $"quality {info.Qf} < {o.Qf}");
    }

    /// <summary>A lossless input that is already an exact integer enlargement of pixel art: the crisp default would
    /// reproduce it pixel for pixel, so it is left alone unless the user asked for a factor or for xBRZ smoothing.</summary>
    static bool CleanScaled(ClassResult cls, Options o) => cls.GridK >= 2 && !o.Smooth && o.Xbrz == 0;

    static (bool, string, string?, string) DoRestore(string f, string outBase, int level, bool blur, RgbImage? plain, JpegInfo info, Options o)
    {
        var img = Jpeg.DecodeRestored(f, level, out var st);
        string what;
        if (img == null) { img = plain ?? Jpeg.DecodePlain(f); if (img == null) return (false, "decode", null, "could not decode"); what = "plain decode (unsupported colour space for restoration)"; }
        else what = $"coefficient smoothing level {level} ({st.Iterations} it, noise {st.NoiseY.ToString(CultureInfo.InvariantCulture)})";
        if (blur) { img = Filters.SelectiveBlur(img, 0.05); what += " + selective-blur"; }
        return WriteRestored(img, outBase, info, o, what);
    }

    static (bool, string, string?, string) WriteRestored(RgbImage img, string outBase, JpegInfo info, Options o, string what)
    {
        // the decoders return pixels in stored order: bake the Exif orientation in and reset the tag, so JPEG XL
        // (which follows the codestream orientation) and every PNG viewer show the image upright
        int orient = Exif.Orientation(info.ExifTiff);
        if (orient > 1) { img = img.Orient(orient); what += $" + orientation {orient} applied"; }
        var exif = Exif.Normalized(info.ExifTiff);
        var icc = IccForRgb(info.IccProfile, out string? iccNote); if (iccNote != null) what += " + " + iccNote;
        var final = img;
        if (o.Mode == "jxl")
        {
            string outp = outBase + ".jxl";
            var bytes = Jxl.EncodeLossless(final, icc, exif, 7, o.Threads);
            var back = Jxl.Decode(bytes);
            if (back == null || RgbImage.Psnr(final, back) < 120) return (false, "jxl", null, "JPEG XL lossless verification failed");
            Atomic(outp, t => File.WriteAllBytes(t, bytes));
            return (true, what + " -> jxl lossless (verified)", outp, "");
        }
        else
        {
            string outp = outBase + ".png";
            Atomic(outp, t => { Png.Write(t, final, icc, exif); VerifyPng(t, final); });
            return (true, what + " -> png (verified)", outp, "");
        }
    }

    static (bool, string, string?, string) DoLossless(string f, string outBase, Options o)
    {
        string jm = "";
        if (o.Mode == "jxl")
        {
            string outj = outBase + ".jxl";
            try
            {
                var jpeg = File.ReadAllBytes(f);
                var jxl = Jxl.TranscodeJpeg(jpeg, 7, o.Threads);
                var back = Jxl.ReconstructJpeg(jxl, jpeg.Length);
                if (back != null && back.AsSpan().SequenceEqual(jpeg)) { Atomic(outj, t => File.WriteAllBytes(t, jxl)); return (true, "reversible JPEG XL transcode (byte-verified)", outj, ""); }
                jm = back == null ? "reconstruction not possible" : $"reconstruction differs ({back.Length} vs {jpeg.Length} bytes)";
                if (Environment.GetEnvironmentVariable("RESTORE_DEBUG") == "1") File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "restore_debug_transcode.jxl"), jxl);
            }
            catch (Exception ex) { jm = ex.Message; }
        }
        // coefficient-level re-encode through a temporary name: only a complete, verified file ever gets the final name,
        // and nothing that this call did not create is ever deleted
        string outp = outBase + ".jpg", tmp = outp + ".partial"; bool ok = false; string m = "";
        try { ok = Jpeg.ReencodeOptimized(f, tmp, out m); if (ok) File.Move(tmp, outp, true); }
        finally { if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } } }
        if (ok) return (true, "Huffman-optimized re-encode (coefficient-verified" + (jm.Length > 0 ? "; JXL: " + jm : "") + ")", outp, "");
        return DoCopy(f, outBase, "lossless re-encode not possible (" + m + "); copied unchanged");
    }

    static (bool, string, string?, string) DoCopy(string f, string outBase, string why)
    {
        string outp = outBase + ".jpg";
        Atomic(outp, t => { File.Copy(f, t, true); if (!Hash(f).SequenceEqual(Hash(t))) throw new IOException("copy verification failed (hash mismatch)"); });
        return (true, "copy (verified)", outp, why);
    }
    static byte[] Hash(string p) { using var s = File.OpenRead(p); return SHA256.HashData(s); }

    /// <summary>Upscale factor for a native-size pixel image: enough for about 1024 px (2-4x), and never below the undone
    /// nearest-neighbour factor, so the output is at least as large as the input. /xbrz N overrides.</summary>
    static int UpscaleFactor(Options o, int w, int h, int gridK) => o.Xbrz > 0 ? o.Xbrz : Math.Max(Math.Max(2, Math.Min(4, (int)Math.Ceiling(1024.0 / Math.Max(1, Math.Max(w, h))))), gridK);

    /// <summary>Crisp nearest-neighbour upscale (the default: blocks stay blocks), or xBRZ smoothing with /smooth.</summary>
    static RgbImage Upscale(RgbImage img, int f, bool smooth, out string how)
    {
        bool alpha = img.HasAlpha;
        if (!smooth) { how = $"nearest-neighbour {f}x" + (alpha ? " with alpha" : ""); return img.ResamplePoint(img.W * f, img.H * f); }
        var r = XbrzScale(img, f, alpha, out string passes);
        how = $"xBRZ {f}x" + (passes.Contains("then") ? $" ({passes})" : "") + (alpha ? " with alpha" : "");
        return r;
    }

    /// <summary>Scales by f with xBRZ in passes of at most 6x (the scaler's limit): 8x is 4x then 2x.</summary>
    static RgbImage XbrzScale(RgbImage img, int f, bool alpha, out string passes)
    {
        var parts = new List<string>();
        while (f > 1)
        {
            int p = f <= 6 ? f : (f % 4 == 0 ? 4 : f % 3 == 0 ? 3 : f % 2 == 0 ? 2 : 6);
            var dst = new Xbrz.XbrzScaler(p, alpha).ScaleImage(img.ToArgb(), null, img.W, img.H);
            img = RgbImage.FromArgb(dst, img.W * p, img.H * p, alpha); parts.Add(p + "x");
            f = f <= 6 ? 1 : (int)Math.Ceiling(f / (double)p);
        }
        passes = string.Join(" then ", parts);
        return img;
    }

    /// <summary>Animated pixel art (GIF/PNG/WebP): every frame gets the same treatment, out comes an animated PNG with the
    /// original delays and loop count. Palette snapping is a JPEG-only step and does not apply here.</summary>
    static (bool, string, string?, string) DoPixelArtAnimated(string f, string outBase, string fmt, ClassResult cls, Options o)
    {
        var anim = Animation.Load(f, fmt);
        if (anim == null || anim.Value.frames.Count == 0) return (false, "decode", null, "could not decode the frames");
        var (frames, loop) = anim.Value; var steps = new List<string>();
        int w = frames[0].Image.W, h = frames[0].Image.H;
        if (cls.GridK >= 2) { var first = frames[0].Image.UndoGrid(cls.GridK, cls.GridPhaseX, cls.GridPhaseY); w = first.W; h = first.H; steps.Add($"undo {cls.GridK}x nearest-neighbour"); }
        int factor = UpscaleFactor(o, w, h, cls.GridK);
        const long maxPixels = 250_000_000;   // all output frames in memory at once: keep it under about 1 GB
        while (factor > 1 && (long)frames.Count * w * factor * h * factor > maxPixels) factor--;
        var outFrames = new List<RgbImage>(frames.Count); var delays = new List<int>(frames.Count); string how = "native size";
        foreach (var fr in frames)
        {
            var img = fr.Image;
            if (cls.GridK >= 2) img = img.UndoGrid(cls.GridK, cls.GridPhaseX, cls.GridPhaseY);
            if (factor > 1) img = Upscale(img, factor, o.Smooth, out how);
            outFrames.Add(img); delays.Add(fr.DelayMs);
        }
        steps.Add(how);
        string outp = outBase + ".png";
        Atomic(outp, t =>
        {
            Animation.WriteApng(t, outFrames, delays, loop);
            var back = Animation.Load(t, "png");
            if (back == null || back.Value.frames.Count != outFrames.Count || RgbImage.Psnr(back.Value.frames[0].Image, outFrames[0]) < 120) throw new IOException("animated PNG verification failed");
        });
        return (true, string.Join(" + ", steps) + $" -> animated png ({frames.Count} frames, verified)", outp, $"native {w}x{h}, {frames.Count} frames");
    }

    static (bool, string, string?, string) DoPixelArt(string f, string outBase, JpegInfo info, ClassResult cls, Options o, RgbImage? plain)
    {
        var steps = new List<string>(); RgbImage? img;
        if (info.IsJpeg)
        {
            if (info.Qf > 0 && info.Qf < o.Qf) { img = Jpeg.DecodeRestored(f, 4, out _) ?? plain ?? Jpeg.DecodePlain(f); steps.Add("coefficient smoothing level 4"); }
            else img = plain ?? Jpeg.DecodePlain(f);
        }
        else img = plain ?? Loader.LoadNonJpeg(f, out _);
        if (img == null) return (false, "decode", null, "could not decode");
        if (cls.GridK >= 2) { img = img.UndoGrid(cls.GridK, cls.GridPhaseX, cls.GridPhaseY); steps.Add($"undo {cls.GridK}x nearest-neighbour"); }
        int orient = info.IsJpeg ? Exif.Orientation(info.ExifTiff) : 1;
        if (orient > 1) { img = img.Orient(orient); steps.Add($"orientation {orient} applied"); }
        if (o.Snap > 0 && info.IsJpeg) { img = Filters.MedianCut(img, o.Snap); steps.Add($"snap to {o.Snap} colours"); }
        int factor = UpscaleFactor(o, img.W, img.H, cls.GridK);
        int nw = img.W, nh = img.H;
        if (factor > 1) { img = Upscale(img, factor, o.Smooth, out string how); steps.Add(how); }
        else if (steps.Count == 0) steps.Add("native size");
        string outp = outBase + ".png";
        var icc = IccForRgb(info.IccProfile, out string? iccNote); if (iccNote != null) steps.Add(iccNote);
        var exif = Exif.Normalized(info.ExifTiff); var final = img;
        Atomic(outp, t => { Png.Write(t, final, icc, exif); VerifyPng(t, final); });
        return (true, string.Join(" + ", steps) + " -> png (verified)", outp, $"native {nw}x{nh}");
    }
}

/// <summary>Command-line front end.</summary>
public static class Cli
{
    static void W(string s, ConsoleColor? c = null) { if (c != null) Console.ForegroundColor = c.Value; Console.WriteLine(s); Console.ResetColor(); }

    public static void Usage()
    {
        W($"unsquish {Engine.Version} - un-squishes compressed images (no helper programs needed)", ConsoleColor.White);
        W("");
        W("Usage: unsquish <file-or-folder> [more files...] [options]        (double-click the exe for the window)");
        W("       unsquish <file-or-folder> <output-folder> [options]");
        W("");
        W("Per file (auto mode, the default): classify the content, then route it.");
        W("  - PHOTO  (soft edges, texture)      JPEG below /qf -> coefficient-constrained smoothing (level 4) + mild selective blur");
        W("  - ART    (flat colour, hard lines)  JPEG below /qf -> coefficient-constrained smoothing (level 6), no blur");
        W("  - PIXEL ART (1-px edges, or an exact nearest-neighbour upscale) -> restoration if JPEG, undo the");
        W("               nearest-neighbour upscale, optional palette snap, crisp nearest-neighbour upscale to ~1024 px");
        W("               (xBRZ smoothing with /smooth) -> PNG; clean pixel art already at an integer scale is left alone");
        W("  - any JPEG at/above /qf -> LOSSLESS: reversible JPEG XL transcode, verified by rebuilding the original bytes");
        W("               (with /png: same coefficients re-encoded with optimized Huffman tables, verified coefficient by coefficient)");
        W("  - 12-bit, CMYK, arithmetic, lossless-JPEG, MPF/Ultra HDR (data after EOI) -> COPY unchanged (hash-verified)");
        W("  - PNG/GIF/BMP/WebP inputs are only processed when they are pixel art; animated images are skipped");
        W("Restored photos and art are written as lossless JPEG XL (ICC profile and EXIF carried over), or PNG with /png.");
        W("Inputs are never modified. Output defaults to <input>_restored next to the input.");
        W("");
        W("Options:");
        W("  /out X       output folder (default: <input>_restored next to the input). Without /out, a last argument that does not");
        W("               exist yet, is an empty folder or holds a previous unsquish_log.csv is the output; a folder of images is an input");
        W("  /noauto      no content detection: every JPEG below /qf is treated as a photo (JPEG-only)");
        W("  /class X     force the class for every file: photo, art or pixelart (skips the classifier's verdict)");
        W("  /qf N        quality-factor threshold (default 90): restore files below N; /qf 1 = lossless for everything");
        W("  /qs N        force the restoration level 0-6 for every class (0-2 fast: 2 iterations; 3-4: 3; 5-6: 4)");
        W("  /blur /noblur  force the selective-blur stage on or off for every class");
        W("  /xbrz N      pixel-art upscale factor (1 = keep size, 2-6; default: enough for ~1024 px, at least the undone enlargement)");
        W("  /smooth      pixel art: xBRZ smoothing (curves instead of blocks) in place of the crisp nearest-neighbour upscale");
        W("  /snap N      pixel art from JPEG only: snap to an N-colour palette before upscaling (off by default)");
        W("  /jxl /png    restored output format: lossless JPEG XL (default) or PNG; /png also makes the lossless");
        W("               branch write Huffman-optimized JPEGs instead of JPEG XL");
        W("  /threads N   CPU threads for restoration and JPEG XL encoding (default: all)");
        W("  /cli         command-line mode even when the process owns its console (scripts, Task Scheduler); /gui forces the window");
        W("  /norecurse   do not descend into subfolders");
        W("  /overwrite   replace existing output files (default: skip them)");
        W("  /dry         show class and decision per file, write nothing");
        W("");
        W("The CSV log (unsquish_log.csv) records class, confidence, edge ratio and grid factor per file.");
    }

    /// <summary>Parses arguments; returns null and an exit code when the program should stop.</summary>
    public static Options? Parse(string[] args, out int exitCode)
    {
        exitCode = 0; var o = new Options(); var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i], l = a.ToLowerInvariant();
            switch (l)
            {
                case "/?": case "/h": case "/help": case "-h": case "--help": Usage(); return null;
                case "/fast": o.Qs = 2; o.ExplicitQs = true; break;
                case "/png": o.Mode = "png"; break;
                case "/jxl": o.Mode = "jxl"; break;
                case "/jpg": W("Note: /jpg (raw jpegqs JPEG output) does not exist in this edition; writing lossless JPEG XL instead.", ConsoleColor.Yellow); break;
                case "/noblur": o.Blur = false; o.ExplicitBlur = true; break;
                case "/blur": o.Blur = true; o.ExplicitBlur = true; break;
                case "/auto": o.Auto = true; break;
                case "/noauto": o.Auto = false; break;
                case "/smooth": o.Smooth = true; break;
                case "/cli": break;   // consumed in Main
                case "/dry": o.Dry = true; break;
                case "/features": o.Dry = true; o.Features = true; break;
                case "/overwrite": o.Overwrite = true; break;
                case "/norecurse": o.Recurse = false; break;
                case "/out":
                    i++;
                    if (i >= args.Length) { W("/out needs a folder", ConsoleColor.Red); exitCode = 2; return null; }
                    o.Output = args[i]; break;
                case "/class":
                    i++;
                    if (i >= args.Length || !(args[i].ToLowerInvariant() is "photo" or "art" or "pixelart")) { W("/class needs photo, art or pixelart", ConsoleColor.Red); exitCode = 2; return null; }
                    o.ForceClass = args[i].ToLowerInvariant(); break;
                case "/qf": case "/qs": case "/threads": case "/xbrz": case "/snap":
                    i++;
                    if (i >= args.Length || !int.TryParse(args[i], out int num)) { W($"Option {a} needs a number", ConsoleColor.Red); exitCode = 2; return null; }
                    if (l == "/qf") o.Qf = num; else if (l == "/qs") { o.Qs = num; o.ExplicitQs = true; } else if (l == "/xbrz") o.Xbrz = num; else if (l == "/snap") o.Snap = num; else o.Threads = num;
                    break;
                default:
                    if (l.StartsWith('/')) { W("Unknown option " + a, ConsoleColor.Red); Usage(); exitCode = 2; return null; }
                    pos.Add(a); break;
            }
        }
        if (pos.Count < 1) { Usage(); exitCode = 2; return null; }
        if (o.Qs < 0 || o.Qs > 6) { W("/qs must be 0-6", ConsoleColor.Red); exitCode = 2; return null; }
        if (o.Qf < 1 || o.Qf > 100) { W("/qf must be 1-100", ConsoleColor.Red); exitCode = 2; return null; }
        if (o.Xbrz > 6) { W("/xbrz must be 1-6", ConsoleColor.Red); exitCode = 2; return null; }
        if (o.Snap != 0 && (o.Snap < 2 || o.Snap > 256)) { W("/snap must be 2-256 (0 = off)", ConsoleColor.Red); exitCode = 2; return null; }
        // positional: input(s). Without /out, the two-argument form "input output-folder" takes the second as the output
        // folder whether it exists yet or not, so the same command works on every run; with more inputs only a last
        // positional that does not exist yet is taken as the output folder.
        // An existing folder is taken as the output only when it is empty or holds a previous run's unsquish_log.csv;
        // an existing folder of images is an input (two folders of photos are two inputs, as documented).
        static bool OutputLike(string p) => !File.Exists(p) && (!Directory.Exists(p) || File.Exists(Path.Combine(p, "unsquish_log.csv")) || !Directory.EnumerateFileSystemEntries(p).Any());
        if (o.Output == null && pos.Count >= 2 && OutputLike(pos[^1])) { o.Output = pos[^1]; pos.RemoveAt(pos.Count - 1); }
        o.Inputs.AddRange(pos);
        return o;
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        var o = Parse(args, out int code); if (o == null) return code;
        try
        {
            var r = Engine.Run(o, (s, c) => W(s, c));
            return r.Counts.TryGetValue("fail", out int fails) && fails > 0 ? 1 : 0;
        }
        catch (Exception ex) { W("Error: " + ex.Message, ConsoleColor.Red); return 1; }
    }
}

static class Program
{
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] list, uint count);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();

    /// <summary>True when this process is the only one on its console: started by double-click, drag-and-drop onto the icon, or Win+R.</summary>
    static bool OwnsConsole() { try { var l = new uint[4]; return GetConsoleProcessList(l, 4) <= 1; } catch { return false; } }

    [STAThread]
    static int Main(string[] args)
    {
        bool gui = args.Any(a => a.Equals("/gui", StringComparison.OrdinalIgnoreCase));
        bool cli = args.Any(a => a.Equals("/cli", StringComparison.OrdinalIgnoreCase));
        // The window is for double-click and drag-and-drop. A script that gives the process its own hidden console
        // (CreateNoWindow, Task Scheduler) owns that console too, but it redirects the streams or runs without an
        // interactive session: those stay on the command line.
        bool own = OwnsConsole() && !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected && Environment.UserInteractive;
        if (gui || (own && !cli))
        {
            // The console window Windows opened for the double-click is closed again; the window takes over.
            FreeConsole();
            Gui.Start(args.Where(a => !a.Equals("/gui", StringComparison.OrdinalIgnoreCase)).ToArray());
            return 0;
        }
        // Started from a terminal: behave as a command-line tool.
        return Cli.Run(args);
    }
}
