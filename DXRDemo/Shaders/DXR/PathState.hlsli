// Iterative path state. The medium stack is keyed by instance rather than material:
// two objects may share a material without sharing an interior.
struct PathState {
    float3 origin, direction, throughput;
    float coneWidth, coneSpread, previousPdf, rouletteEtaScale, distanceSinceScatter, fireflyK;
    bool transmitted, delta;
    AreaProposal previousLightProposal;
    // DXR instance IDs are 24-bit. Pack identity + four-bit priority into
    // one word per volume; immutable material values come from the scene tables.
    uint4 mediumLow, mediumHigh;
    uint mediumCount;
};
PathState BeginPath(float3 origin, float3 direction, float spread) {
    PathState path=(PathState)0;
    path.origin=origin; path.direction=direction; path.throughput=1;
    path.coneSpread=spread; path.rouletteEtaScale=1; path.fireflyK=1;
    return path;
}
uint PackedMedium(PathState path,uint slot) { return slot<4?path.mediumLow[slot&3]:path.mediumHigh[slot&3]; }
uint MediumInstance(PathState path,uint slot) { return PackedMedium(path,slot)&0xffffff; }
uint StoredPriority(PathState path,uint slot) { return (PackedMedium(path,slot)>>24)&15; }
void SetMedium(inout PathState path,uint slot,uint packed) {
    if(slot<4) path.mediumLow[slot&3]=packed; else path.mediumHigh[slot&3]=packed;
}
float MediumIor(PathState path,uint slot) { return slot==0?1:max(Materials[Instances[MediumInstance(path,slot)].mesh.z].transmission.y,1); }
float3 MediumAbsorption(PathState path,uint slot) { return slot==0?0:Absorption(Materials[Instances[MediumInstance(path,slot)].mesh.z]); }
uint ActiveMedium(PathState path,uint excluded=0xffffffff) {
    uint best=0;
    for(uint i=1;i<=path.mediumCount;i++)
        if(MediumInstance(path,i)!=excluded && StoredPriority(path,i)>=StoredPriority(path,best)) best=i;
    return best;
}
uint MediumPriority(Surface s) {
    uint priority=(uint)Materials[s.material].transmission.w;
    return priority==0?15:min(priority,15u);
}
bool UpdateMedium(inout PathState path,Surface s,uint instance) {
    for(uint i=1;i<=path.mediumCount;i++) if(MediumInstance(path,i)==instance) {
        if(s.front!=0) return true; // Repeated entry caused by a nearly coplanar face.
        for(uint j=i;j<path.mediumCount;j++) {
            SetMedium(path,j,PackedMedium(path,j+1));
        }
        path.mediumCount--; return true;
    }
    if(s.front==0) return true; // Camera started inside this volume.
    if(path.mediumCount==7) return false;
    uint slot=++path.mediumCount;
    SetMedium(path,slot,instance|(MediumPriority(s)<<24));
    return true;
}
bool FalseInterface(PathState path,Surface s,uint instance) {
    if(s.transmission<=0 || s.thickness<=0) return false;
    uint active=ActiveMedium(path);
    if(MediumPriority(s)<StoredPriority(path,active)) return true;
    if(s.front==0) for(uint i=1;i<=path.mediumCount;i++)
        if(MediumInstance(path,i)==instance && i!=active) return true;
    return false;
}
float InterfaceEta(PathState path, Surface s, uint instance) {
    uint active=ActiveMedium(path);
    float etaI=MediumIor(path,active);
    if(s.front!=0) return etaI/s.ior;
    if(path.mediumCount==0) return s.ior;
    return etaI/MediumIor(path,ActiveMedium(path,instance));
}
bool AdvancePath(inout PathState path, Surface s, uint instance, ScatterResult scatter) {
    if (!scatter.valid) return false;
    path.throughput*=scatter.weight;
    path.rouletteEtaScale*=scatter.etaScale;
    if(!scatter.delta) path.fireflyK=NextFireflyK(path.fireflyK,scatter.pdf,scatter.lobeProbability);
    path.previousPdf=scatter.pdf; path.transmitted=scatter.transmitted;
    path.delta=scatter.delta;
    path.distanceSinceScatter=0;
    path.origin=Offset(s,scatter.direction); path.direction=SafeNormalize(scatter.direction,path.direction);
    if(scatter.transmitted && s.thickness>0 && !UpdateMedium(path,s,instance)) return false;
    // Carry the footprint along the entire path. Rough scattering broadens it;
    // transmission scales the angular spread with the interface IOR ratio.
    path.coneSpread=min(1,path.coneSpread*rsqrt(scatter.etaScale)+
        (scatter.lobe==0?.25:s.roughness*s.roughness*.125));
    return all(isfinite(path.throughput)) && any(path.throughput>0);
}
