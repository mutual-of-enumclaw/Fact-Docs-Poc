import sys
import fitz

doc = fitz.open(sys.argv[1])
for page in doc:
    for line in page.get_text().splitlines():
        if sys.argv[2].lower() in line.lower():
            print(page.number + 1, repr(line))
    for w in page.get_text('words'):
        if sys.argv[2].lower() in w[4].lower():
            print('word', [round(v, 1) for v in w[:4]], w[4])
