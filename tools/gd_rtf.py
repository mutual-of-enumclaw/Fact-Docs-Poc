"""List rtf-ish elements in a .gd and search for a regex in each. usage: gd_rtf.py GD REGEX [N] [W]"""
import re, sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
n = int(sys.argv[3]) if len(sys.argv) > 3 else 3
w = int(sys.argv[4]) if len(sys.argv) > 4 else 300
for e in root.iter():
    tag = e.tag.rsplit('}', 1)[-1]
    txt = e.text or ''
    if '{\\rtf' not in txt[:50]:
        continue
    txt = re.sub(r'\\(emf|png|jpeg|wmetafile)blip\s*[0-9a-fA-F\s]+', r'\\\1blip<hex>', txt)
    print(f'## <{tag}> len={len(txt)}')
    for i, m in enumerate(re.finditer(sys.argv[2], txt)):
        if i >= n:
            break
        print('...', txt[max(0, m.start() - w): m.end() + w].replace('\r', '').replace('\n', ''), '...\n')
