import argparse, ctypes, hashlib, json, pathlib, random, shutil, statistics, subprocess, xml.etree.ElementTree as ET
from ctypes import wintypes

parser = argparse.ArgumentParser()
parser.add_argument('diskspd', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--rounds', type=int, default=4)
parser.add_argument('--seconds', type=int, default=8)
args = parser.parse_args()
root = args.output.resolve()
root.mkdir(parents=True, exist_ok=False)
if shutil.disk_usage(root).free < 4 * 1024 ** 3:
    raise SystemExit('Need 4 GiB free for owned fixture files')
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
kernel.CreateFileW.restype = wintypes.HANDLE
kernel.DeviceIoControl.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
kernel.DeviceIoControl.restype = wintypes.BOOL
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
kernel.GetCompressedFileSizeW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
kernel.GetCompressedFileSizeW.restype = wintypes.DWORD
kernel.GetFileAttributesW.argtypes = [wintypes.LPCWSTR]
kernel.GetFileAttributesW.restype = wintypes.DWORD

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def compression(path, enabled):
    handle = kernel.CreateFileW(str(path), 0xC0000000, 1, None, 3, 0, None)
    if handle == wintypes.HANDLE(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        value, returned = wintypes.WORD(2 if enabled else 0), wintypes.DWORD()
        if not kernel.DeviceIoControl(handle, 0x9C040, ctypes.byref(value), 2, None, 0, ctypes.byref(returned), None):
            raise ctypes.WinError(ctypes.get_last_error())
        actual = wintypes.WORD()
        if not kernel.DeviceIoControl(handle, 0x9003C, None, 0, ctypes.byref(actual), 2, ctypes.byref(returned), None):
            raise ctypes.WinError(ctypes.get_last_error())
        if actual.value != value.value:
            raise RuntimeError('Compression state readback mismatch')
    finally:
        kernel.CloseHandle(handle)
    high = wintypes.DWORD()
    ctypes.set_last_error(0)
    low = kernel.GetCompressedFileSizeW(str(path), ctypes.byref(high))
    if low == 0xFFFFFFFF and ctypes.get_last_error():
        raise ctypes.WinError(ctypes.get_last_error())
    attributes = kernel.GetFileAttributesW(str(path))
    if attributes == 0xFFFFFFFF or bool(attributes & 0x800) != enabled:
        raise RuntimeError('Compression attribute mismatch')
    return (high.value << 32) | low

def parse_result(path):
    document = ET.parse(path).getroot()
    span = document.find('TimeSpan')
    if span is None:
        raise RuntimeError('Missing measured TimeSpan')
    seconds = float(span.findtext('TestTimeSeconds'))
    targets = span.findall('Thread/Target')
    reads = sum(int(target.findtext('ReadBytes')) for target in targets)
    count = sum(int(target.findtext('ReadCount')) for target in targets)
    if not targets or not count or any(int(target.findtext('WriteCount')) for target in targets):
        raise RuntimeError('Missing read workload or unexpected writes')
    percentiles = {float(bucket.findtext('Percentile')): float(bucket.findtext('ReadMilliseconds')) for bucket in span.findall('Latency/Bucket')}
    if not all(percentile in percentiles for percentile in (50, 95, 99)):
        raise RuntimeError('Missing latency percentiles')
    return {'MiBPerSecond': reads / seconds / 1024 ** 2, 'IOPS': count / seconds, 'p50Ms': percentiles[50], 'p95Ms': percentiles[95], 'p99Ms': percentiles[99]}

manifest, datasets = [], {}
generator = random.Random(20261005)
for kind in ('structured', 'packed'):
    block = b''.join(bytes(2048) + generator.randbytes(2048) for _ in range(256)) if kind == 'structured' else generator.randbytes(1024 * 1024)
    raw = root / f'{kind}-uncompressed.bin'
    compressed = root / f'{kind}-compressed.bin'
    with raw.open('xb') as stream:
        for _ in range(128):
            stream.write(block)
    shutil.copyfile(raw, compressed)
    raw_physical, compressed_physical = compression(raw, False), compression(compressed, True)
    golden = digest(raw)
    if golden != digest(compressed):
        raise RuntimeError('Compressed data differs')
    for file, physical, state in ((raw, raw_physical, False), (compressed, compressed_physical, True)):
        manifest.append({'path': str(file), 'bytes': file.stat().st_size, 'physicalBytes': physical, 'sha256': golden, 'compressed': state})
    datasets[kind] = {'baseline': compressed, 'candidate': raw}
    print(f'Prepared {kind}, compressed physical ratio={compressed_physical/raw_physical:.3f}', flush=True)
(root / 'fixture-manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
summaries = []
for kind in datasets:
    for caching in ('cached', 'unbuffered'):
        rows = []
        for round_index in range(args.rounds):
            order = ['baseline', 'candidate', 'candidate', 'baseline'] if round_index % 2 == 0 else ['candidate', 'baseline', 'baseline', 'candidate']
            for index, arm in enumerate(order):
                output = root / f'{kind}-{caching}-{round_index}-{index}-{arm}.xml'
                command = [str(args.diskspd.resolve()), f'-d{args.seconds}', '-W2', '-C1', '-b64K', '-t1', '-o1', '-r', '-w0', '-L', '-Rxml', '-z1234', '-n', '-Sb' if caching == 'cached' else '-Su', str(datasets[kind][arm])]
                completed = subprocess.run(command, capture_output=True, timeout=args.seconds + 25, creationflags=subprocess.CREATE_NO_WINDOW)
                output.write_bytes(completed.stdout)
                if completed.returncode:
                    (root / f'{output.stem}-stderr.txt').write_bytes(completed.stderr)
                    raise SystemExit('DiskSpd read test failed: ' + str(output))
                measured = parse_result(output)
                measured.update(round=round_index, arm=arm)
                rows.append(measured)
            print(f'Completed {kind}/{caching} round {round_index+1}/{args.rounds}', flush=True)
        metrics = []
        for field in ('MiBPerSecond', 'IOPS', 'p50Ms', 'p95Ms', 'p99Ms'):
            changes, spreads = [], []
            for round_index in range(args.rounds):
                baseline = [r[field] for r in rows if r['round'] == round_index and r['arm'] == 'baseline']
                candidate = [r[field] for r in rows if r['round'] == round_index and r['arm'] == 'candidate']
                mean = statistics.mean(baseline)
                if mean:
                    changes.append((statistics.mean(candidate) / mean - 1) * 100)
                    spreads.append((max(baseline) - min(baseline)) / mean * 100)
            metrics.append({'metric': field, 'baseline': statistics.median(r[field] for r in rows if r['arm'] == 'baseline'), 'candidate': statistics.median(r[field] for r in rows if r['arm'] == 'candidate'), 'roundChangePercent': changes, 'medianChangePercent': statistics.median(changes) if changes else None, 'maxBaselineSpreadPercent': max(spreads) if spreads else None})
        summaries.append({'kind': kind, 'caching': caching, 'metrics': metrics})
        (root / 'summary.json').write_text(json.dumps({'toolSha256': hashlib.sha256(args.diskspd.read_bytes()).hexdigest(), 'rounds': args.rounds, 'seconds': args.seconds, 'scope': 'Owned read-only fixture files, no game modification, no system cache purge', 'results': summaries}, indent=2), encoding='utf-8')
for fixture in manifest:
    if digest(pathlib.Path(fixture['path'])) != fixture['sha256']:
        raise RuntimeError('Fixture changed during read-only workload')
(root / 'integrity-verified.txt').write_text('PASS all fixture data SHA256 unchanged', encoding='utf-8')
print('PASS all paired storage reads complete; fixture data unchanged', flush=True)
