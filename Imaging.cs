// Imaging.cs - pixel containers, resampling, filters, classifier features, median cut, PNG writer.
// Pure managed code; no external tools.
using System.Buffers.Binary;
using System.IO.Compression;

namespace Unsquish;

/// <summary>8-bit RGB image, 3 bytes per pixel, row-major.</summary>
public sealed class RgbImage
{
    public int W, H;
    public byte[] Rgb;
    public byte[]? Alpha;   // optional straight alpha, one byte per pixel (sprites with transparent backgrounds)
    public RgbImage(int w, int h) { W = w; H = h; Rgb = new byte[w * h * 3]; }
    public RgbImage(int w, int h, byte[] rgb) { W = w; H = h; Rgb = rgb; }
    public bool HasAlpha => Alpha != null;

    public int[] ToArgb()
    {
        var o = new int[W * H];
        for (int i = 0, p = 0; i < o.Length; i++, p += 3) o[i] = ((Alpha != null ? Alpha[i] : 255) << 24) | (Rgb[p] << 16) | (Rgb[p + 1] << 8) | Rgb[p + 2];
        return o;
    }
    public static RgbImage FromArgb(int[] a, int w, int h, bool withAlpha)
    {
        var img = new RgbImage(w, h); if (withAlpha) img.Alpha = new byte[w * h];
        for (int i = 0, p = 0; i < a.Length; i++, p += 3) { int v = a[i]; img.Rgb[p] = (byte)(v >> 16); img.Rgb[p + 1] = (byte)(v >> 8); img.Rgb[p + 2] = (byte)v; if (withAlpha) img.Alpha![i] = (byte)(v >> 24); }
        return img;
    }
    public byte[] Gray()
    {
        var g = new byte[W * H];
        for (int i = 0, p = 0; i < g.Length; i++, p += 3) g[i] = (byte)((Rgb[p] * 299 + Rgb[p + 1] * 587 + Rgb[p + 2] * 114 + 500) / 1000);
        return g;
    }
    /// <summary>Point-sample (nearest) down so the long side is at most maxSide. Keeps hard edges hard.</summary>
    public RgbImage PointSample(int maxSide)
    {
        int side = Math.Max(W, H);
        if (side <= maxSide) return this;
        double s = (double)maxSide / side;
        int nw = Math.Max(1, (int)(W * s)), nh = Math.Max(1, (int)(H * s));
        return ResamplePoint(nw, nh);
    }
    public RgbImage ResamplePoint(int nw, int nh)
    {
        var o = new RgbImage(nw, nh); if (Alpha != null) o.Alpha = new byte[nw * nh];
        for (int y = 0; y < nh; y++)
        {
            int sy = Math.Min(H - 1, (int)((y + 0.5) * H / nh));
            for (int x = 0; x < nw; x++)
            {
                int sx = Math.Min(W - 1, (int)((x + 0.5) * W / nw));
                int sp = (sy * W + sx) * 3, dp = (y * nw + x) * 3;
                o.Rgb[dp] = Rgb[sp]; o.Rgb[dp + 1] = Rgb[sp + 1]; o.Rgb[dp + 2] = Rgb[sp + 2];
                if (Alpha != null) o.Alpha![y * nw + x] = Alpha[sy * W + sx];
            }
        }
        return o;
    }
    public RgbImage ResampleBilinear(int nw, int nh)
    {
        var o = new RgbImage(nw, nh);
        for (int y = 0; y < nh; y++)
        {
            double fy = (y + 0.5) * H / nh - 0.5; int y0 = (int)Math.Floor(fy); double ty = fy - y0; int y1 = Math.Min(H - 1, y0 + 1); y0 = Math.Max(0, y0);
            for (int x = 0; x < nw; x++)
            {
                double fx = (x + 0.5) * W / nw - 0.5; int x0 = (int)Math.Floor(fx); double tx = fx - x0; int x1 = Math.Min(W - 1, x0 + 1); x0 = Math.Max(0, x0);
                int dp = (y * nw + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    double v = (1 - ty) * ((1 - tx) * Rgb[(y0 * W + x0) * 3 + c] + tx * Rgb[(y0 * W + x1) * 3 + c]) + ty * ((1 - tx) * Rgb[(y1 * W + x0) * 3 + c] + tx * Rgb[(y1 * W + x1) * 3 + c]);
                    o.Rgb[dp + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
        }
        return o;
    }
    /// <summary>Undo a k x k nearest-neighbour enlargement whose cells start at (phaseX, phaseY): every cell becomes
    /// its middle sample, partial cells at the edges included, so no interior row or column is dropped.</summary>
    public RgbImage UndoGrid(int k, int phaseX, int phaseY)
    {
        var xs = CellCentres(W, k, phaseX); var ys = CellCentres(H, k, phaseY);
        var o = new RgbImage(xs.Length, ys.Length); if (Alpha != null) o.Alpha = new byte[xs.Length * ys.Length];
        for (int y = 0; y < ys.Length; y++)
            for (int x = 0; x < xs.Length; x++)
            {
                int sp = (ys[y] * W + xs[x]) * 3, dp = (y * xs.Length + x) * 3;
                o.Rgb[dp] = Rgb[sp]; o.Rgb[dp + 1] = Rgb[sp + 1]; o.Rgb[dp + 2] = Rgb[sp + 2];
                if (Alpha != null) o.Alpha![y * xs.Length + x] = Alpha[ys[y] * W + xs[x]];
            }
        return o;
    }
    static int[] CellCentres(int n, int k, int phase)
    {
        var list = new List<int>(n / k + 2);
        if (phase > 0) list.Add(phase / 2);                                       // partial leading cell
        for (int s = phase; s < n; s += k) list.Add(Math.Min(n - 1, s + k / 2));   // full cells, partial trailing cell
        return list.ToArray();
    }

    /// <summary>Applies an Exif orientation (2-8) so the pixels display upright with orientation 1.</summary>
    public RgbImage Orient(int o)
    {
        if (o < 2 || o > 8) return this;
        bool swap = o >= 5; int nw = swap ? H : W, nh = swap ? W : H;
        var r = new RgbImage(nw, nh); if (Alpha != null) r.Alpha = new byte[nw * nh];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int dx, dy;
                switch (o)
                {
                    case 2: dx = W - 1 - x; dy = y; break;             // mirrored horizontally
                    case 3: dx = W - 1 - x; dy = H - 1 - y; break;     // rotated 180
                    case 4: dx = x; dy = H - 1 - y; break;             // mirrored vertically
                    case 5: dx = y; dy = x; break;                     // transposed
                    case 6: dx = H - 1 - y; dy = x; break;             // rotate 90 clockwise to display
                    case 7: dx = H - 1 - y; dy = W - 1 - x; break;     // transversed
                    default: dx = y; dy = W - 1 - x; break;            // 8: rotate 90 counter-clockwise to display
                }
                int sp = (y * W + x) * 3, dp = (dy * nw + dx) * 3;
                r.Rgb[dp] = Rgb[sp]; r.Rgb[dp + 1] = Rgb[sp + 1]; r.Rgb[dp + 2] = Rgb[sp + 2];
                if (Alpha != null) r.Alpha![dy * nw + dx] = Alpha[y * W + x];
            }
        return r;
    }

    public RgbImage CenterCrop(int size)
    {
        int cw = Math.Min(W, size), ch = Math.Min(H, size);
        int x0 = (W - cw) / 2, y0 = (H - ch) / 2;
        var o = new RgbImage(cw, ch);
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(Rgb, ((y0 + y) * W + x0) * 3, o.Rgb, y * cw * 3, cw * 3);
        return o;
    }
    public int UniqueColors()
    {
        var set = new HashSet<int>();
        for (int p = 0; p < Rgb.Length; p += 3) set.Add((Rgb[p] << 16) | (Rgb[p + 1] << 8) | Rgb[p + 2]);
        return set.Count;
    }
    public static double Psnr(RgbImage a, RgbImage b)
    {
        if (a.W != b.W || a.H != b.H) return 0;
        double se = 0; int n = a.Rgb.Length;
        for (int i = 0; i < n; i++) { double d = a.Rgb[i] - b.Rgb[i]; se += d * d; }
        if (se == 0) return 120;
        return 10 * Math.Log10(255.0 * 255.0 * n / se);
    }
}

public static class Filters
{
    /// <summary>ImageMagick-like selective blur: 3x3 Gaussian (sigma 1) over neighbours whose value is within
    /// the threshold of the centre pixel; sharp edges are left alone. Threshold is a fraction of full scale.</summary>
    public static RgbImage SelectiveBlur(RgbImage img, double threshold)
    {
        int w = img.W, h = img.H; var src = img.Rgb; var o = new RgbImage(w, h);
        double[] k = { 0.3679, 0.6065, 1.0 }; // exp(-d^2/2) for d = sqrt2, 1, 0
        int thr = (int)Math.Round(threshold * 255);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    int cp = (y * w + x) * 3 + c; int cv = src[cp];
                    double sum = cv, wsum = 1.0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int xx = x + dx; if (xx < 0 || xx >= w) continue;
                            int v = src[(yy * w + xx) * 3 + c];
                            if (Math.Abs(v - cv) > thr) continue;
                            double wt = k[(dx != 0 && dy != 0) ? 0 : 1];
                            sum += wt * v; wsum += wt;
                        }
                    }
                    o.Rgb[cp] = (byte)Math.Clamp((int)Math.Round(sum / wsum), 0, 255);
                }
        return o;
    }

    /// <summary>Median-cut colour quantization to at most k colours (no dither). Used for the opt-in /snap step.</summary>
    public static RgbImage MedianCut(RgbImage img, int k)
    {
        var hist = new Dictionary<int, int>();
        for (int p = 0; p < img.Rgb.Length; p += 3) { int c = (img.Rgb[p] << 16) | (img.Rgb[p + 1] << 8) | img.Rgb[p + 2]; hist[c] = hist.TryGetValue(c, out var n) ? n + 1 : 1; }
        if (hist.Count <= k) return img;
        var boxes = new List<List<KeyValuePair<int, int>>> { hist.ToList() };
        while (boxes.Count < k)
        {
            int bi = -1; long best = -1;
            for (int i = 0; i < boxes.Count; i++) { if (boxes[i].Count < 2) continue; long cnt = 0; foreach (var e in boxes[i]) cnt += e.Value; if (cnt > best) { best = cnt; bi = i; } }
            if (bi < 0) break;
            var box = boxes[bi];
            int[] mn = { 255, 255, 255 }, mx = { 0, 0, 0 };
            foreach (var e in box) for (int c = 0; c < 3; c++) { int v = (e.Key >> (16 - 8 * c)) & 255; if (v < mn[c]) mn[c] = v; if (v > mx[c]) mx[c] = v; }
            int ch = 0; for (int c = 1; c < 3; c++) if (mx[c] - mn[c] > mx[ch] - mn[ch]) ch = c;
            int shift = 16 - 8 * ch;
            box.Sort((a, b) => ((a.Key >> shift) & 255).CompareTo((b.Key >> shift) & 255));
            long total = 0; foreach (var e in box) total += e.Value;
            long acc = 0; int split = 0;
            for (; split < box.Count - 1; split++) { acc += box[split].Value; if (acc * 2 >= total) { split++; break; } }
            if (split <= 0 || split >= box.Count) split = box.Count / 2;
            boxes[bi] = box.GetRange(0, split); boxes.Add(box.GetRange(split, box.Count - split));
        }
        var palette = new (int r, int g, int b)[boxes.Count];
        var map = new Dictionary<int, int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (var e in boxes[i]) { r += (long)((e.Key >> 16) & 255) * e.Value; g += (long)((e.Key >> 8) & 255) * e.Value; b += (long)(e.Key & 255) * e.Value; n += e.Value; map[e.Key] = i; }
            palette[i] = ((int)(r / n), (int)(g / n), (int)(b / n));
        }
        var o = new RgbImage(img.W, img.H) { Alpha = img.Alpha };
        for (int p = 0; p < img.Rgb.Length; p += 3)
        {
            int c = (img.Rgb[p] << 16) | (img.Rgb[p + 1] << 8) | img.Rgb[p + 2];
            var pc = palette[map[c]]; o.Rgb[p] = (byte)pc.r; o.Rgb[p + 1] = (byte)pc.g; o.Rgb[p + 2] = (byte)pc.b;
        }
        return o;
    }
}

/// <summary>Content classifier: photo / art / pixel art, same rules and thresholds as restore.cmd 0.2.x.</summary>
public sealed class ClassResult
{
    public string Class = "unknown"; public double Confidence; public double HardRatio = -1; public int GridK = 1; public double GridDb;
    public int GridPhaseX, GridPhaseY;    // where the k x k cells start in the full image (a cropped sprite rarely starts at 0)
    public int Width, Height, Colors; public double Density = 1; public string Note = "";
    public double HardRatioNative = -1;   // same ratio measured on full-resolution crops (no point sampling)
    public double Soft = -1, Multi = -1;  // texture: tiny-difference share among differing neighbours; share of hard 3x3 windows holding 7+ colours
    public bool Dithered;                 // palette image whose texture says "quantized soft picture": kept out of the pixel-art rules
}

public static class Classifier
{
    /// <param name="lossy">true for JPEG sources: their noise would make the palette-texture veto fire on genuine sprites</param>
    public static ClassResult Classify(RgbImage full, bool lossy = false)
    {
        var res = new ClassResult { Width = full.W, Height = full.H };
        var proxy = full.PointSample(768);
        res.Colors = proxy.UniqueColors();
        res.Density = Math.Round((double)res.Colors / (proxy.W * proxy.H), 4);
        // hard-edge ratio: 3x3 range > 25% of scale among pixels whose range > 3%
        var g = proxy.Gray(); int w = proxy.W, h = proxy.H; long hard = 0, any = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int mn = 255, mx = 0;
                for (int dy = -1; dy <= 1; dy++) { int yy = Math.Clamp(y + dy, 0, h - 1); for (int dx = -1; dx <= 1; dx++) { int v = g[yy * w + Math.Clamp(x + dx, 0, w - 1)]; if (v < mn) mn = v; if (v > mx) mx = v; } }
                int range = mx - mn;
                if (range > 8) any++;      // > 3% of 255
                if (range > 64) hard++;    // > 25% of 255
            }
        double hr = any > 0 ? (double)hard / any : 0;
        res.HardRatio = Math.Round(hr, 3);
        // the same measurement on full-resolution crops: point sampling a large photo makes its soft edges
        // look hard (neighbouring proxy pixels are several source pixels apart), so this is the honest value
        res.HardRatioNative = Math.Round(HardRatioOnCrops(full), 3);
        // nearest-neighbour grid test on a full-resolution centre crop: NN round trip must beat bilinear clearly
        var crop = full.CenterCrop(384);
        double bestGain = 0;
        foreach (int k in new[] { 2, 3, 4, 5, 6, 8 })
        {
            int kw = crop.W / k * k, kh = crop.H / k * k;
            if (kw < 48 || kh < 48) continue;   // too few cells below this: the phase search would find grids in noise
            var c = CropTopLeft(crop, kw, kh);
            // nearest-neighbour round trip (middle sample of each k x k cell) versus a smooth round trip
            // (k x k box average, then bilinear). A real pixel grid reproduces exactly under the first and
            // blurs under the second; a photo or anti-aliased drawing does not prefer the first.
            var (pn, ox, oy) = BestPhaseNn(c, k);   // a cropped or padded sprite rarely starts on a cell boundary: try every grid phase
            var bl = BoxDown(c, k).ResampleBilinear(kw, kh);
            double pb = RgbImage.Psnr(c, bl);
            double gain = pn - pb;
            if (pn >= 28 && gain >= 3 && gain > bestGain)
            {
                bestGain = gain; res.GridK = k; res.GridDb = Math.Round(pn, 1);
                // cells start at c*k - o inside the crop; the crop starts at ((W - cw) / 2, (H - ch) / 2) in the full image
                int x0 = (full.W - crop.W) / 2, y0 = (full.H - crop.H) / 2;
                res.GridPhaseX = ((x0 - ox) % k + k) % k; res.GridPhaseY = ((y0 - oy) % k + k) % k;
            }
        }
        if (res.GridK >= 2 && (full.W > crop.W || full.H > crop.H))
        {
            // the phase decides where the undo samples: re-measure it on a larger area, so a featureless centre
            // (sky, a flat background) does not leave the choice to a tie between phases
            var big = full.CenterCrop(1024); int k = res.GridK;
            var (_, ox, oy) = BestPhaseNn(CropTopLeft(big, big.W / k * k, big.H / k * k), k);
            int bx0 = (full.W - big.W) / 2, by0 = (full.H - big.H) / 2;
            res.GridPhaseX = ((bx0 - ox) % k + k) % k; res.GridPhaseY = ((by0 - oy) % k + k) % k;
        }
        int lng = Math.Max(res.Width, res.Height);
        (res.Soft, res.Multi) = Texture(full);
        // Palette formats make every small GIF look like a sprite (<= 256 colours, dithered hard edges). The texture of a
        // quantized soft picture gives it away: many tiny neighbour differences (adjacent palette entries, >= 16%), or
        // hard 3x3 windows holding 7+ colours (gradient edges, >= 45%), or hardly any hard edges at all (< 12%).
        // The 47 xBRZ sample screens stay below 0.14 / 0.40 and above 0.16; dithered GIFs of photos and drawings land
        // at 0.16-0.85 / 0.45-0.83. JPEG noise would trigger this on genuine sprites, so lossy sources are exempt.
        bool paletteRule = lng <= 512 && res.Density <= 0.02 || lng <= 256 && hr >= 0.4;
        res.Dithered = !lossy && res.GridK < 2 && paletteRule && (res.Soft >= 0.16 || res.Multi >= 0.45 || res.HardRatioNative < 0.12);
        if (res.GridK >= 2) { res.Class = "pixelart"; res.Confidence = res.GridDb >= 60 ? 0.95 : 0.8; res.Note = $"nearest-neighbour grid {res.GridK}x ({res.GridDb} dB)"; }
        else if (!res.Dithered && lng <= 512 && res.Density <= 0.02) { res.Class = "pixelart"; res.Confidence = Math.Round(0.7 + 0.25 * Math.Min(1.0, (0.02 - res.Density) / 0.02), 2); res.Note = $"small palette image ({lng} px, {res.Colors} colours)"; }
        else if (!res.Dithered && lng <= 256 && hr >= 0.4) { res.Class = "pixelart"; res.Confidence = Math.Round(0.6 + 0.3 * Math.Min(1.0, (hr - 0.4) / 0.6), 2); res.Note = $"small image with hard edges ({lng} px)"; }
        else
        {
            // Art needs hard edges both on the proxy and at native resolution: a large photo point-sampled to the
            // proxy looks hard-edged (neighbouring proxy pixels are several source pixels apart) but its native
            // crops are soft; flat-colour art is hard in both. Photographed documents land in art, which is right
            // for text (no blur). Measured on 100 library images + 16 calibration images.
            double hn = res.HardRatioNative; double soft = Math.Min(hr, hn);
            if (hr >= 0.4 && hn >= 0.3) { res.Class = "art"; res.Confidence = Math.Round(0.6 + 0.4 * Math.Min(1.0, (soft - 0.3) / 0.4), 2); res.Note = $"hard-edge ratio {res.HardRatio} (native {hn})"; }
            else if (hr < 0.3 || hn < 0.2) { res.Class = "photo"; res.Confidence = Math.Round(0.6 + 0.4 * Math.Min(1.0, (0.3 - Math.Min(hr, 0.3)) / 0.3 + (0.2 - Math.Min(hn, 0.2)) / 0.2) / 1.0, 2); res.Note = $"hard-edge ratio {res.HardRatio} (native {hn})"; }
            else { res.Class = "photo"; res.Confidence = 0.5; res.Note = $"borderline, painting-like (ratio {res.HardRatio}, native {hn})"; }
            if (res.Confidence > 1) res.Confidence = 1;
            if (res.Dithered) res.Note += "; palette image with gradients, not pixel art";
        }
        return res;
    }
    /// <summary>Texture of a native-resolution centre crop (up to 512x512). soft: among neighbouring pixel pairs that
    /// differ at all, the share that differs by at most 6 levels (adjacent entries of a palette fitted to a soft picture;
    /// pixel-art palettes have coarser steps). multi: among 3x3 windows with a hard edge (gray range > 64), the share
    /// holding 7 or more distinct colours (a gradient crossing the edge; a pixel-art edge is a two- or three-colour step).</summary>
    static (double soft, double multi) Texture(RgbImage img)
    {
        var c = img.CenterCrop(512); int w = c.W, h = c.H; if (w < 3 || h < 3) return (0, 0);
        var a = c.Rgb; var g = c.Gray(); long diff = 0, tiny = 0, hardWin = 0, multi7 = 0;
        static int D(byte[] a, int p, int q) => Math.Max(Math.Abs(a[p] - a[q]), Math.Max(Math.Abs(a[p + 1] - a[q + 1]), Math.Abs(a[p + 2] - a[q + 2])));
        var win = new int[9];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int p = (y * w + x) * 3;
                int dr = D(a, p, p + 3), dd = D(a, p, p + w * 3);
                if (dr > 0) { diff++; if (dr <= 6) tiny++; }
                if (dd > 0) { diff++; if (dd <= 6) tiny++; }
                // colours inside a hard 3x3 window: a pixel-art edge is a two- or three-colour step, a downscaled soft
                // drawing or photo crosses an edge through a gradient of five or more distinct colours
                int gmn = 255, gmx = 0, k = 0;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { int i = (y + dy) * w + x + dx; int v = g[i]; if (v < gmn) gmn = v; if (v > gmx) gmx = v; win[k++] = a[i * 3] << 16 | a[i * 3 + 1] << 8 | a[i * 3 + 2]; }
                if (gmx - gmn > 64)
                {
                    hardWin++; int distinct = 0;
                    for (int i = 0; i < 9; i++) { bool seen = false; for (int j = 0; j < i; j++) if (win[j] == win[i]) { seen = true; break; } if (!seen) distinct++; }
                    if (distinct >= 7) multi7++;
                }
            }
        return (diff > 0 ? Math.Round((double)tiny / diff, 3) : 0, hardWin >= 50 ? Math.Round((double)multi7 / hardWin, 3) : 0);
    }
    /// <summary>Hard-edge ratio over up to five 256x256 full-resolution crops (centre and four quadrants).</summary>
    static double HardRatioOnCrops(RgbImage full)
    {
        int size = Math.Min(256, Math.Min(full.W, full.H));
        var origins = new List<(int x, int y)> { ((full.W - size) / 2, (full.H - size) / 2) };
        if (full.W > 2 * size && full.H > 2 * size)
        {
            origins.Add((full.W / 4 - size / 2, full.H / 4 - size / 2)); origins.Add((3 * full.W / 4 - size / 2, full.H / 4 - size / 2));
            origins.Add((full.W / 4 - size / 2, 3 * full.H / 4 - size / 2)); origins.Add((3 * full.W / 4 - size / 2, 3 * full.H / 4 - size / 2));
        }
        long hard = 0, any = 0;
        foreach (var (ox, oy) in origins)
        {
            var g = new byte[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { int p = ((oy + y) * full.W + ox + x) * 3; g[y * size + x] = (byte)((full.Rgb[p] * 299 + full.Rgb[p + 1] * 587 + full.Rgb[p + 2] * 114 + 500) / 1000); }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int mn = 255, mx = 0;
                    for (int dy = -1; dy <= 1; dy++) { int yy = Math.Clamp(y + dy, 0, size - 1); for (int dx = -1; dx <= 1; dx++) { int v = g[yy * size + Math.Clamp(x + dx, 0, size - 1)]; if (v < mn) mn = v; if (v > mx) mx = v; } }
                    int range = mx - mn; if (range > 8) any++; if (range > 64) hard++;
                }
        }
        return any > 0 ? (double)hard / any : 0;
    }
    /// <summary>PSNR of the nearest-neighbour round trip (every pixel replaced by the middle sample of its k x k cell)
    /// at the best of the k*k possible grid phases.</summary>
    static (double psnr, int ox, int oy) BestPhaseNn(RgbImage c, int k)
    {
        int w = c.W, h = c.H; var nn = new RgbImage(w, h); var src = c.Rgb; var dst = nn.Rgb; double best = 0; int bx = 0, by = 0;
        for (int oy = 0; oy < k; oy++)
            for (int ox = 0; ox < k; ox++)
            {
                for (int y = 0; y < h; y++)
                {
                    int sy = Math.Clamp((y + oy) / k * k - oy + k / 2, 0, h - 1);
                    for (int x = 0; x < w; x++)
                    {
                        int sx = Math.Clamp((x + ox) / k * k - ox + k / 2, 0, w - 1);
                        int s = (sy * w + sx) * 3, d = (y * w + x) * 3;
                        dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2];
                    }
                }
                double p = RgbImage.Psnr(c, nn); if (p > best) { best = p; bx = ox; by = oy; }
                if (best >= 120) return (best, bx, by);
            }
        return (best, bx, by);
    }
    static RgbImage BoxDown(RgbImage img, int k)
    {
        int nw = img.W / k, nh = img.H / k; var o = new RgbImage(nw, nh);
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < k; dy++) for (int dx = 0; dx < k; dx++) sum += img.Rgb[((y * k + dy) * img.W + x * k + dx) * 3 + c];
                    o.Rgb[(y * nw + x) * 3 + c] = (byte)((sum + k * k / 2) / (k * k));
                }
        return o;
    }
    static RgbImage CropTopLeft(RgbImage img, int w, int h)
    {
        var o = new RgbImage(w, h);
        for (int y = 0; y < h; y++) Buffer.BlockCopy(img.Rgb, y * img.W * 3, o.Rgb, y * w * 3, w * 3);
        return o;
    }
}

/// <summary>Minimal PNG writer (8-bit RGB, zlib via the framework, no dependencies).</summary>
public static class Png
{
    static readonly uint[] CrcTable = BuildCrc();
    static uint[] BuildCrc() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    static uint Crc(byte[] data, int off, int len) { uint c = 0xFFFFFFFFu; for (int i = off; i < off + len; i++) c = CrcTable[(c ^ data[i]) & 255] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }

    public static void Write(string path, RgbImage img, byte[]? icc = null, byte[]? exif = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool alpha = img.Alpha != null; int bpp = alpha ? 4 : 3;
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), img.W); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.H);
        ihdr[8] = 8; ihdr[9] = (byte)(alpha ? 6 : 2); ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);
        if (icc != null && icc.Length > 0)
        {
            // iCCP: profile name, null, compression method 0, zlib-compressed profile
            using var zs = new MemoryStream(); using (var z = new ZLibStream(zs, CompressionLevel.Optimal, true)) z.Write(icc, 0, icc.Length);
            var name = System.Text.Encoding.ASCII.GetBytes("ICC Profile"); var data = new byte[name.Length + 2 + zs.Length];
            Buffer.BlockCopy(name, 0, data, 0, name.Length); data[name.Length] = 0; data[name.Length + 1] = 0; Buffer.BlockCopy(zs.ToArray(), 0, data, name.Length + 2, (int)zs.Length);
            Chunk(fs, "iCCP", data);
        }
        if (exif != null && exif.Length > 0) Chunk(fs, "eXIf", exif);   // eXIf carries the raw TIFF structure
        // filter type 1 (Sub) per row: simple and compresses flat art and photos reasonably
        int stride = img.W * bpp; var raw = new byte[(stride + 1) * img.H]; var row = new byte[stride];
        for (int y = 0; y < img.H; y++)
        {
            int ro = y * (stride + 1); raw[ro] = 1;
            if (alpha) { for (int x = 0; x < img.W; x++) { int sp = (y * img.W + x) * 3; row[x * 4] = img.Rgb[sp]; row[x * 4 + 1] = img.Rgb[sp + 1]; row[x * 4 + 2] = img.Rgb[sp + 2]; row[x * 4 + 3] = img.Alpha![y * img.W + x]; } }
            else Buffer.BlockCopy(img.Rgb, y * stride, row, 0, stride);
            for (int i = 0; i < stride; i++) { int left = i >= bpp ? row[i - bpp] : 0; raw[ro + 1 + i] = (byte)(row[i] - left); }
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw, 0, raw.Length);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var buf = new byte[4 + data.Length];
        buf[0] = (byte)type[0]; buf[1] = (byte)type[1]; buf[2] = (byte)type[2]; buf[3] = (byte)type[3];
        Buffer.BlockCopy(data, 0, buf, 4, data.Length);
        var len = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
        s.Write(buf);
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(buf, 0, buf.Length)); s.Write(crc);
    }
}

/// <summary>Loads PNG/GIF/BMP/WebP (first frame) through ImageSharp for the classifier and the pixel-art route.</summary>
public static class Loader
{
    public static RgbImage? LoadNonJpeg(string path, out int frames)
    {
        frames = 1;
        try
        {
            using var im = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);
            frames = im.Frames.Count;
            int w = im.Width, h = im.Height; var img = new RgbImage(w, h); var alpha = new byte[w * h]; bool anyAlpha = false;
            im.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var px = row[x]; int i = y * w + x, p = i * 3;
                        img.Rgb[p] = px.R; img.Rgb[p + 1] = px.G; img.Rgb[p + 2] = px.B; alpha[i] = px.A; if (px.A != 255) anyAlpha = true;
                    }
                }
            });
            if (anyAlpha) img.Alpha = alpha;   // kept straight (not premultiplied); xBRZ and the PNG writer handle it
            return img;
        }
        catch { return null; }
    }

    /// <summary>Identifies the real file type from its first bytes, whatever the extension says.</summary>
    public static string Sniff(string path)
    {
        try
        {
            using var fs = File.OpenRead(path); var b = new byte[16]; int n = fs.Read(b, 0, 16); if (n < 12) return "unknown";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
            if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8') return "gif";
            if (b[0] == (byte)'B' && b[1] == (byte)'M') return "bmp";
            if (b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return "webp";
            return "unknown";
        }
        catch { return "unknown"; }
    }
}
