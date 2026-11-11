// Compression history: finding the traces of an EARLIER, harsher JPEG compression that the file no longer declares.
//
//   re-saved JPEG   a picture compressed at quality 30 and later saved again, unscaled, at quality 90 says "90" in its
//                   tables, but its coefficients still sit on the comb of the first quantization.
//   bitmap          a PNG / BMP / lossless WebP that was saved or screenshotted from a JPEG: its block DCT sits on
//                   the comb as well, up to the rounding of the pixels.
//
// In both cases the true picture lies in the wide bins of the first compression, so the restorers may use those. A
// false alarm would smooth a picture that was never damaged, a miss only leaves things as they were; every threshold
// below is therefore set for "certain or nothing", and the steps found must follow one scaled standard (IJG) table,
// which is what messengers, social networks and most software write.
using System.Runtime.CompilerServices;

namespace Unsquish;

public sealed class HistoryResult
{
    public bool Found;          // an earlier compression was established and is harsher than what the file declares
    public int Quality;         // its quality factor on the IJG scale (0 = nothing established)
    public int Validated;       // low frequencies whose step was established
    public string Chroma = "";  // how chroma was stored then (bitmaps): "4:2:0", "4:4:4" or "" when left untouched
    public string Note = "";    // one line for the log
    public string Steps = "";   // the steps established for the low frequencies, zigzag order, 0 = none (diagnostics)
}

public static class History
{
    const int Low = 15;               // AC frequencies examined, in zigzag order
    const int MaxBlocks = 40000;      // blocks sampled per test
    static readonly int[] Zig = JpegProbe.ZigZag;

    // ------------------------------------------------------------------ re-saved JPEG

    /// <summary>Looks for the comb of an earlier quantization in the levels of a JPEG. When one is established and is
    /// harsher than the file's own tables, the planes get their earlier steps (Q1) and the result says so.</summary>
    public static HistoryResult DetectResaved(CoefImage ci, int ownQuality)
    {
        var res = new HistoryResult();
        if (ci.Planes.Length == 0 || ci.Planes[0].Coef.Length == 0) return res;
        var luma = ci.Planes[0];
        var est = new int[64]; int validated = 0;
        for (int z = 1; z <= Low; z++) { int k = Zig[z]; est[k] = EarlierStep(luma, k, 0); if (est[k] > 0) validated++; }
        res.Validated = validated; res.Steps = string.Join(" ", Enumerable.Range(1, Low).Select(z => est[Zig[z]]));
        if (validated < 5) return res;
        int q1 = FitStandard(est, JpegProbe.StdLuma, out bool fitted);
        if (!fitted) { res.Note = "earlier quantization steps found, but they follow no standard table: left alone"; return res; }
        res.Quality = q1;
        if (q1 > ownQuality - 8) return res;                       // not meaningfully harsher than what the file says
        var tl = JpegProbe.Scaled(JpegProbe.StdLuma, q1, 255);
        luma.Q1 = new float[64]; luma.Slack = 1.5f;
        for (int k = 1; k < 64; k++) { int q = est[k] > 0 && Math.Abs(est[k] - tl[k]) <= 2 ? est[k] : tl[k]; luma.Q1[k] = q >= 2f * luma.Q[k] ? q : 0f; }
        // chroma: the same quality factor implies the standard chroma table; accept it only where the levels confirm it
        var tc = JpegProbe.Scaled(JpegProbe.StdChroma, q1, 255); int chromaOk = 0;
        for (int c = 1; c < ci.Planes.Length; c++)
        {
            var pl = ci.Planes[c]; int confirmed = 0, tested = 0;
            for (int z = 1; z <= 6; z++) { int k = Zig[z]; if (tc[k] < 2f * pl.Q[k]) continue; tested++; if (EarlierStep(pl, k, tc[k]) == tc[k]) confirmed++; }
            if (tested >= 3 && confirmed >= 3)
            {
                pl.Q1 = new float[64]; pl.Slack = 1.5f; chromaOk++;
                for (int k = 1; k < 64; k++) pl.Q1[k] = tc[k] >= 2f * pl.Q[k] ? tc[k] : 0f;
            }
        }
        res.Found = true; res.Chroma = chromaOk > 0 ? "confirmed" : "";
        res.Note = $"re-saved: compressed earlier at about quality {q1}, the file says {ownQuality}";
        return res;
    }

    /// <summary>The step of an earlier quantization of one frequency, or 0. A level k of the file is "reachable" from
    /// an earlier step q1 when some non-zero multiple of q1 lies within half a current step, plus the noise of the
    /// decode-and-encode round trip in between, of k * q. After an earlier compression most levels are reachable: not
    /// all, because the round trip rounds pixels and clips colors to the RGB cube (measured: 3 to 13 percent of the
    /// levels land one off their comb tooth in photographs, far more in flat-colour drawings whose edges ring past
    /// white). Without one, levels fall between the teeth as often as the gaps are wide. The candidate whose comb
    /// stands out most (share of gaps minus share of levels found in them) wins, if that is beyond chance.
    /// Levels that a zero of the first compression plus noise explains say nothing and are left out.
    /// only > 0 tests that single candidate.</summary>
    static int EarlierStep(CoefPlane pl, int k, int only)
    {
        float q2 = pl.Q[k]; int blocks = pl.Bw * pl.Bh; int stride = Math.Max(1, blocks / MaxBlocks);
        float tol = 0.5f * q2 + 1.5f;                                 // strict bound on |k q2 - m q1|
        int first = 1; while (first * q2 < tol) first++;              // the first level that zero plus noise does not explain
        const int Cap = 1024; var hist = new int[Cap + 1]; int n = 0, maxLevel = 0;
        for (int b = 0; b < blocks; b += stride)
        {
            int v = Math.Abs((int)pl.Coef[b * 64 + k]); if (v < first) continue;
            if (v > Cap) v = Cap; hist[v]++; n++; if (v > maxLevel) maxLevel = v;
        }
        if (n < 50) return 0;
        int lo = only > 0 ? only : (int)Math.Max(2f * q2, q2 + 2f), hi = only > 0 ? only : (int)Math.Min(255f, maxLevel * q2);
        var valid = new List<(int q, double score, double sse)>(); double bestScore = 0;
        for (int q1 = lo; q1 <= hi; q1++)
        {
            // share of unreachable levels over a span that covers the data and at least two periods (the power of the test)
            int span = Math.Max(maxLevel, (int)Math.Ceiling(2.0 * q1 / q2)); int unreach = 0;
            for (int lv = first; lv <= span; lv++) if (!Reachable(lv * q2, q1, tol, out _)) unreach++;
            double u = (double)unreach / (span - first + 1);
            if (u < 0.25) continue;
            long nout = 0; double sse = 0;
            for (int lv = first; lv <= maxLevel; lv++)
            {
                int h = hist[lv]; if (h == 0) continue;
                if (!Reachable(lv * q2, q1, tol, out float dist)) nout += h; else sse += (double)h * dist * dist;
            }
            double f = (double)nout / n;
            if (f > 0.4 || f >= u) continue;
            // chance that so few of n levels fall into gaps of total share u (Hoeffding bound), against all candidates tried
            if (-0.8686 * n * (u - f) * (u - f) > -8.0) continue;
            valid.Add((q1, u - f, sse / Math.Max(1, n - nout)));
            if (u - f > bestScore) bestScore = u - f;
        }
        // near the top several neighbours can stand out equally (few multiples in the data): the closest fit decides,
        // and of equal fits the largest step (divisors of the true step fit as well as the step itself)
        int best = 0; double bestSse = double.MaxValue;
        foreach (var (q, score, sse) in valid) if (score >= bestScore - 0.01 && sse < bestSse) bestSse = sse;
        foreach (var (q, score, sse) in valid) if (score >= bestScore - 0.01 && sse <= bestSse + 0.02 && q > best) best = q;
        return best;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Reachable(float value, int q1, float tol, out float dist)
    {
        float m = MathF.Round(value / q1); if (m == 0f) m = 1f;
        dist = MathF.Abs(value - m * q1);
        return dist < tol;
    }

    /// <summary>The quality factor whose scaled standard table matches the established steps: at least 80 percent
    /// within one unit and none further off than two. Neighbouring qualities often share their low-frequency steps;
    /// of such a run the middle is taken.</summary>
    static int FitStandard(int[] est, int[] standard, out bool fitted)
    {
        int count = 0; foreach (int e in est) if (e > 0) count++;
        int bestQ = 0, bestLast = 0, bestMatch = -1;
        for (int q = 1; q <= 100; q++)
        {
            var t = JpegProbe.Scaled(standard, q, 255); int match = 0; bool contradiction = false;
            for (int k = 0; k < 64 && !contradiction; k++) { if (est[k] == 0) continue; int d = Math.Abs(est[k] - t[k]); if (d <= 1) match++; else if (d > 2 && !Harmonic(est[k], t[k])) contradiction = true; }
            if (contradiction) continue;
            if (match > bestMatch) { bestMatch = match; bestQ = bestLast = q; }
            else if (match == bestMatch && q == bestLast + 1) bestLast = q;
        }
        bestQ = (bestQ + bestLast) / 2;
        fitted = count > 0 && bestMatch >= Math.Ceiling(0.8 * count);
        // a constant "table" (every established step equal) is what posterized or palette pictures produce: reject it
        int first = 0; bool allEqual = true; foreach (int e in est) { if (e == 0) continue; if (first == 0) first = e; else if (e != first) allEqual = false; }
        if (allEqual && count >= 3) fitted = false;
        return bestQ;
    }

    /// <summary>True when an established step is the table's step seen through a sparser or denser comb: its half,
    /// third or quarter (noise can make a divisor fit better) or its double (only even multiples occurred).</summary>
    static bool Harmonic(int est, int table)
    {
        for (int m = 2; m <= 4; m++) if (Math.Abs(est * m - table) <= m) return true;
        return Math.Abs(est - 2 * table) <= 2;
    }

    // ------------------------------------------------------------------ bitmaps

    /// <summary>Decides whether a decoded picture was once a JPEG of quality maxQuality or lower. If so, returns its
    /// coefficient description (observed block DCT plus the estimated steps), ready for the restorers.</summary>
    public static CoefImage? DetectBitmap(RgbImage img, int maxQuality, out HistoryResult res)
    {
        res = new HistoryResult();
        int W = img.W, H = img.H;
        if (W < 64 || H < 64) return null;
        var Y = new float[W * H]; bool colour = false;
        var rgb = img.Rgb;
        for (int i = 0, p = 0; i < Y.Length; i++, p += 3)
        {
            float r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
            Y[i] = 0.299f * r + 0.587f * g + 0.114f * b;
            if (!colour && (rgb[p] != rgb[p + 1] || rgb[p] != rgb[p + 2])) colour = true;
        }
        // candidate grid origins: the untouched origin, and the phase with the strongest block seams
        int px = StrongestPhase(Y, W, H, true), py = StrongestPhase(Y, W, H, false);
        var origins = new List<(int x, int y)> { (0, 0) }; if (px != 0 || py != 0) origins.Add((px, py));
        int bestValidated = -1; int[] bestEst = new int[64]; (int x, int y) origin = (0, 0);
        double logTests = Math.Log10(63.0 * 119.0 * origins.Count);
        foreach (var o in origins)
        {
            var samples = SampleBlocks(Y, W, H, o.x, o.y, 8);
            var est = new int[64]; int validated = 0;
            for (int z = 1; z <= Low; z++) { int k = Zig[z]; est[k] = StepOf(samples, k, logTests, 0); if (est[k] > 0 && z <= 10) validated++; }
            if (validated > bestValidated) { bestValidated = validated; bestEst = est; origin = o; }
        }
        res.Validated = Math.Max(0, bestValidated);
        if (bestValidated < 6) return null;
        int q1 = FitStandard(bestEst, JpegProbe.StdLuma, out bool fitted);
        if (!fitted) { res.Note = "block structure found, but its steps follow no standard table: left alone"; return null; }
        res.Quality = q1;
        if (q1 > maxQuality) { res.Note = $"was a JPEG of about quality {q1}: nothing to restore"; return null; }
        var tl = JpegProbe.Scaled(JpegProbe.StdLuma, q1, 255); var tc = JpegProbe.Scaled(JpegProbe.StdChroma, q1, 255);

        // chroma layout: test the standard chroma table of that quality on 4:4:4 and on the four possible 4:2:0 alignments
        string layout = ""; int mx = 0, my = 0; bool fancy = false;
        float[] Cb = Array.Empty<float>(), Cr = Array.Empty<float>();
        if (colour)
        {
            // only now: nearly every bitmap fails the luma test above, and these two planes cost 8 bytes per pixel
            Cb = new float[W * H]; Cr = new float[W * H];
            for (int i = 0, p = 0; i < Cb.Length; i++, p += 3)
            {
                float r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
                Cb[i] = -0.168736f * r - 0.331264f * g + 0.5f * b + 128f; Cr[i] = 0.5f * r - 0.418688f * g - 0.081312f * b + 128f;
            }
            int bestOk = 0;
            int ok444 = Math.Min(ConfirmChroma(Cb, W, H, origin.x, origin.y, 1, false, tc), ConfirmChroma(Cr, W, H, origin.x, origin.y, 1, false, tc));
            if (ok444 >= 3) { bestOk = ok444; layout = "4:4:4"; }
            foreach (bool f in new[] { false, true })
                for (int ay = 0; ay <= 8; ay += 8)
                    for (int ax = 0; ax <= 8; ax += 8)
                    {
                        int ok = Math.Min(ConfirmChroma(Cb, W, H, origin.x + ax, origin.y + ay, 2, f, tc), ConfirmChroma(Cr, W, H, origin.x + ax, origin.y + ay, 2, f, tc));
                        if (ok >= 3 && ok > bestOk) { bestOk = ok; layout = "4:2:0"; mx = ax; my = ay; fancy = f; }
                    }
        }
        res.Chroma = layout;

        // canvas: shift the picture so that the block (or 16-pixel MCU) grid starts at 0, pad by replication
        int unit = layout == "4:2:0" ? 16 : 8;
        int shiftX = (unit - (origin.x + mx) % unit) % unit, shiftY = (unit - (origin.y + my) % unit) % unit;
        int Wc = (W + shiftX + unit - 1) / unit * unit, Hc = (H + shiftY + unit - 1) / unit * unit;
        var ci = new CoefImage { Width = W, Height = H, OffsetX = shiftX, OffsetY = shiftY, Alpha = img.Alpha };
        var lumaSamples = Pad(Y, W, H, Wc, Hc, shiftX, shiftY);
        var planes = new List<CoefPlane> { ObservedPlane(lumaSamples, Wc, Hc, 1, tl, 1.25f) };
        int recovered = RecoverClipped(planes[0], lumaSamples);
        if (colour)
        {
            foreach (var src in new[] { Cb, Cr })
            {
                var full = Pad(src, W, H, Wc, Hc, shiftX, shiftY);
                if (layout == "4:2:0") planes.Add(ObservedPlane(Stored420(full, Wc, Hc, fancy), Wc / 2, Hc / 2, 2, tc, 1.5f));
                else if (layout == "4:4:4") planes.Add(ObservedPlane(full, Wc, Hc, 1, tc, 1.5f));
                else { var pl = ObservedPlane(full, Wc, Hc, 1, new int[64], 0f); pl.Frozen = true; planes.Add(pl); }
            }
        }
        ci.Planes = planes.ToArray();
        res.Found = true;
        res.Note = $"was a JPEG of about quality {q1}" + (origin != (0, 0) ? $", block grid at {origin.x},{origin.y}" : "") + (colour ? (layout.Length > 0 ? $", chroma {layout}" : ", chroma left as it is") : ""); res.Steps = "clipped blocks recovered: " + recovered;
        return ci;
    }

    /// <summary>The column (or row) phase 0-7 at which neighbouring pixels differ most on average: where block seams are.</summary>
    static int StrongestPhase(float[] y, int w, int h, bool columns)
    {
        var sum = new double[8]; var cnt = new long[8];
        int stepY = Math.Max(1, h / 512), stepX = Math.Max(1, w / 512);
        if (columns) { for (int r = 0; r < h; r += stepY) for (int x = 1; x < w; x++) { sum[x & 7] += Math.Abs(y[r * w + x] - y[r * w + x - 1]); cnt[x & 7]++; } }
        else { for (int r = 1; r < h; r++) for (int x = 0; x < w; x += stepX) { sum[r & 7] += Math.Abs(y[r * w + x] - y[(r - 1) * w + x]); cnt[r & 7]++; } }
        int best = 0; double bestV = -1;
        for (int i = 0; i < 8; i++) { double v = cnt[i] > 0 ? sum[i] / cnt[i] : 0; if (v > bestV) { bestV = v; best = i; } }
        return best;
    }

    /// <summary>Block DCTs (64 floats each) of the full blocks on the given grid, skipping flat and clipped blocks
    /// (they carry no usable comb and are where false alarms come from), evenly thinned to MaxBlocks.</summary>
    static List<float[]> SampleBlocks(float[] plane, int w, int h, int ox, int oy, int size)
    {
        int nx = (w - ox) / 8, ny = (h - oy) / 8; long total = (long)nx * ny; int stride = (int)Math.Max(1, total / MaxBlocks);
        var list = new List<float[]>((int)Math.Min(total, MaxBlocks) + 1); Span<float> s = stackalloc float[64]; int idx = 0;
        for (int by = 0; by < ny; by++)
            for (int bx = 0; bx < nx; bx++)
            {
                if (idx++ % stride != 0) continue;
                float mn = 255f, mxv = 0f;
                for (int j = 0; j < 8; j++) { int row = (oy + by * 8 + j) * w + ox + bx * 8; for (int i = 0; i < 8; i++) { float v = plane[row + i]; s[j * 8 + i] = v - 128f; if (v < mn) mn = v; if (v > mxv) mxv = v; } }
                if (mxv - mn < 2f || mn < 1f || mxv > 254f) continue;
                var f = new float[64]; Dct8.Forward(s, f); list.Add(f);
            }
        return list;
    }

    /// <summary>The quantization step of frequency k, or 0. Over the coefficients that do not round to zero, the
    /// distance to the nearest multiple of the true step is tiny, while for a picture that was never quantized it is
    /// uniform; the sum of n uniform errors is Irwin-Hall distributed, which bounds the chance of a false alarm
    /// (the statistic of Nikoukhah et al., IPOL 2022). only > 0 tests that single step and its two neighbours.</summary>
    static int StepOf(List<float[]> blocks, int k, double logTests, int only)
    {
        int count = blocks.Count; if (count < 60) return 0;
        float maxAbs = 0f; foreach (var b in blocks) { float a = MathF.Abs(b[k]); if (a > maxAbs) maxAbs = a; }
        int lo = only > 0 ? Math.Max(2, only - 1) : 2, hi = only > 0 ? only + 1 : (int)Math.Min(120f, 2f * maxAbs);
        int best = 0; double bestLog = double.MaxValue, second = double.MaxValue; int secondQ = 0;
        for (int q = lo; q <= hi; q++)
        {
            double inv = 1.0 / q, s = 0; int n = 0;
            foreach (var b in blocks) { double t = b[k] * inv, r = Math.Round(t); if (r == 0) continue; s += 2 * Math.Abs(t - r); n++; }
            if (n < 30 || s >= n / 2.0) continue;
            // P(S <= s) for the sum of n uniforms: at most s^n / n!, and at most exp(-2 (n/2 - s)^2 / n)
            double simplex = n * Math.Log10(Math.Max(s, 1e-9)) - LogFactorial10(n);
            double hoeffding = -0.8686 * (n / 2.0 - s) * (n / 2.0 - s) / n;
            double logNfa = Math.Min(simplex, hoeffding) + logTests;
            if (logNfa < bestLog) { second = bestLog; secondQ = best; bestLog = logNfa; best = q; }
            else if (logNfa < second) { second = logNfa; secondQ = q; }
        }
        if (best == 0 || bestLog > -6.0) return 0;
        // a runner-up that is not a divisor of the winner and nearly as significant means the comb is ambiguous
        if (secondQ > 0 && best % secondQ != 0 && second - bestLog < 3.0 && second < -6.0 && Math.Abs(secondQ - best) > 1) return 0;
        return only > 0 && best != only ? 0 : best;
    }

    static double LogFactorial10(int n)
    {
        if (n < 2) return 0;
        return (n * Math.Log(n) - n + 0.5 * Math.Log(2 * Math.PI * n) + 1.0 / (12.0 * n)) / Math.Log(10);
    }

    /// <summary>How many of the six lowest chroma frequencies carry exactly the step the standard chroma table predicts.</summary>
    static int ConfirmChroma(float[] chroma, int w, int h, int ox, int oy, int factor, bool fancy, int[] table)
    {
        float[] plane = chroma; int pw = w, ph = h, gx = ox, gy = oy;
        if (factor == 2)
        {
            // stored half-resolution samples: cut the picture at the MCU origin so the 2x2 cells line up
            int cx = ox % 16, cy = oy % 16; int cw = (w - cx) / 2 * 2, chh = (h - cy) / 2 * 2; if (cw < 64 || chh < 64) return 0;
            var cut = new float[cw * chh]; for (int yy = 0; yy < chh; yy++) Array.Copy(chroma, (cy + yy) * w + cx, cut, yy * cw, cw);
            plane = Stored420(cut, cw, chh, fancy); pw = cw / 2; ph = chh / 2; gx = 0; gy = 0;
        }
        var samples = SampleBlocks(plane, pw, ph, gx % 8, gy % 8, 8); int ok = 0;
        double logTests = Math.Log10(6.0 * 3.0 * 9.0 * 2.0);
        for (int z = 1; z <= 6; z++) { int k = Zig[z]; if (table[k] >= 2 && StepOf(samples, k, logTests, table[k]) == table[k]) ok++; }
        return ok;
    }

    /// <summary>The half-resolution chroma a decoder started from. With plain replication it is the 2x2 mean. With the
    /// "fancy" triangle interpolation of libjpeg (what browsers and most viewers use) the 2x2 mean equals the stored
    /// samples filtered by (1, 6, 1) / 8 in both directions, which is inverted exactly by two tridiagonal solves.</summary>
    static float[] Stored420(float[] full, int w, int h, bool fancy)
    {
        int cw = w / 2, ch = h / 2; var s = new float[cw * ch];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++) { int i = 2 * y * w + 2 * x; s[y * cw + x] = 0.25f * (full[i] + full[i + 1] + full[i + w] + full[i + w + 1]); }
        if (!fancy) return s;
        var line = new float[Math.Max(cw, ch)]; var work = new float[Math.Max(cw, ch)];
        for (int y = 0; y < ch; y++) { Array.Copy(s, y * cw, line, 0, cw); SolveTriangle(line, work, cw); Array.Copy(line, 0, s, y * cw, cw); }
        for (int x = 0; x < cw; x++) { for (int y = 0; y < ch; y++) line[y] = s[y * cw + x]; SolveTriangle(line, work, ch); for (int y = 0; y < ch; y++) s[y * cw + x] = line[y]; }
        return s;
    }

    /// <summary>Solves (1, 6, 1) / 8 * x = b with replicated ends (first and last diagonal 7 / 8), in place (Thomas algorithm).</summary>
    static void SolveTriangle(float[] b, float[] c, int n)
    {
        if (n < 2) return;
        const float off = 0.125f; float diag = 0.875f;
        c[0] = off / diag; b[0] /= diag;
        for (int i = 1; i < n; i++)
        {
            float d = (i == n - 1 ? 0.875f : 0.75f) - off * c[i - 1];
            c[i] = off / d; b[i] = (b[i] - off * b[i - 1]) / d;
        }
        for (int i = n - 2; i >= 0; i--) b[i] -= c[i] * b[i + 1];
    }

    static float[] Pad(float[] src, int w, int h, int wc, int hc, int sx, int sy)
    {
        var o = new float[wc * hc];
        for (int y = 0; y < hc; y++)
        {
            int yy = Math.Clamp(y - sy, 0, h - 1);
            for (int x = 0; x < wc; x++) o[y * wc + x] = src[yy * w + Math.Clamp(x - sx, 0, w - 1)];
        }
        return o;
    }

    /// <summary>A decoder clips what rings past white or black, so the DCT of such a block is no longer on the comb
    /// and its coefficients could only be pinned where they are; in drawings on a white page that is every block
    /// with a line in it. Here the levels are recovered: assume the clipped pixels really went further, take the
    /// nearest comb point, decode it, let the clipped pixels follow that decode outwards, repeat. When a comb point
    /// reproduces the block (clipped pixels at or past the limit, the others within a level) it replaces the
    /// observation. Returns the number of blocks recovered.</summary>
    static int RecoverClipped(CoefPlane pl, float[] samples)
    {
        int w = pl.W, recovered = 0; var q = pl.Q; var obs = pl.Obs!;
        Parallel.For(0, pl.Bh, Restorer.Par, () => 0, (by, _, local) =>
        {
            Span<float> o = stackalloc float[64], x = stackalloc float[64], f = stackalloc float[64], y = stackalloc float[64]; Span<sbyte> clip = stackalloc sbyte[64];
            for (int bx = 0; bx < pl.Bw; bx++)
            {
                int nclip = 0;
                for (int j = 0; j < 8; j++)
                {
                    int row = (by * 8 + j) * w + bx * 8;
                    for (int i = 0; i < 8; i++) { float v = samples[row + i]; o[j * 8 + i] = v - 128f; sbyte c = (sbyte)(v >= 254.5f ? 1 : (v <= 0.5f ? -1 : 0)); clip[j * 8 + i] = c; if (c != 0) nclip++; }
                }
                if (nclip == 0) continue;
                o.CopyTo(x); bool ok = false;
                for (int it = 0; it < 12 && !ok; it++)
                {
                    Dct8.Forward(x, f);
                    for (int k = 0; k < 64; k++) if (q[k] > 0f) f[k] = MathF.Round(f[k] / q[k]) * q[k];
                    Dct8.Inverse(f, y); ok = true;
                    for (int i = 0; i < 64; i++)
                    {
                        if (clip[i] > 0) { if (y[i] < o[i] - 0.75f) ok = false; x[i] = MathF.Max(o[i], y[i]); }
                        else if (clip[i] < 0) { if (y[i] > o[i] + 0.75f) ok = false; x[i] = MathF.Min(o[i], y[i]); }
                        else { if (MathF.Abs(y[i] - o[i]) > 1.25f) ok = false; x[i] = o[i]; }
                    }
                }
                if (!ok) continue;
                f.CopyTo(obs.AsSpan((by * pl.Bw + bx) * 64, 64)); local++;
            }
            return local;
        }, local => Interlocked.Add(ref recovered, local));
        return recovered;
    }

    /// <summary>A plane described by what was observed: the block DCT of the samples and the estimated steps.</summary>
    static CoefPlane ObservedPlane(float[] samples, int w, int h, int factor, int[] table, float slack)
    {
        var pl = new CoefPlane { Bw = w / 8, Bh = h / 8, W = w, H = h, CompW = w, CompH = h, Fx = factor, Fy = factor, Slack = slack };
        for (int k = 0; k < 64; k++) pl.Q[k] = table[k];
        pl.Obs = new float[pl.Bw * pl.Bh * 64];
        Parallel.For(0, pl.Bh, Restorer.Par, by =>
        {
            Span<float> s = stackalloc float[64], f = stackalloc float[64];
            for (int bx = 0; bx < pl.Bw; bx++)
            {
                for (int j = 0; j < 8; j++) { int row = (by * 8 + j) * w + bx * 8; for (int i = 0; i < 8; i++) s[j * 8 + i] = samples[row + i] - 128f; }
                Dct8.Forward(s, f); f.CopyTo(pl.Obs.AsSpan((by * pl.Bw + bx) * 64, 64));
            }
        });
        return pl;
    }
}
