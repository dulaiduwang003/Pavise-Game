import argparse, hashlib, json, pathlib, statistics, subprocess, time

parser = argparse.ArgumentParser()
parser.add_argument('exe', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--rounds', type=int, default=4)
parser.add_argument('--seconds', type=float, default=4)
parser.add_argument('--only', choices=['workers', 'wake', 'memory'])
parser.add_argument('--existing-pages', action='store_true')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
experiments = [
    ('workers', 'small', '11', '6'), ('workers', 'heavy', '11', '6'),
    ('wake', 'contended', 'disabled', 'enabled'), ('wake', 'coordinated', 'disabled', 'enabled'),
    ('wake', 'idle', 'default', 'enabled'),
    ('memory', 'bounded', '1', '5'), ('memory', 'unbounded', '1', '5')]
if args.existing_pages:
    experiments = [('memory', 'bounded-existing', '1', '5'), ('memory', 'unbounded-existing', '1', '5')]
results = []
for mode, scenario, baseline, candidate in experiments:
    if args.only and mode != args.only:
        continue
    for round_index in range(args.rounds):
        # 每组候选配新基线，轮次交替使用 BAAB 和 ABBA。
        order = [baseline, candidate, candidate, baseline] if round_index % 2 == 0 else [candidate, baseline, baseline, candidate]
        for order_index, option in enumerate(order):
            name = f'{mode}-{scenario}-{round_index}-{order_index}-{option}'
            output = args.output / f'{name}.json'
            start = time.time()
            completed = subprocess.run([str(args.exe.resolve()), mode, scenario, option, str(args.seconds), str(output.resolve())],
                                       capture_output=True, text=True, timeout=35, creationflags=subprocess.CREATE_NO_WINDOW)
            result = json.loads(output.read_text()) if output.exists() else {'error': 'No result'}
            result.update(round=round_index, order=order_index, arm='baseline' if option == baseline else 'candidate', wall=time.time()-start)
            results.append(result)
            if completed.returncode or result.get('error') or not result.get('restored'):
                print(json.dumps(result), flush=True)
                raise SystemExit(f'Failed {name}')
        print(f'Completed {mode}/{scenario} round {round_index + 1}/{args.rounds}', flush=True)

summary = []
for mode, scenario, baseline, candidate in experiments:
    rows = [r for r in results if r['mode'] == mode and r['scenario'] == scenario]
    if not rows:
        continue
    metrics = []
    for field in ['loopsPerSecond', 'p50Ms', 'p95Ms', 'p99Ms', 'residentPages']:
        round_changes = []
        baseline_spreads = []
        for round_index in range(args.rounds):
            controls = [r[field] for r in rows if r['round'] == round_index and r['arm'] == 'baseline']
            treatment = [r[field] for r in rows if r['round'] == round_index and r['arm'] == 'candidate']
            base = statistics.mean(controls)
            if base:
                round_changes.append((statistics.mean(treatment) / base - 1) * 100)
                baseline_spreads.append((max(controls) - min(controls)) / base * 100)
        metrics.append({'metric': field, 'baseline': statistics.median(r[field] for r in rows if r['arm'] == 'baseline'),
                        'candidate': statistics.median(r[field] for r in rows if r['arm'] == 'candidate'),
                        'roundChangePercent': round_changes,
                        'medianChangePercent': statistics.median(round_changes) if round_changes else None,
                        'maxBaselineSpreadPercent': max(baseline_spreads) if baseline_spreads else None})
    summary.append({'mode': mode, 'scenario': scenario, 'metrics': metrics})
metadata = {'exeSha256': hashlib.sha256(args.exe.read_bytes()).hexdigest(), 'rounds': args.rounds,
            'seconds': args.seconds, 'scope': 'Owned synthetic workloads; not game FPS', 'results': summary}
(args.output / 'summary.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
print(json.dumps(summary), flush=True)
