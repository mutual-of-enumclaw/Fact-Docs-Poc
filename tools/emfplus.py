"""emfplus -- pull positioned text and rules out of an EMF+ picture (GhostDraft's full-page ISO wording sheets).

GhostDraft stores the fixed ISO wording of a form as one EMF per page, anchored behind the document. Those EMFs hold
a single EMR_COMMENT whose payload is an EMF+ stream: DrawString records (one per line/run) with Font and Brush
objects, world transforms, and FillRects/DrawLines for rules and boxes. This module replays that stream and returns
the text and rules in POINTS relative to the picture's top-left, ready for absolute placement.

    from emfplus import extract
    sheet = extract(emf_bytes, width_pt=612, height_pt=792)
    sheet['items']  -> [{'x','y','w','h','text','size','bold','italic','underline','font','color','align'}]
    sheet['rules']  -> [{'x','y','w','h','color'}]   filled rectangles (lines are thin rectangles)
"""

from __future__ import annotations

import math
import struct

EMR_HEADER, EMR_COMMENT, EMR_EOF = 1, 70, 14

# EMF+ record types
HEADER, OBJECT, FILL_RECTS, DRAW_RECTS, DRAW_LINES, FILL_PATH, DRAW_PATH, DRAW_STRING = \
    0x4001, 0x4008, 0x400A, 0x400B, 0x400D, 0x4014, 0x4015, 0x401C
SAVE, RESTORE, BEGIN_CONTAINER_NP, END_CONTAINER = 0x4025, 0x4026, 0x4028, 0x4029
SET_WT, RESET_WT, MULT_WT, TRANS_WT, SCALE_WT, ROT_WT, SET_PAGE = 0x402A, 0x402B, 0x402C, 0x402D, 0x402E, 0x402F, 0x4030
DRAW_DRIVER_STRING = 0x4036

OBJ_BRUSH, OBJ_PEN, OBJ_PATH, OBJ_FONT, OBJ_FORMAT = 1, 2, 3, 6, 7

# GDI+ units -> inches (Display/Pixel use the header's logical DPI)
UNIT_INCH = {3: 1 / 72, 4: 1.0, 5: 1 / 300, 6: 1 / 25.4}


def mul(a, b):
    """3x2 affine matrices (m11, m12, m21, m22, dx, dy): apply a, then b."""
    return (a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
            a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
            a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5])


IDENT = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)


def apply(m, x, y):
    return x * m[0] + y * m[2] + m[4], x * m[1] + y * m[3] + m[5]


def argb(v):
    return f'#{(v >> 16) & 255:02x}{(v >> 8) & 255:02x}{v & 255:02x}'


def emfplus_streams(data: bytes):
    """Yield (emf header dict, EMF+ payload bytes) for every EMF+ comment record."""
    off = 0
    hdr = None
    while off + 8 <= len(data):
        t, size = struct.unpack_from('<II', data, off)
        if size < 8:
            break
        if t == EMR_HEADER:
            b = struct.unpack_from('<4i4i', data, off + 8)            # rclBounds, rclFrame (0.01 mm)
            dev = struct.unpack_from('<2i2i', data, off + 72)         # szlDevice (px), szlMillimeters
            hdr = {'frame': b[4:8], 'dev': dev[0:2], 'mm': dev[2:4]}
        elif t == EMR_COMMENT:
            cb = struct.unpack_from('<I', data, off + 8)[0]
            payload = data[off + 12: off + 12 + cb]
            if payload[:4] == b'EMF+':
                yield hdr, payload[4:]
        off += size


def read_path(d):
    """EmfPlusPath -> list of subpaths, each a list of (x, y) in world units. Beziers keep their control points,
    which only matters for the bounding box of a curved path (none seen in the ISO sheets)."""
    if len(d) < 12:
        return []
    _, count, pflags = struct.unpack_from('<III', d, 0)
    if pflags & 0x0800:                                   # relative (EmfPlusPointR) points: not produced by GhostDraft
        return []
    o = 12
    if pflags & 0x4000:
        pts = [struct.unpack_from('<2h', d, o + 4 * i) for i in range(count)]
        o += 4 * count
    else:
        pts = [struct.unpack_from('<2f', d, o + 8 * i) for i in range(count)]
        o += 8 * count
    if pflags & 0x1000:                                   # run-length encoded point types
        types = []
        while len(types) < count and o + 1 < len(d):
            head, t = d[o], d[o + 1]
            types.extend([t] * (head & 0x3F))
            o += 2
    else:
        types = list(d[o:o + count])
    subs, cur = [], []
    for (x, y), t in zip(pts, types):
        if (t & 0x07) == 0 and cur:
            subs.append(cur)
            cur = []
        cur.append((float(x), float(y)))
        if t & 0x80:
            if cur[-1] != cur[0]:
                cur.append(cur[0])            # closed subpath: the closing edge is drawn too
            subs.append(cur)
            cur = []
    if cur:
        subs.append(cur)
    return subs


def extract(data: bytes, width_pt: float, height_pt: float) -> dict:
    items, rules = [], []
    fonts, brushes, formats, paths, pens = {}, {}, {}, {}, {}
    world, page_scale, page_unit = IDENT, 1.0, 2
    dpi = (96.0, 96.0)
    saved = {}
    hdr = None
    for hdr, stream in emfplus_streams(data):
        o = 0
        while o + 12 <= len(stream):
            typ, flags, size, dsize = struct.unpack_from('<HHII', stream, o)
            if size < 12:
                break
            d = stream[o + 12: o + 12 + dsize]
            o += size
            if typ == HEADER and len(d) >= 16:
                _, _, dx, dy = struct.unpack_from('<IIII', d, 0)
                dpi = (float(dx or 96), float(dy or 96))
            elif typ == OBJECT:
                oid, otype = flags & 0xFF, (flags >> 8) & 0x7F
                if otype == OBJ_FONT and len(d) >= 24:
                    _, em, unit, style, _, n = struct.unpack_from('<IfIiII', d, 0)
                    fonts[oid] = {'em': em, 'unit': unit, 'bold': bool(style & 1), 'italic': bool(style & 2),
                                  'underline': bool(style & 4), 'strike': bool(style & 8),
                                  'family': d[24:24 + 2 * n].decode('utf-16le', 'replace')}
                elif otype == OBJ_BRUSH and len(d) >= 12:
                    _, btype, color = struct.unpack_from('<III', d, 0)
                    brushes[oid] = argb(color) if btype == 0 else '#000000'
                elif otype == OBJ_FORMAT and len(d) >= 16:
                    _, sflags, _, align = struct.unpack_from('<IIII', d, 0)
                    formats[oid] = {'align': align, 'flags': sflags}
                elif otype == OBJ_PATH:
                    paths[oid] = read_path(d)
                elif otype == OBJ_PEN and len(d) >= 20:
                    # version, type, then EmfPlusPenData (flags, unit, width, optional data), then the brush
                    _, _, pflags, unit, width = struct.unpack_from('<IIIIf', d, 0)
                    o2 = 20 + _pen_optional_size(d, 20, pflags)
                    color = '#000000'
                    if len(d) >= o2 + 12:
                        _, btype, c = struct.unpack_from('<III', d, o2)
                        color = argb(c) if btype == 0 else color
                    pens[oid] = {'width': width, 'unit': unit, 'color': color}
            elif typ == SET_WT:
                world = struct.unpack_from('<6f', d, 0)
            elif typ == RESET_WT:
                world = IDENT
            elif typ in (MULT_WT, TRANS_WT, SCALE_WT, ROT_WT):
                if typ == MULT_WT:
                    m = struct.unpack_from('<6f', d, 0)
                elif typ == TRANS_WT:
                    tx, ty = struct.unpack_from('<2f', d, 0)
                    m = (1.0, 0.0, 0.0, 1.0, tx, ty)
                elif typ == SCALE_WT:
                    sx, sy = struct.unpack_from('<2f', d, 0)
                    m = (sx, 0.0, 0.0, sy, 0.0, 0.0)
                else:
                    a = math.radians(struct.unpack_from('<f', d, 0)[0])
                    m = (math.cos(a), math.sin(a), -math.sin(a), math.cos(a), 0.0, 0.0)
                world = mul(world, m) if flags & 0x2000 else mul(m, world)
            elif typ == SET_PAGE:
                page_unit, page_scale = flags & 0xFF, struct.unpack_from('<f', d, 0)[0]
            elif typ in (SAVE, BEGIN_CONTAINER_NP):
                saved[struct.unpack_from('<I', d, 0)[0]] = (world, page_scale, page_unit)
            elif typ in (RESTORE, END_CONTAINER):
                world, page_scale, page_unit = saved.get(struct.unpack_from('<I', d, 0)[0],
                                                         (world, page_scale, page_unit))
            elif typ == DRAW_STRING and len(d) >= 28:
                brush, fmt, n = struct.unpack_from('<III', d, 0)
                rx, ry, rw, rh = struct.unpack_from('<4f', d, 12)
                text = d[28:28 + 2 * n].decode('utf-16le', 'replace')
                font = fonts.get(flags & 0xFF, {'em': 10, 'unit': 3, 'bold': False, 'italic': False,
                                                'underline': False, 'strike': False, 'family': 'Arial'})
                color = argb(brush) if flags & 0x8000 else brushes.get(brush, '#000000')
                m = device_matrix(world, page_unit, page_scale, dpi)
                x0, y0 = apply(m, rx, ry)
                x1, y1 = apply(m, rx + rw, ry + rh)
                scale = math.sqrt(abs(m[0] * m[3] - m[1] * m[2]))
                em_px = font['em'] * scale * (1.0 if font['unit'] == 0 else unit_px(font['unit'], dpi[1]))
                items.append({'x': x0, 'y': y0, 'w': x1 - x0, 'h': y1 - y0, 'text': text, 'size': em_px,
                              'bold': font['bold'], 'italic': font['italic'], 'underline': font['underline'],
                              'strike': font['strike'], 'font': font['family'], 'color': color,
                              'align': formats.get(fmt, {}).get('align', 0)})
            elif typ in (FILL_PATH, DRAW_PATH, DRAW_LINES, DRAW_RECTS):
                m = device_matrix(world, page_unit, page_scale, dpi)
                if typ == FILL_PATH:
                    brush = struct.unpack_from('<I', d, 0)[0] if len(d) >= 4 else 0
                    color = argb(brush) if flags & 0x8000 else brushes.get(brush, '#000000')
                    subs, pen = paths.get(flags & 0xFF, []), None
                else:
                    pen = pens.get(struct.unpack_from('<I', d, 0)[0] if typ == DRAW_PATH else flags & 0xFF,
                                   {'width': 1.0, 'unit': 2, 'color': '#000000'})
                    color = pen['color']
                    if typ == DRAW_PATH:
                        subs = paths.get(flags & 0xFF, [])
                    else:
                        subs = _points_record(d, typ, flags)
                for sub in subs:
                    pts = [apply(m, x, y) for x, y in sub]
                    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
                    x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
                    if pen is None:
                        rules.append({'x': x0, 'y': y0, 'w': x1 - x0, 'h': y1 - y0, 'color': color})
                        continue
                    # stroked: one rule per axis-aligned segment, pen width in device px
                    scale = math.sqrt(abs(m[0] * m[3] - m[1] * m[2]))
                    pw = max(pen['width'] * scale * (1.0 if pen['unit'] == 0 else
                                                     unit_px(pen['unit'], dpi[1]) if pen['unit'] in UNIT_INCH else 1.0), 0.5)
                    segs = list(zip(pts, pts[1:]))
                    for (ax, ay), (bx, by) in segs:
                        if abs(ay - by) < 0.01:
                            rules.append({'x': min(ax, bx) - pw / 2, 'y': ay - pw / 2, 'w': abs(bx - ax) + pw, 'h': pw,
                                          'color': color})
                        elif abs(ax - bx) < 0.01:
                            rules.append({'x': ax - pw / 2, 'y': min(ay, by) - pw / 2, 'w': pw, 'h': abs(by - ay) + pw,
                                          'color': color})
            elif typ == FILL_RECTS and len(d) >= 8:
                brush, count = struct.unpack_from('<II', d, 0)
                color = argb(brush) if flags & 0x8000 else brushes.get(brush, '#000000')
                m = device_matrix(world, page_unit, page_scale, dpi)
                compressed = bool(flags & 0x4000)
                for i in range(count):
                    if compressed:
                        rx, ry, rw, rh = struct.unpack_from('<4h', d, 8 + 8 * i)
                    else:
                        rx, ry, rw, rh = struct.unpack_from('<4f', d, 8 + 16 * i)
                    x0, y0 = apply(m, rx, ry)
                    x1, y1 = apply(m, rx + rw, ry + rh)
                    rules.append({'x': min(x0, x1), 'y': min(y0, y1), 'w': abs(x1 - x0), 'h': abs(y1 - y0),
                                  'color': color})
    # device px -> points relative to the picture: the EMF frame (0.01 mm) is the picture's extent
    if hdr is None:
        return {'items': [], 'rules': []}
    fl, ft, fr, fb = hdr['frame']
    px_mm_x = hdr['mm'][0] / hdr['dev'][0] if hdr['dev'][0] else 25.4 / 96
    px_mm_y = hdr['mm'][1] / hdr['dev'][1] if hdr['dev'][1] else 25.4 / 96
    frame_w_mm, frame_h_mm = (fr - fl) / 100 or 215.9, (fb - ft) / 100 or 279.4
    sx = width_pt / frame_w_mm
    sy = height_pt / frame_h_mm

    def to_pt(x, y):
        return (x * px_mm_x - fl / 100) * sx, (y * px_mm_y - ft / 100) * sy

    for it in items:
        it['x'], it['y'] = to_pt(it['x'], it['y'])
        it['w'], it['h'] = it['w'] * px_mm_x * sx, it['h'] * px_mm_y * sy
        it['size'] = it['size'] * px_mm_y * sy
    for r in rules:
        r['x'], r['y'] = to_pt(r['x'], r['y'])
        r['w'], r['h'] = r['w'] * px_mm_x * sx, r['h'] * px_mm_y * sy
    return {'items': items, 'rules': rules}


def _pen_optional_size(d, o, pflags):
    """Byte size of EmfPlusPenOptionalData for the flags that GDI+ writes (transform, caps, join, miter, style,
    dash cap/offset, dashes, alignment, compound); custom caps are skipped by their own size field."""
    start = o
    try:
        if pflags & 0x01: o += 24             # transform
        for bit in (0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80):   # start cap, end cap, join, miter, style, dash cap, dash offset
            if pflags & bit: o += 4
        if pflags & 0x100:                    # dashed line: count + floats
            n = struct.unpack_from('<I', d, o)[0]; o += 4 + 4 * n
        if pflags & 0x200: o += 4             # alignment
        if pflags & 0x400:                    # compound line
            n = struct.unpack_from('<I', d, o)[0]; o += 4 + 4 * n
        for bit in (0x800, 0x1000):           # custom start / end cap
            if pflags & bit:
                n = struct.unpack_from('<I', d, o)[0]; o += 4 + n
    except struct.error:
        pass
    return o - start


def _points_record(d, typ, flags):
    """DrawLines / DrawRects payload -> subpaths (world units)."""
    compressed = bool(flags & 0x4000)
    count = struct.unpack_from('<I', d, 0)[0] if len(d) >= 4 else 0
    if typ == DRAW_RECTS:
        out = []
        for i in range(count):
            x, y, w, h = struct.unpack_from('<4h' if compressed else '<4f', d, 4 + (8 if compressed else 16) * i)
            out.append([(x, y), (x + w, y), (x + w, y + h), (x, y + h), (x, y)])
        return out
    pts = [struct.unpack_from('<2h' if compressed else '<2f', d, 4 + (4 if compressed else 8) * i) for i in range(count)]
    pts = [(float(x), float(y)) for x, y in pts]
    if flags & 0x2000 and pts:                # closed polygon
        pts.append(pts[0])
    return [pts]


def unit_px(unit, dpi):
    if unit in UNIT_INCH:
        return UNIT_INCH[unit] * dpi
    return 1.0


def device_matrix(world, page_unit, page_scale, dpi):
    """World -> device pixels: world transform, then the page transform (unit * scale)."""
    k = page_scale * (UNIT_INCH[page_unit] * dpi[0] if page_unit in UNIT_INCH else 1.0)
    return mul(world, (k, 0.0, 0.0, k, 0.0, 0.0))


if __name__ == '__main__':
    import re
    import sys
    import xml.etree.ElementTree as ET
    root = ET.parse(sys.argv[1]).getroot()
    rtf = next(e for e in root.iter() if e.tag.rsplit('}', 1)[-1] == 'rtf').text
    for n, m in enumerate(re.finditer(r'\\picwgoal(\d+)\\pichgoal(\d+).*?\\emfblip\s*([0-9a-fA-F\s]+)\}', rtf, re.S)):
        sheet = extract(bytes.fromhex(re.sub(r'\s+', '', m.group(3))), int(m.group(1)) / 20, int(m.group(2)) / 20)
        print(f'--- picture {n}: {len(sheet["items"])} strings, {len(sheet["rules"])} rects')
        for it in sheet['items'][:int(sys.argv[2]) if len(sys.argv) > 2 else 40]:
            flags = ('B' if it['bold'] else '') + ('I' if it['italic'] else '') + ('U' if it['underline'] else '')
            print(f"  {it['x']:6.1f},{it['y']:6.1f} w{it['w']:6.1f} {it['size']:4.1f}pt {flags:3} {it['font'][:10]:10} {it['text']!r}")
        for r in sheet['rules'][:8]:
            print(f"  rect {r['x']:6.1f},{r['y']:6.1f} {r['w']:6.1f}x{r['h']:5.1f} {r['color']}")
