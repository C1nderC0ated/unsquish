// Orthonormal 8x8 DCT-II and its inverse, the transform JPEG quantizes in. Used thousands of times per block by the
// iterative restorers, so there is a SIMD path (two passes of 64 fused row operations, no transposes) and a scalar one.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Unsquish;

public static class Dct8
{
    // B[u*8 + x] = c(u) * cos((2x+1) u pi / 16) / 2, c(0) = 1/sqrt(2): rows are the orthonormal basis vectors
    static readonly float[] B = BuildBasis();
    static readonly Vector256<float>[] Scalars = BuildScalars();   // Scalars[u*8 + x] = B[u, x] broadcast
    static readonly Vector256<float>[] Rows = BuildRows();         // Rows[u] = (B[u, 0], ..., B[u, 7])
    static readonly Vector256<float>[] Cols = BuildCols();         // Cols[x] = (B[0, x], ..., B[7, x])

    static float[] BuildBasis()
    {
        var b = new float[64];
        for (int u = 0; u < 8; u++) for (int x = 0; x < 8; x++) b[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) * Math.Cos((2 * x + 1) * u * Math.PI / 16) / 2.0);
        return b;
    }
    static Vector256<float>[] BuildScalars() { var v = new Vector256<float>[64]; for (int i = 0; i < 64; i++) v[i] = Vector256.Create(B[i]); return v; }
    static Vector256<float>[] BuildRows() { var v = new Vector256<float>[8]; for (int u = 0; u < 8; u++) v[u] = Vector256.Create(B[u * 8], B[u * 8 + 1], B[u * 8 + 2], B[u * 8 + 3], B[u * 8 + 4], B[u * 8 + 5], B[u * 8 + 6], B[u * 8 + 7]); return v; }
    static Vector256<float>[] BuildCols() { var v = new Vector256<float>[8]; for (int x = 0; x < 8; x++) v[x] = Vector256.Create(B[x], B[8 + x], B[16 + x], B[24 + x], B[32 + x], B[40 + x], B[48 + x], B[56 + x]); return v; }

    /// <summary>Samples (index y*8 + x) to coefficients (index v*8 + u). The spans may be the same memory.</summary>
    public static void Forward(ReadOnlySpan<float> px, Span<float> f)
    {
        if (Vector256.IsHardwareAccelerated) ForwardSimd(px, f); else ForwardScalar(px, f);
    }

    /// <summary>Coefficients (index v*8 + u) to samples (index y*8 + x). The spans may be the same memory.</summary>
    public static void Inverse(ReadOnlySpan<float> f, Span<float> px)
    {
        if (Vector256.IsHardwareAccelerated) InverseSimd(f, px); else InverseScalar(f, px);
    }

    [SkipLocalsInit]
    static void ForwardSimd(ReadOnlySpan<float> px, Span<float> f)
    {
        ref float p = ref MemoryMarshal.GetReference(px);
        ref Vector256<float> s = ref MemoryMarshal.GetArrayDataReference(Scalars);
        ref Vector256<float> cols = ref MemoryMarshal.GetArrayDataReference(Cols);
        Vector256<float> x0 = Vector256.LoadUnsafe(ref p), x1 = Vector256.LoadUnsafe(ref p, 8), x2 = Vector256.LoadUnsafe(ref p, 16), x3 = Vector256.LoadUnsafe(ref p, 24),
                         x4 = Vector256.LoadUnsafe(ref p, 32), x5 = Vector256.LoadUnsafe(ref p, 40), x6 = Vector256.LoadUnsafe(ref p, 48), x7 = Vector256.LoadUnsafe(ref p, 56);
        Span<float> t = stackalloc float[64];
        ref float tr = ref MemoryMarshal.GetReference(t);
        // vertical pass: T[v] = sum_y B[v, y] * X[y]
        for (int v = 0; v < 8; v++)
        {
            ref Vector256<float> b = ref Unsafe.Add(ref s, v * 8);
            var acc = b * x0 + Unsafe.Add(ref b, 1) * x1 + Unsafe.Add(ref b, 2) * x2 + Unsafe.Add(ref b, 3) * x3
                    + Unsafe.Add(ref b, 4) * x4 + Unsafe.Add(ref b, 5) * x5 + Unsafe.Add(ref b, 6) * x6 + Unsafe.Add(ref b, 7) * x7;
            acc.StoreUnsafe(ref tr, (nuint)(v * 8));
        }
        // horizontal pass: F[v] = sum_x T[v, x] * Cols[x]
        ref float o = ref MemoryMarshal.GetReference(f);
        for (int v = 0; v < 8; v++)
        {
            ref float tv = ref Unsafe.Add(ref tr, v * 8);
            var acc = Vector256.Create(tv) * cols + Vector256.Create(Unsafe.Add(ref tv, 1)) * Unsafe.Add(ref cols, 1) + Vector256.Create(Unsafe.Add(ref tv, 2)) * Unsafe.Add(ref cols, 2)
                    + Vector256.Create(Unsafe.Add(ref tv, 3)) * Unsafe.Add(ref cols, 3) + Vector256.Create(Unsafe.Add(ref tv, 4)) * Unsafe.Add(ref cols, 4) + Vector256.Create(Unsafe.Add(ref tv, 5)) * Unsafe.Add(ref cols, 5)
                    + Vector256.Create(Unsafe.Add(ref tv, 6)) * Unsafe.Add(ref cols, 6) + Vector256.Create(Unsafe.Add(ref tv, 7)) * Unsafe.Add(ref cols, 7);
            acc.StoreUnsafe(ref o, (nuint)(v * 8));
        }
    }

    [SkipLocalsInit]
    static void InverseSimd(ReadOnlySpan<float> f, Span<float> px)
    {
        ref float p = ref MemoryMarshal.GetReference(f);
        ref Vector256<float> s = ref MemoryMarshal.GetArrayDataReference(Scalars);
        ref Vector256<float> rows = ref MemoryMarshal.GetArrayDataReference(Rows);
        Vector256<float> f0 = Vector256.LoadUnsafe(ref p), f1 = Vector256.LoadUnsafe(ref p, 8), f2 = Vector256.LoadUnsafe(ref p, 16), f3 = Vector256.LoadUnsafe(ref p, 24),
                         f4 = Vector256.LoadUnsafe(ref p, 32), f5 = Vector256.LoadUnsafe(ref p, 40), f6 = Vector256.LoadUnsafe(ref p, 48), f7 = Vector256.LoadUnsafe(ref p, 56);
        Span<float> t = stackalloc float[64];
        ref float tr = ref MemoryMarshal.GetReference(t);
        // vertical pass: T[y] = sum_v B[v, y] * F[v]
        for (int y = 0; y < 8; y++)
        {
            var acc = Unsafe.Add(ref s, y) * f0 + Unsafe.Add(ref s, 8 + y) * f1 + Unsafe.Add(ref s, 16 + y) * f2 + Unsafe.Add(ref s, 24 + y) * f3
                    + Unsafe.Add(ref s, 32 + y) * f4 + Unsafe.Add(ref s, 40 + y) * f5 + Unsafe.Add(ref s, 48 + y) * f6 + Unsafe.Add(ref s, 56 + y) * f7;
            acc.StoreUnsafe(ref tr, (nuint)(y * 8));
        }
        // horizontal pass: X[y] = sum_u T[y, u] * Rows[u]
        ref float o = ref MemoryMarshal.GetReference(px);
        for (int y = 0; y < 8; y++)
        {
            ref float ty = ref Unsafe.Add(ref tr, y * 8);
            var acc = Vector256.Create(ty) * rows + Vector256.Create(Unsafe.Add(ref ty, 1)) * Unsafe.Add(ref rows, 1) + Vector256.Create(Unsafe.Add(ref ty, 2)) * Unsafe.Add(ref rows, 2)
                    + Vector256.Create(Unsafe.Add(ref ty, 3)) * Unsafe.Add(ref rows, 3) + Vector256.Create(Unsafe.Add(ref ty, 4)) * Unsafe.Add(ref rows, 4) + Vector256.Create(Unsafe.Add(ref ty, 5)) * Unsafe.Add(ref rows, 5)
                    + Vector256.Create(Unsafe.Add(ref ty, 6)) * Unsafe.Add(ref rows, 6) + Vector256.Create(Unsafe.Add(ref ty, 7)) * Unsafe.Add(ref rows, 7);
            acc.StoreUnsafe(ref o, (nuint)(y * 8));
        }
    }

    [SkipLocalsInit]
    static void ForwardScalar(ReadOnlySpan<float> px, Span<float> f)
    {
        Span<float> t = stackalloc float[64];
        for (int y = 0; y < 8; y++) for (int u = 0; u < 8; u++) { float sum = 0; for (int x = 0; x < 8; x++) sum += px[y * 8 + x] * B[u * 8 + x]; t[y * 8 + u] = sum; }
        for (int v = 0; v < 8; v++) for (int u = 0; u < 8; u++) { float sum = 0; for (int y = 0; y < 8; y++) sum += t[y * 8 + u] * B[v * 8 + y]; f[v * 8 + u] = sum; }
    }

    [SkipLocalsInit]
    static void InverseScalar(ReadOnlySpan<float> f, Span<float> px)
    {
        Span<float> t = stackalloc float[64];
        for (int v = 0; v < 8; v++) for (int x = 0; x < 8; x++) { float sum = 0; for (int u = 0; u < 8; u++) sum += f[v * 8 + u] * B[u * 8 + x]; t[v * 8 + x] = sum; }
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) { float sum = 0; for (int v = 0; v < 8; v++) sum += t[v * 8 + x] * B[v * 8 + y]; px[y * 8 + x] = sum; }
    }

    /// <summary>Self-check used by the benchmark tool: the largest round-trip and SIMD-versus-scalar differences on random blocks.</summary>
    public static (float roundTrip, float simdVsScalar) SelfTest(int blocks = 2000, int seed = 1)
    {
        var rnd = new Random(seed); float rt = 0, sv = 0;
        Span<float> x = stackalloc float[64], f = stackalloc float[64], f2 = stackalloc float[64], y = stackalloc float[64];
        for (int n = 0; n < blocks; n++)
        {
            for (int i = 0; i < 64; i++) x[i] = (float)(rnd.NextDouble() * 255 - 128);
            Forward(x, f); ForwardScalar(x, f2); Inverse(f, y);
            for (int i = 0; i < 64; i++) { rt = Math.Max(rt, Math.Abs(x[i] - y[i])); sv = Math.Max(sv, Math.Abs(f[i] - f2[i])); }
        }
        return (rt, sv);
    }
}
