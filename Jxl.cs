// Jxl.cs - JPEG XL output through the bundled libjxl 0.12.0 DLLs (BSD-3): pixel-lossless encode with ICC/EXIF,
// reversible JPEG transcode with byte-exact verification, and decoding for self-checks.
using System.Runtime.InteropServices;

namespace Unsquish;

public static unsafe class Jxl
{
    const string L = "jxl"; const string LT = "jxl_threads";

    [StructLayout(LayoutKind.Sequential)]
    struct PixelFormat { public uint num_channels; public int data_type; public int endianness; public nuint align; }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicInfo
    {
        public int have_container; public uint xsize, ysize, bits_per_sample, exponent_bits_per_sample;
        public float intensity_target, min_nits; public int relative_to_max_display; public float linear_below;
        public int uses_original_profile, have_preview, have_animation; public int orientation;
        public uint num_color_channels, num_extra_channels, alpha_bits, alpha_exponent_bits; public int alpha_premultiplied;
        public uint preview_xsize, preview_ysize; public uint tps_numerator, tps_denominator, num_loops; public int have_timecodes;
        public uint intrinsic_xsize, intrinsic_ysize; public fixed byte padding[100];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ColorEncoding
    {
        public int color_space, white_point; public double wx, wy; public int primaries; public double rx, ry, gx, gy, bx, by;
        public int transfer_function; public double gamma; public int rendering_intent;
    }

    [DllImport(L)] static extern uint JxlEncoderVersion();
    [DllImport(L)] static extern IntPtr JxlEncoderCreate(IntPtr mm);
    [DllImport(L)] static extern void JxlEncoderDestroy(IntPtr enc);
    [DllImport(L)] static extern int JxlEncoderSetParallelRunner(IntPtr enc, IntPtr runner, IntPtr opaque);
    [DllImport(L)] static extern void JxlEncoderInitBasicInfo(BasicInfo* info);
    [DllImport(L)] static extern int JxlEncoderSetBasicInfo(IntPtr enc, BasicInfo* info);
    [DllImport(L)] static extern void JxlColorEncodingSetToSRGB(ColorEncoding* ce, int isGray);
    [DllImport(L)] static extern int JxlEncoderSetColorEncoding(IntPtr enc, ColorEncoding* ce);
    [DllImport(L)] static extern int JxlEncoderSetICCProfile(IntPtr enc, byte* icc, nuint size);
    [DllImport(L)] static extern int JxlEncoderUseContainer(IntPtr enc, int use);
    [DllImport(L)] static extern int JxlEncoderUseBoxes(IntPtr enc);
    [DllImport(L)] static extern int JxlEncoderAddBox(IntPtr enc, byte* type, byte* contents, nuint size, int compress);
    [DllImport(L)] static extern void JxlEncoderCloseBoxes(IntPtr enc);
    [DllImport(L)] static extern int JxlEncoderStoreJPEGMetadata(IntPtr enc, int store);
    [DllImport(L)] static extern IntPtr JxlEncoderFrameSettingsCreate(IntPtr enc, IntPtr source);
    [DllImport(L)] static extern int JxlEncoderSetFrameLossless(IntPtr fs, int lossless);
    [DllImport(L)] static extern int JxlEncoderFrameSettingsSetOption(IntPtr fs, int option, long value);
    [DllImport(L)] static extern int JxlEncoderAddImageFrame(IntPtr fs, PixelFormat* pf, byte* buffer, nuint size);
    [DllImport(L)] static extern int JxlEncoderAddJPEGFrame(IntPtr fs, byte* buffer, nuint size);
    [DllImport(L)] static extern void JxlEncoderCloseInput(IntPtr enc);
    [DllImport(L)] static extern int JxlEncoderProcessOutput(IntPtr enc, byte** nextOut, nuint* availOut);

    [DllImport(L)] static extern IntPtr JxlDecoderCreate(IntPtr mm);
    [DllImport(L)] static extern void JxlDecoderDestroy(IntPtr dec);
    [DllImport(L)] static extern int JxlDecoderSubscribeEvents(IntPtr dec, int events);
    [DllImport(L)] static extern int JxlDecoderSetInput(IntPtr dec, byte* data, nuint size);
    [DllImport(L)] static extern void JxlDecoderCloseInput(IntPtr dec);
    [DllImport(L)] static extern int JxlDecoderProcessInput(IntPtr dec);
    [DllImport(L)] static extern int JxlDecoderGetBasicInfo(IntPtr dec, BasicInfo* info);
    [DllImport(L)] static extern int JxlDecoderImageOutBufferSize(IntPtr dec, PixelFormat* pf, nuint* size);
    [DllImport(L)] static extern int JxlDecoderSetImageOutBuffer(IntPtr dec, PixelFormat* pf, byte* buffer, nuint size);
    [DllImport(L)] static extern int JxlDecoderSetJPEGBuffer(IntPtr dec, byte* data, nuint size);
    [DllImport(L)] static extern nuint JxlDecoderReleaseJPEGBuffer(IntPtr dec);

    [DllImport(LT)] static extern IntPtr JxlThreadParallelRunnerCreate(IntPtr mm, nuint threads);
    [DllImport(LT)] static extern void JxlThreadParallelRunnerDestroy(IntPtr runner);

    const int ENC_SUCCESS = 0, ENC_NEED_MORE_OUTPUT = 2;
    const int DEC_SUCCESS = 0, DEC_ERROR = 1, DEC_NEED_IMAGE_OUT_BUFFER = 5, DEC_JPEG_NEED_MORE_OUTPUT = 6, DEC_BASIC_INFO = 0x40, DEC_FULL_IMAGE = 0x1000, DEC_JPEG_RECONSTRUCTION = 0x2000;

    static bool? available; static IntPtr runnerFn;
    public static bool Available
    {
        get
        {
            if (available == null)
            {
                try { JxlEncoderVersion(); available = true; } catch { available = false; }
                if (available == true)
                {
                    try { if (NativeLibrary.TryLoad(LT, typeof(Jxl).Assembly, null, out var h)) runnerFn = NativeLibrary.GetExport(h, "JxlThreadParallelRunner"); } catch { runnerFn = IntPtr.Zero; }
                }
            }
            return available.Value;
        }
    }
    public static string VersionString { get { try { uint v = JxlEncoderVersion(); return $"{v / 1000000}.{v / 1000 % 1000}.{v % 1000}"; } catch { return "n/a"; } } }

    static byte[] Collect(IntPtr enc)
    {
        var ms = new MemoryStream(); var buf = new byte[1 << 20];
        fixed (byte* p = buf)
        {
            while (true)
            {
                byte* next = p; nuint avail = (nuint)buf.Length;
                int st = JxlEncoderProcessOutput(enc, &next, &avail);
                int written = (int)(next - p); if (written > 0) ms.Write(buf, 0, written);
                if (st == ENC_SUCCESS) break;
                if (st != ENC_NEED_MORE_OUTPUT) throw new Exception("JxlEncoderProcessOutput failed (" + st + ")");
            }
        }
        return ms.ToArray();
    }

    /// <summary>Pixel-lossless JPEG XL of an 8-bit RGB image, with optional ICC profile and EXIF (TIFF payload).</summary>
    public static byte[] EncodeLossless(RgbImage img, byte[]? icc, byte[]? exif, int effort, int threads)
    {
        IntPtr enc = JxlEncoderCreate(IntPtr.Zero); IntPtr runner = IntPtr.Zero;
        try
        {
            if (runnerFn != IntPtr.Zero) { runner = JxlThreadParallelRunnerCreate(IntPtr.Zero, (nuint)Math.Max(1, threads)); JxlEncoderSetParallelRunner(enc, runnerFn, runner); }
            var info = new BasicInfo(); JxlEncoderInitBasicInfo(&info);
            info.xsize = (uint)img.W; info.ysize = (uint)img.H; info.bits_per_sample = 8; info.exponent_bits_per_sample = 0;
            info.uses_original_profile = 1; info.num_color_channels = 3; info.alpha_bits = 0; info.num_extra_channels = 0;
            bool boxes = exif != null && exif.Length > 0;
            if (boxes) { Check(JxlEncoderUseContainer(enc, 1), "UseContainer"); }
            Check(JxlEncoderSetBasicInfo(enc, &info), "SetBasicInfo");
            if (icc != null && icc.Length > 0) { fixed (byte* p = icc) Check(JxlEncoderSetICCProfile(enc, p, (nuint)icc.Length), "SetICCProfile"); }
            else { var ce = new ColorEncoding(); JxlColorEncodingSetToSRGB(&ce, 0); Check(JxlEncoderSetColorEncoding(enc, &ce), "SetColorEncoding"); }
            if (boxes)
            {
                Check(JxlEncoderUseBoxes(enc), "UseBoxes");
                var payload = new byte[4 + exif!.Length]; Buffer.BlockCopy(exif, 0, payload, 4, exif.Length);   // 4-byte TIFF offset (0) + TIFF data
                byte* type = stackalloc byte[4]; type[0] = (byte)'E'; type[1] = (byte)'x'; type[2] = (byte)'i'; type[3] = (byte)'f';
                fixed (byte* p = payload) Check(JxlEncoderAddBox(enc, type, p, (nuint)payload.Length, 1), "AddBox");
                JxlEncoderCloseBoxes(enc);
            }
            IntPtr fs = JxlEncoderFrameSettingsCreate(enc, IntPtr.Zero);
            Check(JxlEncoderSetFrameLossless(fs, 1), "SetFrameLossless");
            Check(JxlEncoderFrameSettingsSetOption(fs, 0 /* EFFORT */, Math.Clamp(effort, 1, 9)), "SetOption effort");
            var pf = new PixelFormat { num_channels = 3, data_type = 2 /* UINT8 */, endianness = 0, align = 0 };
            fixed (byte* p = img.Rgb) Check(JxlEncoderAddImageFrame(fs, &pf, p, (nuint)img.Rgb.Length), "AddImageFrame");
            JxlEncoderCloseInput(enc);
            return Collect(enc);
        }
        finally { JxlEncoderDestroy(enc); if (runner != IntPtr.Zero) JxlThreadParallelRunnerDestroy(runner); }
    }

    /// <summary>Reversible JPEG to JPEG XL transcode (coefficients re-coded, reconstruction data stored).</summary>
    public static byte[] TranscodeJpeg(byte[] jpeg, int effort, int threads)
    {
        IntPtr enc = JxlEncoderCreate(IntPtr.Zero); IntPtr runner = IntPtr.Zero;
        try
        {
            if (runnerFn != IntPtr.Zero) { runner = JxlThreadParallelRunnerCreate(IntPtr.Zero, (nuint)Math.Max(1, threads)); JxlEncoderSetParallelRunner(enc, runnerFn, runner); }
            Check(JxlEncoderUseContainer(enc, 1), "UseContainer");
            Check(JxlEncoderStoreJPEGMetadata(enc, 1), "StoreJPEGMetadata");
            IntPtr fs = JxlEncoderFrameSettingsCreate(enc, IntPtr.Zero);
            Check(JxlEncoderFrameSettingsSetOption(fs, 0, Math.Clamp(effort, 1, 9)), "SetOption effort");
            fixed (byte* p = jpeg) Check(JxlEncoderAddJPEGFrame(fs, p, (nuint)jpeg.Length), "AddJPEGFrame");
            JxlEncoderCloseInput(enc);
            return Collect(enc);
        }
        finally { JxlEncoderDestroy(enc); if (runner != IntPtr.Zero) JxlThreadParallelRunnerDestroy(runner); }
    }

    /// <summary>Rebuilds the original JPEG bytes from a reversibly transcoded JXL; null if not reconstructible.</summary>
    public static byte[]? ReconstructJpeg(byte[] jxl, int expectedSize)
    {
        IntPtr dec = JxlDecoderCreate(IntPtr.Zero);
        var outBuf = new byte[Math.Max(expectedSize * 2, 1 << 16)];
        var hIn = GCHandle.Alloc(jxl, GCHandleType.Pinned); var hOut = GCHandle.Alloc(outBuf, GCHandleType.Pinned); GCHandle hImg = default;
        try
        {
            // The decoder still walks through the frame, so it may ask for an image buffer; give it a throwaway one.
            if (JxlDecoderSubscribeEvents(dec, DEC_BASIC_INFO | DEC_JPEG_RECONSTRUCTION | DEC_FULL_IMAGE) != DEC_SUCCESS) return null;
            if (JxlDecoderSetInput(dec, (byte*)hIn.AddrOfPinnedObject(), (nuint)jxl.Length) != DEC_SUCCESS) return null;
            JxlDecoderCloseInput(dec);
            bool gotBuffer = false; var pf = new PixelFormat { num_channels = 3, data_type = 2, endianness = 0, align = 0 };
            while (true)
            {
                int st = JxlDecoderProcessInput(dec);
                if (st == DEC_JPEG_RECONSTRUCTION) { if (JxlDecoderSetJPEGBuffer(dec, (byte*)hOut.AddrOfPinnedObject(), (nuint)outBuf.Length) != DEC_SUCCESS) return null; gotBuffer = true; }
                else if (st == DEC_NEED_IMAGE_OUT_BUFFER)
                {
                    nuint size; if (JxlDecoderImageOutBufferSize(dec, &pf, &size) != DEC_SUCCESS) return null;
                    var scratch = new byte[(long)size]; hImg = GCHandle.Alloc(scratch, GCHandleType.Pinned);
                    if (JxlDecoderSetImageOutBuffer(dec, &pf, (byte*)hImg.AddrOfPinnedObject(), size) != DEC_SUCCESS) return null;
                }
                else if (st == DEC_BASIC_INFO || st == DEC_FULL_IMAGE) continue;
                else if (st == DEC_SUCCESS) break;
                else return null;   // error, truncated input, or an output buffer too small for the reconstruction
            }
            if (!gotBuffer) return null;
            int used = outBuf.Length - (int)JxlDecoderReleaseJPEGBuffer(dec);
            var result = new byte[used]; Buffer.BlockCopy(outBuf, 0, result, 0, used); return result;
        }
        finally { hIn.Free(); hOut.Free(); if (hImg.IsAllocated) hImg.Free(); JxlDecoderDestroy(dec); }
    }

    /// <summary>Decodes a JXL to 8-bit RGB (used to verify pixel-lossless output).</summary>
    public static RgbImage? Decode(byte[] jxl)
    {
        IntPtr dec = JxlDecoderCreate(IntPtr.Zero); var hIn = GCHandle.Alloc(jxl, GCHandleType.Pinned); GCHandle hOut = default; RgbImage? img = null;
        try
        {
            if (JxlDecoderSubscribeEvents(dec, DEC_BASIC_INFO | DEC_FULL_IMAGE) != DEC_SUCCESS) return null;
            if (JxlDecoderSetInput(dec, (byte*)hIn.AddrOfPinnedObject(), (nuint)jxl.Length) != DEC_SUCCESS) return null;
            JxlDecoderCloseInput(dec);
            var pf = new PixelFormat { num_channels = 3, data_type = 2, endianness = 0, align = 0 };
            while (true)
            {
                int st = JxlDecoderProcessInput(dec);
                if (st == DEC_BASIC_INFO) { var bi = new BasicInfo(); if (JxlDecoderGetBasicInfo(dec, &bi) != DEC_SUCCESS) return null; img = new RgbImage((int)bi.xsize, (int)bi.ysize); }
                else if (st == DEC_NEED_IMAGE_OUT_BUFFER)
                {
                    if (img == null) return null; nuint size; if (JxlDecoderImageOutBufferSize(dec, &pf, &size) != DEC_SUCCESS || (int)size != img.Rgb.Length) return null;
                    hOut = GCHandle.Alloc(img.Rgb, GCHandleType.Pinned);
                    if (JxlDecoderSetImageOutBuffer(dec, &pf, (byte*)hOut.AddrOfPinnedObject(), size) != DEC_SUCCESS) return null;
                }
                else if (st == DEC_FULL_IMAGE || st == DEC_SUCCESS) break;
                else if (st == DEC_ERROR) return null;
            }
            return img;
        }
        finally { hIn.Free(); if (hOut.IsAllocated) hOut.Free(); JxlDecoderDestroy(dec); }
    }

    static void Check(int status, string what) { if (status != ENC_SUCCESS) throw new Exception("libjxl " + what + " failed (" + status + ")"); }
}
