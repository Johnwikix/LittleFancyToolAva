// HDR 的传递函数、色域与色调映射复用主程序烘焙的浮点 LUT；处理帧留在显存。
struct Rgb { float r,g,b; };
struct LutEntry { float r,g,b,pad; };
__device__ Rgb mix_lut(Rgb rgb,const LutEntry* lut,int n) {
    if(!lut) return rgb;
    float p[3]={fminf(1.f,fmaxf(0.f,rgb.r))*(n-1),fminf(1.f,fmaxf(0.f,rgb.g))*(n-1),fminf(1.f,fmaxf(0.f,rgb.b))*(n-1)};
    int i[3]={min(n-2,(int)p[0]),min(n-2,(int)p[1]),min(n-2,(int)p[2])};
    float f[3]={p[0]-i[0],p[1]-i[1],p[2]-i[2]}; int step[3]={1,n,n*n};
    // 四面体插值：每像素四个 LUT 顶点，避免三线性插值的额外读取。
    for(int a=0;a<2;a++) for(int b=a+1;b<3;b++) if(f[a]<f[b]) {
        float t=f[a];f[a]=f[b];f[b]=t;int k=step[a];step[a]=step[b];step[b]=k;
    }
    int base=i[0]+n*i[1]+n*n*i[2];
    LutEntry a=lut[base],b=lut[base+step[0]],c=lut[base+step[0]+step[1]],d=lut[base+step[0]+step[1]+step[2]];
    return {a.r+(b.r-a.r)*f[0]+(c.r-b.r)*f[1]+(d.r-c.r)*f[2],
            a.g+(b.g-a.g)*f[0]+(c.g-b.g)*f[1]+(d.g-c.g)*f[2],
            a.b+(b.b-a.b)*f[0]+(c.b-b.b)*f[1]+(d.b-c.b)*f[2]};
}
__device__ float code(const unsigned char* p,int pitch,int x,int y,int kind) {
    return kind==2 ? (float)(((const unsigned short*)(p+y*pitch))[x]>>6) : (float)p[y*pitch+x];
}
__device__ Rgb read_rgb(const unsigned char* yy,const unsigned char* uv,int yp,int up,int w,int h,int x,int y,int kind,int matrix,int full) {
    x=max(0,min(w-1,x));y=max(0,min(h-1,y));
    if(kind==0) {
        unsigned v=((const unsigned*)(yy+y*yp))[x];
        return {((v>>16)&255)/255.f,((v>>8)&255)/255.f,(v&255)/255.f};
    }
    float cx=x*.5f,cy=y*.5f-.25f;int ix=(int)floorf(cx),iy=(int)floorf(cy);float fx=cx-ix,fy=cy-iy;
    float u=0,v=0;
    for(int dy=0;dy<2;dy++) for(int dx=0;dx<2;dx++) {
        int sx=max(0,min(w/2-1,ix+dx)),sy=max(0,min(h/2-1,iy+dy));
        float weight=(dx?fx:1-fx)*(dy?fy:1-fy);
        u+=weight*code(uv,up,sx*2,sy,kind);v+=weight*code(uv,up,sx*2+1,sy,kind);
    }
    float factor=kind==2?4.f:1.f,maximum=kind==2?1023.f:255.f;
    float Y=(code(yy,yp,x,y,kind)-(full?0.f:16.f*factor))/(full?maximum:219.f*factor);
    u=(u-128.f*factor)/(full?maximum:224.f*factor);v=(v-128.f*factor)/(full?maximum:224.f*factor);
    float kr=matrix==2?.2627f:matrix==1?.2126f:.299f,kb=matrix==2?.0593f:matrix==1?.0722f:.114f,kg=1-kr-kb;
    return {Y+2*(1-kr)*v,Y-2*kb*(1-kb)/kg*u-2*kr*(1-kr)/kg*v,Y+2*(1-kb)*u};
}
__device__ unsigned quant(float value,int maximum) { return (unsigned)max(0,min(maximum,__float2int_rn(value))); }
__device__ unsigned bgra(Rgb p) { return 0xff000000u|(quant(p.r*255,255)<<16)|(quant(p.g*255,255)<<8)|quant(p.b*255,255); }
extern "C" __global__ void hdr_bgra(const unsigned char* yy,const unsigned char* uv,int yp,int up,int w,int h,int kind,int matrix,int full,const LutEntry* lut,int n,unsigned* dst,int dp) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x<w && y<h) dst[y*dp+x]=bgra(mix_lut(read_rgb(yy,uv,yp,up,w,h,x,y,kind,matrix,full),lut,n));
}
__device__ void write_code(unsigned char* p,int pitch,int x,int y,int ten,float value) {
    if(ten) ((unsigned short*)(p+y*pitch))[x]=(unsigned short)(quant(value,1023)<<6);
    else p[y*pitch+x]=(unsigned char)quant(value,255);
}
__device__ Rgb yuv(Rgb p,int matrix) {
    float kr=matrix==2?.2627f:matrix==1?.2126f:.299f,kb=matrix==2?.0593f:matrix==1?.0722f:.114f;
    float y=kr*p.r+(1-kr-kb)*p.g+kb*p.b;
    return {y,(p.b-y)/(2*(1-kb)),(p.r-y)/(2*(1-kr))};
}
extern "C" __global__ void hdr_yuv(const unsigned char* yy,const unsigned char* uv,int yp,int up,int w,int h,int kind,int matrix,int full,const LutEntry* lut,int n,unsigned char* dstY,unsigned char* dstUv,int dp,int dup,int ten,int outMatrix) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w || y>=h) return;
    Rgb p=yuv(mix_lut(read_rgb(yy,uv,yp,up,w,h,x,y,kind,matrix,full),lut,n),outMatrix);
    float factor=ten?4.f:1.f;
    write_code(dstY,dp,x,y,ten,(16+219*p.r)*factor);
    if(!(x&1) && !(y&1)) {
        float u=0,v=0;
        for(int dy=-1;dy<=2;dy++) for(int dx=-1;dx<=1;dx++) {
            Rgb c=yuv(mix_lut(read_rgb(yy,uv,yp,up,w,h,x+dx,y+dy,kind,matrix,full),lut,n),outMatrix);
            float weight=(dx==0?.5f:.25f)*(dy==0 || dy==1?.375f:.125f);
            u+=c.g*weight;v+=c.b*weight;
        }
        write_code(dstUv,dup,x,y/2,ten,(128+224*u)*factor);
        write_code(dstUv,dup,x+1,y/2,ten,(128+224*v)*factor);
    }
}
extern "C" __global__ void hdr_preview(const unsigned char* yy,const unsigned char* uv,int yp,int up,int w,int h,int kind,int matrix,int full,const LutEntry* lut,int n,unsigned* dst,int dw,int dh) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x<dw && y<dh) {
        int sx=min(w-1,(int)((x+.5f)*w/dw)),sy=min(h-1,(int)((y+.5f)*h/dh));
        dst[y*dw+x]=bgra(mix_lut(read_rgb(yy,uv,yp,up,w,h,sx,sy,kind,matrix,full),lut,n));
    }
}
