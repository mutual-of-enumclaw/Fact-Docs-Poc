"""Decode a Documaker `.LOG` raster image to PNG.

Format, worked out from the files themselves:

    header line   " rows,cols,bytesPerRow,dpi,bpp,0,..."   (fields are zero-padded)
    data lines    uppercase hex, one row split across several lines, every line except
                  the last of a row ending in a backslash continuation marker

`bytesPerRow` is the row STRIDE and is padded -- it runs one byte beyond
ceil(cols*bpp/8) on many files -- so it is used as given rather than recomputed.
24bpp is BGR in file order. The final header field is a PALETTE SIZE: when it is
non-zero, that many "r,g,b" lines follow the header before the pixel data, and the
8bpp samples are indices into it.

Usage:  python tools/logdecode.py <file.LOG> [out.png]
        python tools/logdecode.py --scan          (report every .LOG on disk)
"""
import pathlib
import struct
import sys
import zlib

SEARCH = [
    pathlib.Path(r"C:\src\FaCT-DocProd-Development\mstrres\AGCYLNK\FORMS"),
    pathlib.Path(r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS"),
]


def decode(path):
    """-> (width, height, dpi, bpp, rgb_bytes) or raises ValueError."""
    raw = pathlib.Path(path).read_bytes()
    nl = b"\r\n" if b"\r\n" in raw else b"\n"
    head, _, rest = raw.partition(nl)
    f = [x.strip().strip('"').strip() for x in head.decode("ascii", "replace").split(",")]
    rows, cols, bpr, dpi, bpp = (int(f[i]) for i in range(5))
    need = -(-cols * bpp // 8)                    # ceil
    if bpr < need:
        raise ValueError(f"bytesPerRow {bpr} < required {need}")

    # A non-zero final field is a palette size: that many "r,g,b" lines follow.
    palette = None
    try:
        npal = int(f[-1])
    except ValueError:
        npal = 0
    if npal:
        pal_lines = rest.split(nl)[:npal]
        palette = []
        for pl in pal_lines:
            parts = pl.decode("ascii", "replace").split(",")
            palette.append(tuple(int(x) for x in parts[:3]))
        rest = nl.join(rest.split(nl)[npal:])

    # Strip continuation markers and line breaks; what remains is one hex stream.
    hexstream = rest.replace(b"\\" + nl, b"").replace(nl, b"")
    data = bytes.fromhex(hexstream.decode("ascii", "replace"))
    want = rows * bpr
    if len(data) < want:
        raise ValueError(f"short data: {len(data)} < {want}")

    if bpp == 24:
        rgb = bytearray(rows * cols * 3)
        for y in range(rows):
            src = y * bpr
            for x in range(cols):
                b, g, r = data[src + x * 3: src + x * 3 + 3]
                d = (y * cols + x) * 3
                rgb[d], rgb[d + 1], rgb[d + 2] = r, g, b
    elif bpp == 8:
        rgb = bytearray(rows * cols * 3)
        for y in range(rows):
            src = y * bpr
            for x in range(cols):
                v = data[src + x]
                d = (y * cols + x) * 3
                if palette and v < len(palette):
                    rgb[d], rgb[d + 1], rgb[d + 2] = palette[v]
                else:
                    rgb[d] = rgb[d + 1] = rgb[d + 2] = v
    elif bpp == 1:
        rgb = bytearray(rows * cols * 3)
        for y in range(rows):
            src = y * bpr
            for x in range(cols):
                bit = (data[src + (x >> 3)] >> (7 - (x & 7))) & 1
                # 1bpp is INK-set, not luminance: a set bit is black. Taking it the
                # other way renders a signature as white-on-black.
                v = 0 if bit else 255
                d = (y * cols + x) * 3
                rgb[d] = rgb[d + 1] = rgb[d + 2] = v
    else:
        raise ValueError(f"unsupported bpp {bpp}")
    return cols, rows, dpi, bpp, bytes(rgb)


def write_png(path, w, h, rgb):
    def chunk(tag, payload):
        c = struct.pack(">I", len(payload)) + tag + payload
        return c + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)

    scan = bytearray()
    for y in range(h):
        scan.append(0)                       # filter: none
        scan += rgb[y * w * 3:(y + 1) * w * 3]
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(bytes(scan), 6))
           + chunk(b"IEND", b""))
    pathlib.Path(path).write_bytes(png)


def scan():
    seen, ok, bad = {}, 0, []
    for d in SEARCH:
        for f in sorted(d.glob("*.LOG")):
            if f.name.upper() in seen:
                continue
            seen[f.name.upper()] = f
    for name, f in sorted(seen.items()):
        try:
            w, h, dpi, bpp, _ = decode(f)
            ok += 1
            print(f"  {name:<28}{w:>5} x {h:<5} {dpi:>4}dpi {bpp:>3}bpp")
        except Exception as exc:
            bad.append((name, str(exc)[:60]))
    print(f"\n{ok}/{len(seen)} decoded")
    for n, e in bad:
        print(f"  FAILED {n}: {e}")
    return len(bad)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--scan":
        sys.exit(1 if scan() else 0)
    src = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else str(pathlib.Path(src).with_suffix(".png"))
    w, h, dpi, bpp, rgb = decode(src)
    write_png(out, w, h, rgb)
    print(f"{src}: {w}x{h} {dpi}dpi {bpp}bpp -> {out}")
