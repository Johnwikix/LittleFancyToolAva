"""Run exported candidates serially in fresh application processes; retain failures."""
import argparse
import json
import os
from pathlib import Path
import subprocess
from datetime import datetime, timezone


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    parser.add_argument('--runner', type=Path, required=True)
    parser.add_argument('--model')
    parser.add_argument('--dml-only', action='store_true')
    parser.add_argument('--gpu-only', action='store_true')
    parser.add_argument('--fp32', action='store_true')
    parser.add_argument('--benchmark-dml', action='store_true')
    parser.add_argument('--results-subdir', default='results-final')
    args = parser.parse_args()
    work = args.work.resolve()
    results = work / args.results_subdir
    results.mkdir(exist_ok=True)
    env = dict(os.environ, FANCYTOOL_DATA_DIRECTORY=str(work / 'app-data'))
    records = []
    for model in json.loads((work / 'manifest.json').read_text(encoding='utf-8-sig'))['models']:
        if args.model and args.model != model['name']:
            continue
        for backend in (['dml'] if args.dml_only else ['gpu'] if args.gpu_only else ['gpu', 'dml']):
            name = model['name']
            destination = results / name
            destination.mkdir(exist_ok=True)
            result_path = destination / f'{name}-{backend}.json'
            if result_path.exists():
                records.append(json.loads(result_path.read_text(encoding='utf-8-sig')))
                continue
            command = [str(args.runner.resolve()), str(destination), '--onnx-candidate', '--candidate-root', str(work),
                       '--candidate-name', name]
            if backend == 'dml':
                command.append('--candidate-dml')
            if args.fp32:
                command.append('--candidate-fp32')
            if args.benchmark_dml:
                command.append('--candidate-benchmark-dml')
            print(f'{datetime.now(timezone.utc).isoformat()} START {name}/{backend}', flush=True)
            try:
                timeout_seconds = 120 if backend == 'dml' and not args.benchmark_dml else 600
                with (destination / f'{backend}.log').open('w', encoding='utf-8') as log:
                    process = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, env=env,
                                             timeout=timeout_seconds)
                if not result_path.exists():
                    result_path.write_text(json.dumps({'name': name, 'backend': backend, 'passed': False,
                                                      'error': f'Process exited {process.returncode} without a result'}), encoding='utf-8')
            except subprocess.TimeoutExpired:
                result_path.write_text(json.dumps({'name': name, 'backend': backend, 'passed': False,
                                                  'error': f'Per-process {timeout_seconds} second timeout'}), encoding='utf-8')
            record = json.loads(result_path.read_text(encoding='utf-8-sig'))
            records.append(record)
            print(f'DONE {name}/{backend}: passed={record["passed"]}, device={record.get("device")}', flush=True)
            (work / 'results.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
    (work / 'results.json').write_text(json.dumps(records, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
