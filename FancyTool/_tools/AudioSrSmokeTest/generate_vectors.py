import numpy as np
import sys
import onnx
from onnx import helper,TensorProto
from pathlib import Path
import importlib.util

root=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else Path.cwd()
spec=importlib.util.spec_from_file_location('dsp',Path(__file__).resolve().parent/'dsp_reference.py')
dsp=importlib.util.module_from_spec(spec);spec.loader.exec_module(dsp)
out=root/'vectors';out.mkdir(exist_ok=True)
rng=np.random.default_rng(123)
t=np.arange(245760)/48000
wave=(.15*np.sin(2*np.pi*440*t)+.08*np.sin(2*np.pi*7800*t)+.01*rng.standard_normal(len(t))).astype(np.float64)
np.save(out/'input.npy',wave)
normalized=dsp.normalize_wav(wave).astype(np.float64)
np.save(out/'normalized.npy',normalized)
magnitude=dsp.stft_magnitude(normalized)
np.save(out/'magnitude.npy',magnitude)
basis=np.load(root/'models-fp32/mel_basis.npy')
np.save(out/'mel.npy',dsp.log_mel(magnitude,basis).astype(np.float32))
np.save(out/'lowpass.npy',dsp.lowpass_simulate(normalized,8437.5,'butter').astype(np.float32))
restored=normalized+.02*np.sin(2*np.pi*19000*t)
np.save(out/'restored.npy',restored)
np.save(out/'anchor.npy',dsp.replace_low_band_stft(restored,normalized))
x=rng.standard_normal(1001).astype(np.float32);v=rng.standard_normal(1001).astype(np.float32);noise=rng.standard_normal(1001).astype(np.float32)
a=.2;prev=.21
sigma=np.sqrt((1-prev)/(1-a)*(1-a/prev))
result=np.sqrt(prev)*(np.sqrt(a)*x-np.sqrt(1-a)*v)+np.sqrt(max(0,1-prev-sigma*sigma))*(np.sqrt(a)*v+np.sqrt(1-a)*x)+sigma*noise
for name,data in [('x',x),('v',v),('noise',noise),('step',result.astype(np.float32))]:np.save(out/(name+'.npy'),data)
print('Independent numpy/scipy STFT, mel, lowpass, anchoring and DDIM vectors generated.')

image=helper.make_tensor_value_info('image',TensorProto.FLOAT,['batch',3,'height','width'])
result=helper.make_tensor_value_info('result',TensorProto.FLOAT,['batch',3,'height','width'])
model=helper.make_model(helper.make_graph([helper.make_node('Identity',['image'],['result'])],'legacy-video',[image],[result]),opset_imports=[helper.make_opsetid('',17)])
model.ir_version=10
onnx.save(model,out/'video_identity.onnx')
