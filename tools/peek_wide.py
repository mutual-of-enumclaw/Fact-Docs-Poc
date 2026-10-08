"""List CSS classes with widths/margins that exceed the flow page's content width, per template."""
import json, re, glob, os, sys

BATCH = r'C:\src\fact-poc\output\ghostdraft\batch'
for f in sorted(glob.glob(os.path.join(BATCH, (sys.argv[1] if len(sys.argv) > 1 else '') + '*.json'))):
    d = json.load(open(f, encoding='utf-8'))
    css = d['css']
    m = re.search(r'@page gdflow\{margin:([\d.]+)pt ([\d.]+)pt ([\d.]+)pt ([\d.]+)pt', css)
    if not m:
        continue
    content = 612 - float(m.group(2)) - float(m.group(4))
    wide = []
    for cls, body in re.findall(r'\.(gd-t\d+)\{([^}]*)\}', css):
        w = re.search(r'width:([\d.]+)pt', body)
        ml = re.search(r'margin-left:(-?[\d.]+)pt', body)
        total = (float(w.group(1)) if w else 0) + (float(ml.group(1)) if ml else 0)
        if total > content + 1:
            wide.append((cls, round(total, 1)))
    flows = sum(1 for c in d['components'] if 'gd-flow' in (c.get('classes') or []))
    if wide and flows:
        print(os.path.basename(f)[:60], 'content', content, wide[:6])
