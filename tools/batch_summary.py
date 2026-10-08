import json, glob, collections, re
notes = collections.Counter(); forms = []
for f in sorted(glob.glob(r'C:\src\fact-poc\output\ghostdraft\batch\*.json')):
    r = json.load(open(f, encoding='utf-8'))
    c = r['report']['counts']; n = r['report']['notes']
    lost = [x for x in n if 'kept unconditionally' in x or 'dropped' in x or 'not in package' in x]
    forms.append((r['name'], c.get('form pages', 0), c.get('fill points', 0), len(n), len(lost)))
    for x in n:
        notes[re.sub(r"\[.*?\]|'[^']*'|\"[^\"]*\"|%\[\d+\]|\d+", '#', x)[:110]] += 1
print('forms', len(forms), 'sheet-mode', sum(1 for f in forms if f[1]), 'flow-only', sum(1 for f in forms if not f[1]))
print('clean (0 notes)', sum(1 for f in forms if not f[3]), ' with lost logic', sum(1 for f in forms if f[4]))
for k, v in notes.most_common(25): print(f'{v:4} {k}')
