// Three path-space planes: primary residual, first smooth reflection, first transmission.
// Radiance still follows the sampled BSDF; deterministic paths only provide guides.
RWTexture2D<float4> StableReflection : register(u20);
RWTexture2D<float4> StableReflectionSurface : register(u21);
RWTexture2D<float4> StableReflectionNormal : register(u22);
RWTexture2D<float4> StableTransmission : register(u23);
RWTexture2D<float4> StableTransmissionSurface : register(u24);
RWTexture2D<float4> StableTransmissionNormal : register(u25);
bool SplitSurface(Surface s) {
    // 低粗糙的建筑玻璃仍用真实 GGX 散射，但须分层采样两条光路。
    // RainyCorner 的玻璃粗糙度为 0.06，镜像金属维持原有阈值。
    return (s.transmission>0 && s.roughness<=.08) || (s.metallic>=.99 && s.roughness<=.05);
}
float3 RotateBetween(float3 v,float3 from,float3 to) {
    float cosine=clamp(dot(from,to),-1,1); float3 axis=cross(from,to); float sine=length(axis);
    if(sine<1e-6) {
        if(cosine>0) return v;
        axis=SafeNormalize(cross(from,abs(from.y)<.9?float3(0,1,0):float3(1,0,0)),float3(0,0,1));
        return 2*axis*dot(axis,v)-v;
    }
    axis/=sine; return v*cosine+cross(axis,v)*sine+axis*dot(axis,v)*(1-cosine);
}
float3x3 InterfaceTransform(Surface s,float3 incoming,float3 outgoing,bool transmitted) {
    float3 x=float3(1,0,0),y=float3(0,1,0),z=float3(0,0,1);
    if(transmitted) return transpose(float3x3(RotateBetween(x,outgoing,incoming),RotateBetween(y,outgoing,incoming),RotateBetween(z,outgoing,incoming)));
    return transpose(float3x3(reflect(x,s.n),reflect(y,s.n),reflect(z,s.n)));
}
// 折射界面保持切平面的坐标，法向按入射/透射方向的深度比缩放。
float3x3 InterfacePositionTransform(Surface s,float3 incoming,float3 outgoing,bool transmitted,float eta,float3x3 normalTransform) {
    if(!transmitted || s.thickness<=0) return normalTransform;
    float scale=clamp(eta*abs(dot(incoming,s.n))/max(abs(dot(outgoing,s.n)),1e-6),.001,1000);
    float3 n=s.n;
    return float3x3(1,0,0,0,1,0,0,0,1)+(scale-1)*float3x3(n.x*n,n.y*n,n.z*n);
}
void StableGuide(Surface first,float3 primary,uint instance,bool transmission,out float4 surface,out float4 normal,out uint interfaces) {
    interfaces=0;
    surface=float4(0,0,0,-1); normal=float4(0,0,1,1);
    PathState path=BeginPath(CameraOrigin.xyz,primary,CameraForward.w*2/Size.y);
    float3x3 imageRotation=float3x3(1,0,0,0,1,0,0,0,1),imagePosition=imageRotation; float3 imageTranslation=0;
    Surface s=first; uint rng=0x73a1982u,object=instance;
    for(uint bounce=0;bounce<12;bounce++) {
        float eta=s.thickness>0?InterfaceEta(path,s,object):1/s.ior;
        float3 outgoing=transmission?(s.thickness>0?refract(path.direction,s.n,eta):path.direction):reflect(path.direction,s.n);
        if(dot(outgoing,outgoing)<1e-8) {
            if(bounce==0) return;
            transmission=false; outgoing=reflect(path.direction,s.n);
        }
        interfaces++;
        float3x3 local=InterfaceTransform(s,path.direction,outgoing,transmission);
        float3x3 positionTransform=InterfacePositionTransform(s,path.direction,outgoing,transmission,eta,local);
        imageTranslation+=mul(imagePosition,s.p-mul(positionTransform,s.p));
        imagePosition=mul(imagePosition,positionTransform);
        imageRotation=mul(imageRotation,local);
        if(transmission && s.thickness>0 && !UpdateMedium(path,s,object)) return;
        path.origin=Offset(s,outgoing); path.direction=outgoing;
        Payload hit;
        bool accepted=false;
        for(uint rejected=0;rejected<16;rejected++) {
            hit=Trace(path.origin,path.direction,CameraRight.w,false,rng,true);
            if(hit.instance==0xffffffff) return;
            s=Evaluate(hit.instance,hit.primitive,hit.bary,hit.t*path.coneSpread,-path.direction); object=hit.instance;
            if(!FalseInterface(path,s,object)) { accepted=true; break; }
            if(!UpdateMedium(path,s,object)) return;
            path.origin=Offset(s,path.direction);
        }
        if(!accepted) return;
        if(!SplitSurface(s)) {
            float3 virtualPosition=mul(imagePosition,s.p)+imageTranslation;
            surface=float4(virtualPosition,length(virtualPosition-CameraOrigin.xyz));
            normal=float4(SafeNormalize(mul(imageRotation,s.n),float3(0,0,1)),s.roughness);
            return;
        }
        // Continue the dominant ideal lobe for guide construction; actual samples remain unmodified.
        transmission=s.transmission>0 && Fresnel(saturate(dot(-path.direction,s.n)),s.thickness>0?InterfaceEta(path,s,object):1/s.ior)<.5;
    }
}
