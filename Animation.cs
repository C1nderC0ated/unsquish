// Animated GIF / PNG / WebP in and out through ImageSharp: full-canvas frames with their delays and the loop count.
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Unsquish;

public static class Animation
{
    public sealed class Frame { public RgbImage Image = null!; public int DelayMs; }

    /// <summary>All frames, composited by the decoder (disposal methods applied), with delays in milliseconds.
    /// loopCount 0 means repeat forever.</summary>
    public static (List<Frame> frames, int loopCount)? Load(string path, string fmt)
    {
        try
        {
            using var im = Image.Load<Rgba32>(path);
            int loop = 0;
            try
            {
                loop = fmt switch
                {
                    "gif" => im.Metadata.GetGifMetadata().RepeatCount,
                    "png" => (int)im.Metadata.GetPngMetadata().RepeatCount,
                    "webp" => im.Metadata.GetWebpMetadata().RepeatCount,
                    _ => 0,
                };
            }
            catch { }
            var list = new List<Frame>(im.Frames.Count);
            foreach (ImageFrame<Rgba32> fr in im.Frames)
            {
                int delay = 100;
                try
                {
                    delay = fmt switch
                    {
                        "gif" => fr.Metadata.GetGifMetadata().FrameDelay * 10,
                        "png" => Ms(fr.Metadata.GetPngMetadata().FrameDelay),
                        "webp" => (int)fr.Metadata.GetWebpMetadata().FrameDelay,
                        _ => 100,
                    };
                }
                catch { }
                if (delay <= 0) delay = 100;   // a zero delay is shown as about 100 ms by browsers
                list.Add(new Frame { Image = ToRgb(fr), DelayMs = delay });
            }
            return (list, loop);
        }
        catch { return null; }
    }

    static int Ms(Rational r) => (int)Math.Round(1000.0 * r.Numerator / (r.Denominator == 0 ? 100 : r.Denominator));   // APNG: a zero denominator means 100

    static RgbImage ToRgb(ImageFrame<Rgba32> fr)
    {
        int w = fr.Width, h = fr.Height; var img = new RgbImage(w, h); var alpha = new byte[w * h]; bool anyAlpha = false;
        fr.ProcessPixelRows(acc =>
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
        if (anyAlpha) img.Alpha = alpha;
        return img;
    }

    /// <summary>Writes an animated PNG (APNG). Viewers without APNG support show the first frame.</summary>
    public static void WriteApng(string path, List<RgbImage> frames, List<int> delaysMs, int loopCount)
    {
        bool alpha = frames.Any(f => f.HasAlpha); int w = frames[0].W, h = frames[0].H;
        using var img = new Image<Rgba32>(w, h);
        for (int i = 0; i < frames.Count; i++)
        {
            var target = i == 0 ? img.Frames.RootFrame : img.Frames.CreateFrame();
            var src = frames[i];
            target.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++) { int k = y * w + x, p = k * 3; row[x] = new Rgba32(src.Rgb[p], src.Rgb[p + 1], src.Rgb[p + 2], src.Alpha != null ? src.Alpha[k] : (byte)255); }
                }
            });
            target.Metadata.GetPngMetadata().FrameDelay = new Rational((uint)Math.Max(1, delaysMs[i]), 1000);
        }
        img.Metadata.GetPngMetadata().RepeatCount = (uint)Math.Max(0, loopCount);
        img.SaveAsPng(path, new PngEncoder { ColorType = alpha ? PngColorType.RgbWithAlpha : PngColorType.Rgb, BitDepth = PngBitDepth.Bit8, CompressionLevel = PngCompressionLevel.BestCompression });
    }
}
