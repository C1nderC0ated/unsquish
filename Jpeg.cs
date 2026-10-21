// Jpeg.cs - JPEG probe (quality factor, markers), coefficient-domain restoration, decode, lossless re-encode.
// Uses BitMiracle.LibJpeg.NET (BSD) for entropy decoding/encoding and coefficient access.
using System.Text;
using BitMiracle.LibJpeg.Classic;

namespace Unsquish;

public sealed class JpegInfo
{
    public bool Ok, IsJpeg, Progressive, Arith, Lossless, Adobe, Mpf, Exif, Icc, Hdrgm, Exact;
    public int Len, Prec, Comps, Tail, Qf, Tables, Width, Height;
    public double Nse = 1.0;
    public string Error = "";
    public Dictionary<int, int[]> QuantTables = new();   // DQT tables by id, natural order
    public byte[]? IccProfile;    // concatenated ICC_PROFILE APP2 chunks, in sequence order
    public byte[]? ExifTiff;      // APP1 Exif payload after the "Exif\0\0" header (a TIFF structure)
}

/// <summary>Byte-level JPEG probe: same logic as the PowerShell RqProbe (DQT least-squares quality, SOF type, APP markers, data after EOI).</summary>
public static class JpegProbe
{
    static readonly int[] ZigZag = { 0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63 };
    public static readonly int[] StdLuma = { 16,11,10,16,24,40,51,61, 12,12,14,19,26,58,60,55, 14,13,16,24,40,57,69,56, 14,17,22,29,51,87,80,62, 18,22,37,56,68,109,103,77, 24,35,55,64,81,104,113,92, 49,64,78,87,103,121,120,101, 72,92,95,98,112,100,103,99 };
    public static readonly int[] StdChroma = BuildChroma();
    static int[] BuildChroma() { var t = new int[64]; int[] head = { 17,18,24,47,99,99,99,99, 18,21,26,66,99,99,99,99, 24,26,56,99,99,99,99,99, 47,66,99,99,99,99,99,99 }; for (int i = 0; i < 64; i++) t[i] = i < 32 ? head[i] : 99; return t; }

    static int[] Scaled(int[] b, int q, int max)
    {
        int scale = q < 50 ? 5000 / q : 200 - 2 * q; var o = new int[64];
        for (int k = 0; k < 64; k++) { int v = (b[k] * scale + 50) / 100; if (v < 1) v = 1; if (v > max) v = max; o[k] = v; }
        return o;
    }

    public static JpegInfo Probe(string path)
    {
        var info = new JpegInfo();
        byte[] b = File.ReadAllBytes(path); info.Len = b.Length;
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) { info.Error = "no SOI marker (not a JPEG)"; return info; }
        info.IsJpeg = true;
        var tables = new Dictionary<int, int[]>(); int tprec = 8; int p = 2;
        var iccChunks = new SortedDictionary<int, byte[]>();
        while (p < b.Length - 1)
        {
            if (b[p] != 0xFF) { info.Error = "garbage between markers"; break; }
            int m = b[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
            if (m == 0xD9) { info.Error = "EOI before SOS"; break; }
            if (p + 3 >= b.Length) { info.Error = "truncated header"; break; }
            int len = (b[p + 2] << 8) | b[p + 3]; int segStart = p + 4, segEnd = p + 2 + len;
            if (segEnd > b.Length) { info.Error = "truncated segment"; break; }
            if (m == 0xDA) { info.Ok = true; p = segEnd; break; }
            if (m == 0xDB)
            {
                int q = segStart;
                while (q < segEnd)
                {
                    int pq = b[q] >> 4, tq = b[q] & 15; q++; var nat = new int[64];
                    for (int k = 0; k < 64; k++) { int v; if (pq == 1) { v = (b[q] << 8) | b[q + 1]; q += 2; tprec = 16; } else { v = b[q]; q++; } nat[ZigZag[k]] = v; }
                    tables[tq] = nat;
                }
            }
            else if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
            {
                info.Prec = b[segStart]; info.Height = (b[segStart + 1] << 8) | b[segStart + 2]; info.Width = (b[segStart + 3] << 8) | b[segStart + 4]; info.Comps = b[segStart + 5];
                if (m == 0xC2 || m == 0xC6 || m == 0xCA || m == 0xCE) info.Progressive = true;
                if (m >= 0xC9) info.Arith = true;
                if (m == 0xC3 || m == 0xC7 || m == 0xCB || m == 0xCF) info.Lossless = true;
            }
            else if (m == 0xE1)
            {
                string head = Latin1(b, segStart, Math.Min(32, segEnd - segStart));
                if (head.StartsWith("Exif") && info.ExifTiff == null && segEnd - segStart > 6)
                {
                    info.Exif = true; int off = segStart + 6; info.ExifTiff = new byte[segEnd - off]; Buffer.BlockCopy(b, off, info.ExifTiff, 0, info.ExifTiff.Length);
                }
                if (head.StartsWith("http://ns.adobe.com/xap/1.0/") && Latin1(b, segStart, segEnd - segStart).Contains("hdrgm:")) info.Hdrgm = true;
            }
            else if (m == 0xE2)
            {
                string head = Latin1(b, segStart, Math.Min(16, segEnd - segStart));
                if (head.StartsWith("ICC_PROFILE") && segEnd - segStart > 14)
                {
                    info.Icc = true; int seq = b[segStart + 12]; int off = segStart + 14; var chunk = new byte[segEnd - off]; Buffer.BlockCopy(b, off, chunk, 0, chunk.Length); iccChunks[seq] = chunk;
                }
                if (head.StartsWith("MPF")) info.Mpf = true;
            }
            else if (m == 0xEE && Latin1(b, segStart, Math.Min(8, segEnd - segStart)).StartsWith("Adobe")) info.Adobe = true;
            p = segEnd;
        }
        if (info.Ok)
        {
            int eoi = -1;
            for (int i = p; i < b.Length - 1; i++) if (b[i] == 0xFF && b[i + 1] == 0xD9) { eoi = i; break; }
            if (eoi >= 0) info.Tail = b.Length - (eoi + 2); else { info.Error = "no EOI marker"; info.Ok = false; }
        }
        else if (info.Error.Length == 0) info.Error = "no SOS marker";
        if (iccChunks.Count > 0) { using var ms = new MemoryStream(); foreach (var c in iccChunks.Values) ms.Write(c, 0, c.Length); info.IccProfile = ms.ToArray(); }
        info.Tables = tables.Count; info.QuantTables = tables;
        if (tables.Count > 0)
        {
            int max = tprec == 16 ? 32767 : 255; int bestQ = 0; double bestSse = double.MaxValue, sumSq = 0;
            foreach (var t in tables.Values) foreach (var v in t) sumSq += (double)v * v;
            for (int q = 1; q <= 100; q++)
            {
                double sse = 0;
                if (tables.TryGetValue(0, out var t0)) { var s = Scaled(StdLuma, q, max); for (int k = 0; k < 64; k++) { double d = s[k] - t0[k]; sse += d * d; } }
                if (tables.TryGetValue(1, out var t1)) { var s = Scaled(StdChroma, q, max); for (int k = 0; k < 64; k++) { double d = s[k] - t1[k]; sse += d * d; } }
                if (sse < bestSse) { bestSse = sse; bestQ = q; }
            }
            info.Qf = bestQ; info.Exact = bestSse == 0; info.Nse = sumSq > 0 ? bestSse / sumSq : 0;
        }
        return info;
    }
    static string Latin1(byte[] b, int off, int len) => Encoding.Latin1.GetString(b, off, Math.Max(0, Math.Min(len, b.Length - off)));
}

/// <summary>Coefficient-domain JPEG restoration and lossless handling.</summary>
public static class Jpeg
{
    public static int Threads = Environment.ProcessorCount;
    static ParallelOptions Par => new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Threads) };
    // orthonormal 8x8 DCT basis: C[u,x] = c(u) * cos((2x+1) u pi / 16) / 2
    static readonly double[,] Basis = BuildBasis();
    static double[,] BuildBasis()
    {
        var b = new double[8, 8];
        for (int u = 0; u < 8; u++) for (int x = 0; x < 8; x++) b[u, x] = (u == 0 ? Math.Sqrt(0.5) : 1.0) * Math.Cos((2 * x + 1) * u * Math.PI / 16) / 2.0;
        return b;
    }

    sealed class Plane
    {
        public int Bw, Bh, W, H;           // blocks and padded pixel size (W = Bw*8)
        public int CompW, CompH;           // real component size
        public int Hs, Vs;                 // sampling factors
        public int[] Q = new int[64];      // quant table, natural order
        public short[] Coef = Array.Empty<short>(); // quantized coefficients, natural order, per block
        public float[] Px = Array.Empty<float>();   // working pixels (level-shifted, 0..255)
    }

    public sealed class RestoreStats { public int Iterations; public double NoiseY; public int Planes; }

    /// <summary>Decode with coefficient-constrained smoothing (the jpegqs idea): smooth the pixels across block
    /// boundaries with an edge-aware filter scaled to the quantization noise, re-project every DCT coefficient into
    /// the interval the file allows, repeat. Returns null for colour spaces it does not handle (caller falls back).</summary>
    public static RgbImage? DecodeRestored(string path, int level, out RestoreStats stats)
    {
        stats = new RestoreStats();
        var probe = JpegProbe.Probe(path);
        if (!probe.Ok || probe.QuantTables.Count == 0) return null;
        using var fs = File.OpenRead(path);
        var d = new jpeg_decompress_struct(); d.jpeg_stdio_src(fs); d.jpeg_read_header(true);
        var cs = d.Jpeg_color_space;
        if (d.Data_precision != 8 || (cs != J_COLOR_SPACE.JCS_YCbCr && cs != J_COLOR_SPACE.JCS_GRAYSCALE)) { d.jpeg_abort(); return null; }
        var coefArrays = d.jpeg_read_coefficients();
        int n = d.Num_components; var comps = d.Comp_info;
        int maxH = 1, maxV = 1; for (int i = 0; i < n; i++) { maxH = Math.Max(maxH, comps[i].H_samp_factor); maxV = Math.Max(maxV, comps[i].V_samp_factor); }
        var planes = new Plane[n];
        for (int c = 0; c < n; c++)
        {
            var ci = comps[c]; var pl = new Plane { Hs = ci.H_samp_factor, Vs = ci.V_samp_factor };
            pl.CompW = (int)Math.Ceiling((double)d.Image_width * ci.H_samp_factor / maxH);
            pl.CompH = (int)Math.Ceiling((double)d.Image_height * ci.V_samp_factor / maxV);
            pl.Bw = ci.Width_in_blocks; pl.Bh = (pl.CompH + 7) / 8; pl.W = pl.Bw * 8; pl.H = pl.Bh * 8;
            if (!probe.QuantTables.TryGetValue(ci.Quant_tbl_no, out var qt)) { d.jpeg_abort(); return null; }
            for (int k = 0; k < 64; k++) pl.Q[k] = Math.Max(1, qt[k]);
            pl.Coef = new short[pl.Bw * pl.Bh * 64];
            for (int by = 0; by < pl.Bh; by++)
            {
                var rows = coefArrays[c].Access(by, 1);
                for (int bx = 0; bx < pl.Bw; bx++) { var blk = rows[0][bx]; int o = (by * pl.Bw + bx) * 64; for (int k = 0; k < 64; k++) pl.Coef[o + k] = blk[k]; }
            }
            planes[c] = pl;
        }
        d.jpeg_finish_decompress(); d.jpeg_destroy();

        int iters = level <= 2 ? 2 : (level <= 4 ? 3 : 4);
        bool diagonals = level >= 3;
        double sigmaScale = level >= 5 ? 1.25 : 1.0;
        stats.Iterations = iters; stats.Planes = n;
        for (int c = 0; c < n; c++)
        {
            var pl = planes[c];
            // Pixel-domain noise scale from the low-frequency AC quantization steps. Most high-frequency
            // coefficients quantize to zero with little real error, so the full-table RMS would overstate
            // the noise several-fold and smooth away real detail; the first AC steps track the visible
            // blocking and ringing amplitude (measured: q20 -> ~8 levels, actual RMS error ~7).
            double noise = (2.0 * (pl.Q[1] + pl.Q[8] + pl.Q[9]) / 3.0) / 8.0 + 1.0;
            noise *= sigmaScale;
            if (c == 0) stats.NoiseY = Math.Round(noise, 2);
            pl.Px = new float[pl.W * pl.H];
            // start from the plain decode; block rows are independent, so run them in parallel
            Parallel.For(0, pl.Bh, Par, by => { var blk = new float[64]; var tmp = new float[64]; for (int bx = 0; bx < pl.Bw; bx++) { int b = by * pl.Bw + bx; Idct(pl, b, blk, tmp); PutBlock(pl, b, blk); } });
            var smoothed = new float[pl.W * pl.H];
            for (int it = 0; it < iters; it++)
            {
                Bilateral(pl.Px, smoothed, pl.W, pl.H, pl.CompW, pl.CompH, (float)noise, diagonals);
                // project each block back into its quantization intervals
                Parallel.For(0, pl.Bh, Par, by =>
                {
                    var deq = new float[64]; var blk = new float[64]; var tmp = new float[64];
                    for (int bx = 0; bx < pl.Bw; bx++)
                    {
                        int b = by * pl.Bw + bx;
                        GetBlock(pl, smoothed, b, blk);
                        Fdct(blk, deq, tmp);
                        int o = b * 64;
                        for (int k = 0; k < 64; k++)
                        {
                            float q = pl.Q[k]; float center = pl.Coef[o + k] * q; float lo = center - 0.5f * q, hi = center + 0.5f * q;
                            if (deq[k] < lo) deq[k] = lo; else if (deq[k] > hi) deq[k] = hi;
                        }
                        IdctFrom(deq, blk, tmp); PutBlock(pl, b, blk);
                    }
                });
            }
        }
        // colour conversion with chroma upsampling (bilinear, centre-aligned)
        int W = planes[0].CompW, H = planes[0].CompH; var img = new RgbImage(W, H);
        var full = new float[n][];
        for (int c = 0; c < n; c++) full[c] = Upsample(planes[c], W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, p = i * 3; float Y = full[0][i];
                if (n >= 3)
                {
                    float cb = full[1][i] - 128f, cr = full[2][i] - 128f;
                    img.Rgb[p] = Clamp(Y + 1.402f * cr); img.Rgb[p + 1] = Clamp(Y - 0.344136f * cb - 0.714136f * cr); img.Rgb[p + 2] = Clamp(Y + 1.772f * cb);
                }
                else { img.Rgb[p] = img.Rgb[p + 1] = img.Rgb[p + 2] = Clamp(Y); }
            }
        return img;
    }

    static byte Clamp(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    static void Idct(Plane pl, int b, float[] outBlk, float[] tmp)
    {
        var deq = new float[64]; int o = b * 64;
        for (int k = 0; k < 64; k++) deq[k] = pl.Coef[o + k] * (float)pl.Q[k];
        IdctFrom(deq, outBlk, tmp);
    }
    // F is indexed [v*8+u] (row = vertical frequency), pixels [y*8+x]
    static void IdctFrom(float[] F, float[] px, float[] tmp)
    {
        for (int v = 0; v < 8; v++) for (int x = 0; x < 8; x++) { double s = 0; for (int u = 0; u < 8; u++) s += F[v * 8 + u] * Basis[u, x]; tmp[v * 8 + x] = (float)s; }
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) { double s = 0; for (int v = 0; v < 8; v++) s += tmp[v * 8 + x] * Basis[v, y]; px[y * 8 + x] = (float)s + 128f; }
    }
    static void Fdct(float[] px, float[] F, float[] tmp)
    {
        for (int y = 0; y < 8; y++) for (int u = 0; u < 8; u++) { double s = 0; for (int x = 0; x < 8; x++) s += (px[y * 8 + x] - 128f) * Basis[u, x]; tmp[y * 8 + u] = (float)s; }
        for (int v = 0; v < 8; v++) for (int u = 0; u < 8; u++) { double s = 0; for (int y = 0; y < 8; y++) s += tmp[y * 8 + u] * Basis[v, y]; F[v * 8 + u] = (float)s; }
    }
    static void PutBlock(Plane pl, int b, float[] blk) { int bx = b % pl.Bw, by = b / pl.Bw; for (int y = 0; y < 8; y++) Array.Copy(blk, y * 8, pl.Px, (by * 8 + y) * pl.W + bx * 8, 8); }
    static void GetBlock(Plane pl, float[] src, int b, float[] blk) { int bx = b % pl.Bw, by = b / pl.Bw; for (int y = 0; y < 8; y++) Array.Copy(src, (by * 8 + y) * pl.W + bx * 8, blk, y * 8, 8); }

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

    static float[] Upsample(Plane pl, int W, int H)
    {
        var o = new float[W * H];
        if (pl.CompW == W && pl.CompH == H) { for (int y = 0; y < H; y++) Array.Copy(pl.Px, y * pl.W, o, y * W, W); return o; }
        double sx = (double)pl.CompW / W, sy = (double)pl.CompH / H;
        for (int y = 0; y < H; y++)
        {
            double fy = (y + 0.5) * sy - 0.5; int y0 = (int)Math.Floor(fy); double ty = fy - y0; int y1 = Math.Min(pl.CompH - 1, y0 + 1); y0 = Math.Max(0, y0);
            for (int x = 0; x < W; x++)
            {
                double fx = (x + 0.5) * sx - 0.5; int x0 = (int)Math.Floor(fx); double tx = fx - x0; int x1 = Math.Min(pl.CompW - 1, x0 + 1); x0 = Math.Max(0, x0);
                o[y * W + x] = (float)((1 - ty) * ((1 - tx) * pl.Px[y0 * pl.W + x0] + tx * pl.Px[y0 * pl.W + x1]) + ty * ((1 - tx) * pl.Px[y1 * pl.W + x0] + tx * pl.Px[y1 * pl.W + x1]));
            }
        }
        return o;
    }

    /// <summary>Plain decode to RGB (libjpeg defaults: fancy upsampling, slow-integer DCT).</summary>
    public static RgbImage? DecodePlain(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var d = new jpeg_decompress_struct(); d.jpeg_stdio_src(fs); d.jpeg_read_header(true);
            d.Out_color_space = J_COLOR_SPACE.JCS_RGB; d.jpeg_start_decompress();
            int w = d.Output_width, h = d.Output_height, nc = d.Output_components;
            var img = new RgbImage(w, h); var row = new byte[1][]; row[0] = new byte[w * nc];
            for (int y = 0; y < h; y++)
            {
                d.jpeg_read_scanlines(row, 1);
                if (nc == 3) Buffer.BlockCopy(row[0], 0, img.Rgb, y * w * 3, w * 3);
                else for (int x = 0; x < w; x++) { byte v = row[0][x * nc]; int p = (y * w + x) * 3; img.Rgb[p] = img.Rgb[p + 1] = img.Rgb[p + 2] = v; }
            }
            d.jpeg_finish_decompress(); d.jpeg_destroy();
            return img;
        }
        catch { return null; }
    }

    /// <summary>Lossless re-encode: same DCT coefficients and tables, Huffman tables optimized, all COM/APPn markers
    /// kept. Verified by re-reading the output and comparing every coefficient with the input.</summary>
    public static bool ReencodeOptimized(string inPath, string outPath, out string message)
    {
        message = "";
        try
        {
            using (var fin = File.OpenRead(inPath))
            using (var fout = File.Create(outPath))
            {
                var d = new jpeg_decompress_struct(); d.jpeg_stdio_src(fin);
                d.jpeg_save_markers(0xFE, 0xFFFF); for (int m = 0; m < 16; m++) d.jpeg_save_markers(0xE0 + m, 0xFFFF);
                d.jpeg_read_header(true);
                var coefs = d.jpeg_read_coefficients();
                var c = new jpeg_compress_struct(); c.jpeg_stdio_dest(fout);
                d.jpeg_copy_critical_parameters(c);
                c.Optimize_coding = true;
                c.jpeg_write_coefficients(coefs);
                foreach (var mk in d.Marker_list)
                {
                    if (mk.Marker == 0xE0 && c.Write_JFIF_header) continue;      // libjpeg writes JFIF itself
                    if (mk.Marker == 0xEE && c.Write_Adobe_marker) continue;     // and the Adobe marker
                    c.jpeg_write_marker(mk.Marker, mk.Data);
                }
                c.jpeg_finish_compress(); c.jpeg_destroy();
                d.jpeg_finish_decompress(); d.jpeg_destroy();
            }
            if (!CoefficientsEqual(inPath, outPath)) { message = "verification failed: coefficients differ"; return false; }
            return true;
        }
        catch (Exception ex) { message = ex.Message; return false; }
    }

    static bool CoefficientsEqual(string a, string b)
    {
        // quantization tables: compare the DQT segments as parsed from both files
        var pa = JpegProbe.Probe(a); var pb = JpegProbe.Probe(b);
        if (pa.QuantTables.Count != pb.QuantTables.Count) return false;
        foreach (var kv in pa.QuantTables) { if (!pb.QuantTables.TryGetValue(kv.Key, out var tb)) return false; for (int k = 0; k < 64; k++) if (kv.Value[k] != tb[k]) return false; }
        using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
        var da = new jpeg_decompress_struct(); da.jpeg_stdio_src(fa); da.jpeg_read_header(true);
        var db = new jpeg_decompress_struct(); db.jpeg_stdio_src(fb); db.jpeg_read_header(true);
        if (da.Num_components != db.Num_components || da.Image_width != db.Image_width || da.Image_height != db.Image_height) return false;
        var ca = da.jpeg_read_coefficients(); var cb = db.jpeg_read_coefficients();
        bool ok = true;
        for (int c = 0; c < da.Num_components && ok; c++)
        {
            var ia = da.Comp_info[c]; var ib = db.Comp_info[c];
            if (ia.Width_in_blocks != ib.Width_in_blocks || ia.Quant_tbl_no != ib.Quant_tbl_no) { ok = false; break; }
            int maxV = 1; for (int i = 0; i < da.Num_components; i++) maxV = Math.Max(maxV, da.Comp_info[i].V_samp_factor);
            int compH = (int)Math.Ceiling((double)da.Image_height * ia.V_samp_factor / maxV); int bh = (compH + 7) / 8;
            for (int by = 0; by < bh && ok; by++)
            {
                var ra = ca[c].Access(by, 1); var rb = cb[c].Access(by, 1);
                for (int bx = 0; bx < ia.Width_in_blocks && ok; bx++) for (int k = 0; k < 64; k++) if (ra[0][bx][k] != rb[0][bx][k]) { ok = false; break; }
            }
        }
        da.jpeg_finish_decompress(); da.jpeg_destroy(); db.jpeg_finish_decompress(); db.jpeg_destroy();
        return ok;
    }
}
