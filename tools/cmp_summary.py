"""cmp_summary.py OLD NEW - per-case score deltas between two summary.csv files."""
import csv, sys


def load(p):
    with open(p, newline='', encoding='utf-8') as f:
        return {r['case']: r for r in csv.DictReader(f)}


a, b = load(sys.argv[1]), load(sys.argv[2])
print(f'old {len(a)} cases, new {len(b)} cases')
rows = []
for k in sorted(set(a) & set(b)):
    sa, sb = float(a[k]['score']), float(b[k]['score'])
    if abs(sa - sb) > 0.0005:
        rows.append((sb - sa, k, sa, sb))
for d, k, sa, sb in sorted(rows):
    print(f'{k:<28} {sa:.3f} -> {sb:.3f}  {d:+.3f}')
if b:
    print('avg new', round(sum(float(r['score']) for r in b.values()) / len(b), 4),
          'avg old', round(sum(float(r['score']) for r in a.values()) / max(len(a), 1), 4))
