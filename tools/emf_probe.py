"""emf_probe -- list EMF record types and any text records inside a .gd's \\emfblip pictures (debug)."""
import re
import struct
import sys
import xml.etree.ElementTree as ET
from collections import Counter

path = sys.argv[1]
root = ET.parse(path).getroot()
rtf = next(e for e in root.iter() if e.tag.rsplit('}', 1)[-1] == 'rtf').text
for n, m in enumerate(re.finditer(r'\\emfblip\s*([0-9a-fA-F\s]+)\}', rtf)):
    data = bytes.fromhex(re.sub(r'\s+', '', m.group(1)))
    types = Counter()
    texts = []
    off = 0
    while off + 8 <= len(data):
        t, size = struct.unpack_from('<II', data, off)
        if size < 8:
            break
        types[t] += 1
        if t == 84:  # EMR_EXTTEXTOUTW
            # header(8) bounds(16) iGraphicsMode(4) exScale(4) eyScale(4) EMRTEXT: ptlReference(8) nChars(4) offString(4)
            x, y, nchars, offs = struct.unpack_from('<iiII', data, off + 36)
            s = data[off + offs: off + offs + nchars * 2].decode('utf-16le', 'replace')
            texts.append((x, y, s))
        off += size
    print(f'picture {n}: {len(data)} bytes, records={sum(types.values())}')
    print('  types:', dict(types.most_common(12)))
    print(f'  text records: {len(texts)}')
    for x, y, s in texts[:25]:
        print(f'   ({x},{y}) {s!r}')
    off = struct.unpack_from('<I', data, 4)[0]
    while off + 12 <= len(data):
        t, size = struct.unpack_from('<II', data, off)
        if t == 70:
            cb = struct.unpack_from('<I', data, off + 8)[0]
            c = data[off + 12: off + 12 + cb]
            print('  comment', cb, 'bytes, starts', c[:24])
            if c[:4] == b'EMF+':
                o, et = 4, Counter()
                while o + 12 <= len(c):
                    typ, flags, sz, dsz = struct.unpack_from('<HHII', c, o)
                    if sz < 12:
                        break
                    et[hex(typ)] += 1
                    o += sz
                print('  EMF+ records:', dict(et))
        off += max(size, 8)
