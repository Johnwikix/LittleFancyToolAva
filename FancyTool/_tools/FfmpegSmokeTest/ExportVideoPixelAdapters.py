"""生成无权重的 ONNX GraphProto 前后处理模板；运行时仅拼接，不依赖 Python。"""
from pathlib import Path
import numpy as np
from onnx import helper as H, TensorProto as T, numpy_helper as N

root = Path(__file__).resolve().parents[2] / 'Libraries/VideoPixels'
root.mkdir(parents=True, exist_ok=True)
P = 'fancytool_pixels_v1/'

def export(kind):
    nodes, initial = [], []
    def c(name, data):
        name = P + name
        initial.append(N.from_array(np.asarray(data), name))
        return name
    def n(op, inputs, name, **kw):
        name = P + name
        nodes.append(H.make_node(op, inputs, [name], name=name, **kw))
        return name
    divisor = c('lut', np.arange(256, dtype=np.float32) / np.float32(255)) if kind in ('rife', 'sr32') else c('divisor', np.array(255, dtype=np.float32))
    indices = c('bgr_indices', np.array([2, 1, 0], dtype=np.int64))
    inputs = []
    def frame(name):
        inputs.append(H.make_tensor_value_info(P + name, T.UINT8, [1, 'height', 'width', 4]))
        f = n('Cast', [P + name], name + '_float', to=T.FLOAT)
        if kind in ('rife', 'sr32'):
            f = n('Gather', [divisor, n('Cast', [f], name + '_index', to=T.INT32)], name + '_lookup', axis=0)
        rgb = n('Gather', [f, indices], name + '_rgb', axis=3)
        chw = n('Transpose', [rgb], name + '_chw', perm=[0, 3, 1, 2])
        return chw if kind in ('rife', 'sr32') else n('Div', [chw, divisor], name + '_norm')
    first = frame('first')
    if kind == 'rife':
        second = frame('second')
        inputs.append(H.make_tensor_value_info(P + 'amount', T.FLOAT, [1]))
        one_channel = n('Gather', [first, c('first_channel_index', np.array([0], dtype=np.int64))], 'first_channel', axis=1)
        shape = n('Shape', [one_channel], 'time_shape')
        time = n('Expand', [P + 'amount', shape], 'time')
        nodes.append(H.make_node('Concat', [first, second, time], ['__source_input__'], axis=1, name=P+'source_input'))
    else:
        nodes.append(H.make_node('Cast' if kind == 'sr16' else 'Identity', [first], ['__source_input__'],
            name=P+'source_input', **({'to': T.FLOAT16} if kind == 'sr16' else {})))
    prefix = H.make_graph(nodes, kind + '_prefix', inputs,
        [H.make_tensor_value_info('__source_input__', T.FLOAT16 if kind == 'sr16' else T.FLOAT,
            [1, 7 if kind == 'rife' else 3, 'height', 'width'])], initial)
    (root / (kind + '-prefix.pb')).write_bytes(prefix.SerializeToString())
    nodes, initial = [], []
    divisor = c('out_divisor', np.array(255, dtype=np.float32))
    zero = c('out_zero', np.array(0, dtype=np.float32))
    f = n('Cast', ['__source_output__'], 'result', to=T.FLOAT)
    scaled = n('Mul', [f, divisor], 'scaled')
    if kind == 'rife':
        finite = n('Not', [n('Or', [n('IsNaN', [f], 'nan'), n('IsInf', [f], 'inf')], 'nonfinite')], 'finite')
        clipped = n('Clip', [scaled, zero, divisor], 'clipped')
        low = n('Floor', [clipped], 'low')
        fraction = n('Sub', [clipped, low], 'fraction')
        # 对齐 CPU float 加 0.5 后截断，包括首个舍入边界的 ties-to-even。
        up = n('GreaterOrEqual', [fraction, c('half', np.nextafter(np.float32(.5), np.float32(0)))], 'up')
        rounded = n('Add', [low, n('Cast', [up], 'up_float', to=T.FLOAT)], 'rounded')
        bounded = n('Where', [finite, rounded, zero], 'bounded')
    else:
        bounded = n('Clip', [n('Round', [scaled], 'rounded'), zero, divisor], 'bounded')
    red = n('Gather', [bounded, c('red_index', np.array(0, dtype=np.int64))], 'red', axis=1)
    green = n('Gather', [bounded, c('green_index', np.array(1, dtype=np.int64))], 'green', axis=1)
    blue = n('Gather', [bounded, c('blue_index', np.array(2, dtype=np.int64))], 'blue', axis=1)
    rg = n('Add', [n('Mul', [red,c('red_shift',np.array(65536,dtype=np.float32))], 'red_bits'),
        n('Mul', [green,c('green_shift',np.array(256,dtype=np.float32))], 'green_bits')], 'rg_bits')
    rgb = n('Add', [rg, blue], 'rgb_bits')
    # 所有整数运算均在 ±2^24 内，FP32 可精确表示；INT32 的小端字节直接就是 BGRA。
    nhwc = n('Add', [rgb,c('alpha_bits',np.array(-16777216,dtype=np.float32))], 'bgra_bits')
    if kind != 'rife':
        nhwc = n('Slice', [nhwc, c('crop_start', np.array([64, 64], dtype=np.int64)),
            c('crop_end', np.array([-64, -64], dtype=np.int64)), c('crop_axes', np.array([1, 2], dtype=np.int64))], 'crop')
    n('Cast', [nhwc], 'output', to=T.INT32)
    suffix = H.make_graph(nodes, kind + '_suffix',
        [H.make_tensor_value_info('__source_output__', T.FLOAT16 if kind == 'sr16' else T.FLOAT, [1, 3, 'height', 'width'])],
        [H.make_tensor_value_info(P + 'output', T.INT32, [1, 'out_height', 'out_width'])], initial)
    (root / (kind + '-suffix.pb')).write_bytes(suffix.SerializeToString())

for kind in ('sr16', 'sr32', 'rife'):
    export(kind)

# RAVU R3 的批量 Einsum 在本机 cuDNN 图编译失败；以同一乘加表达式避开矩阵引擎。
r = P + 'ravu/'
nodes = [
    H.make_node('Unsqueeze', ['__source_output__', r+'expand_axes'], [r+'expanded']),
    H.make_node('Mul', ['__source_input__', r+'expanded'], [r+'product']),
    H.make_node('ReduceSum', [r+'product', r+'reduce_axes'], [r+'sum'], keepdims=0),
    H.make_node('Transpose', [r+'sum'], ['__source_result__'], perm=[2, 0, 1])
]
initial = [N.from_array(np.array([2], dtype=np.int64), r+'expand_axes'),
    N.from_array(np.array([3], dtype=np.int64), r+'reduce_axes')]
(root/'ravu3-reduction.pb').write_bytes(H.make_graph(nodes, 'ravu3_reduction', [], [], initial).SerializeToString())
