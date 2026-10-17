"""开发时重建 CUDA 像素 PTX；应用运行不依赖 Python 或 CUDA Toolkit。"""
import argparse
import ctypes as c
import struct
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--nvrtc', required=True, help='CUDA Toolkit nvrtc64_*.dll 的绝对路径')
args = parser.parse_args()
assets = Path(__file__).resolve().parents[2] / 'Libraries' / 'VideoCuda'
source = assets / 'pixels.cu'
code = source.read_text(encoding='utf-8-sig')
first = code.index('// normalize_lut_begin')
last = code.index('// normalize_lut_end', first)
rows = ',\n'.join('    ' + ','.join(f'0x{struct.unpack("<I", struct.pack("<f", i / 255))[0]:08x}u'
    for i in range(start, start + 8)) for start in range(0, 256, 8))
code = code[:first] + '// normalize_lut_begin\n__device__ const unsigned normalizeLut[256]={\n' + rows + '\n};\n' + code[last:]
source.write_text(code, encoding='utf-8', newline='\r\n')
nvrtc = c.WinDLL(str(Path(args.nvrtc).resolve()))
program = c.c_void_p()

def check(code):
    if code:
        raise RuntimeError(f'NVRTC error {code}')

check(nvrtc.nvrtcCreateProgram(c.byref(program), (assets / 'pixels.cu').read_bytes(), b'pixels.cu', 0, None, None))
try:
    options = (c.c_char_p * 2)(b'--gpu-architecture=compute_75', b'--fmad=false')
    result = nvrtc.nvrtcCompileProgram(program, len(options), options)
    size = c.c_size_t()
    check(nvrtc.nvrtcGetProgramLogSize(program, c.byref(size)))
    log = c.create_string_buffer(size.value)
    check(nvrtc.nvrtcGetProgramLog(program, log))
    if log.value:
        print(log.value.decode())
    check(result)
    check(nvrtc.nvrtcGetPTXSize(program, c.byref(size)))
    output = c.create_string_buffer(size.value)
    check(nvrtc.nvrtcGetPTX(program, output))
    ptx = '\n'.join(line.rstrip() for line in output.value.decode().splitlines()).rstrip() + '\n'
    (assets / 'pixels.ptx').write_text(ptx, encoding='utf-8', newline='\r\n')
    print(f'PTX: {size.value} bytes')
finally:
    check(nvrtc.nvrtcDestroyProgram(c.byref(program)))
