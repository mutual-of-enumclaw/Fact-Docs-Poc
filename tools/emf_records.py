"""Count EMF and EMF+ record types in each full-page picture of a .gd template."""
import re, struct, sys, collections
import xml.etree.ElementTree as ET
sys.path.insert(0, r'C:\src\fact-poc\tools')
import emfplus

root = ET.parse(sys.argv[1]).getroot()
rtf = next(e for e in root.iter() if e.tag.rsplit('}', 1)[-1] == 'rtf').text
for n, m in enumerate(re.finditer(r'\\picwgoal(\d+)\\pichgoal(\d+).*?\\emfblip\s*([0-9a-fA-F\s]+)\}', rtf, re.S)):
    data = bytes.fromhex(re.sub(r'\s+', '', m.group(3)))
    emf = collections.Counter()
    off = 0
    while off + 8 <= len(data):
        t, size = struct.unpack_from('<II', data, off)
        if size < 8:
            break
        emf[t] += 1
        off += size
    plus = collections.Counter()
    for _, stream in emfplus.emfplus_streams(data):
        o = 0
        while o + 12 <= len(stream):
            typ, flags, size, dsize = struct.unpack_from('<HHII', stream, o)
            if size < 12:
                break
            plus[hex(typ)] += 1
            o += size
    print(f'picture {n}: EMF {dict(sorted(emf.items()))}')
    print(f'           EMF+ {dict(sorted(plus.items()))}')
