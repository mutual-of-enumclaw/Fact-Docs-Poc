import sys, os
import fitz
root = r'C:\src\fact-poc\output\ghostdraft'
case, page, word = sys.argv[1], int(sys.argv[2]), sys.argv[3]
for name, pdf in (('gd', os.path.join(root, 'serverxml', case, 'GhostDraft.pdf')), ('html', os.path.join(root, 'html-snapshots', case, 'Html.pdf'))):
    with fitz.open(pdf) as d:
        p = d[page - 1]
        print(name, p.rect, p.mediabox, p.rotation)
        ws = p.get_text('words')
        for i, w in enumerate(ws):
            if w[4] == word:
                for v in ws[max(0, i - 6): i + 6]:
                    print(f'   {v[0]:7.2f} {v[1]:7.2f} {v[2]:7.2f} {v[3]:7.2f} {v[4]}')
                break
