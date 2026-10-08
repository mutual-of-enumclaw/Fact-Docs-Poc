"""Print the absolutely positioned text boxes (non-EMF) in a converted template with their style and text."""
import json, sys, glob, os

f = glob.glob(os.path.join(r'C:\src\fact-poc\output\ghostdraft\batch', sys.argv[1] + '*.json'))[0]
d = json.load(open(f, encoding='utf-8'))
print(d['css'][:3000])


def text(c):
    if c.get('type') == 'textnode':
        return c.get('content', '')
    if c.get('type') == 'data-field':
        return '{' + c.get('field', '') + '}'
    return ''.join(text(k) for k in c.get('components') or [])


def walk(c):
    if 'gd-tb' in (c.get('classes') or []):
        print(c.get('style'), '|', text(c)[:100])
        print('   ', json.dumps(c)[:600])
    for k in c.get('components') or []:
        walk(k)


for c in d['components']:
    walk(c)
