// What a JPEG file actually states about its picture: per component, the quantized DCT levels and the quantization
// steps. Every true (pre-compression) coefficient lies somewhere in the bin [(v - 0.5) q, (v + 0.5) q]; the restorers
// look for a better picture inside those bins instead of taking the bin centres like a plain decoder does.
using System.Runtime.CompilerServices;
using BitMiracle.LibJpeg.Classic;

namespace Unsquish;

public sealed class CoefPlane
{
    public int Bw, Bh;             // size in 8x8 blocks
    public int W, H;               // padded plane size in samples (Bw * 8, Bh * 8)
    public int CompW, CompH;       // the part of the plane that carries picture
    public int Fx = 1, Fy = 1;     // full-resolution pixels per sample (2, 2 for 4:2:0 chroma)
    public float[] Q = new float[64];        // quantization steps, natural order (index v * 8 + u)
    public short[] Coef = Array.Empty<short>();   // quantized levels, 64 per block
    public float[]? Obs;           // observed coefficient values when the source is not a JPEG (see History): overrides Coef * Q
    public float[]? Q1;            // steps of an earlier, harsher compression per frequency (0 = none found)
    public float Slack;            // extra half-width in coefficient units (rounding noise between two compressions)
    public bool Frozen;            // nothing is known about this plane's quantization: it is decoded and left alone
    public float[] Delta = new float[64];    // Laplacian centroid shift per frequency in steps (0 = bin centre)

    /// <summary>The admissible interval [lo, hi] and the preferred value c of coefficient k of the block at offset o.
    /// shrink is the half-width in steps around the preferred value (0.5 or more = the whole bin).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Interval(int o, int k, float shrink, out float c, out float lo, out float hi)
    {
        float q = Q[k], centre, half, slack = 0f;
        if (Obs != null)
        {
            // a bitmap that was once a JPEG: Q holds the estimated steps and the observed value sits on their comb,
            // up to the rounding of the pixels. A value off the comb (edited pixels, padding, a wrong estimate) or a
            // frequency without a known step is left exactly as observed.
            float d = Obs[o + k]; float k1 = q > 0f ? MathF.Round(d / q) : 0f;
            if (q > 0f && MathF.Abs(d - k1 * q) <= Slack) { centre = k1 * q; half = 0.5f * q; slack = Slack; }
            else { centre = d; half = 0f; }
        }
        else
        {
            centre = Coef[o + k] * q; half = 0.5f * q;
            if (Q1 != null && Q1[k] > 0f)
            {
                // an earlier compression with step q1 left its comb in the levels: the value before THAT quantization
                // lies in the q1-bin around the nearest multiple, provided this coefficient is consistent with the comb
                float q1 = Q1[k]; float k1 = MathF.Round(centre / q1);
                if (MathF.Abs(centre - k1 * q1) < half + Slack) { centre = k1 * q1; half = 0.5f * q1; q = q1; slack = Slack; }
            }
        }
        lo = centre - half - slack; hi = centre + half + slack;
        float delta = Delta[k];
        c = delta > 0f && centre != 0f ? centre - MathF.Sign(centre) * delta * q : centre;
        if (shrink < 0.5f) { float l2 = c - shrink * q, h2 = c + shrink * q; if (l2 > lo) lo = l2; if (h2 < hi) hi = h2; }
    }

    /// <summary>Estimates, per AC frequency, where inside its bin a non-zero coefficient sits on average. DCT
    /// coefficients are Laplacian-distributed, so the mass of every bin leans towards zero; the maximum-likelihood
    /// decay rate follows from the counts of zero and non-zero levels (Price and Rabbani, biased reconstruction).</summary>
    public void EstimateCentroids()
    {
        Array.Clear(Delta);
        if (Obs != null || Coef.Length == 0) return;
        int blocks = Bw * Bh;
        var zeros = new long[64]; var nonzero = new long[64]; var sumAbs = new long[64];
        for (int b = 0; b < blocks; b++)
        {
            int o = b * 64;
            for (int k = 1; k < 64; k++) { int v = Coef[o + k]; if (v == 0) zeros[k]++; else { nonzero[k]++; sumAbs[k] += Math.Abs(v); } }
        }
        for (int k = 1; k < 64; k++)
        {
            long n1 = nonzero[k], n0 = zeros[k], s = sumAbs[k]; double n = n0 + n1;
            if (n1 < 32) continue;                                  // too little evidence: keep the bin centre
            double a = n + 2.0 * s, c = 2.0 * s - n1;
            double r = (-n0 + Math.Sqrt((double)n0 * n0 + 4.0 * a * c)) / (2.0 * a);   // r = exp(-lambda q / 2)
            if (!(r > 1e-6) || r >= 0.999999) continue;
            double delta = (1 + r * r) / (2 * (1 - r * r)) + 1 / (2 * Math.Log(r));
            Delta[k] = (float)Math.Clamp(delta, 0.0, 0.45);
        }
    }
}

public sealed class CoefImage
{
    public CoefPlane[] Planes = Array.Empty<CoefPlane>();
    public int Width, Height;            // picture size in pixels
    public int OffsetX, OffsetY;         // where the picture starts on the planes (bitmaps whose block grid is shifted)
    public bool IntegerSubsampling = true;   // every component sample spans a whole number of pixels
    public byte[]? Alpha;                // carried through untouched (bitmap sources)

    /// <summary>Reads levels and tables of a baseline or progressive 8-bit YCbCr or grayscale JPEG. Null for anything
    /// else, including sampling ratios that are not whole numbers (3x1 against 2x1), which libjpeg cannot decode either.</summary>
    public static CoefImage? FromJpeg(string path)
    {
        var probe = JpegProbe.Probe(path);
        if (!probe.Ok || probe.QuantTables.Count == 0) return null;
        using var fs = File.OpenRead(path);
        var d = new jpeg_decompress_struct(); d.jpeg_stdio_src(fs); d.jpeg_read_header(true);
        var cs = d.Jpeg_color_space;
        if (d.Data_precision != 8 || (cs != J_COLOR_SPACE.JCS_YCbCr && cs != J_COLOR_SPACE.JCS_GRAYSCALE)) { d.jpeg_abort(); return null; }
        var coefArrays = d.jpeg_read_coefficients();
        int n = d.Num_components; var comps = d.Comp_info;
        int maxH = 1, maxV = 1; for (int i = 0; i < n; i++) { maxH = Math.Max(maxH, comps[i].H_samp_factor); maxV = Math.Max(maxV, comps[i].V_samp_factor); }
        var img = new CoefImage { Width = d.Image_width, Height = d.Image_height, Planes = new CoefPlane[n] };
        for (int c = 0; c < n; c++)
        {
            var ci = comps[c]; var pl = new CoefPlane();
            pl.CompW = (int)Math.Ceiling((double)d.Image_width * ci.H_samp_factor / maxH);
            pl.CompH = (int)Math.Ceiling((double)d.Image_height * ci.V_samp_factor / maxV);
            pl.Bw = ci.Width_in_blocks; pl.Bh = (pl.CompH + 7) / 8; pl.W = pl.Bw * 8; pl.H = pl.Bh * 8;
            if (maxH % ci.H_samp_factor != 0 || maxV % ci.V_samp_factor != 0) { d.jpeg_abort(); return null; }
            pl.Fx = Math.Max(1, maxH / ci.H_samp_factor); pl.Fy = Math.Max(1, maxV / ci.V_samp_factor);
            if (!probe.QuantTables.TryGetValue(ci.Quant_tbl_no, out var qt)) { d.jpeg_abort(); return null; }
            for (int k = 0; k < 64; k++) pl.Q[k] = Math.Max(1, qt[k]);
            pl.Coef = new short[pl.Bw * pl.Bh * 64];
            for (int by = 0; by < pl.Bh; by++)
            {
                var rows = coefArrays[c].Access(by, 1);
                for (int bx = 0; bx < pl.Bw; bx++) { var blk = rows[0][bx]; int o = (by * pl.Bw + bx) * 64; for (int k = 0; k < 64; k++) pl.Coef[o + k] = blk[k]; }
            }
            img.Planes[c] = pl;
        }
        d.jpeg_finish_decompress(); d.jpeg_destroy();
        return img;
    }
}
