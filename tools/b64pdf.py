import re, base64, sys, fitz
src, stem = sys.argv[1], sys.argv[2]
t = open(src, encoding='utf-8').read()
for n, m in enumerate(re.finditer(r'B64START(.*?)B64END', t, re.S), 1):
    dst = f'{stem}-{n}.pdf'
    open(dst, 'wb').write(base64.b64decode(m.group(1)))
    d = fitz.open(dst)
    print(dst, d.page_count, 'pages')
    for i, p in enumerate(d):
        p.get_pixmap(dpi=80).save(f'{stem}-{n}-p{i+1}.png')
for e in re.findall(r'ERR\d+[^\n]*', t):
    print(e)
