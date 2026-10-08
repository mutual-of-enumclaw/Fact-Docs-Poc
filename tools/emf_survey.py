"""emf_survey -- how many inventory forms carry EMF pictures / full-page EMF wording (debug)."""
import csv
import glob
import os
import re

rows = list(csv.DictReader(open(r'C:\src\fact-poc\ghostdraft-forms.csv', encoding='utf-8-sig')))
idx = {}
for pk in ('commercial-auto-2607', 'proprietary-ca-2607'):
    for f in glob.glob(os.path.join(r'C:\src\fact-pdf-tools\output\iso-packages', pk, 'Templates', '*.gd')):
        idx[os.path.splitext(os.path.basename(f))[0].lower()] = f
tot = img = full = 0
for r in rows:
    f = idx.get(r['GhostDraftForm'].lower())
    if not f:
        continue
    s = open(f, encoding='utf-8', errors='replace').read()
    n = len(re.findall(r'picwgoal1[12]\d{3}\\pichgoal1[456]\d{3}', s))
    e = s.count('emfblip')
    tot += 1
    img += e > 0
    full += n > 0
    print(f"{r['GhostDraftForm'][:60]:60} emf={e} fullpage={n}")
print('forms', tot, 'with emf', img, 'with full-page emf', full)
