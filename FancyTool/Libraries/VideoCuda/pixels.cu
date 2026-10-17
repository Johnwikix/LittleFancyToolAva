// 自有像素内核；仅使用 CUDA Driver API 装载 PTX，运行时无需 CUDA Toolkit。
extern "C" __global__ void pad(const unsigned* src, int pitch, int w, int h, unsigned* dst, int dw, int dh, int ox, int oy) {
    int x=blockIdx.x*blockDim.x+threadIdx.x, y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x<dw && y<dh) { int sx=max(0,min(w-1,x+ox)), sy=max(0,min(h-1,y+oy)); dst[y*dw+x]=src[sy*pitch+sx]; }
}
extern "C" __global__ void paste(const unsigned* src, int sp, unsigned* dst, int dp, int w, int h, int ox, int oy) {
    int x=blockIdx.x*blockDim.x+threadIdx.x, y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x<w && y<h) dst[(y+oy)*dp+x+ox]=src[y*sp+x];
}
__device__ unsigned char clip(float v) { return (unsigned char)max(0,min(255,__float2int_rn(v))); }
extern "C" __global__ void nv12(const unsigned char* yy, const unsigned char* uv, int yp, int up, unsigned* dst, int dp, int w, int h, int bt709, int full) {
    int x=blockIdx.x*blockDim.x+threadIdx.x, y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w || y>=h) return;
    float cx=x*.5f,cy=y*.5f-.25f;
    int ix=(int)floorf(cx),iy=(int)floorf(cy);float fx=cx-ix,fy=cy-iy;
    float U=0,V=0;
    for(int dy=0;dy<2;dy++) for(int dx=0;dx<2;dx++) {
        int sx=max(0,min(w/2-1,ix+dx)),sy=max(0,min(h/2-1,iy+dy));
        float weight=(dx?fx:1-fx)*(dy?fy:1-fy);
        U+=weight*(float)uv[sy*up+sx*2];V+=weight*(float)uv[sy*up+sx*2+1];
    }
    U-=128.f;V-=128.f;float Y=yy[y*yp+x];
    Y=full ? Y : (Y-16.f)*(255.f/219.f);
    float k=full ? 1.f : 255.f/224.f;
    float r=Y+k*(bt709 ? 1.5748f : 1.402f)*V;
    float g=Y-k*(bt709 ? .187324f : .344136f)*U-k*(bt709 ? .468124f : .714136f)*V;
    float b=Y+k*(bt709 ? 1.8556f : 1.772f)*U;
    dst[y*dp+x]=0xff000000u|((unsigned)clip(r)<<16)|((unsigned)clip(g)<<8)|clip(b);
}
extern "C" __global__ void compare(const unsigned* a, int ap, const unsigned* b, int bp, int w, int h, unsigned* result, int scene) {
    int x=blockIdx.x*blockDim.x+threadIdx.x, y=blockIdx.y*blockDim.y+threadIdx.y;
    int different=x<w && y<h && a[y*ap+x]!=b[y*bp+x];
    // 每个线程块最多写一次标志，避免变化画面的数百万次全局原子争用。
    if(__syncthreads_or(different) && threadIdx.x==0 && threadIdx.y==0) atomicOr(result,1u);
    // 场景检测保持原实现的采样点、整数亮度与阈值，只回传两个标志位。
    if(x==0 && y==0 && scene) {
        int ha[32]={0},hb[32]={0},sum=0,count=0;
        for(int sy=0;sy<h;sy+=max(1,h/36)) for(int sx=0;sx<w;sx+=max(1,w/64)) {
            unsigned aa=a[sy*ap+sx],bb=b[sy*bp+sx];
            int la=((aa&255)*29+((aa>>8)&255)*150+((aa>>16)&255)*77)>>8;
            int lb=((bb&255)*29+((bb>>8)&255)*150+((bb>>16)&255)*77)>>8;
            ha[la>>3]++;hb[lb>>3]++;sum+=abs(la-lb);count++;
        }
        int d=0;for(int i=0;i<32;i++) d+=abs(ha[i]-hb[i]);
        result[1]=(sum>count*255*.55 || (sum>count*255*.18 && d>count*.8));
    }
}
// NVENC 接收 NV12 CUDA 帧；显式 BT.709/BT.601 转换避免编码器内部隐式 RGB 矩阵。
__device__ float component(unsigned v,int c,int bt709) {
    float b=v&255,g=(v>>8)&255,r=(v>>16)&255;
    float kr=bt709?.2126f:.299f,kb=bt709?.0722f:.114f;
    float y=kr*r+(1-kr-kb)*g+kb*b;
    return c==0 ? 16.f+y*(219.f/255.f) : 128.f+(c==1 ? (b-y)/(2*(1-kb)) : (r-y)/(2*(1-kr)))*(224.f/255.f);
}
extern "C" __global__ void to_nv12(const unsigned* src,int sp,unsigned char* yy,unsigned char* uv,int yp,int up,int w,int h,int bt709) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w || y>=h) return;
    yy[y*yp+x]=clip(component(src[y*sp+x],0,bt709));
    if(!(x&1) && !(y&1)) {
        float u=0,v=0;
        // 左置水平、居中垂直色度的三角低通；避免直接抽样造成彩边混叠。
        for(int dy=-1;dy<=2;dy++) for(int dx=-1;dx<=1;dx++) {
            unsigned p=src[max(0,min(h-1,y+dy))*sp+max(0,min(w-1,x+dx))];
            float weight=(dx==0?.5f:.25f)*(dy==0 || dy==1?.375f:.125f);
            u+=component(p,1,bt709)*weight;v+=component(p,2,bt709)*weight;
        }
        uv[(y/2)*up+x]=clip(u);uv[(y/2)*up+x+1]=clip(v);
    }
}
// 只下载 UI 所需的小预览，避免为预览回读整幅 4K 帧。
extern "C" __global__ void preview(const unsigned char* a,const unsigned char* uv,int ap,int up,int w,int h,unsigned* dst,int dw,int dh,int nv,int bt709,int full) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=dw || y>=dh) return;
    int sx=min(w-1,(int)((x+.5f)*w/dw)),sy=min(h-1,(int)((y+.5f)*h/dh));
    if(!nv) { dst[y*dw+x]=((const unsigned*)(a+sy*ap))[sx];return; }
    float Y=a[sy*ap+sx],U=(float)uv[(sy/2)*up+(sx/2)*2]-128,V=(float)uv[(sy/2)*up+(sx/2)*2+1]-128;
    Y=full?Y:(Y-16)*(255.f/219.f);float k=full?1.f:255.f/224.f;
    unsigned r=clip(Y+k*(bt709?1.5748f:1.402f)*V);
    unsigned g=clip(Y-k*(bt709?.187324f:.344136f)*U-k*(bt709?.468124f:.714136f)*V);
    unsigned b=clip(Y+k*(bt709?1.8556f:1.772f)*U);
    dst[y*dw+x]=0xff000000u|(r<<16)|(g<<8)|b;
}

// 与 ONNX 像素模板相同的 i/255 FP32 查表，避免近似倒数乘法改变舍入。
// normalize_lut_begin
__device__ const unsigned normalizeLut[256]={
    0x00000000u,0x3b808081u,0x3c008081u,0x3c40c0c1u,0x3c808081u,0x3ca0a0a1u,0x3cc0c0c1u,0x3ce0e0e1u,
    0x3d008081u,0x3d109091u,0x3d20a0a1u,0x3d30b0b1u,0x3d40c0c1u,0x3d50d0d1u,0x3d60e0e1u,0x3d70f0f1u,
    0x3d808081u,0x3d888889u,0x3d909091u,0x3d989899u,0x3da0a0a1u,0x3da8a8a9u,0x3db0b0b1u,0x3db8b8b9u,
    0x3dc0c0c1u,0x3dc8c8c9u,0x3dd0d0d1u,0x3dd8d8d9u,0x3de0e0e1u,0x3de8e8e9u,0x3df0f0f1u,0x3df8f8f9u,
    0x3e008081u,0x3e048485u,0x3e088889u,0x3e0c8c8du,0x3e109091u,0x3e149495u,0x3e189899u,0x3e1c9c9du,
    0x3e20a0a1u,0x3e24a4a5u,0x3e28a8a9u,0x3e2cacadu,0x3e30b0b1u,0x3e34b4b5u,0x3e38b8b9u,0x3e3cbcbdu,
    0x3e40c0c1u,0x3e44c4c5u,0x3e48c8c9u,0x3e4ccccdu,0x3e50d0d1u,0x3e54d4d5u,0x3e58d8d9u,0x3e5cdcddu,
    0x3e60e0e1u,0x3e64e4e5u,0x3e68e8e9u,0x3e6cecedu,0x3e70f0f1u,0x3e74f4f5u,0x3e78f8f9u,0x3e7cfcfdu,
    0x3e808081u,0x3e828283u,0x3e848485u,0x3e868687u,0x3e888889u,0x3e8a8a8bu,0x3e8c8c8du,0x3e8e8e8fu,
    0x3e909091u,0x3e929293u,0x3e949495u,0x3e969697u,0x3e989899u,0x3e9a9a9bu,0x3e9c9c9du,0x3e9e9e9fu,
    0x3ea0a0a1u,0x3ea2a2a3u,0x3ea4a4a5u,0x3ea6a6a7u,0x3ea8a8a9u,0x3eaaaaabu,0x3eacacadu,0x3eaeaeafu,
    0x3eb0b0b1u,0x3eb2b2b3u,0x3eb4b4b5u,0x3eb6b6b7u,0x3eb8b8b9u,0x3ebababbu,0x3ebcbcbdu,0x3ebebebfu,
    0x3ec0c0c1u,0x3ec2c2c3u,0x3ec4c4c5u,0x3ec6c6c7u,0x3ec8c8c9u,0x3ecacacbu,0x3ecccccdu,0x3ecececfu,
    0x3ed0d0d1u,0x3ed2d2d3u,0x3ed4d4d5u,0x3ed6d6d7u,0x3ed8d8d9u,0x3edadadbu,0x3edcdcddu,0x3edededfu,
    0x3ee0e0e1u,0x3ee2e2e3u,0x3ee4e4e5u,0x3ee6e6e7u,0x3ee8e8e9u,0x3eeaeaebu,0x3eececedu,0x3eeeeeefu,
    0x3ef0f0f1u,0x3ef2f2f3u,0x3ef4f4f5u,0x3ef6f6f7u,0x3ef8f8f9u,0x3efafafbu,0x3efcfcfdu,0x3efefeffu,
    0x3f008081u,0x3f018182u,0x3f028283u,0x3f038384u,0x3f048485u,0x3f058586u,0x3f068687u,0x3f078788u,
    0x3f088889u,0x3f09898au,0x3f0a8a8bu,0x3f0b8b8cu,0x3f0c8c8du,0x3f0d8d8eu,0x3f0e8e8fu,0x3f0f8f90u,
    0x3f109091u,0x3f119192u,0x3f129293u,0x3f139394u,0x3f149495u,0x3f159596u,0x3f169697u,0x3f179798u,
    0x3f189899u,0x3f19999au,0x3f1a9a9bu,0x3f1b9b9cu,0x3f1c9c9du,0x3f1d9d9eu,0x3f1e9e9fu,0x3f1f9fa0u,
    0x3f20a0a1u,0x3f21a1a2u,0x3f22a2a3u,0x3f23a3a4u,0x3f24a4a5u,0x3f25a5a6u,0x3f26a6a7u,0x3f27a7a8u,
    0x3f28a8a9u,0x3f29a9aau,0x3f2aaaabu,0x3f2babacu,0x3f2cacadu,0x3f2dadaeu,0x3f2eaeafu,0x3f2fafb0u,
    0x3f30b0b1u,0x3f31b1b2u,0x3f32b2b3u,0x3f33b3b4u,0x3f34b4b5u,0x3f35b5b6u,0x3f36b6b7u,0x3f37b7b8u,
    0x3f38b8b9u,0x3f39b9bau,0x3f3ababbu,0x3f3bbbbcu,0x3f3cbcbdu,0x3f3dbdbeu,0x3f3ebebfu,0x3f3fbfc0u,
    0x3f40c0c1u,0x3f41c1c2u,0x3f42c2c3u,0x3f43c3c4u,0x3f44c4c5u,0x3f45c5c6u,0x3f46c6c7u,0x3f47c7c8u,
    0x3f48c8c9u,0x3f49c9cau,0x3f4acacbu,0x3f4bcbccu,0x3f4ccccdu,0x3f4dcdceu,0x3f4ececfu,0x3f4fcfd0u,
    0x3f50d0d1u,0x3f51d1d2u,0x3f52d2d3u,0x3f53d3d4u,0x3f54d4d5u,0x3f55d5d6u,0x3f56d6d7u,0x3f57d7d8u,
    0x3f58d8d9u,0x3f59d9dau,0x3f5adadbu,0x3f5bdbdcu,0x3f5cdcddu,0x3f5ddddeu,0x3f5ededfu,0x3f5fdfe0u,
    0x3f60e0e1u,0x3f61e1e2u,0x3f62e2e3u,0x3f63e3e4u,0x3f64e4e5u,0x3f65e5e6u,0x3f66e6e7u,0x3f67e7e8u,
    0x3f68e8e9u,0x3f69e9eau,0x3f6aeaebu,0x3f6bebecu,0x3f6cecedu,0x3f6dedeeu,0x3f6eeeefu,0x3f6feff0u,
    0x3f70f0f1u,0x3f71f1f2u,0x3f72f2f3u,0x3f73f3f4u,0x3f74f4f5u,0x3f75f5f6u,0x3f76f6f7u,0x3f77f7f8u,
    0x3f78f8f9u,0x3f79f9fau,0x3f7afafbu,0x3f7bfbfcu,0x3f7cfcfdu,0x3f7dfdfeu,0x3f7efeffu,0x3f800000u
};
// normalize_lut_end
extern "C" __global__ void rife_prepare(const unsigned* a,int ap,const unsigned* b,int bp,int sw,int sh,float* out,int w,int h,float amount) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w || y>=h) return;
    int sx=min(x,sw-1),sy=min(y,sh-1),i=y*w+x,plane=w*h;
    unsigned first=a[sy*ap+sx],second=b[sy*bp+sx];
    out[i]=__uint_as_float(normalizeLut[(first>>16)&255]);
    out[i+plane]=__uint_as_float(normalizeLut[(first>>8)&255]);
    out[i+plane*2]=__uint_as_float(normalizeLut[first&255]);
    out[i+plane*3]=__uint_as_float(normalizeLut[(second>>16)&255]);
    out[i+plane*4]=__uint_as_float(normalizeLut[(second>>8)&255]);
    out[i+plane*5]=__uint_as_float(normalizeLut[second&255]);
    out[i+plane*6]=amount;
}
__device__ unsigned rife_quant(float value) {
    if(!isfinite(value)) return 0;
    float scaled=fminf(255.f,fmaxf(0.f,__fmul_rn(value,255.f)));
    float low=floorf(scaled);
    return (unsigned)low+(scaled-low>=__uint_as_float(0x3effffffu));
}
extern "C" __global__ void rife_pack(const float* src,int sw,int sh,unsigned* dst,int dp,int w,int h) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w || y>=h) return;
    int i=y*sw+x,plane=sw*sh;
    dst[y*dp+x]=0xff000000u|(rife_quant(src[i])<<16)|(rife_quant(src[i+plane])<<8)|rife_quant(src[i+plane*2]);
}
