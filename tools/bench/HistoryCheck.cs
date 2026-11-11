// unsquish-bench history <refs> <work> <magick.exe> <tmp> [--sets a,b] [--q 20,50] [--every N] [--mode clean,single,bitmap,resave]
//                        [--resaveq 92] [--level N]
// Tests the search for an earlier compression (History.cs):
//   clean   no reference picture may be reported as a former JPEG
//   single  no once-compressed test JPEG may be reported as re-saved
//   bitmap  test JPEGs decoded to PNG (three ways: smooth chroma upsampling, box upsampling, cropped by 3,5) must be
//           recognized with the right quality; the restoration gain is compared with restoring the JPEG itself
//   resave  test JPEGs re-saved at a high quality must be recognized; gain with and without the recovered history
using System.Diagnostics;
using System.Globalization;

namespace Unsquish.Bench;

static class HistoryCheck
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static void Magick(string magick, params string[] args)
    {
        var psi = new ProcessStartInfo(magick) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var s in args) psi.ArgumentList.Add(s);
        using var p = Process.Start(psi)!; p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException("magick failed: " + string.Join(" ", args));
    }

    static string F(double v) => v.ToString("+0.00;-0.00", Inv);

    public static int Run(string[] args)
    {
        var a = args.Skip(1).ToList();
        string? setsOpt = Program.Opt(a, "--sets"), qOpt = Program.Opt(a, "--q");
        int every = int.Parse(Program.Opt(a, "--every") ?? "1", Inv), resaveQ = int.Parse(Program.Opt(a, "--resaveq") ?? "92", Inv), level = int.Parse(Program.Opt(a, "--level") ?? "4", Inv);
        var modes = (Program.Opt(a, "--mode") ?? "clean,single,bitmap,resave").Split(',');
        bool why = a.Remove("--why"); var misses = new List<string>();
        if (a.Count < 4) { Console.WriteLine("history <refs> <work> <magick.exe> <tmp> [options]"); return 2; }
        string refs = a[0], work = a[1], magick = a[2], tmp = a[3]; Directory.CreateDirectory(tmp);
        var sets = setsOpt != null ? setsOpt.Split(',') : Directory.GetDirectories(refs).Select(d => Path.GetFileName(d)!).ToArray();
        var qualities = (qOpt ?? "10,20,30,50,70,85").Split(',').Select(int.Parse).ToArray();
        int bad = 0;

        if (modes.Contains("clean"))
        {
            int n = 0, hits = 0;
            foreach (var set in sets)
                foreach (var png in Directory.GetFiles(Path.Combine(refs, set), "*.png"))
                {
                    var img = Loader.LoadNonJpeg(png, out _)!; n++;
                    var ci = History.DetectBitmap(img, 89, out var res);
                    if (ci != null) { hits++; Console.WriteLine($"  FALSE POSITIVE (clean)  {set}/{Path.GetFileName(png)}: quality {res.Quality}, {res.Note}"); }
                }
            Console.WriteLine($"clean references reported as former JPEGs: {hits} of {n}"); bad += hits;
        }

        if (modes.Contains("single"))
        {
            int n = 0, hits = 0;
            foreach (var set in sets)
                foreach (var jpg in Directory.GetFiles(Path.Combine(work, set), "*.jpg"))
                {
                    var ci = CoefImage.FromJpeg(jpg); if (ci == null) continue; n++;
                    var res = History.DetectResaved(ci, JpegProbe.Probe(jpg).Qf);
                    if (res.Found) { hits++; Console.WriteLine($"  FALSE POSITIVE (single)  {set}/{Path.GetFileName(jpg)}: quality {res.Quality}, {res.Note}"); }
                }
            Console.WriteLine($"once-compressed JPEGs reported as re-saved: {hits} of {n}"); bad += hits;
        }

        foreach (var mode in new[] { "bitmap", "resave" })
        {
            if (!modes.Contains(mode)) continue;
            var kinds = mode == "bitmap" ? new[] { "smooth", "box", "crop" } : new[] { "resave" };
            foreach (var kind in kinds)
            {
                Console.WriteLine();
                Console.WriteLine($"== {mode}/{kind}: found n/m, mean quality error, gain with history / gain of restoring the first JPEG itself (dB)");
                foreach (var set in sets)
                {
                    bool art = set is "anime" or "art"; var preset = Presets.For(art ? "art" : "photo", level).p;
                    var pngs = Directory.GetFiles(Path.Combine(refs, set), "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Where((_, i) => i % every == 0).ToList();
                    Console.Write($"{set,-9}");
                    foreach (int q in qualities)
                    {
                        int m = 0, found = 0; double qerr = 0, gain = 0, ideal = 0, blind = 0;
                        foreach (var png in pngs)
                        {
                            string name = Path.GetFileNameWithoutExtension(png), jpg = Path.Combine(work, set, $"{name}.q{q}.420.jpg");
                            if (!File.Exists(jpg)) continue;
                            var reference = Loader.LoadNonJpeg(png, out _)!; m++;
                            var first = CoefImage.FromJpeg(jpg)!;
                            double pPlain = Program.Psnr(reference, Jpeg.DecodePlain(jpg)!);
                            ideal += Program.Psnr(reference, Restorer.Run(first, preset.Clone(), out _)) - pPlain;
                            if (mode == "bitmap")
                            {
                                string t = Path.Combine(tmp, $"h_{Environment.ProcessId}.png");
                                if (kind == "smooth") Magick(magick, jpg, t);
                                else if (kind == "box") Magick(magick, "-define", "jpeg:fancy-upsampling=off", jpg, t);
                                else Magick(magick, jpg, "-crop", "+3+5", "+repage", t);
                                var img = Loader.LoadNonJpeg(t, out _)!; File.Delete(t);
                                var refc = kind == "crop" ? Crop(reference, 3, 5) : reference;
                                var ci = History.DetectBitmap(img, 89, out var res);
                                if (ci == null) { if (why) misses.Add($"{kind} {name} q{q}: validated {res.Validated}, quality {res.Quality}, {res.Note}"); continue; }
                                found++; qerr += Math.Abs(res.Quality - q);
                                gain += Program.Psnr(refc, Restorer.Run(ci, preset.Clone(), out _)) - Program.Psnr(refc, img);
                            }
                            else
                            {
                                string t = Path.Combine(tmp, $"h_{Environment.ProcessId}.jpg");
                                Magick(magick, jpg, "-sampling-factor", "4:2:0", "-quality", resaveQ.ToString(Inv), t);
                                var ci = CoefImage.FromJpeg(t)!; int own = JpegProbe.Probe(t).Qf; var plain = Jpeg.DecodePlain(t)!;
                                double p0 = Program.Psnr(reference, plain);
                                blind += Program.Psnr(reference, Restorer.Run(CoefImage.FromJpeg(t)!, preset.Clone(), out _)) - p0;
                                var res = History.DetectResaved(ci, own); File.Delete(t);
                                if (!res.Found) { if (why) misses.Add($"{kind} {name} q{q}: own {own}, validated {res.Validated}, quality {res.Quality}, steps [{res.Steps}] {res.Note}"); continue; }
                                found++; qerr += Math.Abs(res.Quality - q);
                                gain += Program.Psnr(reference, Restorer.Run(ci, preset.Clone(), out _)) - p0;
                            }
                        }
                        string cell = m == 0 ? "-" : $"q{q}: {found}/{m}" + (found > 0 ? $" dq {(qerr / found).ToString("0.0", Inv)} {F(gain / found)}/{F(ideal / m)}" : $" -/{F(ideal / m)}") + (mode == "resave" ? $" (blind {F(blind / m)})" : "");
                        Console.Write($"  {cell,-30}");
                    }
                    Console.WriteLine();
                }
            }
        }
        foreach (var m in misses) Console.WriteLine("  miss: " + m);
        return bad == 0 ? 0 : 1;
    }

    static RgbImage Crop(RgbImage src, int x0, int y0)
    {
        int w = src.W - x0, h = src.H - y0; var o = new RgbImage(w, h);
        for (int y = 0; y < h; y++) Array.Copy(src.Rgb, ((y + y0) * src.W + x0) * 3, o.Rgb, y * w * 3, w * 3);
        return o;
    }
}
