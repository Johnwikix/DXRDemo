// Opaque blockers use first-hit termination, without material/normal-map evaluation.
// Thick refractors lit by analytic lights are handled by the photon pass.
Payload TraceGlass(float3 origin,float3 direction,float maximum,inout uint rng) {
    RayDesc ray; ray.Origin=origin; ray.Direction=direction; ray.TMin=Limits.x; ray.TMax=maximum;
    Payload hit=(Payload)0; hit.t=maximum; hit.instance=0xffffffff; hit.random=rng; hit.transparency=8;
#if DXR_USE_SER
    TraceClosestInline(ray,2,hit);
#else
    TraceRay(Scene,RAY_FLAG_NONE,2,0,0,0,ray,hit);
#endif
    rng=hit.random; return hit;
}
float3 TransmissionVisibility(float3 origin,float3 direction,float maximum,inout uint rng,out bool stochastic,bool analytic=false) {
    float3 visibility=1;stochastic=false;
    if(maximum<=Limits.x*2)return 1;
    if(Limits.w==0) return 1;
    for(uint layer=0;layer<24;layer++) {
        // Alpha coverage was already sampled by the opaque query. Skip it on the ordered glass query.
        Payload hit=TraceGlass(origin,direction,maximum,rng);
        stochastic=stochastic||(hit.transparency&1)!=0;
        if(hit.instance==0xffffffff) return visibility;
        Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,0,-direction);
        if(s.transmission<=0) return 0;
        // Only focusing geometry delegates transmission to the photon map. Parallel panes retain
        // continuous analytic lighting instead of replacing it with isolated photon footprints.
        if(analytic && CausticSettings.x>0 && s.causticCaster!=0 && s.thickness>0 && s.ior>1.001) return 0;
        // A thin sheet is surrounded by air on both sides. Evaluate already uses
        // 1/IOR for either facing; using IOR on its back face invents total
        // internal reflection and makes indoor sun shadow rays see an opaque pane.
        float f=Fresnel(saturate(dot(s.g,-direction)),s.eta);
        visibility*=s.transmission*(1-f);
        if(s.thickness==0) visibility*=s.base;
        else if(s.front!=0) visibility*=exp(-Absorption(Materials[s.material])*s.thickness/max(abs(dot(s.g,direction)),.05));
        if(max(max(visibility.x,visibility.y),visibility.z)<1e-5) return 0;
        maximum-=hit.t; if(maximum<=Limits.x*4) return visibility;
        origin=Offset(s,direction);
    }
    return 0;
}
float3 Visibility(float3 origin,float3 direction,float maximum,inout uint rng,out bool stochastic,bool analytic=false) {
    stochastic=false;
    if(maximum<=Limits.x*2) return 0;
    Payload blocker=Trace(origin,direction,maximum,true,rng);
    stochastic=(blocker.transparency&1)!=0;
    if(blocker.instance!=0xffffffff) return 0;
    bool glassStochastic;
    float3 visibility=TransmissionVisibility(origin,direction,maximum,rng,glassStochastic,analytic);
    stochastic=stochastic||glassStochastic;
    return visibility;
}
