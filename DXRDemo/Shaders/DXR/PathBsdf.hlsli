// Renderer-owned material bridge to RTXPT's GGX visible-normal sampling library.
// Evaluation, continuation PDFs and NEE all use the same lobe probabilities.
#include "RTXPT/Microfacet.hlsli"

float3 F0(Surface s) {
    float dielectric = (s.ior - 1) / (s.ior + 1);
    return lerp((dielectric * dielectric).xxx, s.base, s.metallic);
}
float Fresnel(float cosine, float eta) {
    float sin2 = eta * eta * max(0, 1 - cosine * cosine);
    if (sin2 >= 1) return 1;
    float ct = sqrt(1 - sin2);
    float rs = (eta * cosine - ct) / max(eta * cosine + ct, 1e-7);
    float rp = (cosine - eta * ct) / max(cosine + eta * ct, 1e-7);
    return saturate(.5 * (rs * rs + rp * rp));
}
float3 Absorption(Material m) {
    return m.attenuation.w > 0 ? -log(clamp(m.attenuation.rgb, 1e-6, 1)) / m.attenuation.w : 0;
}
// Evaluate stores transmission already multiplied by (1 - metallic). Keep
// material weights separate from the conditional diffuse/transmission choice.
float3 DiffuseAlbedo(Surface s) { return s.base*max(1-s.metallic-s.transmission,0); }
float TransmissionProbability(Surface s) { return saturate(s.transmission/max(1-s.metallic,1e-8)); }
float DielectricFresnel(Surface s, float cosine) {
    if (s.transmission>0) return Fresnel(cosine,s.eta);
    float f0=(s.ior-1)/(s.ior+1); f0*=f0;
    return f0+(1-f0)*pow(1-cosine,5);
}
float3 ReflectionFresnel(Surface s, float cosine, float dielectric) {
    return lerp(dielectric.xxx,s.base+(1-s.base)*pow(1-cosine,5),s.metallic);
}
float G1(float cosine, float a2) { return evalG1GGX(a2, cosine); }
float3 Local(float3 n, float3 v) {
    float3 t = normalize(cross(abs(n.y) < .99 ? float3(0,1,0) : float3(1,0,0), n));
    return t * v.x + cross(n,t) * v.y + n * v.z;
}
float3 ToLocal(float3 n, float3 v) {
    float3 t = normalize(cross(abs(n.y) < .99 ? float3(0,1,0) : float3(1,0,0), n));
    return float3(dot(v,t), dot(v,cross(n,t)), dot(v,n));
}
float SpecProbability(Surface s, float3 view) {
    if (s.metallic == 1) return 1;
    float reflection = saturate(Luminance(approxSpecularIntegralGGX(F0(s), s.roughness*s.roughness, dot(s.n,view))));
    float residual = Luminance(DiffuseAlbedo(s)) + s.transmission*(s.thickness>0?1:Luminance(s.base));
    return clamp(reflection / max(reflection + (1-reflection)*residual, 1e-6), .05, .95);
}
float StableNdfGGX(float alpha,float cosine) {
    // The expanded RTXPT expression cancels alpha^2 at near-delta roughness
    // when cosine rounds to one. Keep the positive terms separate in FP32.
    precise float cosine2=cosine*cosine;
    precise float sine2=1-cosine2;
    precise float denominator=max(sine2,0)+alpha*alpha*cosine2;
    float ratio=alpha/denominator;
    return ratio*ratio/PI;
}
float ReflectionPdf(Surface s, float3 view, float3 direction) {
    if(s.roughness<.001) return 0; // Delta lobes have no solid-angle density.
    float nv = dot(s.n,view), nl = dot(s.n,direction);
    if (nv <= 0 || nl <= 0 || dot(s.g,direction) <= 0) return 0;
    float3 h = SafeNormalize(view+direction,s.n);
    float a = s.roughness*s.roughness;
    return StableNdfGGX(a,saturate(dot(s.n,h))) * evalG1GGX(a*a,nv) / max(4*nv,1e-8);
}
void Bsdf(Surface s, float3 v, float3 l, out float3 diffuse, out float3 specular, out float pdf) {
    diffuse=specular=0; pdf=0;
    float nv=dot(s.n,v), nl=dot(s.n,l);
    if (nv<=0 || nl<=0 || dot(s.g,l)<=0) return;
    float3 h=SafeNormalize(v+l,s.n);
    float nh=saturate(dot(s.n,h)), vh=saturate(dot(v,h)), a=s.roughness*s.roughness;
    float dielectric=DielectricFresnel(s,vh);
    float3 f=ReflectionFresnel(s,vh,dielectric);
    if(s.roughness>=.001) specular=StableNdfGGX(a,nh)*evalMaskingSmithGGXCorrelated(a,nv,nl)*f/max(4*nv*nl,1e-8);
    diffuse=(1-dielectric)*DiffuseAlbedo(s)/PI;
    float p=SpecProbability(s,v), t=TransmissionProbability(s);
    bool tir=s.thickness>0 && s.eta*s.eta*max(0,1-vh*vh)>=1;
    pdf=((1-p)*(1-t)*nl/PI+(p+(tir?(1-p)*t:0))*ReflectionPdf(s,v,l))*s.bsdfSampling;
}

struct ScatterResult {
    float3 direction, weight;
    float pdf, etaScale, lobeProbability;
    uint lobe;
    bool transmitted, valid, delta;
};
ScatterResult Scatter(Surface s, float3 view, float3 u) {
    ScatterResult result=(ScatterResult)0;
    result.etaScale=1;
    float nv=dot(s.n,view);
    if (nv<=0) return result;
    float pSpec=SpecProbability(s,view), t=TransmissionProbability(s), pTrans=(1-pSpec)*t;
    float pDiffuse=(1-pSpec)*(1-t);
    float a=s.roughness*s.roughness;
    if (u.z>=pSpec+pTrans) {
        float radius=sqrt(u.x), phi=2*PI*u.y;
        result.direction=Local(s.n,float3(radius*cos(phi),radius*sin(phi),sqrt(1-u.x)));
        float3 d,r; Bsdf(s,view,result.direction,d,r,result.pdf);
        // Physical lobe decomposition is preserved at every vertex, including the NRD primary split.
        result.weight=d*PI/max(pDiffuse,1e-8);
        result.lobeProbability=pDiffuse;
    } else {
        result.delta=s.roughness<.001;
        float3 micro=result.delta?s.n:Local(s.n,sampleGGX_VNDF(a,ToLocal(s.n,view),u.xy));
        float vm=saturate(dot(view,micro));
        bool volume=s.thickness>0;
        // A smooth thin sheet has coincident entry/exit interfaces: its net
        // transmission is straight through. Negating a reflection reverses the
        // tangential direction and makes oblique windows sample the wrong room.
        // Fold the rough reflection through the macroscopic sheet normal. The
        // two reflections cancel as micro approaches n, so arbitrarily small
        // roughness cannot suddenly mirror the transmitted tangential direction.
        float3 refracted=volume?refract(-view,micro,s.eta):(result.delta?-view:reflect(reflect(-view,micro),s.n));
        bool tir=volume && dot(refracted,refracted)<1e-10;
        result.transmitted=u.z>=pSpec && !tir;
        result.direction=result.transmitted?refracted:reflect(-view,micro);
        float nl=abs(dot(s.n,result.direction));
        float masking=result.delta?1:evalMaskingSmithGGXCorrelated(a,nv,nl)/max(evalG1GGX(a*a,nv),1e-8);
        float dielectric=DielectricFresnel(s,vm);
        float3 f=ReflectionFresnel(s,vm,dielectric);
        if (result.transmitted) {
            result.lobeProbability=pTrans;
            result.weight=(1-dielectric)*s.transmission*masking/max(pTrans,1e-8);
            if (volume) { result.weight*=s.eta*s.eta; result.etaScale=rcp(max(s.eta*s.eta,1e-8)); }
            else result.weight*=s.base;
            // The current NEE sampler has no BTDF proposal. Transmitted light hits get unit MIS weight.
            result.pdf=0;
        } else {
            result.lobeProbability=pSpec+(tir?pTrans:0);
            result.weight=f*masking/max(pSpec+(tir?pTrans:0),1e-8);
            float3 d,r; Bsdf(s,view,result.direction,d,r,result.pdf);
            if(result.delta) result.pdf=0;
        }
        result.lobe=1;
    }
    result.valid=((dot(s.g,result.direction)<0)==result.transmitted) &&
        all(isfinite(result.weight)) && all(isfinite(result.direction)) && any(result.weight>0);
    return result;
}
