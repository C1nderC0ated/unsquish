// unsquish-bench: measures the restoration engines against clean reference pictures.
//
//   unsquish-bench gen  <refs> <work> <magick.exe> [--q 10,20,30,50,70,85]
//       compresses every reference (refs\<set>\*.png) to <work>\<set>\<name>.q<Q>.<420|444>.jpg
//   unsquish-bench run  <refs> <work> [--sets kodak,anime] [--q 10,30] [--ss 420] [--only text] [--limit N]
//                       [--save dir] [--csv file] [--jpegqs jpegqs64.exe] label=spec ...
//       spec: "plain" | "legacy:<level>" | "preset:<art|photo>:<level>" | "ext:jpegqs:<level>" | engine parameters as in RestoreParams.Parse
//             ("shifts=64,iterations=100,shrink=0.3"); append "|blur" for the selective blur of the 0.7 photo route.
//   unsquish-bench history <refs> <work> <magick.exe> <tmp> ...      (see HistoryCheck.cs)
//   unsquish-bench errmap <ref.png> <a.png> <b.png> <out.png>        where b is closer to the reference than a (green) or further (red)
//   unsquish-bench guard <jpg> <out.png> [scale] [radius] [lo] [hi]  the texture guard of a picture, white = smoothing allowed
//   unsquish-bench psnr <a> <b>                                      PSNR between two pictures, or "identical"
//   unsquish-bench levels <jpg> [plane] [zigzag index ...]           histogram of the levels of some frequencies
//   unsquish-bench dct
//       self-test and timing of the DCT
// Reports PSNR over RGB and SSIM over luma, as gains over the plain decode, averaged per set and quality.
using System.Diagnostics;
using System.Globalization;

namespace Unsquish.Bench;

static class Program
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: unsquish-bench gen|run|history|errmap|guard|dct ... (see the header of Program.cs)"); return 2; }
        switch (args[0])
        {
            case "gen": return Gen(args);
            case "run": return Run(args);
            case "dct": return DctCheck();
            case "errmap": return ErrMap(args);
            case "guard": return GuardMap(args);
            case "history": return HistoryCheck.Run(args);
            case "levels": return Levels(args);
            case "psnr": return PsnrOf(args);

            default: Console.WriteLine("unknown command " + args[0]); return 2;
        }
    }

    internal static string? Opt(List<string> a, string name)
    {
        int i = a.IndexOf(name); if (i < 0 || i + 1 >= a.Count) return null;
        string v = a[i + 1]; a.RemoveRange(i, 2); return v;
    }
    static bool Flag(List<string> a, string name) { int i = a.IndexOf(name); if (i < 0) return false; a.RemoveAt(i); return true; }

    static int Gen(string[] args)
    {
        var a = args.Skip(1).ToList();
        string qs = Opt(a, "--q") ?? "10,20,30,50,70,85";
        if (a.Count < 3) { Console.WriteLine("gen <refs> <work> <magick.exe>"); return 2; }
        string refs = a[0], work = a[1], magick = a[2]; int made = 0;
        foreach (var set in Directory.GetDirectories(refs))
        {
            string outDir = Path.Combine(work, Path.GetFileName(set)); Directory.CreateDirectory(outDir);
            foreach (var png in Directory.GetFiles(set, "*.png"))
                foreach (var q in qs.Split(','))
                    foreach (var ss in new[] { "420", "444" })
                    {
                        if (ss == "444" && q != "30") continue;   // one 4:4:4 point is enough to see the chroma-resolution effect
                        string jpg = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(png)}.q{q}.{ss}.jpg");
                        if (File.Exists(jpg)) continue;
                        var psi = new ProcessStartInfo(magick) { UseShellExecute = false, CreateNoWindow = true };
                        foreach (var s in new[] { png, "-sampling-factor", ss == "420" ? "4:2:0" : "4:4:4", "-quality", q, jpg }) psi.ArgumentList.Add(s);
                        using var p = Process.Start(psi)!; p.WaitForExit();
                        if (p.ExitCode != 0) { Console.WriteLine("magick failed for " + jpg); return 1; }
                        made++;
                    }
        }
        Console.WriteLine($"generated {made} JPEG files under {work}");
        return 0;
    }

    sealed class Variant { public string Label = "", Spec = ""; public bool Blur; public RestoreParams? P; public int Legacy = -1, Ext = -1; public bool Plain; }

    static Variant ParseVariant(string s)
    {
        int eq = s.IndexOf('='); var v = new Variant();
        v.Label = eq < 0 ? s : s[..eq]; string spec = eq < 0 ? s : s[(eq + 1)..];
        if (spec.EndsWith("|blur")) { v.Blur = true; spec = spec[..^5]; }
        v.Spec = spec;
        if (spec == "plain") v.Plain = true;
        else if (spec.StartsWith("legacy:")) { v.Legacy = int.Parse(spec[7..]); v.P = RestoreParams.Legacy(v.Legacy); }
        else if (spec.StartsWith("preset:")) { var a = spec[7..].Split(':'); v.P = Presets.For(a[0], int.Parse(a[1])).p; }   // preset:art:6
        else if (spec.StartsWith("ext:jpegqs:")) v.Ext = int.Parse(spec[11..]);
        else v.P = RestoreParams.Parse(spec);
        return v;
    }

    static int Run(string[] args)
    {
        var a = args.Skip(1).ToList();
        string? setsOpt = Opt(a, "--sets"), qOpt = Opt(a, "--q"), only = Opt(a, "--only"), save = Opt(a, "--save"), csv = Opt(a, "--csv"), jpegqs = Opt(a, "--jpegqs");
        string ss = Opt(a, "--ss") ?? "420"; int limit = int.Parse(Opt(a, "--limit") ?? "0", Inv); int every = int.Parse(Opt(a, "--every") ?? "1", Inv);
        bool detail = Flag(a, "--detail");
        if (a.Count < 3) { Console.WriteLine("run <refs> <work> [options] label=spec ..."); return 2; }
        string refs = a[0], work = a[1];
        var variants = a.Skip(2).Select(ParseVariant).ToList();
        var qualities = (qOpt ?? "10,20,30,50,70,85").Split(',').Select(int.Parse).ToArray();
        var sets = setsOpt != null ? setsOpt.Split(',') : Directory.GetDirectories(refs).Select(d => Path.GetFileName(d)!).ToArray();
        if (save != null) Directory.CreateDirectory(save);
        var csvLines = new List<string> { "set,image,q,ss,variant,psnr,ssim,ms,mp" };

        foreach (var set in sets)
        {
            var pngs = Directory.GetFiles(Path.Combine(refs, set), "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Where(f => only == null || f.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (every > 1) pngs = pngs.Where((_, i) => i % every == 0).ToList();
            if (limit > 0 && pngs.Count > limit) pngs = pngs.Take(limit).ToList();
            if (pngs.Count == 0) continue;
            // sums[variant][qualityIndex] = (delta psnr, delta ssim, ms, megapixels, n)
            var sum = new (double dp, double ds, double ms, double mp, int n)[variants.Count, qualities.Length];
            var plainSum = new (double p, double s, int n)[qualities.Length];
            foreach (var png in pngs)
            {
                var reference = Loader.LoadNonJpeg(png, out _) ?? throw new IOException("cannot read " + png);
                string name = Path.GetFileNameWithoutExtension(png);
                for (int qi = 0; qi < qualities.Length; qi++)
                {
                    string jpg = Path.Combine(work, set, $"{name}.q{qualities[qi]}.{ss}.jpg");
                    if (!File.Exists(jpg)) continue;
                    var plain = Jpeg.DecodePlain(jpg) ?? throw new IOException("cannot decode " + jpg);
                    double p0 = Psnr(reference, plain), s0 = Ssim(reference, plain);
                    plainSum[qi].p += p0; plainSum[qi].s += s0; plainSum[qi].n++;
                    double mp = reference.W * (double)reference.H / 1e6;
                    for (int vi = 0; vi < variants.Count; vi++)
                    {
                        var v = variants[vi]; RgbImage outImg; var sw = Stopwatch.StartNew();
                        if (v.Plain) outImg = plain;
                        else if (v.Ext >= 0)
                        {
                            if (jpegqs == null) throw new ArgumentException("--jpegqs path needed for " + v.Label);
                            string tmp = Path.Combine(Path.GetTempPath(), $"bench_{Environment.ProcessId}.jpg");
                            var psi = new ProcessStartInfo(jpegqs) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                            foreach (var s in new[] { "-q", v.Ext.ToString(Inv), jpg, tmp }) psi.ArgumentList.Add(s);
                            using (var pr = Process.Start(psi)!) { pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd(); pr.WaitForExit(); }
                            outImg = Jpeg.DecodePlain(tmp) ?? plain; File.Delete(tmp);
                        }
                        else
                        {
                            var ci = CoefImage.FromJpeg(jpg) ?? throw new IOException("no coefficients: " + jpg);
                            outImg = Restorer.Run(ci, v.P!.Clone(), out _);
                        }
                        if (v.Blur) outImg = Filters.SelectiveBlur(outImg, 0.05);
                        sw.Stop();
                        double p1 = Psnr(reference, outImg), s1 = Ssim(reference, outImg);
                        sum[vi, qi].dp += p1 - p0; sum[vi, qi].ds += s1 - s0; sum[vi, qi].ms += sw.Elapsed.TotalMilliseconds; sum[vi, qi].mp += mp; sum[vi, qi].n++;
                        csvLines.Add(string.Join(",", set, name, qualities[qi], ss, v.Label, p1.ToString("0.000", Inv), s1.ToString("0.00000", Inv), sw.ElapsedMilliseconds, mp.ToString("0.00", Inv)));
                        if (detail) Console.WriteLine($"  {name,-28} q{qualities[qi],-3} {v.Label,-14} {p1 - p0,+7:+0.00;-0.00} dB  ssim {s1 - s0,+8:+0.0000;-0.0000}  {sw.ElapsedMilliseconds} ms");
                        if (save != null && !v.Plain) Png.Write(Path.Combine(save, $"{set}_{name}.q{qualities[qi]}.{v.Label}.png"), outImg);
                    }
                    if (save != null) Png.Write(Path.Combine(save, $"{set}_{name}.q{qualities[qi]}.0plain.png"), plain);
                }
            }
            Console.WriteLine();
            Console.WriteLine($"== {set}: {pngs.Count} pictures, {ss}  (gain over plain decode: PSNR dB / SSIM x1000)");
            Console.Write($"{"",-22}"); foreach (var q in qualities) Console.Write($"{"q" + q,14}"); Console.WriteLine($"{"ms/MP",9}");
            Console.Write($"{"plain (absolute)",-22}");
            for (int qi = 0; qi < qualities.Length; qi++) Console.Write(plainSum[qi].n == 0 ? $"{"-",14}" : $"{(plainSum[qi].p / plainSum[qi].n).ToString("0.00", Inv) + "/" + (plainSum[qi].s / plainSum[qi].n).ToString("0.000", Inv),14}");
            Console.WriteLine();
            for (int vi = 0; vi < variants.Count; vi++)
            {
                Console.Write($"{variants[vi].Label,-22}"); double ms = 0, mpx = 0;
                for (int qi = 0; qi < qualities.Length; qi++)
                {
                    var c = sum[vi, qi];
                    Console.Write(c.n == 0 ? $"{"-",14}" : $"{(c.dp / c.n).ToString("+0.00;-0.00", Inv) + "/" + (1000 * c.ds / c.n).ToString("+0.0;-0.0", Inv),14}");
                    ms += c.ms; mpx += c.mp;
                }
                Console.WriteLine($"{(mpx > 0 ? ms / mpx : 0).ToString("0", Inv),9}");
            }
        }
        if (csv != null) File.WriteAllLines(csv, csvLines);
        return 0;
    }

    /// <summary>errmap ref a b out: where is picture b closer to the reference than picture a (green) or further (red)?
    /// Squared luma error, averaged over 9x9 windows, drawn over a dimmed copy of the reference.</summary>
    static int ErrMap(string[] args)
    {
        if (args.Length < 5) { Console.WriteLine("errmap <ref.png> <a.png> <b.png> <out.png>"); return 2; }
        var r = Loader.LoadNonJpeg(args[1], out _)!; var a = Loader.LoadNonJpeg(args[2], out _)!; var b = Loader.LoadNonJpeg(args[3], out _)!;
        int w = r.W, h = r.H; var gr = r.Gray(); var ga = a.Gray(); var gb = b.Gray();
        var d = new double[w * h]; double sa = 0, sb = 0;
        for (int i = 0; i < d.Length; i++) { double ea = ga[i] - gr[i], eb = gb[i] - gr[i]; sa += ea * ea; sb += eb * eb; d[i] = eb * eb - ea * ea; }
        var o = new RgbImage(w, h); double worse = 0, better = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double s = 0; int n = 0;
                for (int j = Math.Max(0, y - 4); j <= Math.Min(h - 1, y + 4); j++) for (int i = Math.Max(0, x - 4); i <= Math.Min(w - 1, x + 4); i++) { s += d[j * w + i]; n++; }
                s /= n; if (d[y * w + x] > 0) worse += d[y * w + x]; else better -= d[y * w + x];
                int basev = 40 + gr[y * w + x] / 4; double m = Math.Min(1.0, Math.Abs(s) / 60.0); int k = (y * w + x) * 3;
                o.Rgb[k] = (byte)(basev + (s > 0 ? m * (255 - basev) : 0)); o.Rgb[k + 1] = (byte)(basev + (s < 0 ? m * (255 - basev) : 0)); o.Rgb[k + 2] = (byte)basev;
            }
        Png.Write(args[4], o);
        Console.WriteLine($"luma MSE a {sa / d.Length:0.00}  b {sb / d.Length:0.00}; b worse by {worse / d.Length:0.00}, better by {better / d.Length:0.00} (per pixel)");
        return 0;
    }

    /// <summary>guard jpg out [scale] [radius]: the texture guard of the plain decode, white = smoothing allowed.</summary>
    static int GuardMap(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("guard <jpg> <out.png> [scale] [radius] [lo] [hi]"); return 2; }
        var ci = CoefImage.FromJpeg(args[1])!; var img = Jpeg.DecodePlain(args[1])!;
        float scale = args.Length > 3 ? float.Parse(args[3], Inv) : 1f; int radius = args.Length > 4 ? int.Parse(args[4], Inv) : 2;
        var y = Array.ConvertAll(img.Gray(), v => (float)v);
        float lo = args.Length > 5 ? float.Parse(args[5], Inv) : 0.45f, hi = args.Length > 6 ? float.Parse(args[6], Inv) : 0.75f;
        var g = Restorer.TextureGuard(y, img.W, img.H, (float)Restorer.NoiseScale(ci.Planes[0]) * scale, radius, lo, hi);
        var o = new RgbImage(img.W, img.H); double mean = 0;
        for (int i = 0; i < g.Length; i++) { byte v = (byte)Math.Clamp(g[i] * 255f, 0f, 255f); o.Rgb[i * 3] = o.Rgb[i * 3 + 1] = o.Rgb[i * 3 + 2] = v; mean += g[i]; }
        Png.Write(args[2], o);
        Console.WriteLine($"noise scale {Restorer.NoiseScale(ci.Planes[0]):0.00}, mean weight {mean / g.Length:0.000}");
        return 0;
    }

    /// <summary>psnr a b: PSNR between two pictures (JPEG, PNG, GIF, BMP or WebP), "identical" when no sample differs.</summary>
    static int PsnrOf(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("psnr <a> <b>"); return 2; }
        static RgbImage Load(string p) => (Loader.Sniff(p) == "jpeg" ? Jpeg.DecodePlain(p) : Loader.LoadNonJpeg(p, out _)) ?? throw new IOException("cannot read " + p);
        var a = Load(args[1]); var b = Load(args[2]);
        if (a.W != b.W || a.H != b.H) { Console.WriteLine($"sizes differ: {a.W}x{a.H} and {b.W}x{b.H}"); return 1; }
        Console.WriteLine(a.Rgb.AsSpan().SequenceEqual(b.Rgb) ? "identical" : "psnr " + Psnr(a, b).ToString("0.00", Inv));
        return 0;
    }

    /// <summary>levels jpg [plane] [zigzag index ...]: the step and the histogram of absolute levels of some frequencies.</summary>
    static int Levels(string[] args)
    {
        var ci = CoefImage.FromJpeg(args[1])!; int plane = args.Length > 2 ? int.Parse(args[2]) : 0; var pl = ci.Planes[plane];
        var zs = args.Length > 3 ? args.Skip(3).Select(int.Parse).ToArray() : new[] { 1, 2, 5 };
        foreach (int z in zs)
        {
            int k = JpegProbe.ZigZag[z]; var hist = new int[61]; int blocks = pl.Bw * pl.Bh;
            for (int b = 0; b < blocks; b++) hist[Math.Min(60, Math.Abs((int)pl.Coef[b * 64 + k]))]++;
            Console.WriteLine($"zigzag {z} (index {k}), step {pl.Q[k]}, {blocks} blocks:");
            Console.WriteLine("  " + string.Join(" ", hist.Select((h, i) => h > 0 ? $"{i}:{h}" : "").Where(s => s.Length > 0)));
        }
        return 0;
    }

    /// <summary>PSNR over all three channels.</summary>
    public static double Psnr(RgbImage a, RgbImage b)
    {
        if (a.W != b.W || a.H != b.H) return 0;
        double se = 0; var x = a.Rgb; var y = b.Rgb;
        for (int i = 0; i < x.Length; i++) { double d = x[i] - y[i]; se += d * d; }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 * x.Length / se);
    }

    /// <summary>Mean SSIM over luma, 8x8 windows every 4 pixels (the common fast variant; good for comparing methods).</summary>
    public static double Ssim(RgbImage a, RgbImage b)
    {
        if (a.W != b.W || a.H != b.H) return 0;
        var x = a.Gray(); var y = b.Gray(); int w = a.W, h = a.H; const double c1 = 6.5025, c2 = 58.5225;
        double total = 0; long n = 0;
        for (int by = 0; by + 8 <= h; by += 4)
            for (int bx = 0; bx + 8 <= w; bx += 4)
            {
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                for (int j = 0; j < 8; j++)
                {
                    int row = (by + j) * w + bx;
                    for (int i = 0; i < 8; i++) { double p = x[row + i], q = y[row + i]; sx += p; sy += q; sxx += p * p; syy += q * q; sxy += p * q; }
                }
                double mx = sx / 64, my = sy / 64, vx = sxx / 64 - mx * mx, vy = syy / 64 - my * my, cov = sxy / 64 - mx * my;
                total += (2 * mx * my + c1) * (2 * cov + c2) / ((mx * mx + my * my + c1) * (vx + vy + c2)); n++;
            }
        return n == 0 ? 0 : total / n;
    }

    static int DctCheck()
    {
        var (rt, sv) = Dct8.SelfTest();
        Console.WriteLine($"DCT round trip max error {rt.ToString("0.000000", Inv)}, SIMD vs scalar {sv.ToString("0.000000", Inv)}, hardware accelerated: {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}");
        var f = new float[64]; var s = new float[64]; var rnd = new Random(1); for (int i = 0; i < 64; i++) s[i] = (float)rnd.NextDouble() * 255;
        var sw = Stopwatch.StartNew(); const int n = 2_000_000;
        for (int i = 0; i < n; i++) { Dct8.Forward(s, f); Dct8.Inverse(f, s); }
        sw.Stop();
        Console.WriteLine($"forward + inverse: {(sw.Elapsed.TotalMilliseconds * 1e6 / n).ToString("0", Inv)} ns per block");
        return rt < 1e-3 && sv < 1e-3 ? 0 : 1;
    }
}
