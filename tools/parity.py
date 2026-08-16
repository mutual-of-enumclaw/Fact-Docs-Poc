"""Parity harness spike: rasterize two PDFs and score how closely they match.

ref = Documaker (FAP2PDF/GENDAW32) render.  ours = Form Studio HTML -> Chromium.
Emits per-page ink-diff stats plus an overlay PNG (red = legacy only, green = ours only).
"""
import sys
import fitz
import numpy as np

DPI = 150
INK = 200  # <= this grayscale value counts as ink


def raster(path, page, dpi=DPI):
    doc = fitz.open(path)
    if page >= doc.page_count:
        return None, None
    pm = doc[page].get_pixmap(dpi=dpi, colorspace=fitz.csGRAY)
    a = np.frombuffer(pm.samples, dtype=np.uint8).reshape(pm.height, pm.width)
    return a, (doc[page].rect.width, doc[page].rect.height)


def pad_to(a, h, w):
    out = np.full((h, w), 255, dtype=np.uint8)
    out[:a.shape[0], :a.shape[1]] = a[:h, :w]
    return out


def dilate(b, r=1):
    """Binary dilation by an (2r+1) square, numpy-only."""
    out = b.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out |= np.roll(np.roll(b, dy, axis=0), dx, axis=1)
    return out


def best_shift(rb, ob, rng=10):
    """Whole-page (dy,dx) that maximizes ink overlap — detects systematic offset.

    rng must exceed the largest plausible offset: the default was 3, which silently
    CLAMPED and made a 6.7px error read as "dy=3", understating it by half.
    """
    best, bxy = -1, (0, 0)
    for dy in range(-rng, rng + 1):
        for dx in range(-rng, rng + 1):
            s = np.logical_and(rb, np.roll(np.roll(ob, dy, axis=0), dx, axis=1)).sum()
            if s > best:
                best, bxy = s, (dy, dx)
    return bxy


def main(ref_path, our_path, out_prefix):
    nref, nour = fitz.open(ref_path).page_count, fitz.open(our_path).page_count
    print(f"pages: legacy={nref} ours={nour}")
    print(f"{'pg':>3} {'size legacy':>16} {'size ours':>16} {'ink L':>8} {'ink O':>8} "
          f"{'diff%':>7} {'IoU':>6}")

    scores = []
    for p in range(max(nref, nour)):
        ra, rsz = raster(ref_path, p)
        oa, osz = raster(our_path, p)
        if ra is None or oa is None:
            print(f"{p+1:>3} {'MISSING PAGE':>16}")
            scores.append(0.0)
            continue

        h = max(ra.shape[0], oa.shape[0])
        w = max(ra.shape[1], oa.shape[1])
        ra, oa = pad_to(ra, h, w), pad_to(oa, h, w)

        rb, ob = ra <= INK, oa <= INK
        inter = np.logical_and(rb, ob).sum()
        union = np.logical_or(rb, ob).sum()
        diff = np.logical_xor(rb, ob).sum()
        iou = inter / union if union else 1.0
        scores.append(iou)

        print(f"{p+1:>3} {rsz[0]:7.1f}x{rsz[1]:<8.1f} {osz[0]:7.1f}x{osz[1]:<8.1f} "
              f"{rb.sum():>8} {ob.sum():>8} {100*diff/(h*w):>6.2f}% {iou:>6.3f}")

        # Tolerance view: how much survives a 1px allowance (anti-aliasing / hinting)?
        rd, od = dilate(rb), dilate(ob)
        our_unmatched = np.logical_and(ob, ~rd)   # ink we draw that legacy has nowhere near
        leg_unmatched = np.logical_and(rb, ~od)   # ink legacy draws that we miss
        dy, dx = best_shift(rb, ob)
        print(f"      1px-tolerance: ours-unmatched {100*our_unmatched.sum()/max(1,ob.sum()):5.2f}%  "
              f"legacy-unmatched {100*leg_unmatched.sum()/max(1,rb.sum()):5.2f}%  "
              f"best whole-page shift dy={dy} dx={dx}")

        pmu = fitz.Pixmap(fitz.csGRAY, w, h,
                          np.where(np.logical_or(our_unmatched, leg_unmatched), 0, 255)
                          .astype(np.uint8).tobytes(), False)
        pmu.save(f"{out_prefix}_p{p+1}_unmatched.png")

        rgb = np.full((h, w, 3), 255, dtype=np.uint8)
        rgb[np.logical_and(rb, ob)] = (30, 30, 30)      # agreement -> black
        rgb[np.logical_and(rb, ~ob)] = (220, 40, 40)    # legacy only -> red
        rgb[np.logical_and(~rb, ob)] = (30, 170, 60)    # ours only  -> green
        pm = fitz.Pixmap(fitz.csRGB, w, h, rgb.tobytes(), False)
        pm.save(f"{out_prefix}_p{p+1}_overlay.png")

        for tag, arr in (("legacy", ra), ("ours", oa)):
            pmx = fitz.Pixmap(fitz.csGRAY, w, h, arr.tobytes(), False)
            pmx.save(f"{out_prefix}_p{p+1}_{tag}.png")

    print(f"\nmean IoU: {sum(scores)/len(scores):.3f}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
