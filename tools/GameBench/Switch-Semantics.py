"""What Pavise keeps or releases while the game is switched out: per foreground phase of a `switch` bench run,
the scheduling attributes the probe sampled each second for the game and for background processes.
usage: Switch-Semantics.py <runDir>"""
import collections, csv, os, sys

run = sys.argv[1]
bench = os.path.join(run, 'bench')
phases = []  # (start_qpc, target)
with open(os.path.join(bench, 'focus-events.csv'), encoding='utf-8-sig') as f:
    for r in csv.DictReader(f):
        phases.append((int(r['qpc']), r['target'], r['kind']))
phases.sort()
manifest = {}
for line in open(os.path.join(bench, 'manifest.tsv'), encoding='utf-8-sig', errors='replace'):
    k, _, v = line.rstrip('\n').partition('\t')
    manifest[k] = v
game_pid = manifest.get('pid', '')


def phase_at(q):
    current = None
    for start, target, kind in phases:
        if start > q: break
        current = target if kind != 'end' else None
    return current


summary = collections.defaultdict(collections.Counter)
for line in open(os.path.join(run, 'probe-attributes.tsv'), encoding='utf-8', errors='replace'):
    p = line.rstrip('\n').split('\t')
    if len(p) < 7 or p[0] != 'qpc' or p[2] != 'proc': continue
    q, name, pid, attrs = int(p[1]), p[3], p[5], p[6]
    ph = phase_at(q)
    if ph is None: continue
    who = 'game' if pid == game_pid else name
    summary[(who, ph)][attrs] += 1

for (who, ph), counter in sorted(summary.items()):
    print('%-12s while %-8s foreground:' % (who, ph))
    for attrs, n in counter.most_common(3):
        print('    %3d x %s' % (n, attrs))
