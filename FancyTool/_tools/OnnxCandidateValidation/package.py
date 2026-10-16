"""Package the six selected RGB models, optional FP16 graphs, and CPU references."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys

import numpy as np
import onnx
import onnxruntime as ort
from onnxruntime.transformers.float16 import convert_float_to_float16

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from prepare import fixtures


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('work', type=Path)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[2] / 'Libraries/VideoSr')
    args = parser.parse_args()
    root, output = args.work.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = json.loads((root / 'manifest.json').read_text(encoding='utf-8-sig'))
    records = []
    for entry in source['models']:
        if entry['family'] not in ['anime4k', 'fsrcnnx', 'ravu']:
            continue
        model_path = root / entry['rgbPath']
        graph = onnx.load(model_path)
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        cpu = ort.InferenceSession(str(model_path), options, providers=['CPUExecutionProvider'])
        name = entry['name']
        for half in ([False, True] if entry['family'] != 'ravu' else [False]):
            filename = name + ('_fp16' if half else '_fp32') + '.onnx'
            target = output / filename
            if half:
                converted = convert_float_to_float16(onnx.load(model_path), keep_io_types=False)
                # The converter appends casts after their consumers for Resize;
                # restore ONNX's required topological order without changing operations.
                pending = list(converted.graph.node)
                available = {value.name for value in converted.graph.input} | {value.name for value in converted.graph.initializer} | {''}
                ordered = []
                while pending:
                    ready = [node for node in pending if all(value in available for value in node.input)]
                    if not ready:
                        raise ValueError('Converted graph has an unresolved input or cycle')
                    for node in ready:
                        ordered.append(node)
                        available.update(node.output)
                        pending.remove(node)
                del converted.graph.node[:]
                converted.graph.node.extend(ordered)
                onnx.checker.check_model(converted)
                onnx.save(converted, target)
            else:
                shutil.copyfile(model_path, target)
            descriptor = dict(entry, name=target.stem, path=str(target), rgbPath=str(target),
                              channels=3, cases=[], precision='fp16' if half else 'fp32')
            refs = root / 'references' / target.stem
            refs.mkdir(parents=True, exist_ok=True)
            for label, data in fixtures(3, 64, 64):
                data.tofile(refs / f'{label}-input.f32')
                cpu.run(None, {cpu.get_inputs()[0].name: data})[0].tofile(refs / f'{label}-output.f32')
                descriptor['cases'].append({'name': label})
            descriptor['sha256'] = hashlib.sha256(target.read_bytes()).hexdigest()
            descriptor['rgbSha256'] = descriptor['sha256']
            records.append(descriptor)
    (root / 'packaged-manifest.json').write_text(json.dumps({'models': records}, indent=2), encoding='utf-8')
    public = [{key: record[key] for key in ['name', 'family', 'precision', 'sha256']} for record in records]
    (output / 'models.json').write_text(json.dumps(public, indent=2), encoding='utf-8')
    print(json.dumps(public, indent=2))


if __name__ == '__main__':
    main()
