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
    public string Mode = "jxl";          // restored output: jxl or png
    public string? ForceClass;           // photo | art | pixelart: skip the classifier's verdict
}

public sealed class RunResult { public Dictionary<string, int> Counts = new(); public string OutDir = ""; public double Seconds; public int Files; }

/// <summary>Everything the tool does, driven by Options; reports through callbacks so the CLI and the window share it.</summary>
public static class Engine
{
    public const string Version = "0.5.1";

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
            string full = Path.GetFullPath(inp).TrimEnd('\\');
            if (Directory.Exists(full))
            {
                firstFolder ??= full;
                var opts = new EnumerationOptions { RecurseSubdirectories = o.Recurse, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(full, "*", opts).Where(f => extRe.IsMatch(Path.GetExtension(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) files.Add((f, full));
            }
            else if (File.Exists(full)) { firstFileDir ??= Path.GetDirectoryName(full)!; files.Add((full, Path.GetDirectoryName(full)!)); }
            else log("Input not found: " + inp, ConsoleColor.Red);
        }
        string outDir = o.Output != null ? Path.GetFullPath(o.Output) : (firstFolder != null ? firstFolder + "_restored" : Path.Combine(firstFileDir ?? ".", "_restored"));
        string outPrefix = outDir.TrimEnd('\\') + "\\";
        files = files.Where(f => !f.path.StartsWith(outPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!o.Dry) Directory.CreateDirectory(outDir);
        result.OutDir = outDir; result.Files = files.Count;

        log($"unsquish {Version}: {files.Count} image file(s); output -> {outDir}", ConsoleColor.White);
        var forced = new List<string>();
        if (o.ExplicitQs) forced.Add("level " + o.Qs); if (o.ExplicitBlur) forced.Add("blur " + (o.Blur ? "on" : "off")); if (o.Xbrz > 0) forced.Add("xbrz " + o.Xbrz + "x"); if (o.Snap > 0) forced.Add("snap " + o.Snap + " colours"); if (o.ForceClass != null) forced.Add("class " + o.ForceClass);
        log(o.Auto ? "  auto:   photo -> level 4 + blur; art -> level 6, no blur; pixel art -> xBRZ" + (forced.Count > 0 ? "  (forced: " + string.Join(", ", forced) + ")" : "")
                   : $"  auto:   off (every JPEG below qf {o.Qf} is treated as a photo)", null);
        log($"  jxl:    {(Jxl.Available ? "libjxl " + Jxl.VersionString + " (bundled)" : "not available")}", null);
        log($"  mode: {o.Mode}, level {o.Qs}, blur {(o.Blur ? "on" : "off")}, threshold qf {o.Qf}, threads {o.Threads}{(o.Dry ? ", DRY RUN" : "")}", null);
        log("", null);

        var csv = new List<string> { "\"file\",\"class\",\"confidence\",\"edge_ratio\",\"grid\",\"colors\",\"route\",\"reason\",\"qf\",\"exact_tables\",\"progressive\",\"comps\",\"action\",\"in_bytes\",\"out_bytes\",\"ms\",\"status\",\"message\"" };
        var counts = new Dictionary<string, int> { ["restore"] = 0, ["lossless"] = 0, ["pixelart"] = 0, ["copy"] = 0, ["skip"] = 0, ["exists"] = 0, ["fail"] = 0 };
        var swAll = System.Diagnostics.Stopwatch.StartNew(); int n = 0;
        foreach (var (f, baseDir) in files)
        {
            if (ct.IsCancellationRequested) { log("Cancelled.", ConsoleColor.Yellow); break; }
            n++; progress?.Invoke(n, files.Count); var sw = System.Diagnostics.Stopwatch.StartNew();
            string rel = f.Substring(baseDir.Length).TrimStart('\\');
            string relBase = Path.Combine(Path.GetDirectoryName(rel) ?? "", Path.GetFileNameWithoutExtension(rel));
            string outBase = Path.Combine(outDir, relBase);
            string status = "ok", msg = "", action = ""; long outBytes = 0; long inBytes = new FileInfo(f).Length;
            string fmt = Loader.Sniff(f);
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
                    if (decoded != null && frames <= 1) cls = Classifier.Classify(decoded); else if (decoded == null) cls.Note = "decode failed";
                }
                catch (Exception ex) { cls.Note = "classify failed: " + ex.Message; }
                if (o.ForceClass != null && decoded != null) { cls.Class = o.ForceClass; cls.Confidence = 1; cls.Note = "forced"; }
            }
            var (route, reason) = Route(info, o, cls, fmt, frames);
            int effQs = o.Qs; bool effBlur = o.Blur;
            if (o.Auto && cls.Class == "art") { if (!o.ExplicitQs) effQs = 6; if (!o.ExplicitBlur) effBlur = false; }
            string tag = $"[{n}/{files.Count}] {rel}" + (o.Auto && cls.Class != "-" ? $"  [{cls.Class} {cls.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}]" : "");
            if (route == "skip") { status = "skip"; msg = reason; counts["skip"]++; log($"{tag}  SKIP  {msg}", ConsoleColor.Yellow); }
            else if (o.Dry)
            {
                status = "dry"; msg = reason; counts[route]++;
                if (o.Features) log($"{rel}\t{cls.Class}\thr_proxy={cls.HardRatio.ToString(CultureInfo.InvariantCulture)}\thr_native={cls.HardRatioNative.ToString(CultureInfo.InvariantCulture)}\tdensity={cls.Density.ToString(CultureInfo.InvariantCulture)}\tgrid={cls.GridK}\t{cls.Width}x{cls.Height}\tqf={info.Qf}\t{route}", ConsoleColor.Cyan);
                else log($"{tag}  would {route.ToUpperInvariant()}  ({reason})", ConsoleColor.Cyan);
            }
            else
            {
                string ext = route switch { "copy" => ".jpg", "lossless" => (o.Mode == "jxl" ? ".jxl" : ".jpg"), "pixelart" => ".png", _ => (o.Mode == "jxl" ? ".jxl" : ".png") };
                string existing = outBase + ext;
                if (File.Exists(existing) && !o.Overwrite) { status = "exists"; msg = "output exists (use /overwrite)"; counts["exists"]++; log($"{tag}  EXISTS  {existing}", ConsoleColor.DarkGray); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outBase)!);
                    (bool ok, string act, string? outPath, string m) res;
                    try
                    {
                        res = route switch
                        {
                            "restore" => DoRestore(f, outBase, effQs, effBlur, decoded, info, o),
                            "lossless" => DoLossless(f, outBase, o),
                            "pixelart" => DoPixelArt(f, outBase, info, cls, o, decoded),
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
        if (!o.Dry && files.Count > 0) File.WriteAllLines(Path.Combine(outDir, "unsquish_log.csv"), csv, new UTF8Encoding(true));
        log("", null);
        string summary = o.Dry
            ? $"DRY RUN, nothing written. Without the preview this would: restore {counts["restore"]}, re-save {counts["lossless"]} losslessly, upscale {counts["pixelart"]} pixel-art, copy {counts["copy"]}, skip {counts["skip"]}. Output folder would be {outDir}"
            : $"Done in {swAll.Elapsed.TotalSeconds.ToString("n1", CultureInfo.InvariantCulture)} s: {counts["restore"]} restored, {counts["lossless"]} lossless, {counts["pixelart"]} pixel-art, {counts["copy"]} copied, {counts["skip"]} skipped, {counts["exists"]} already existed, {counts["fail"]} failed";
        log(summary, counts["fail"] > 0 ? ConsoleColor.Red : ConsoleColor.White);
        if (!o.Dry && files.Count > 0) log("Log: " + Path.Combine(outDir, "unsquish_log.csv"), null);
        return result;
    }

    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    static (string route, string reason) Route(JpegInfo info, Options o, ClassResult cls, string fmt, int frames)
    {
        if (frames > 1) return ("skip", $"animated image ({frames} frames)");
        if (!info.IsJpeg)
        {
            if (fmt == "unknown") return ("skip", "unrecognized file format");
            if (o.Auto && cls.Class == "pixelart") return ("pixelart", "pixel art: " + cls.Note);
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
        if (o.Mode == "jxl")
        {
            string outp = outBase + ".jxl";
            var bytes = Jxl.EncodeLossless(img, info.IccProfile, info.ExifTiff, 7, o.Threads);
            var back = Jxl.Decode(bytes);
            if (back == null || RgbImage.Psnr(img, back) < 120) return (false, "jxl", null, "JPEG XL lossless verification failed");
            File.WriteAllBytes(outp, bytes);
            return (true, what + " -> jxl lossless (verified)", outp, "");
        }
        else { string outp = outBase + ".png"; Png.Write(outp, img, info.IccProfile, info.ExifTiff); return (true, what + " -> png", outp, ""); }
    }

    static (bool, string, string?, string) DoLossless(string f, string outBase, Options o)
    {
        if (o.Mode == "jxl")
        {
            string outj = outBase + ".jxl"; string jm;
            try
            {
                var jpeg = File.ReadAllBytes(f);
                var jxl = Jxl.TranscodeJpeg(jpeg, 7, o.Threads);
                var back = Jxl.ReconstructJpeg(jxl, jpeg.Length);
                if (back != null && back.AsSpan().SequenceEqual(jpeg)) { File.WriteAllBytes(outj, jxl); return (true, "reversible JPEG XL transcode (byte-verified)", outj, ""); }
                jm = back == null ? "reconstruction not possible" : $"reconstruction differs ({back.Length} vs {jpeg.Length} bytes)";
                if (Environment.GetEnvironmentVariable("RESTORE_DEBUG") == "1") File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "restore_debug_transcode.jxl"), jxl);
            }
            catch (Exception ex) { jm = ex.Message; }
            string outp = outBase + ".jpg";
            if (Jpeg.ReencodeOptimized(f, outp, out string m)) return (true, "Huffman-optimized re-encode (coefficient-verified; JXL: " + jm + ")", outp, "");
            if (File.Exists(outp)) File.Delete(outp);
            return DoCopy(f, outBase, "lossless re-encode not possible (" + m + "); copied unchanged");
        }
        else
        {
            string outp = outBase + ".jpg";
            if (Jpeg.ReencodeOptimized(f, outp, out string m)) return (true, "Huffman-optimized re-encode (coefficient-verified)", outp, "");
            if (File.Exists(outp)) File.Delete(outp);
            return DoCopy(f, outBase, "lossless re-encode not possible (" + m + "); copied unchanged");
        }
    }

    static (bool, string, string?, string) DoCopy(string f, string outBase, string why)
    {
        string outp = outBase + ".jpg"; File.Copy(f, outp, true);
        if (!Hash(f).SequenceEqual(Hash(outp))) { File.Delete(outp); return (false, "copy", null, "copy verification failed (hash mismatch)"); }
        return (true, "copy (verified)", outp, why);
    }
    static byte[] Hash(string p) { using var s = File.OpenRead(p); return SHA256.HashData(s); }

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
        if (cls.GridK >= 2) { img = img.ResamplePoint(Math.Max(1, img.W / cls.GridK), Math.Max(1, img.H / cls.GridK)); steps.Add($"undo {cls.GridK}x nearest-neighbour"); }
        if (o.Snap > 0 && info.IsJpeg) { img = Filters.MedianCut(img, o.Snap); steps.Add($"snap to {o.Snap} colours"); }
        int factor = Math.Max(2, Math.Min(4, (int)Math.Ceiling(1024.0 / Math.Max(1, Math.Max(img.W, img.H)))));
        if (o.Xbrz > 0) factor = o.Xbrz;
        int nw = img.W, nh = img.H;
        if (factor > 1)
        {
            bool alpha = img.HasAlpha;
            var scaler = new Xbrz.XbrzScaler(factor, alpha);
            var src = img.ToArgb(); var dst = scaler.ScaleImage(src, null, img.W, img.H);
            img = RgbImage.FromArgb(dst, img.W * factor, img.H * factor, alpha); steps.Add($"xBRZ {factor}x" + (alpha ? " with alpha" : ""));
        }
        else if (steps.Count == 0) steps.Add("native size");
        string outp = outBase + ".png"; Png.Write(outp, img);
        return (true, string.Join(" + ", steps) + " -> png", outp, $"native {nw}x{nh}");
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
        W("               nearest-neighbour upscale, optional palette snap, xBRZ 2-4x -> PNG");
        W("  - any JPEG at/above /qf -> LOSSLESS: reversible JPEG XL transcode, verified by rebuilding the original bytes");
        W("               (with /png: same coefficients re-encoded with optimized Huffman tables, verified coefficient by coefficient)");
        W("  - 12-bit, CMYK, arithmetic, lossless-JPEG, MPF/Ultra HDR (data after EOI) -> COPY unchanged (hash-verified)");
        W("  - PNG/GIF/BMP/WebP inputs are only processed when they are pixel art; animated images are skipped");
        W("Restored photos and art are written as lossless JPEG XL (ICC profile and EXIF carried over), or PNG with /png.");
        W("Inputs are never modified. Output defaults to <input>_restored next to the input.");
        W("");
        W("Options:");
        W("  /noauto      no content detection: every JPEG below /qf is treated as a photo (JPEG-only)");
        W("  /class X     force the class for every file: photo, art or pixelart (skips the classifier's verdict)");
        W("  /qf N        quality-factor threshold (default 90): restore files below N; /qf 1 = lossless for everything");
        W("  /qs N        force the restoration level 0-6 for every class (0-2 fast: 2 iterations; 3-4: 3; 5-6: 4)");
        W("  /blur /noblur  force the selective-blur stage on or off for every class");
        W("  /xbrz N      force the xBRZ factor for pixel art (1 = keep size, 2-6; default: enough for ~1024 px)");
        W("  /snap N      pixel art from JPEG only: snap to an N-colour palette before xBRZ (off by default)");
        W("  /jxl /png    restored output format: lossless JPEG XL (default) or PNG; /png also makes the lossless");
        W("               branch write Huffman-optimized JPEGs instead of JPEG XL");
        W("  /threads N   CPU threads for restoration and JPEG XL encoding (default: all)");
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
                case "/dry": o.Dry = true; break;
                case "/features": o.Dry = true; o.Features = true; break;
                case "/overwrite": o.Overwrite = true; break;
                case "/norecurse": o.Recurse = false; break;
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
        // positional: input(s); a second positional that does not exist yet is the output folder (classic two-argument form)
        if (pos.Count == 2 && !File.Exists(pos[1]) && !Directory.Exists(pos[1])) { o.Inputs.Add(pos[0]); o.Output = pos[1]; }
        else o.Inputs.AddRange(pos);
        return o;
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        var o = Parse(args, out int code); if (o == null) return code;
        var r = Engine.Run(o, (s, c) => W(s, c));
        return r.Counts.TryGetValue("fail", out int fails) && fails > 0 ? 1 : 0;
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
        if (gui || OwnsConsole())
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
