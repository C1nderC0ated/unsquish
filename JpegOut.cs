// High-quality JPEG output, for when the result has to open in every program: quality 95 by default, no chroma
// subsampling, optimized Huffman tables. Still lossy, unlike the JPEG XL and PNG outputs, and labelled as such.
using BitMiracle.LibJpeg.Classic;

namespace Unsquish;

public static class JpegWriter
{
    public static void Write(string path, RgbImage img, int quality, byte[]? icc, byte[]? exif, bool gray)
    {
        using var fs = File.Create(path);
        var c = new jpeg_compress_struct(); c.jpeg_stdio_dest(fs);
        c.Image_width = img.W; c.Image_height = img.H;
        c.Input_components = gray ? 1 : 3; c.In_color_space = gray ? J_COLOR_SPACE.JCS_GRAYSCALE : J_COLOR_SPACE.JCS_RGB;
        c.jpeg_set_defaults(); c.jpeg_set_quality(Math.Clamp(quality, 1, 100), true); c.Optimize_coding = true;
        if (!gray) { c.Component_info[0].H_samp_factor = 1; c.Component_info[0].V_samp_factor = 1; }   // 4:4:4
        c.jpeg_start_compress(true);
        if (exif != null && exif.Length > 0 && exif.Length <= 65527)
        {
            var m = new byte[6 + exif.Length]; m[0] = (byte)'E'; m[1] = (byte)'x'; m[2] = (byte)'i'; m[3] = (byte)'f';
            Buffer.BlockCopy(exif, 0, m, 6, exif.Length); c.jpeg_write_marker(0xE1, m);
        }
        if (icc != null && icc.Length > 0)
        {
            const int max = 65519; int count = (icc.Length + max - 1) / max;
            if (count <= 255)
                for (int i = 0; i < count; i++)
                {
                    int len = Math.Min(max, icc.Length - i * max); var m = new byte[14 + len];
                    System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE").CopyTo(m, 0); m[12] = (byte)(i + 1); m[13] = (byte)count;
                    Buffer.BlockCopy(icc, i * max, m, 14, len); c.jpeg_write_marker(0xE2, m);
                }
        }
        int w = img.W; var row = new byte[1][]; row[0] = new byte[w * (gray ? 1 : 3)];
        for (int y = 0; y < img.H; y++)
        {
            if (gray) { for (int x = 0; x < w; x++) row[0][x] = img.Rgb[(y * w + x) * 3]; }
            else Buffer.BlockCopy(img.Rgb, y * w * 3, row[0], 0, w * 3);
            c.jpeg_write_scanlines(row, 1);
        }
        c.jpeg_finish_compress(); c.jpeg_destroy();
    }
}
