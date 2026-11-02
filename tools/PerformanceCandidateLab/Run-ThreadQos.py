import argparse, hashlib, json, pathlib, statistics, subprocess, time

parser = argparse.ArgumentParser()
parser.add_argument('exe', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--rounds', type=int, default=4)
parser.add_argument('--seconds', type=float, default=10)
parser.add_argument('--mode', choices=['qos', 'ideal'], default='qos')
parser.add_argument('--candidate-option', default='candidate')
parser.add_argument('--scenarios')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
summaries = []
scenarios = ['eco-continuous', 'eco-bursty', 'default-continuous', 'default-bursty'] if args.mode == 'qos' else ['default-compute', 'samecore-compute', 'default-shared', 'samecore-shared']
if args.scenarios:
    requested = args.scenarios.split(',')
    if any(scenario not in scenarios for scenario in requested):
        raise SystemExit('Unknown scenario')
    scenarios = requested
for scenario in scenarios:
    rows = []
    for round_index in range(args.rounds):
        order = ['baseline', 'candidate', 'candidate', 'baseline'] if round_index % 2 == 0 else ['candidate', 'baseline', 'baseline', 'candidate']
        for index, arm in enumerate(order):
            output = args.output / f'{scenario}-{round_index}-{index}-{arm}.json'
            option = args.candidate_option if arm == 'candidate' else 'baseline'
            completed = subprocess.run([str(args.exe.resolve()), args.mode, scenario, option, str(args.seconds), str(output.resolve())], capture_output=True, text=True, timeout=35, creationflags=subprocess.CREATE_NO_WINDOW)
            result = json.loads(output.read_text()) if output.exists() else {'error': 'Missing result'}
            if completed.returncode or result.get('error') or not result.get('restored'):
                raise SystemExit(json.dumps(result))
            result.update(round=round_index, arm=arm)
            rows.append(result)
        print(f'Completed {scenario} round {round_index+1}/{args.rounds}', flush=True)
    metrics = []
    for field in ['loopsPerSecond', 'p50Ms', 'p95Ms', 'p99Ms', 'cpuSeconds']:
        changes, spreads = [], []
        for round_index in range(args.rounds):
            base = [row[field] for row in rows if row['round'] == round_index and row['arm'] == 'baseline']
            candidate = [row[field] for row in rows if row['round'] == round_index and row['arm'] == 'candidate']
            mean = statistics.mean(base)
            if mean:
                changes.append((statistics.mean(candidate) / mean - 1) * 100)
                spreads.append((max(base) - min(base)) / mean * 100)
        metrics.append({'metric': field, 'baseline': statistics.median(r[field] for r in rows if r['arm'] == 'baseline'), 'candidate': statistics.median(r[field] for r in rows if r['arm'] == 'candidate'), 'roundChangePercent': changes, 'medianChangePercent': statistics.median(changes) if changes else None, 'maxBaselineSpreadPercent': max(spreads) if spreads else None})
    summaries.append({'scenario': scenario, 'threadStateReadable': all(r['threadStateReadable'] for r in rows), 'writeReadbackVerified': all(r['writeReadbackVerified'] for r in rows), 'metrics': metrics})
    (args.output / 'summary.json').write_text(json.dumps({'exeSha256': hashlib.sha256(args.exe.read_bytes()).hexdigest(), 'rounds': args.rounds, 'seconds': args.seconds, 'candidateOption': args.candidate_option, 'scope': 'Owned High process / AboveNormal thread; synthetic work only', 'results': summaries}, indent=2), encoding='utf-8')
print(json.dumps(summaries), flush=True)
