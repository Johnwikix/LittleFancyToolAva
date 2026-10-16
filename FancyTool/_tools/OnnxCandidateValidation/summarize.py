"""Archive small verification evidence without committing models, wheels or videos."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    root, dest = args.work.resolve(), args.destination.resolve()
    dest.mkdir(parents=True, exist_ok=True)
    manifests = json.loads((root / 'manifest.json').read_text(encoding='utf-8-sig'))
    for model in manifests['models']:
        path = Path(model['path'])
        if not path.is_absolute():
            path = root / path
        model['sha256'] = hashlib.file_digest(path.open('rb'), 'sha256').hexdigest()
        if model['family'] == 'baseline':
            model['path'] = path.name
            model['rgbPath'] = path.name
    runs = []
    for directory in ['results-final', 'results-precision', 'results-dml-benchmark', 'results-bounded']:
        for path in sorted((root / directory).glob('**/*.json')):
            run = json.loads(path.read_text(encoding='utf-8-sig'))
            run['source'] = str(path.relative_to(root)).replace('\\', '/')
            runs.append(run)
    bundle = {'hardware': json.loads((root / 'hardware.json').read_text(encoding='utf-8-sig')),
              'provenance': json.loads((root / 'provenance.json').read_text(encoding='utf-8-sig')),
              'manifest': manifests,
              'exportChecks': json.loads((root / 'export-checks.json').read_text(encoding='utf-8-sig')),
              'runs': runs}
    (dest / 'measurements.json').write_text(json.dumps(bundle, indent=2, ensure_ascii=False), encoding='utf-8')
    models_by_name = {model['name']: model for model in manifests['models']}
    eligible = []
    for run in runs:
        model = models_by_name[run['name']]
        checks = {check['label']: check for check in run.get('checks', [])}
        if (model['family'] == 'baseline' or not run['passed']
                or not (run.get('device') or '').startswith('GPU')
                or not all(checks.get(label, {}).get('passed') for label in ['random', 'flat', 'edges'])):
            continue
        eligible.append({'name': run['name'], 'backend': run['backend'], 'device': run['device'],
                         'sha256': model['sha256'], 'rgbSha256': model.get('rgbSha256'),
                         'bitmapPathValidated': bool(run.get('benchmarks')),
                         'transcodeValidated': checks.get('application-transcode', {}).get('passed', False),
                         'source': run['source']})
    policy = {'scope': 'Model inference only; host scheduling, bitmap conversion, decoding and encoding are outside the GPU-only claim.',
              'enforcement': 'GPU device selected explicitly; session.disable_cpu_ep_fallback=1; no AUTO/HETERO device.',
              'numericAcceptance': {'maximumAbsoluteError': .02, 'meanAbsoluteError': .001},
              'note': 'Passing a 64/128px tensor does not establish larger-tile performance or complete application integration.',
              'eligibleConfigurations': eligible}
    (dest / 'eligible-configurations.json').write_text(json.dumps(policy, indent=2, ensure_ascii=False), encoding='utf-8')
    for name in ['python-lock.txt', 'export.log', 'export-edvr-isolated.log', 'build.log']:
        shutil.copyfile(root / name, dest / name)
    for directory in ['results-final', 'results-precision', 'results-dml-benchmark', 'results-bounded']:
        for path in sorted((root / directory).glob('**/*.log')):
            target = dest / 'logs' / directory / path.relative_to(root / directory)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)
    print('Archived', len(runs), 'runs')
    for run in runs:
        print(run['name'], run['backend'], run['passed'],
              [(b['height'], round(b['warmFps'], 2)) for b in run.get('benchmarks', [])])


if __name__ == '__main__':
    main()
