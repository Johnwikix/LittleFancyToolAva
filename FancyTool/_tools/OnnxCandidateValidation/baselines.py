"""Append existing application models as timing-only controls (no numerical qualification)."""
import argparse
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    parser.add_argument('models', type=Path)
    args = parser.parse_args()
    path = args.work / 'manifest.json'
    manifest = json.loads(path.read_text(encoding='utf-8-sig'))
    manifest['models'] = [model for model in manifest['models'] if model['family'] != 'baseline']
    for name, filename, scale in [('baseline_clearreality', '4x-ClearRealityV1_fp16.onnx', 4),
                                   ('baseline_liveaction', '2xLiveActionV1_SPAN.onnx', 2)]:
        model_path = (args.models / filename).resolve(strict=True)
        manifest['models'].append({'name': name, 'family': 'baseline', 'path': str(model_path),
                                   'rgbPath': str(model_path), 'channels': 3, 'nativeScale': scale,
                                   'window': 384, 'fixedSize': False, 'referenceSize': 64,
                                   'cases': [], 'temporalNeighbourMae': None})
    path.write_text(json.dumps(manifest, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
