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
    public static readonly int[] ZigZag = { 0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63 };
    public static readonly int[] StdLuma = { 16,11,10,16,24,40,51,61, 12,12,14,19,26,58,60,55, 14,13,16,24,40,57,69,56, 14,17,22,29,51,87,80,62, 18,22,37,56,68,109,103,77, 24,35,55,64,81,104,113,92, 49,64,78,87,103,121,120,101, 72,92,95,98,112,100,103,99 };
    public static readonly int[] StdChroma = BuildChroma();
    static int[] BuildChroma() { var t = new int[64]; int[] head = { 17,18,24,47,99,99,99,99, 18,21,26,66,99,99,99,99, 24,26,56,99,99,99,99,99, 47,66,99,99,99,99,99,99 }; for (int i = 0; i < 64; i++) t[i] = i < 32 ? head[i] : 99; return t; }

    public static int[] Scaled(int[] b, int q, int max)
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
            if (b[p] != 0xFF) { p++; continue; }   // stray bytes between segments: skipped, as libjpeg does
            int m = b[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0x00) { p += 2; continue; }   // a stuffed zero is not a marker
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
    public static int Threads { get => Restorer.Threads; set => Restorer.Threads = value; }

    /// <summary>Restores a JPEG with the given engine parameters. Returns null for colour spaces the engines do not
    /// handle (CMYK, 12-bit); the caller falls back to a plain decode.</summary>
    public static RgbImage? DecodeRestored(string path, RestoreParams p, out RestoreStats stats)
    {
        stats = new RestoreStats();
        var ci = CoefImage.FromJpeg(path);
        if (ci == null) return null;
        return Restorer.Run(ci, p, out stats);
    }
    /// <summary>Plain decode to RGB: the centre of every bin, chroma interpolated bilinearly with JPEG's centred siting
    /// (what libjpeg calls fancy upsampling). 8-bit YCbCr and grayscale files go through the engine's own decoder: the
    /// pixel pipeline of LibJpeg.NET returns garbage for some legal layouts, such as a lone grey component declared 2x2.
    /// Other colour spaces fall back to LibJpeg.NET.</summary>
    public static RgbImage? DecodePlain(string path)
    {
        try { var ci = CoefImage.FromJpeg(path); if (ci != null) return Restorer.Run(ci, Presets.Plain, out _); }
        catch { }
        return DecodeWithLibJpeg(path);
    }

    static RgbImage? DecodeWithLibJpeg(string path)
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
                    // libjpeg writes the JFIF header itself; any other APP0 (JFXX thumbnails, vendor data) is kept
                    if (mk.Marker == 0xE0 && c.Write_JFIF_header && mk.Data.Length >= 5 && mk.Data[0] == (byte)'J' && mk.Data[1] == (byte)'F' && mk.Data[2] == (byte)'I' && mk.Data[3] == (byte)'F' && mk.Data[4] == 0) continue;
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
        using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
        var da = new jpeg_decompress_struct(); da.jpeg_stdio_src(fa); da.jpeg_read_header(true);
        var db = new jpeg_decompress_struct(); db.jpeg_stdio_src(fb); db.jpeg_read_header(true);
        if (da.Num_components != db.Num_components || da.Image_width != db.Image_width || da.Image_height != db.Image_height) return false;
        // only the tables a component refers to matter: the writer drops tables that nothing uses
        for (int c = 0; c < da.Num_components; c++)
        {
            int id = da.Comp_info[c].Quant_tbl_no;
            if (!pa.QuantTables.TryGetValue(id, out var ta) || !pb.QuantTables.TryGetValue(id, out var tb)) return false;
            for (int k = 0; k < 64; k++) if (ta[k] != tb[k]) return false;
        }
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
