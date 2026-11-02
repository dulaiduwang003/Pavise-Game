"""Compare before/during/after system snapshots (Snapshot-System.ps1 output).
usage: Compare-Snapshots.py before.tsv during.tsv after.tsv [report.md]
Prints what the session changed and what was not restored afterwards. Process rows are matched by name#pid, so a
process that exited or started in between is reported separately and never counted as a restore failure."""
import sys, re, collections


def load(path):
    rows = {}
    for line in open(path, encoding='utf-8-sig', errors='replace'):
        parts = line.rstrip('\n').split('\t', 2)
        if len(parts) < 3: continue
        rows[(parts[0], parts[1])] = parts[2]
    return rows


def volatile(section, key, value):
    if section == 'meta': return True
    if section == 'thread': return False
    if section == 'reg' and re.search(r'GameDVR_.*Time|Timestamp|LastRun|ActivationTime', key, re.I): return True
    return False


def strip_cpu(value):
    return re.sub(r' cpu_ms=\d+', '', value)


before, during, after = (load(p) for p in sys.argv[1:4])
out = []
def emit(s=''): out.append(s); print(s)

def diff(a, b, label):
    changed = collections.defaultdict(list)
    for k in sorted(set(a) | set(b)):
        sec, key = k
        if volatile(sec, key, a.get(k, '')): continue
        va, vb = a.get(k), b.get(k)
        if sec == 'thread':
            va = strip_cpu(va) if va else va; vb = strip_cpu(vb) if vb else vb
        if va == vb: continue
        if sec in ('proc', 'thread') and (va is None or vb is None): continue  # process/thread lifetime, not state
        changed[sec].append((key, va, vb))
    emit('## ' + label)
    if not changed: emit('(no differences)')
    for sec in sorted(changed):
        emit('### %s (%d)' % (sec, len(changed[sec])))
        for key, va, vb in changed[sec][:400]:
            emit('- `%s`: `%s` -> `%s`' % (key, va if va is not None else '<missing>', vb if vb is not None else '<missing>'))
    emit()
    return changed

diff(before, during, 'Before -> during (what the session applied)')
lasting = diff(before, after, 'Before -> after (not restored)')
if len(sys.argv) > 4:
    open(sys.argv[4], 'w', encoding='utf-8').write('\n'.join(out) + '\n')
