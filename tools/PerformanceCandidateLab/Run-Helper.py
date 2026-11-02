import argparse, hashlib, json, pathlib, statistics, subprocess

parser = argparse.ArgumentParser()
parser.add_argument('exe', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--seconds', type=float, default=10)
parser.add_argument('--rounds', type=int, default=4)
parser.add_argument('--powershell', action='store_true')
parser.add_argument('--all-cores', action='store_true')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
summaries = []
for scenario in (['psone', 'psfour'] if args.powershell else ['one', 'four']):
    rows = []
    for round_index in range(args.rounds):
        order = ['baseline', 'candidate', 'candidate', 'baseline'] if round_index % 2 == 0 else ['candidate', 'baseline', 'baseline', 'candidate']
        for index, arm in enumerate(order):
            output = args.output / f'{scenario}-{round_index}-{index}-{arm}.json'
            command = [str(args.exe.resolve()), 'helper', scenario, arm, str(args.seconds), str(output.resolve())]
            if args.all_cores:
                command.append('all-cores')
            completed = subprocess.run(command, capture_output=True, text=True, timeout=48, creationflags=subprocess.CREATE_NO_WINDOW)
            result = json.loads(output.read_text()) if output.exists() else {'error': 'Missing output', 'stderr': completed.stderr}
            if completed.returncode or result.get('error') or not result.get('restored'):
                raise SystemExit(json.dumps(result))
            result.update(round=round_index, arm=arm)
            rows.append(result)
        print(f'Completed {scenario} round {round_index+1}/{args.rounds}', flush=True)
    metrics = []
    for field in ['loopsPerSecond', 'p50Ms', 'p95Ms', 'p99Ms', 'cpuSeconds', 'helper.loopsPerSecond', 'helper.cpuSeconds']:
        def value(row):
            return row['helper'][field[7:]] if field.startswith('helper.') else row[field]
        changes, spreads = [], []
        for round_index in range(args.rounds):
            baseline = [value(r) for r in rows if r['round'] == round_index and r['arm'] == 'baseline']
            candidate = [value(r) for r in rows if r['round'] == round_index and r['arm'] == 'candidate']
            mean = statistics.mean(baseline)
            changes.append((statistics.mean(candidate)/mean-1)*100)
            spreads.append((max(baseline)-min(baseline))/mean*100)
        metrics.append({'metric': field, 'baseline': statistics.median(value(r) for r in rows if r['arm']=='baseline'), 'candidate': statistics.median(value(r) for r in rows if r['arm']=='candidate'), 'roundChangePercent': changes, 'medianChangePercent': statistics.median(changes), 'maxBaselineSpreadPercent': max(spreads)})
    summaries.append({'scenario': scenario, 'metrics': metrics})
    (args.output/'summary.json').write_text(json.dumps({'exeSha256': hashlib.sha256(args.exe.read_bytes()).hexdigest(), 'rounds': args.rounds, 'seconds': args.seconds, 'allForegroundCores': args.all_cores, 'scope': 'Owned High/AboveNormal foreground compute vs own Normal helper; no real game FPS', 'results': summaries}, indent=2), encoding='utf-8')
