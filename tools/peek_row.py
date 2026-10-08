"""Print the <tr> markup around the Nth occurrence of text in a case's Template.html body.
usage: peek_row.py CASE TEXT [N] [LEN]"""
import sys
case, text = sys.argv[1], sys.argv[2]
n = int(sys.argv[3]) if len(sys.argv) > 3 else 0
ln = int(sys.argv[4]) if len(sys.argv) > 4 else 1500
t = open(rf'C:\src\fact-poc\output\ghostdraft\html-snapshots\{case}\Template.html', encoding='utf-8').read()
body = t.index('</style>')
i = body
for _ in range(n + 1):
    i = t.index(text, i + 1)
j = t.rfind('<tr', 0, i)
print(t[j:j + ln])
