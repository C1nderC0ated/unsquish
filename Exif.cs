// The one Exif tag the pixel pipeline must honour: Orientation (0x0112). Decoders hand back pixels in stored order;
// restored outputs get the rotation baked in and the tag reset to 1 so every viewer agrees.
namespace Unsquish;

public static class Exif
{
    /// <summary>Orientation 1-8 from a TIFF structure (the Exif payload after "Exif\0\0"), or 1 when absent or malformed.</summary>
    public static int Orientation(byte[]? tiff) => Find(tiff, out _, out _);

    /// <summary>A copy of the TIFF structure with the Orientation tag set to 1 (the original when the tag is absent).</summary>
    public static byte[]? Normalized(byte[]? tiff)
    {
        if (Find(tiff, out int at, out bool le) <= 1) return tiff;
        var copy = (byte[])tiff!.Clone();
        if (le) { copy[at] = 1; copy[at + 1] = 0; } else { copy[at] = 0; copy[at + 1] = 1; }
        return copy;
    }

    static int Find(byte[]? t, out int valueAt, out bool littleEndian)
    {
        valueAt = -1; littleEndian = true;
        if (t == null || t.Length < 14) return 1;
        bool le; if (t[0] == 'I' && t[1] == 'I') le = true; else if (t[0] == 'M' && t[1] == 'M') le = false; else return 1;
        littleEndian = le;
        int U16(int p) => le ? t[p] | t[p + 1] << 8 : t[p] << 8 | t[p + 1];
        long U32(int p) => le ? (uint)(t[p] | t[p + 1] << 8 | t[p + 2] << 16 | t[p + 3] << 24) : (uint)(t[p] << 24 | t[p + 1] << 16 | t[p + 2] << 8 | t[p + 3]);
        if (U16(2) != 42) return 1;
        long ifd = U32(4);
        if (ifd < 8 || ifd + 2 > t.Length) return 1;
        int count = U16((int)ifd);
        for (int i = 0; i < count; i++)
        {
            int e = (int)ifd + 2 + i * 12;
            if (e + 12 > t.Length) return 1;
            if (U16(e) != 0x0112) continue;
            if (U16(e + 2) != 3 || U32(e + 4) != 1) return 1;   // SHORT, one value, stored inline
            int v = U16(e + 8);
            valueAt = e + 8;
            return v >= 1 && v <= 8 ? v : 1;
        }
        return 1;
    }
}
