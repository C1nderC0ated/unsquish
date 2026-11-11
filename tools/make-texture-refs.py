"""Synthetic references with natural-image statistics, for the texture-preservation half of the benchmark.

The Windows wallpapers used as photo references are mostly smooth renders; a restorer tuned on them alone would
learn to over-smooth. These targets fill the gap without downloading anything:
  dead leaves   occluding disks with a scale-invariant size distribution (the model behind the ISO texture chart)
  fractal noise 1/f^a fields (clouds, fabric-like texture)
  film grain    a smooth render with grain on top (grain must survive, block edges must not)
Needs numpy and opencv-python. Usage: python make-texture-refs.py <out_dir> [palette_image]
"""
import sys, os
import numpy as np
import cv2

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
rng = np.random.default_rng(20261110)
W, H = 960, 600


def palette(n):
    """Colours sampled from a real picture when one is given, so the colour statistics are plausible."""
    if len(sys.argv) > 2 and os.path.exists(sys.argv[2]):
        img = cv2.imread(sys.argv[2], cv2.IMREAD_COLOR)
        pix = img.reshape(-1, 3)
        return pix[rng.integers(0, len(pix), n)].astype(np.float32)
    return rng.uniform(20, 235, (n, 3)).astype(np.float32)


def fractal(shape, alpha, seed):
    """Real-valued noise with amplitude spectrum 1/f^alpha, zero mean, unit variance."""
    r = np.random.default_rng(seed)
    h, w = shape
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.rfftfreq(w)[None, :]
    f = np.sqrt(fx * fx + fy * fy)
    f[0, 0] = 1.0
    spec = (r.normal(size=(h, w // 2 + 1)) + 1j * r.normal(size=(h, w // 2 + 1))) / f ** alpha
    spec[0, 0] = 0
    x = np.fft.irfft2(spec, s=(h, w))
    return ((x - x.mean()) / x.std()).astype(np.float32)


def dead_leaves(n, rmin, rmax, textured, seed):
    r = np.random.default_rng(seed)
    img = np.zeros((H, W, 3), np.float32)
    img[:] = palette(1)[0]
    # radius density proportional to r^-3 between rmin and rmax (scale invariance)
    u = r.uniform(0, 1, n)
    rad = 1.0 / np.sqrt((1 - u) / rmin ** 2 + u / rmax ** 2)
    cols = palette(n)
    xs = r.uniform(-rmax, W + rmax, n)
    ys = r.uniform(-rmax, H + rmax, n)
    tex = fractal((H, W), 1.0, seed + 1) if textured else None
    for i in np.argsort(-rad):  # big leaves first, small ones on top
        cx, cy, rr = int(xs[i]), int(ys[i]), max(1, int(rad[i]))
        x0, x1, y0, y1 = max(0, cx - rr - 2), min(W, cx + rr + 3), max(0, cy - rr - 2), min(H, cy + rr + 3)
        if x0 >= x1 or y0 >= y1:
            continue
        mask = np.zeros((y1 - y0, x1 - x0), np.uint8)   # only the leaf's bounding box is touched
        cv2.circle(mask, (cx - x0, cy - y0), rr, 255, -1, cv2.LINE_AA)
        m = (mask.astype(np.float32) / 255.0)[..., None]
        c = cols[i][None, None, :].copy()
        if textured:
            gain = 1.0 + 0.18 * tex[y0:y1, x0:x1, None] * r.uniform(0.3, 1.0)
            c = np.clip(c * gain, 0, 255)
        img[y0:y1, x0:x1] = img[y0:y1, x0:x1] * (1 - m) + c * m
    img = cv2.GaussianBlur(img, (0, 0), 0.7)          # optics
    img += r.normal(0, 1.2, img.shape)                # sensor noise
    return np.clip(img, 0, 255)


def save(name, img):
    cv2.imwrite(os.path.join(out, name), np.clip(np.rint(img), 0, 255).astype(np.uint8))


save("tx_leaves.png", dead_leaves(6000, 2.0, 220.0, False, 1))
save("tx_leaves_fine.png", dead_leaves(30000, 1.2, 60.0, True, 2))

# fractal clouds, three decorrelated channels blended towards a common luminance
lum = fractal((H, W), 1.3, 10)
chans = [0.75 * lum + 0.25 * fractal((H, W), 1.3, 11 + k) for k in range(3)]
clouds = np.stack(chans, -1) * 42 + np.array([150, 135, 120], np.float32)
save("tx_clouds.png", clouds)

# fabric / foliage-like fine texture: mid frequencies, strong local contrast, green-brown tones
fine = fractal((H, W), 0.6, 20)
shade = fractal((H, W), 1.6, 21)
base = np.array([60, 110, 70], np.float32)[None, None, :] + shade[..., None] * np.array([18, 30, 22], np.float32)
foliage = base * (1.0 + 0.35 * np.tanh(1.5 * fine))[..., None]
save("tx_foliage.png", foliage)

# skin-like: smooth shading, low-contrast pores, a few thin dark strands
g = np.linspace(0, 1, W, dtype=np.float32)[None, :] * np.ones((H, 1), np.float32)
skin = np.stack([150 + 40 * g, 170 + 35 * g, 215 + 25 * g], -1) + (fractal((H, W), 1.8, 30) * 9)[..., None]
skin += (fractal((H, W), 0.3, 31) * 2.2)[..., None]
strands = np.zeros((H, W), np.uint8)
r2 = np.random.default_rng(32)
for _ in range(40):
    pts = np.cumsum(r2.normal(0, 18, (12, 2)), 0) + r2.uniform((0, 0), (W, H))
    cv2.polylines(strands, [pts.astype(np.int32)], False, 255, 1, cv2.LINE_AA)
skin = skin * (1 - 0.6 * (strands.astype(np.float32) / 255.0)[..., None])
save("tx_skin.png", skin)

# film grain over a smooth render (second argument), if given
if len(sys.argv) > 2 and os.path.exists(sys.argv[2]):
    render = cv2.resize(cv2.imread(sys.argv[2], cv2.IMREAD_COLOR), (W, H), interpolation=cv2.INTER_AREA).astype(np.float32)
    grain = cv2.GaussianBlur(np.random.default_rng(40).normal(0, 4.0, (H, W)).astype(np.float32), (0, 0), 0.6)
    save("tx_grain.png", render + grain[..., None])
print("textures written to", out)
