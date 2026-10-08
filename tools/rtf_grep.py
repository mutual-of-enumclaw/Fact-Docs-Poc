"""Print RTF snippets around a regex in a .gd template's rtf."""
import re, sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
rtf = next(e for e in root.iter() if e.tag.rsplit('}', 1)[-1] == 'rtf').text
rtf = re.sub(r'\\(emf|png|jpeg|wmetafile)blip\s*[0-9a-fA-F\s]+', r'\\\1blip<hex>', rtf)
n = int(sys.argv[3]) if len(sys.argv) > 3 else 5
w = int(sys.argv[4]) if len(sys.argv) > 4 else 300
for i, m in enumerate(re.finditer(sys.argv[2], rtf)):
    if i >= n:
        break
    print('...', rtf[max(0, m.start() - w): m.end() + w].replace('\r', '').replace('\n', ''), '...\n')
