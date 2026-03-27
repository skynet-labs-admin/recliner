#!/usr/bin/env python3
"""
gen_icon.py  —  RECLINER app icon generator
-----------------------------------------------------------------
Design:  horizontal baseline  |  pivot circle  |  arm @ 225°
         gradient annular arc  (opaque-dark at arm → transparent)
Output:  Resources/recliner.ico   (16 / 32 / 48 / 64 / 128 / 256 px)
Requires: pip install Pillow
Run:     python gen_icon.py
-----------------------------------------------------------------
"""

import math
import os
from PIL import Image, ImageDraw

# ── Palette ──────────────────────────────────────────────────────────────────
BG          = (20, 20, 36, 255)       # very dark navy background
ARM         = (214, 198, 158, 255)    # warm cream-gold  (baseline / arm / circle)
ARC_ROOT    = ( 83,  72,  40, 255)    # ~3 stops darker than ARM  (arc @ arm tip)
# Arc gradient: ARC_ROOT (alpha 255) ──► transparent (alpha 0), sweeping clockwise

# ── Layout (fractions of rendered pixel size s) ──────────────────────────────
BG_PAD      = 1 / 14    # inset of rounded background square
BG_RADIUS   = 1 /  5    # corner radius
BASELINE_Y  = 0.63      # Y position of the baseline (fraction of s)
LINE_HALF   = 0.30      # half-width of baseline
LINE_W      = 0.042     # stroke width of baseline / arm
ARM_LEN     = 0.292     # arm length from pivot centre
PIVOT_R     = 0.073     # radius of pivot circle  (slightly > line half-width)
INNER_GAP   = 0.008     # gap between pivot edge and arc inner radius

# ── Arc ───────────────────────────────────────────────────────────────────────
# PIL angle convention: 0° = right (3 o'clock), increases CLOCKWISE.
# 225° = upper-left  ≈  10:30 o'clock  →  the "green / left" gauge position.
# Arc sweeps  225° → 325° (clockwise):  upper-left → top → upper-right.
ARC_START   = 225.0    # opaque end  (coincides with arm direction)
ARC_END     = 325.0    # transparent end  (trailing clockwise)

SUPERSAMPLE = 4        # internal render multiplier for anti-aliasing


# ── Core renderer ─────────────────────────────────────────────────────────────

def _draw(s: int) -> Image.Image:
    """Paint the icon on a canvas of s × s pixels (no supersampling)."""
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d   = ImageDraw.Draw(img)

    # Rounded background
    pad = max(1, int(s * BG_PAD))
    d.rounded_rectangle(
        [pad, pad, s - pad - 1, s - pad - 1],
        radius=max(2, int(s * BG_RADIUS)),
        fill=BG,
    )

    cx      = s // 2
    cy      = int(s * BASELINE_Y)
    lw      = max(1, int(s * LINE_W))          # stroke width
    alen    = int(s * ARM_LEN)                  # arm length
    pr      = max(2, int(s * PIVOT_R))          # pivot radius
    bhl     = int(s * LINE_HALF)                # baseline half-length

    arc_in  = pr + max(1, int(s * INNER_GAP))  # arc inner radius
    arc_out = alen                              # arc outer radius = arm tip

    # ── Gradient arc  (radial-line sweep) ────────────────────────────────────
    span  = ARC_END - ARC_START                # 100°
    steps = max(240, s * 6)                    # density → smooth at all sizes
    thick = max(1, s // 80)                    # line width for each radial stroke

    for i in range(steps):
        t   = i / (steps - 1)                  # 0 = arm tip (opaque), 1 = tail (gone)
        ang = ARC_START + t * span
        rad = math.radians(ang)

        # Ease-out so the fade feels natural (not linear)
        alpha = int(255 * (1.0 - t) ** 1.35)

        # Colour interpolates from ARC_ROOT (dark) toward ARM (gold) as it fades
        r = int(ARC_ROOT[0] + t * (ARM[0] - ARC_ROOT[0]))
        g = int(ARC_ROOT[1] + t * (ARM[1] - ARC_ROOT[1]))
        b = int(ARC_ROOT[2] + t * (ARM[2] - ARC_ROOT[2]))

        ca, sa = math.cos(rad), math.sin(rad)
        xi, yi = cx + arc_in  * ca, cy + arc_in  * sa
        xo, yo = cx + arc_out * ca, cy + arc_out * sa

        d.line([(xi, yi), (xo, yo)], fill=(r, g, b, alpha), width=thick)

    # ── Baseline ─────────────────────────────────────────────────────────────
    d.line([(cx - bhl, cy), (cx + bhl, cy)], fill=ARM, width=lw)

    # ── Arm  (draw after arc so it sits on top) ───────────────────────────────
    arm_rad = math.radians(ARC_START)
    ax = cx + alen * math.cos(arm_rad)
    ay = cy + alen * math.sin(arm_rad)
    d.line([(cx, cy), (ax, ay)], fill=ARM, width=lw)

    # ── Pivot circle  (draw last — sits above everything) ────────────────────
    d.ellipse([cx - pr, cy - pr, cx + pr, cy + pr], fill=ARM)

    return img


def generate_frame(size: int) -> Image.Image:
    """Render at SUPERSAMPLE× then LANCZOS-downsample for crisp edges."""
    big = _draw(size * SUPERSAMPLE)
    return big.resize((size, size), Image.LANCZOS)


# ── ICO assembly ──────────────────────────────────────────────────────────────

def build_ico(path: str):
    sizes  = [256, 128, 64, 48, 32, 16]
    frames = []
    for s in sizes:
        print(f"  rendering {s}×{s} …", end="", flush=True)
        frames.append(generate_frame(s))
        print("  done")

    frames[0].save(path, format="ICO", append_images=frames[1:])
    print(f"\n  OK  {path}")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    root    = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.join(root, "Resources")
    os.makedirs(out_dir, exist_ok=True)

    print("RECLINER icon generator")
    print("-" * 36)
    build_ico(os.path.join(out_dir, "recliner.ico"))
