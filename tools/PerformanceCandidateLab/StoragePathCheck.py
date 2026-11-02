import argparse, ctypes, json, pathlib, subprocess, time, xml.etree.ElementTree as ET
from ctypes import wintypes

parser = argparse.ArgumentParser()
parser.add_argument('diskspd', type=pathlib.Path)
parser.add_argument('fixtures', type=pathlib.Path)
args = parser.parse_args()
root = args.fixtures.resolve()
manifest = json.loads((root / 'fixture-manifest.json').read_text())
paths = {row['compressed']: pathlib.Path(row['path']) for row in manifest if pathlib.Path(row['path']).name.startswith('structured-')}
if any(path.resolve().parent != root for path in paths.values()) or len(paths) != 2:
    raise SystemExit('Only owned fixture paths allowed')
pdh = ctypes.WinDLL('pdh')
pdh.PdhOpenQueryW.argtypes = [wintypes.LPCWSTR, ctypes.c_size_t, ctypes.POINTER(wintypes.HANDLE)]
pdh.PdhAddEnglishCounterW.argtypes = [wintypes.HANDLE, wintypes.LPCWSTR, ctypes.c_size_t, ctypes.POINTER(wintypes.HANDLE)]
pdh.PdhCollectQueryData.argtypes = [wintypes.HANDLE]
pdh.PdhCloseQuery.argtypes = [wintypes.HANDLE]
class Value(ctypes.Structure):
    _fields_ = [('status', wintypes.DWORD), ('number', ctypes.c_double)]
pdh.PdhGetFormattedCounterValue.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(Value)]
query, counter = wintypes.HANDLE(), wintypes.HANDLE()
def check(status):
    if status:
        raise RuntimeError(f'PDH status {status & 0xffffffff:08x}')
check(pdh.PdhOpenQueryW(None, 0, ctypes.byref(query)))
rows = []
try:
    check(pdh.PdhAddEnglishCounterW(query, '\\PhysicalDisk(_Total)\\Disk Read Bytes/sec', 0, ctypes.byref(counter)))
    for index, compressed in enumerate([True, False, False, True]):
        output = root / f'physical-path-{index}-{compressed}.xml'
        check(pdh.PdhCollectQueryData(query))
        command = [str(args.diskspd.resolve()), '-d6', '-W0', '-C0', '-b64K', '-t1', '-o1', '-r', '-w0', '-L', '-Rxml', '-z1234', '-n', '-Su', str(paths[compressed])]
        output_stream = output.open('wb')
        error_path = root / f'{output.stem}-stderr.txt'
        error_stream = error_path.open('wb')
        process = subprocess.Popen(command, stdout=output_stream, stderr=error_stream, creationflags=subprocess.CREATE_NO_WINDOW)
        samples = []
        try:
            start = time.monotonic()
            while process.poll() is None:
                time.sleep(1)
                check(pdh.PdhCollectQueryData(query))
                value = Value()
                check(pdh.PdhGetFormattedCounterValue(counter, 0x200, None, ctypes.byref(value)))
                if value.status not in (0, 1):
                    raise RuntimeError('Invalid physical disk counter')
                samples.append(value.number / 1024 ** 2)
                if time.monotonic() - start > 20:
                    raise RuntimeError('Owned DiskSpd timeout')
            process.wait(timeout=2)
            if process.returncode:
                error_stream.flush()
                raise RuntimeError(error_path.read_text(errors='replace'))
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=3)
            output_stream.close()
            error_stream.close()
        span = ET.parse(output).getroot().find('TimeSpan')
        seconds = float(span.findtext('TestTimeSeconds'))
        read_bytes = sum(int(target.findtext('ReadBytes')) for target in span.findall('Thread/Target'))
        rows.append({'compressed': compressed, 'logicalMiBPerSecond': read_bytes / seconds / 1024 ** 2, 'wholeMachinePhysicalReadMiBPerSecond': sum(samples) / len(samples), 'physicalSamples': samples, 'requestedNoBuffering': True})
        print(json.dumps(rows[-1]), flush=True)
finally:
    pdh.PdhCloseQuery(query)
(root / 'physical-path-summary.json').write_text(json.dumps({'scope': 'Read-only owned fixtures; whole-machine PhysicalDisk counter is not exclusive attribution', 'results': rows}, indent=2), encoding='utf-8')
