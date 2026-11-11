// Which engine settings a class of picture gets at a restoration level. The numbers come from the benchmark
// (tools\bench): each was chosen on a tuning subset and confirmed on the full reference sets (see the README).
namespace Unsquish;

public static class Presets
{
    /// <summary>cls: "art" for flat color and line work, anything else is treated as a photograph. level 0-6.</summary>
    public static (RestoreParams p, string label) For(string cls, int level)
    {
        level = Math.Clamp(level, 0, 6);
        if (level <= 1)
            return (new RestoreParams { PocsIterations = 2, Diagonals = false }, "light edge-aware smoothing");
        if (level == 2)
            return (new RestoreParams { Shifts = 16, PocsIterations = 1, Shrink = 0.3f }, "DCT denoising over 16 block shifts");
        if (cls == "art")
        {
            // drawings: denoise, then smooth what is flat; more iterations flatten flat color further. The guard and
            // the pull towards the denoised picture (fidelity) are set so that a photograph that lands here by mistake
            // still comes out above a plain decode in PSNR and in SSIM.
            int it = level == 3 ? 30 : (level == 4 ? 60 : (level == 5 ? 100 : 150));
            return (new RestoreParams { Shifts = 64, Shrink = 0.3f, Iterations = it, Fidelity = 1f },
                    $"DCT denoising over 64 block shifts + guarded smoothing ({it} iterations)");
        }
        if (level == 3)
            return (new RestoreParams { Shifts = 64, PocsIterations = 1, Shrink = 0.3f }, "DCT denoising over 64 block shifts");
        // photographs: luma keeps its texture, only chroma is smoothed, guided by the luma edges
        int ci = level == 4 ? 30 : (level == 5 ? 60 : 100);
        return (new RestoreParams { Shifts = 64, PocsIterations = 1, Shrink = 0.3f, Iterations = ci, ChromaOnly = true },
                $"DCT denoising over 64 block shifts + luma-guided chroma ({ci} iterations)");
    }

    /// <summary>A plain decode: the centre of every bin, no restoration.</summary>
    public static RestoreParams Plain => new();
}
