"""Export a bounded selection of upstream models and deterministic CPU references.

Run with the isolated Python environment. Sources live under WORK/upstream;
no application model installation or preferences are modified.
"""
import argparse
import hashlib
import importlib
import json
import subprocess
import sys
import traceback
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch
from onnx import TensorProto, helper, numpy_helper


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def fixtures(channels, height, width):
    rng = np.random.default_rng(20261016)
    yield 'random', rng.random((1, channels, height, width), dtype=np.float32)
    yield 'flat', np.full((1, channels, height, width), .5, dtype=np.float32)
    yy, xx = np.mgrid[:height, :width]
    image = np.stack([(xx / max(1, width - 1)), (yy / max(1, height - 1)),
                      ((xx + yy) % 19 < 9).astype(float)]).astype(np.float32)
    if channels == 1:
        image = (.2126 * image[0] + .7152 * image[1] + .0722 * image[2])[None]
    elif channels == 15:
        image = np.concatenate([np.roll(image, f - 2, axis=2) for f in range(5)])
    yield 'edges', image[None]


def rgb_wrapper(path):
    """Adapt a full-range BT.709 luma model to the application's RGB contract."""
    graph = onnx.load(path)
    source = graph.graph.input[0].name
    predicted = graph.graph.output[0].name
    graph.graph.initializer.extend([
        numpy_helper.from_array(np.array([.2126, .7152, .0722], np.float32).reshape(1, 3, 1, 1), 'adapter_luma_weights'),
        numpy_helper.from_array(np.array([1, 1, 2, 2], np.float32), 'adapter_scales'),
        numpy_helper.from_array(np.array(0, np.float32), 'adapter_min'),
        numpy_helper.from_array(np.array(1, np.float32), 'adapter_max')])
    nodes = [helper.make_node('Conv', ['adapter_rgb', 'adapter_luma_weights'], [source], kernel_shape=[1, 1])]
    nodes.extend(graph.graph.node)
    for src, dst in [('adapter_rgb', 'adapter_rgb_base'), (source, 'adapter_y_base')]:
        nodes.append(helper.make_node('Resize', [src, '', 'adapter_scales'], [dst], mode='linear', coordinate_transformation_mode='half_pixel'))
    nodes.extend([helper.make_node('Sub', [predicted, 'adapter_y_base'], ['adapter_delta']),
                  helper.make_node('Add', ['adapter_rgb_base', 'adapter_delta'], ['adapter_unclipped']),
                  helper.make_node('Clip', ['adapter_unclipped', 'adapter_min', 'adapter_max'], ['adapter_output'])])
    del graph.graph.node[:]
    graph.graph.node.extend(nodes)
    del graph.graph.input[:]
    del graph.graph.output[:]
    graph.graph.input.append(helper.make_tensor_value_info('adapter_rgb', TensorProto.FLOAT, [1, 3, 'height', 'width']))
    graph.graph.output.append(helper.make_tensor_value_info('adapter_output', TensorProto.FLOAT, [1, 3, 'height2', 'width2']))
    # Internal symbolic shapes from the original one-channel graph are stale.
    del graph.graph.value_info[:]
    onnx.checker.check_model(graph)
    target = path.with_stem(path.stem + '_rgb')
    onnx.save(graph, target)
    return target


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    parser.add_argument('--family', choices=['anime4k', 'fsrcnnx', 'nnedi3', 'ravu', 'edvr'])
    args = parser.parse_args()
    work = args.work.resolve()
    upstream = work / 'upstream'
    converters = upstream / 'HWEnc-onnx-models'
    sys.path.insert(0, str(converters))
    torch.set_num_threads(4)
    # Audit export equivalence rather than assuming that a successful checker
    # proves the weight layout and tensor operations were preserved.
    common = importlib.import_module('onnx_export_common')
    original_export = common.export_onnx
    export_checks = []

    def checked_export(model, sample, output_path, **kwargs):
        result = original_export(model, sample, output_path, **kwargs)
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        session = ort.InferenceSession(str(output_path), options, providers=['CPUExecutionProvider'])
        for label, data in fixtures(int(sample.shape[1]), int(sample.shape[2]), int(sample.shape[3])):
            with torch.no_grad():
                expected = model(torch.from_numpy(data)).numpy()
            actual = session.run(None, {session.get_inputs()[0].name: data})[0]
            delta = np.abs(actual - expected)
            maximum, mean = float(delta.max()), float(delta.mean())
            passed = bool(np.isfinite(actual).all() and maximum <= .001 and mean <= .0001)
            export_checks.append({'model': Path(output_path).stem, 'case': label, 'max': maximum, 'mae': mean, 'passed': passed})
            if not passed:
                raise ValueError(f'PyTorch/CPU ONNX mismatch: {output_path}/{label}: max={maximum}, mae={mean}')
        return result

    common.export_onnx = checked_export
    models = work / 'models'
    models.mkdir(exist_ok=True)
    errors = []
    records = []
    families = [args.family] if args.family else ['anime4k', 'fsrcnnx', 'nnedi3', 'ravu', 'edvr']
    for family in families:
        dest = models / family
        dest.mkdir(exist_ok=True)
        try:
            if family == 'anime4k':
                mod = importlib.import_module('export_anime4k_upscale_cnn')
                glsl = str(upstream / 'Anime4K/glsl/Upscale')
                mod.export_S('s', 'Anime4K_Upscale_CNN_x2_S.glsl', glsl, str(dest))
                mod.export_M('m', 'Anime4K_Upscale_CNN_x2_M.glsl', glsl, str(dest))
            elif family == 'fsrcnnx':
                mod = importlib.import_module('export_fsrcnnx')
                text = (work / 'weights/FSRCNNX.p').read_text(encoding='utf-8')
                for suffix in ['s', 'm']:
                    prefix, features = mod.VARIANTS[suffix]
                    mod.export_one(suffix, prefix, features, text, str(dest))
            elif family == 'nnedi3':
                mod = importlib.import_module('export_nnedi3')
                mod.UP = str(upstream / 'mpv-prescalers')
                for neurons in [16, 32]:
                    mod.convert(neurons, 8, 4, dest, 18)
            elif family == 'ravu':
                mod = importlib.import_module('convert_ravu_lite')
                for radius in [2, 3]:
                    mod.convert(radius, str(upstream / 'mpv-prescalers'), str(dest))
            else:
                subprocess.run([sys.executable, '-E', '-s', str(converters / 'export_edvr.py'),
                                '--repo-root', str(upstream / 'BasicSR'), '--weights', str(work / 'weights/edvrm_x4.pth'),
                                '--output', str(dest), '--size', '128x128'], check=True)
            for path in sorted(dest.glob('*.onnx')):
                if path.stem.endswith('_rgb'):
                    continue
                graph = onnx.load(path)
                onnx.checker.check_model(graph)
                options = ort.SessionOptions()
                options.intra_op_num_threads = 4
                session = ort.InferenceSession(str(path), options, providers=['CPUExecutionProvider'])
                channels = session.get_inputs()[0].shape[1]
                size = 128 if family == 'edvr' else 64
                reference_dir = work / 'references' / path.stem
                reference_dir.mkdir(parents=True, exist_ok=True)
                cases = []
                outputs = {}
                for label, data in fixtures(channels, size, size):
                    output = session.run(None, {session.get_inputs()[0].name: data})[0]
                    if not np.isfinite(output).all():
                        raise ValueError(f'{path.stem}/{label}: non-finite CPU output')
                    data.tofile(reference_dir / f'{label}-input.f32')
                    output.tofile(reference_dir / f'{label}-output.f32')
                    cases.append({'name': label, 'inputShape': list(data.shape), 'outputShape': list(output.shape)})
                    outputs[label] = output
                temporal_delta = None
                if channels == 15:
                    data = dict(fixtures(channels, size, size))['edges']
                    changed = data.copy()
                    changed[:, :3] = 1 - changed[:, :3]
                    other = session.run(None, {session.get_inputs()[0].name: changed})[0]
                    temporal_delta = float(np.mean(np.abs(outputs['edges'] - other)))
                    if temporal_delta <= 1e-7:
                        raise ValueError('EDVR output did not depend on a neighbouring frame')
                adapted = rgb_wrapper(path) if channels == 1 else path
                records.append({'name': path.stem, 'family': family, 'path': str(path.relative_to(work)),
                                'rgbPath': str(adapted.relative_to(work)), 'rgbSha256': sha(adapted),
                                'sha256': sha(path), 'channels': channels, 'nativeScale': 4 if family == 'edvr' else 2,
                                'window': 128 if family == 'edvr' else 384,
                                'fixedSize': family == 'edvr', 'referenceSize': size, 'cases': cases,
                                'temporalNeighbourMae': temporal_delta,
                                'operators': sorted({f'{node.domain}:{node.op_type}' for node in graph.graph.node})})
                print(f'REFERENCE {path.stem}: {channels}ch, {cases[0]["outputShape"]}', flush=True)
        except Exception as ex:
            traceback.print_exc()
            errors.append({'family': family, 'error': str(ex)})
        manifest_path = work / 'manifest.json'
        if args.family and manifest_path.exists():
            previous = json.loads(manifest_path.read_text(encoding='utf-8'))
            merged = [x for x in previous['models'] if x['family'] not in families] + records
        else:
            merged = records
        manifest_path.write_text(json.dumps({'models': merged, 'exportErrors': errors}, indent=2), encoding='utf-8')
    provenance = {'python': sys.version, 'torch': torch.__version__, 'onnx': onnx.__version__,
                  'onnxruntime': ort.__version__, 'numpy': np.__version__, 'sources': {}}
    for repo in upstream.iterdir():
        if (repo / '.git').exists():
            provenance['sources'][repo.name] = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    provenance['weights'] = {p.name: sha(p) for p in (work / 'weights').iterdir() if p.is_file()}
    (work / 'provenance.json').write_text(json.dumps(provenance, indent=2), encoding='utf-8')
    checks_path = work / 'export-checks.json'
    if args.family and checks_path.exists():
        current_names = {x['name'] for x in records}
        export_checks = [x for x in json.loads(checks_path.read_text(encoding='utf-8'))
                         if x['model'] not in current_names] + export_checks
    checks_path.write_text(json.dumps(export_checks, indent=2), encoding='utf-8')
    return bool(errors)


if __name__ == '__main__':
    raise SystemExit(main())
