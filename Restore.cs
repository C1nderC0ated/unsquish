// The restoration engine. Everything happens inside the quantization bins of the file (see Coefs.cs): whatever a
// stage does to the picture, its coefficients are put back into the intervals the file allows, so the result is
// always a picture the same JPEG settings would have compressed to (nearly) the same file. Three stages, each
// optional, run in this order:
//   shift  translation-invariant DCT shrinkage: the picture is cut into 8x8 blocks on every shifted grid, each
//          block is denoised in the DCT domain with the file's own steps as the noise level, and the results are
//          averaged. Removes blocking and ringing and keeps texture; the stage for photographs.
//   pocs   a few rounds of edge-aware 3x3 smoothing, each followed by the projection into the bins. Fast and mild;
//          the 0.3-0.7 engine, now the quick setting and the finishing touch after "shift".
//   tv     minimizes the total variation over all channels jointly, chroma at full resolution under the constraint
//          that its box-downsampled version stays in the bins (Bredies and Holler; the idea behind jpeg2png), solved
//          with the Chambolle-Pock primal-dual iteration. Flat colour becomes flat and edges clean, which is right
//          for drawings and wrong for texture, so a guard switches it off wherever the picture is densely detailed.
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Unsquish;

public sealed class RestoreParams
{
    // bins
    public float Shrink = 0.5f;         // half-width of the admissible interval around the preferred value, in steps (0.5 = whole bin)
    public bool Centroid;               // prefer the Laplacian centroid of each bin over its centre
    public float CentroidScale = 1f;    // how much of the estimated centroid shift to apply
    // shift stage
    public int Shifts;                  // 0 = off, 64 = every block shift, 16 = every second one in each direction, 4 = every fourth
    public float ShiftStrength = 1f;    // noise level of the Wiener rule relative to the uniform-bin variance q^2 / 12
    // pocs stage
    public int PocsIterations;          // 0 = off
    public bool Diagonals = true;
    public float SigmaScale = 1f;
    // tv stage
    public int Iterations;              // primal-dual iterations, 0 = off
    public bool ChromaOnly;             // keep luma and solve for chroma alone (chroma follows the luma edges)
    public float Guard = 1.5f;          // 0 = smooth everywhere; otherwise the noise scale multiple above which dense detail is protected
    public int GuardRadius = 2;
    public float GuardLo = 0.45f, GuardHi = 0.75f;   // density of detail at which the smoothing weight starts to fall / reaches zero
    public float Tol;                   // stop early when the mean change per iteration drops below this many grey levels (0 = never)
    public bool SmoothStart = true;     // subsampled chroma enters the tv stage bilinearly interpolated instead of replicated
    public float Fidelity;              // weight of the squared distance to the picture the tv stage starts from, in units of
                                        // 1 / noise scale^2 so that one value serves every quality (0 = bins only)

    public RestoreParams Clone() => (RestoreParams)MemberwiseClone();

    /// <summary>The 0.3-0.7 behaviour of restoration levels 0-6 (kept for comparison in the benchmark).</summary>
    public static RestoreParams Legacy(int level) => new()
    {
        PocsIterations = level <= 2 ? 2 : (level <= 4 ? 3 : 4), Diagonals = level >= 3, SigmaScale = level >= 5 ? 1.25f : 1f,
    };

    /// <summary>"shifts=64,pocsiterations=1,shrink=0.3" style: field=value pairs, case-insensitive (benchmark tool).</summary>
    public static RestoreParams Parse(string spec)
    {
        var p = new RestoreParams();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) throw new ArgumentException("expected name=value, got " + part);
            string key = part[..eq].Trim(), val = part[(eq + 1)..].Trim();
            var f = typeof(RestoreParams).GetFields().FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("unknown parameter " + key);
            object v = f.FieldType == typeof(int) ? int.Parse(val, CultureInfo.InvariantCulture)
                     : f.FieldType == typeof(float) ? float.Parse(val, CultureInfo.InvariantCulture)
                     : f.FieldType == typeof(bool) ? (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)) : val;
            f.SetValue(p, v);
        }
        return p;
    }
}

public sealed class RestoreStats
{
    public int Planes;
    public double NoiseY;       // noise scale of the luma plane in grey levels
    public int Iterations;      // iterations the smoothing stage ran
    public string Note = "";    // anything the caller should tell the user (a stage that was skipped)
}

public static class Restorer
{
    public static int Threads = Environment.ProcessorCount;
    internal static ParallelOptions Par => new() { MaxDegreeOfParallelism = Math.Max(1, Threads) };
    /// <summary>Working memory the smoothing stage may use: half of what the machine has, at least 1.5 GB (about 80 bytes
    /// per pixel are needed, so 16 GB of memory carry a 100-megapixel picture). Larger pictures skip the stage.</summary>
    public static long MemoryBudget = Math.Max(1_500_000_000L, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2);
    // rows of eight samples are handled as one or two vectors
    static readonly bool Vec = Vector.IsHardwareAccelerated && (Vector<float>.Count == 8 || Vector<float>.Count == 4);

    public static RgbImage Run(CoefImage ci, RestoreParams p, out RestoreStats stats)
    {
        int n = ci.Planes.Length;
        stats = new RestoreStats { Planes = n };
        foreach (var pl in ci.Planes)
        {
            if (p.Centroid && pl.Q1 == null && pl.Obs == null) { pl.EstimateCentroids(); if (p.CentroidScale != 1f) for (int k = 0; k < 64; k++) pl.Delta[k] *= p.CentroidScale; }
            else Array.Clear(pl.Delta);
        }
        // plain decode of every plane (the preferred value of every bin)
        var planes = new float[n][];
        for (int c = 0; c < n; c++) planes[c] = Decode(ci.Planes[c], p.Shrink);
        stats.NoiseY = Math.Round(NoiseScale(ci.Planes[0]) * p.SigmaScale, 2);

        if (p.Shifts > 0)
            for (int c = 0; c < n; c++)
            {
                var pl = ci.Planes[c]; if (pl.Frozen) continue;
                ShiftAverage(pl, planes[c], p); Project(pl, planes[c], planes[c], p.Shrink);
            }
        if (p.PocsIterations > 0)
            for (int c = 0; c < n; c++) if (!ci.Planes[c].Frozen) Pocs(ci.Planes[c], planes[c], p.PocsIterations, p);

        bool tv = p.Iterations > 0 && ci.IntegerSubsampling && (!p.ChromaOnly || n >= 3);
        int wf = 0, hf = 0;
        foreach (var pl in ci.Planes) { wf = Math.Max(wf, pl.W * pl.Fx); hf = Math.Max(hf, pl.H * pl.Fy); }
        if (tv)
        {
            // u, its extrapolation and two dual fields per channel, the bins of every coefficient, the guard
            long need = (long)wf * hf * (n * 16L + 12L); foreach (var pl in ci.Planes) need += (long)pl.Bw * pl.Bh * 64 * 12;
            if (need > MemoryBudget) { tv = false; stats.Note = "picture too large for the smoothing stage, which was skipped"; }
        }
        float[][]? full = null; RgbImage result;
        if (tv)
        {
            try { full = TotalVariation(ci, planes, p, stats, wf, hf); }
            catch (OutOfMemoryException) { full = null; GC.Collect(); stats.Note = "not enough free memory for the smoothing stage, which was skipped"; }
        }
        if (full != null) result = ToRgb(full, wf, ci.OffsetX, ci.OffsetY, ci.Width, ci.Height);
        else
        {
            full = new float[n][];
            for (int c = 0; c < n; c++) full[c] = Upsample(ci.Planes[c], planes[c], ci);
            result = ToRgb(full, ci.Width, 0, 0, ci.Width, ci.Height);
        }
        result.Alpha = ci.Alpha;
        return result;
    }

    // ---------------------------------------------------------------- shared pieces

    /// <summary>Pixel-domain noise scale from the low-frequency AC steps. Most high-frequency coefficients quantize
    /// to zero with little real error, so the full-table RMS would overstate the noise several-fold; the first AC
    /// steps track the visible blocking and ringing amplitude (measured: q20 -> about 8 levels, actual RMS error 7).</summary>
    internal static double NoiseScale(CoefPlane pl)
    {
        float Eff(int k) => pl.Q1 != null && pl.Q1[k] > 0f ? pl.Q1[k] : pl.Q[k];
        return (2.0 * (Eff(1) + Eff(8) + Eff(9)) / 3.0) / 8.0 + 1.0;
    }

    /// <summary>Plain decode of a plane from the preferred value of every bin; samples are 0..255 floats.</summary>
    static float[] Decode(CoefPlane pl, float shrink)
    {
        var px = new float[pl.W * pl.H];
        Parallel.For(0, pl.Bh, Par, by =>
        {
            Span<float> f = stackalloc float[64], s = stackalloc float[64];
            for (int bx = 0; bx < pl.Bw; bx++)
            {
                int o = (by * pl.Bw + bx) * 64;
                for (int k = 0; k < 64; k++) { pl.Interval(o, k, shrink, out float c, out _, out _); f[k] = c; }
                Dct8.Inverse(f, s);
                for (int y = 0; y < 8; y++) { int row = (by * 8 + y) * pl.W + bx * 8; for (int x = 0; x < 8; x++) px[row + x] = s[y * 8 + x] + 128f; }
            }
        });
        return px;
    }

    /// <summary>Projects every block of a plane into its bins (src and dst may be the same array).</summary>
    static void Project(CoefPlane pl, float[] src, float[] dst, float shrink)
    {
        Parallel.For(0, pl.Bh, Par, by =>
        {
            Span<float> f = stackalloc float[64], s = stackalloc float[64];
            for (int bx = 0; bx < pl.Bw; bx++)
            {
                int o = (by * pl.Bw + bx) * 64;
                for (int y = 0; y < 8; y++) { int row = (by * 8 + y) * pl.W + bx * 8; for (int x = 0; x < 8; x++) s[y * 8 + x] = src[row + x] - 128f; }
                Dct8.Forward(s, f);
                for (int k = 0; k < 64; k++) { pl.Interval(o, k, shrink, out _, out float lo, out float hi); float v = f[k]; f[k] = v < lo ? lo : (v > hi ? hi : v); }
                Dct8.Inverse(f, s);
                for (int y = 0; y < 8; y++) { int row = (by * 8 + y) * pl.W + bx * 8; for (int x = 0; x < 8; x++) dst[row + x] = s[y * 8 + x] + 128f; }
            }
        });
    }

    /// <summary>The admissible interval of every coefficient of a plane, 64 per block.</summary>
    static void Bounds(CoefPlane pl, float shrink, out float[] lo, out float[] hi)
    {
        var l = new float[pl.Bw * pl.Bh * 64]; var h = new float[l.Length];
        Parallel.For(0, pl.Bh, Par, by =>
        {
            for (int o = by * pl.Bw * 64, end = o + pl.Bw * 64; o < end; o += 64)
                for (int k = 0; k < 64; k++) { pl.Interval(o, k, shrink, out _, out float a, out float b); l[o + k] = a; h[o + k] = b; }
        });
        lo = l; hi = h;
    }

    /// <summary>The DCT coefficients of every block of a plane (64 per block, level shift removed).</summary>
    static float[] BlockDct(CoefPlane pl, float[] px)
    {
        var o = new float[pl.Bw * pl.Bh * 64];
        Parallel.For(0, pl.Bh, Par, by =>
        {
            Span<float> s = stackalloc float[64], f = stackalloc float[64];
            for (int bx = 0; bx < pl.Bw; bx++)
            {
                for (int y = 0; y < 8; y++) { int row = (by * 8 + y) * pl.W + bx * 8; for (int x = 0; x < 8; x++) s[y * 8 + x] = px[row + x] - 128f; }
                Dct8.Forward(s, f); f.CopyTo(o.AsSpan((by * pl.Bw + bx) * 64, 64));
            }
        });
        return o;
    }

    // ---------------------------------------------------------------- shift

    /// <summary>Translation-invariant DCT shrinkage (after Nosratinia's "re-application of JPEG", 2001): the plane is
    /// cut into 8x8 blocks on every shifted grid, each block's coefficients are shrunk by the Wiener rule with the
    /// variance of the file's own quantization as the noise level, and the results are averaged. Block edges of the
    /// original grid fall inside the shifted blocks and average away; real structure, which is far above the noise
    /// level in the coefficients that carry it, passes nearly unchanged.</summary>
    static void ShiftAverage(CoefPlane pl, float[] px, RestoreParams p)
    {
        int w = pl.W, pw = w + 16, ph = pl.H + 16;
        var pad = new float[pw * ph]; var acc = new float[pw * ph];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Clamp(y - 8, 0, pl.CompH - 1);
            for (int x = 0; x < pw; x++) pad[y * pw + x] = px[sy * w + Math.Clamp(x - 8, 0, pl.CompW - 1)];
        }
        var noise = new float[64];
        for (int k = 1; k < 64; k++) { float q = pl.Q1 != null && pl.Q1[k] > 0f ? pl.Q1[k] : pl.Q[k]; noise[k] = p.ShiftStrength * q * q / 12f; }
        int step = p.Shifts >= 64 ? 1 : (p.Shifts >= 16 ? 2 : 4), shifts = 0, vs = Vector<float>.Count;
        for (int sy = 0; sy < 8; sy += step)
            for (int sx = 0; sx < 8; sx += step)
            {
                shifts++; int ox = sx, oy = sy, nbx = (pw - ox) / 8, nby = (ph - oy) / 8;
                Parallel.For(0, nby, Par, by =>
                {
                    Span<float> s = stackalloc float[64], f = stackalloc float[64];
                    var v128 = new Vector<float>(128f);
                    for (int bx = 0; bx < nbx; bx++)
                    {
                        int i0 = (oy + by * 8) * pw + ox + bx * 8;
                        if (Vec)
                        {
                            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i += vs) (new Vector<float>(pad, i0 + j * pw + i) - v128).CopyTo(s.Slice(j * 8 + i));
                            Dct8.Forward(s, f);
                            for (int k = 0; k < 64; k += vs)
                            {
                                // c (c^2 - n) / c^2 where the coefficient stands above the noise, nothing where it does not
                                var c = new Vector<float>(f.Slice(k)); var nn = new Vector<float>(noise, k);
                                Vector.ConditionalSelect(Vector.GreaterThan(c * c, nn), c - nn / c, Vector<float>.Zero).CopyTo(f.Slice(k));
                            }
                            Dct8.Inverse(f, s);
                            for (int j = 0; j < 8; j++)
                                for (int i = 0; i < 8; i += vs) { int a = i0 + j * pw + i; (new Vector<float>(acc, a) + new Vector<float>(s.Slice(j * 8 + i)) + v128).CopyTo(acc, a); }
                        }
                        else
                        {
                            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++) s[j * 8 + i] = pad[i0 + j * pw + i] - 128f;
                            Dct8.Forward(s, f);
                            for (int k = 1; k < 64; k++) { float c = f[k], c2 = c * c; f[k] = c2 > noise[k] ? c - noise[k] / c : 0f; }
                            Dct8.Inverse(f, s);
                            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++) acc[i0 + j * pw + i] += s[j * 8 + i] + 128f;
                        }
                    }
                });
                // the strips a shifted grid does not reach keep the input
                int x1 = ox + nbx * 8, y1 = oy + nby * 8;
                for (int y = 0; y < ph; y++)
                {
                    int row = y * pw;
                    if (y < oy || y >= y1) { for (int x = 0; x < pw; x++) acc[row + x] += pad[row + x]; continue; }
                    for (int x = 0; x < ox; x++) acc[row + x] += pad[row + x];
                    for (int x = x1; x < pw; x++) acc[row + x] += pad[row + x];
                }
            }
        float inv = 1f / shifts;
        for (int y = 0; y < pl.CompH; y++) for (int x = 0; x < pl.CompW; x++) px[y * w + x] = acc[(y + 8) * pw + x + 8] * inv;
    }

    // ---------------------------------------------------------------- pocs

    static void Pocs(CoefPlane pl, float[] px, int iterations, RestoreParams p)
    {
        float noise = (float)(NoiseScale(pl) * p.SigmaScale);
        var smoothed = new float[px.Length];
        for (int it = 0; it < iterations; it++)
        {
            Bilateral(px, smoothed, pl.W, pl.H, pl.CompW, pl.CompH, noise, p.Diagonals);
            Project(pl, smoothed, px, p.Shrink);
        }
    }

    /// <summary>Edge-aware 3x3 smoothing: neighbours are weighted by exp(-d^2 / 2 sigma^2) so real edges (d >> sigma)
    /// are kept while quantization noise and block seams (d ~ sigma) are averaged away. Only the real component
    /// area is filtered; padding keeps its values.</summary>
    static void Bilateral(float[] src, float[] dst, int w, int h, int cw, int ch, float sigma, bool diagonals)
    {
        float inv = -1f / (2f * sigma * sigma); float diagW = diagonals ? 0.5f : 0f;
        Array.Copy(src, dst, src.Length);
        Parallel.For(0, ch, Par, y =>
        {
            for (int x = 0; x < cw; x++)
            {
                int i = y * w + x; float c = src[i]; float sum = c, wsum = 1f;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= ch) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int xx = x + dx; if (xx < 0 || xx >= cw) continue;
                        float sp = (dx != 0 && dy != 0) ? diagW : 1f; if (sp == 0f) continue;
                        float v = src[yy * w + xx]; float d = v - c; float wt = sp * MathF.Exp(d * d * inv);
                        sum += wt * v; wsum += wt;
                    }
                }
                dst[i] = sum / wsum;
            }
        });
    }

    // ---------------------------------------------------------------- to pixels

    /// <summary>Bilinear upsampling of a plane to the picture size with JPEG's centred chroma siting.</summary>
    static float[] Upsample(CoefPlane pl, float[] px, CoefImage ci)
    {
        int W = ci.Width, H = ci.Height, ox = ci.OffsetX, oy = ci.OffsetY;
        var o = new float[W * H];
        if (pl.Fx == 1 && pl.Fy == 1) { for (int y = 0; y < H; y++) Array.Copy(px, (y + oy) * pl.W + ox, o, y * W, W); return o; }
        // exact sampling ratio (not CompW / W: that drifts by up to a pixel when the width is odd)
        double sx = 1.0 / pl.Fx, sy = 1.0 / pl.Fy; int cw = pl.CompW, ch = pl.CompH, pw = pl.W;
        Parallel.For(0, H, Par, y =>
        {
            double fy = (y + oy + 0.5) * sy - 0.5; int y0 = (int)Math.Floor(fy); float ty = (float)(fy - y0); int y1 = Math.Min(ch - 1, y0 + 1); y0 = Math.Max(0, y0);
            for (int x = 0; x < W; x++)
            {
                double fx = (x + ox + 0.5) * sx - 0.5; int x0 = (int)Math.Floor(fx); float tx = (float)(fx - x0); int x1 = Math.Min(cw - 1, x0 + 1); x0 = Math.Max(0, x0);
                o[y * W + x] = (1 - ty) * ((1 - tx) * px[y0 * pw + x0] + tx * px[y0 * pw + x1]) + ty * ((1 - tx) * px[y1 * pw + x0] + tx * px[y1 * pw + x1]);
            }
        });
        return o;
    }

    static RgbImage ToRgb(float[][] full, int stride, int offX, int offY, int W, int H)
    {
        int n = full.Length; var img = new RgbImage(W, H);
        Parallel.For(0, H, Par, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y + offY) * stride + x + offX, p = (y * W + x) * 3; float Y = full[0][i];
                if (n >= 3)
                {
                    float cb = full[1][i] - 128f, cr = full[2][i] - 128f;
                    img.Rgb[p] = Clamp(Y + 1.402f * cr); img.Rgb[p + 1] = Clamp(Y - 0.344136f * cb - 0.714136f * cr); img.Rgb[p + 2] = Clamp(Y + 1.772f * cb);
                }
                else { img.Rgb[p] = img.Rgb[p + 1] = img.Rgb[p + 2] = Clamp(Y); }
            }
        });
        return img;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte Clamp(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    // ---------------------------------------------------------------- total variation

    /// <summary>Chambolle-Pock iteration for   min  sum over pixels of g |grad u|   subject to  D S u in the bins,
    /// with one Euclidean norm per pixel over the gradients of all channels (so edges form at the same place in
    /// luma and chroma), g the texture guard, S the box-downsampling of a subsampled channel and D the block DCT.
    /// Every iterate satisfies the constraint. Works on the block-padded grid of the largest plane.</summary>
    static float[][] TotalVariation(CoefImage ci, float[][] planes, RestoreParams p, RestoreStats stats, int Wf, int Hf)
    {
        int n = ci.Planes.Length, N = Wf * Hf, vs = Vector<float>.Count;
        var u = new float[n][]; var ub = new float[n][]; var px = new float[n][]; var py = new float[n][];
        for (int c = 0; c < n; c++) { u[c] = Spread(ci.Planes[c], planes[c], Wf, Hf, p.SmoothStart); ub[c] = (float[])u[c].Clone(); px[c] = new float[N]; py[c] = new float[N]; }
        // channels that are solved for: not a kept luma, not a plane whose quantization is unknown
        var mv = new bool[n]; int moving = 0;
        for (int c = 0; c < n; c++) { mv[c] = !(p.ChromaOnly && c == 0) && !ci.Planes[c].Frozen; if (mv[c]) moving++; }
        if (moving == 0) return u;
        // with luma kept, the joint norm already lets chroma move freely wherever luma is busy
        float[]? guard = p.Guard > 0f && !p.ChromaOnly
            ? TextureGuard(u[0], Wf, Hf, (float)NoiseScale(ci.Planes[0]) * p.Guard, Math.Max(1, p.GuardRadius), p.GuardLo, p.GuardHi) : null;
        var lo = new float[n][]; var hi = new float[n][];
        for (int c = 0; c < n; c++) if (mv[c]) Bounds(ci.Planes[c], p.Shrink, out lo[c], out hi[c]);
        // the data term: the coefficients the stage starts from. Without it the bins alone hold the picture, and every
        // pixel whose guard weight is not exactly zero is flattened as far as they allow, sooner or later.
        float[][]? anchor = null;
        if (p.Fidelity > 0f) { anchor = new float[n][]; for (int c = 0; c < n; c++) if (mv[c]) anchor[c] = BlockDct(ci.Planes[c], planes[c]); }
        float tau = 1f / MathF.Sqrt(8f), sigma = tau;     // tau sigma |grad|^2 <= 1, |grad|^2 <= 8
        bool simd = Vec && Wf > 2 * vs;
        int done = 0;
        for (int it = 0; it < p.Iterations; it++)
        {
            // ---- dual ascent, then projection onto the ball of radius g
            Parallel.For(0, Hf, Par, y =>
            {
                int row = y * Wf, x = 0; bool lastRow = y == Hf - 1;
                if (simd)
                {
                    var vsig = new Vector<float>(sigma);
                    for (; x + vs <= Wf - 1; x += vs)
                    {
                        int i = row + x; var n2 = Vector<float>.Zero;
                        for (int c = 0; c < n; c++)
                        {
                            var ubc = ub[c]; var cur = new Vector<float>(ubc, i);
                            var a = new Vector<float>(px[c], i) + vsig * (new Vector<float>(ubc, i + 1) - cur);
                            a.CopyTo(px[c], i); n2 += a * a;
                            if (lastRow) continue;
                            var b = new Vector<float>(py[c], i) + vsig * (new Vector<float>(ubc, i + Wf) - cur);
                            b.CopyTo(py[c], i); n2 += b * b;
                        }
                        var rad = guard != null ? new Vector<float>(guard, i) : Vector<float>.One;
                        var norm = Vector.SquareRoot(n2);
                        if (!Vector.GreaterThanAny(norm, rad)) continue;
                        var s = Vector.ConditionalSelect(Vector.GreaterThan(norm, rad), rad / norm, Vector<float>.One);
                        for (int c = 0; c < n; c++)
                        {
                            (new Vector<float>(px[c], i) * s).CopyTo(px[c], i);
                            if (!lastRow) (new Vector<float>(py[c], i) * s).CopyTo(py[c], i);
                        }
                    }
                }
                for (; x < Wf; x++)
                {
                    int i = row + x; bool lastCol = x == Wf - 1; float n2 = 0f;
                    for (int c = 0; c < n; c++)
                    {
                        var ubc = ub[c]; float cur = ubc[i];
                        float a = px[c][i] + (lastCol ? 0f : sigma * (ubc[i + 1] - cur)), b = py[c][i] + (lastRow ? 0f : sigma * (ubc[i + Wf] - cur));
                        px[c][i] = a; py[c][i] = b; n2 += a * a + b * b;
                    }
                    float rad = guard != null ? guard[i] : 1f;
                    if (n2 > rad * rad) { float s = rad / MathF.Sqrt(n2); for (int c = 0; c < n; c++) { px[c][i] *= s; py[c][i] *= s; } }
                }
            });
            // ---- primal descent: the gradient step goes into ub (as scratch). The dual field is zero in the last
            //      column and row, which makes the backward differences below the negative adjoint of the forward ones.
            Parallel.For(0, Hf, Par, y =>
            {
                int row = y * Wf; bool firstRow = y == 0;
                for (int c = 0; c < n; c++)
                {
                    if (!mv[c]) continue;
                    var pxc = px[c]; var pyc = py[c]; var uc = u[c]; var ubc = ub[c];
                    ubc[row] = uc[row] + tau * (pxc[row] + (firstRow ? pyc[row] : pyc[row] - pyc[row - Wf]));
                    int x = 1;
                    if (simd)
                    {
                        var vtau = new Vector<float>(tau);
                        for (; x + vs <= Wf; x += vs)
                        {
                            int i = row + x;
                            var d = new Vector<float>(pxc, i) - new Vector<float>(pxc, i - 1) + new Vector<float>(pyc, i);
                            if (!firstRow) d -= new Vector<float>(pyc, i - Wf);
                            (new Vector<float>(uc, i) + vtau * d).CopyTo(ubc, i);
                        }
                    }
                    for (; x < Wf; x++) { int i = row + x; ubc[i] = uc[i] + tau * (pxc[i] - pxc[i - 1] + (firstRow ? pyc[i] : pyc[i] - pyc[i - Wf])); }
                }
            });
            // ---- projection into the bins, then over-relaxation
            double change = 0;
            for (int c = 0; c < n; c++)
            {
                if (!mv[c]) continue;
                var pl = ci.Planes[c]; var uc = u[c]; var ubc = ub[c]; var loc = lo[c]; var hic = hi[c]; var anc = anchor?[c];
                int fx = pl.Fx, fy = pl.Fy; float inv = 1f / (fx * fy); bool full = fx == 1 && fy == 1;
                float sd = (float)NoiseScale(pl);
                float t = tau * p.Fidelity / (sd * sd) * inv, t1 = 1f / (1f + t);       // prox of the data term, seen through the box mean
                object gate = new(); double planeChange = 0;
                Parallel.For(0, pl.Bh, Par, () => 0.0, (by, _, local) =>
                {
                    Span<float> s = stackalloc float[64], f = stackalloc float[64], d = stackalloc float[64];
                    var v128 = new Vector<float>(128f); var moved = Vector<float>.Zero;
                    for (int bx = 0; bx < pl.Bw; bx++)
                    {
                        int o = (by * pl.Bw + bx) * 64, i0 = by * 8 * fy * Wf + bx * 8 * fx;
                        // the block as this plane sees it: the samples themselves, or their box means
                        if (full && Vec)
                            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i += vs) (new Vector<float>(ubc, i0 + j * Wf + i) - v128).CopyTo(s.Slice(j * 8 + i));
                        else
                            for (int j = 0; j < 8; j++)
                                for (int i = 0; i < 8; i++)
                                {
                                    int r = i0 + j * fy * Wf + i * fx; float sum = 0f;
                                    for (int b = 0; b < fy; b++) for (int a = 0; a < fx; a++) sum += ubc[r + b * Wf + a];
                                    s[j * 8 + i] = sum * inv - 128f;
                                }
                        Dct8.Forward(s, f);
                        if (anc != null)
                        {
                            if (Vec) { var vt = new Vector<float>(t); var vt1 = new Vector<float>(t1); for (int k = 0; k < 64; k += vs) ((new Vector<float>(f.Slice(k)) + vt * new Vector<float>(anc, o + k)) * vt1).CopyTo(f.Slice(k)); }
                            else for (int k = 0; k < 64; k++) f[k] = (f[k] + t * anc[o + k]) * t1;
                        }
                        if (Vec)
                            for (int k = 0; k < 64; k += vs)
                                Vector.Min(Vector.Max(new Vector<float>(f.Slice(k)), new Vector<float>(loc, o + k)), new Vector<float>(hic, o + k)).CopyTo(f.Slice(k));
                        else
                            for (int k = 0; k < 64; k++) { float v = f[k], a = loc[o + k], b = hic[o + k]; f[k] = v < a ? a : (v > b ? b : v); }
                        Dct8.Inverse(f, d);
                        // every sample moves by what its block mean moved; u takes the result, ub the extrapolation 2 u - u_old
                        if (full && Vec)
                            for (int j = 0; j < 8; j++)
                                for (int i = 0; i < 8; i += vs)
                                {
                                    int r = i0 + j * Wf + i;
                                    var z = new Vector<float>(d.Slice(j * 8 + i)) + v128; var old = new Vector<float>(uc, r);
                                    moved += Vector.Abs(z - old); (z + z - old).CopyTo(ubc, r); z.CopyTo(uc, r);
                                }
                        else
                            for (int j = 0; j < 8; j++)
                                for (int i = 0; i < 8; i++)
                                {
                                    float delta = d[j * 8 + i] - s[j * 8 + i]; int r = i0 + j * fy * Wf + i * fx;
                                    for (int b = 0; b < fy; b++)
                                        for (int a = 0; a < fx; a++) { int t = r + b * Wf + a; float z = ubc[t] + delta, old = uc[t]; local += Math.Abs(z - old); ubc[t] = 2f * z - old; uc[t] = z; }
                                }
                    }
                    return local + Vector.Sum(moved);
                }, local => { lock (gate) planeChange += local; });
                change += planeChange;
                // pixels of the working grid that no block of this plane covers follow the gradient step freely
                int coverW = pl.W * fx, coverH = pl.H * fy;
                if (coverW < Wf || coverH < Hf)
                    Parallel.For(0, Hf, Par, y =>
                    {
                        int xs = y < coverH ? coverW : 0;
                        for (int x = xs; x < Wf; x++) { int i = y * Wf + x; float z = ubc[i], old = uc[i]; ubc[i] = 2f * z - old; uc[i] = z; }
                    });
            }
            done = it + 1;
            if (p.Tol > 0f && it >= 9 && change / ((double)N * moving) < p.Tol) break;
        }
        stats.Iterations = done;
        return u;
    }

    /// <summary>Per-pixel weight of the smoothness term: 1 where smoothing is wanted, towards 0 where the picture is
    /// densely detailed (screentone, dither, hatching, grain, fine soft lines). A smoothness prior is right for flat
    /// areas cut by separate edges, where the gradient sits in a few pixels of any small window; where most pixels of
    /// the window carry gradient, minimizing it only pushes every coefficient to the low-contrast end of its bin.
    /// The measure is the participation ratio of the gradient, (mean |g|)^2 / mean g^2: about 1/width of the window
    /// for one edge, near 1 for dense detail. Compression noise is dense too, so the test only counts where the
    /// gradient energy stands clear of the noise scale.</summary>
    internal static float[] TextureGuard(float[] y, int w, int h, float noise, int radius, float lo, float hi)
    {
        int n = w * h; var a = new float[n]; var b = new float[n];
        Parallel.For(0, h, Par, row =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = row * w + x;
                float gx = x < w - 1 ? y[i + 1] - y[i] : 0f, gy = row < h - 1 ? y[i + w] - y[i] : 0f;
                float e = gx * gx + gy * gy; a[i] = MathF.Sqrt(e); b[i] = e;
            }
        });
        BoxMean(a, w, h, radius); BoxMean(b, w, h, radius);
        var g = new float[n]; float r0 = 0.6f * noise, r1 = 1.6f * noise, span = MathF.Max(1e-3f, hi - lo);
        Parallel.For(0, h, Par, row =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = row * w + x; float e = MathF.Max(0f, b[i]);
                float pr = e > 1e-6f ? MathF.Min(1f, a[i] * a[i] / e) : 0f;
                float v = 1f - Smooth((pr - lo) / span) * Smooth((MathF.Sqrt(e) - r0) / (r1 - r0));
                g[i] = v >= 0f && v <= 1f ? v : 1f;
            }
        });
        BoxMean(g, w, h, 1);
        return g;
    }

    static float Smooth(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }

    /// <summary>In-place mean over a (2r+1) x (2r+1) window, edges clamped.</summary>
    static void BoxMean(float[] v, int w, int h, int r)
    {
        var tmp = new float[v.Length]; float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, Par, y =>
        {
            int row = y * w; double sum = 0;
            for (int k = -r; k <= r; k++) sum += v[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++) { tmp[row + x] = (float)(sum * inv); sum += v[row + Math.Min(w - 1, x + r + 1)] - v[row + Math.Max(0, x - r)]; }
        });
        Parallel.For(0, w, Par, x =>
        {
            double sum = 0;
            for (int k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++) { v[y * w + x] = (float)(sum * inv); sum += tmp[Math.Min(h - 1, y + r + 1) * w + x] - tmp[Math.Max(0, y - r) * w + x]; }
        });
    }

    /// <summary>Spreads a plane over the working grid, extending the last covered row and column outwards. A
    /// subsampled plane is interpolated bilinearly with JPEG's centred siting (a start the solver has little to do
    /// from; the first projection puts its box means back into the bins) or, with smooth off, replicated.</summary>
    static float[] Spread(CoefPlane pl, float[] px, int Wf, int Hf, bool smooth)
    {
        var o = new float[Wf * Hf]; int fx = pl.Fx, fy = pl.Fy, w = pl.W, h = pl.H;
        if (!smooth || (fx == 1 && fy == 1))
        {
            Parallel.For(0, Hf, Par, y =>
            {
                int sy = Math.Min(h - 1, y / fy);
                for (int x = 0; x < Wf; x++) o[y * Wf + x] = px[sy * w + Math.Min(w - 1, x / fx)];
            });
            return o;
        }
        Parallel.For(0, Hf, Par, y =>
        {
            float v = Math.Clamp((y + 0.5f) / fy - 0.5f, 0f, h - 1); int y0 = (int)v, y1 = Math.Min(h - 1, y0 + 1); float ty = v - y0;
            for (int x = 0; x < Wf; x++)
            {
                float s = Math.Clamp((x + 0.5f) / fx - 0.5f, 0f, w - 1); int x0 = (int)s, x1 = Math.Min(w - 1, x0 + 1); float tx = s - x0;
                o[y * Wf + x] = (1 - ty) * ((1 - tx) * px[y0 * w + x0] + tx * px[y0 * w + x1]) + ty * ((1 - tx) * px[y1 * w + x0] + tx * px[y1 * w + x1]);
            }
        });
        return o;
    }
}
