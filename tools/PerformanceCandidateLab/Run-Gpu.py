import argparse, hashlib, json, pathlib, statistics, subprocess

parser = argparse.ArgumentParser()
parser.add_argument('exe', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--seconds', type=float, default=4)
parser.add_argument('--rounds', type=int, default=4)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
summaries = []
for scenario in ['none', 'normal', 'idle']:
    rows = []
    for round_index in range(args.rounds):
        order = ['baseline', 'candidate', 'candidate', 'baseline'] if round_index%2==0 else ['candidate', 'baseline', 'baseline', 'candidate']
        for index, arm in enumerate(order):
            output = args.output/f'{scenario}-{round_index}-{index}-{arm}.json'
            completed = subprocess.run([str(args.exe.resolve()), 'gpu', scenario, arm, str(args.seconds), str(output.resolve())], capture_output=True, text=True, timeout=32, creationflags=subprocess.CREATE_NO_WINDOW)
            result = json.loads(output.read_text()) if output.exists() else {'error':'Missing output', 'stderr':completed.stderr}
            if completed.returncode or not result.get('restored'):
                raise SystemExit(json.dumps(result))
            result.update(round=round_index, arm=arm)
            rows.append(result)
        print(f'Completed {scenario} round {round_index+1}/{args.rounds}', flush=True)
    metrics=[]
    for field in ['loopsPerSecond','p50Ms','p95Ms','p99Ms']:
        changes, spreads=[],[]
        for round_index in range(args.rounds):
            base=[r[field] for r in rows if r['round']==round_index and r['arm']=='baseline']
            candidate=[r[field] for r in rows if r['round']==round_index and r['arm']=='candidate']
            mean=statistics.mean(base)
            changes.append((statistics.mean(candidate)/mean-1)*100)
            spreads.append((max(base)-min(base))/mean*100)
        metrics.append({'metric':field,'baseline':statistics.median(r[field] for r in rows if r['arm']=='baseline'),'candidate':statistics.median(r[field] for r in rows if r['arm']=='candidate'),'roundChangePercent':changes,'medianChangePercent':statistics.median(changes),'maxBaselineSpreadPercent':max(spreads)})
    summaries.append({'scenario':scenario,'metrics':metrics})
    (args.output/'summary.json').write_text(json.dumps({'exeSha256':hashlib.sha256(args.exe.read_bytes()).hexdigest(),'rounds':args.rounds,'seconds':args.seconds,'scope':'Owned RTX offscreen DirectCompute; GPU High (4) vs Realtime (5), no game FPS or display latency measurement','results':summaries},indent=2),encoding='utf-8')
