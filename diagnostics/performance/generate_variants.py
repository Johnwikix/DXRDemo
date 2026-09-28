"""Generate diagnostic variants without editing application shaders."""
from pathlib import Path

root = Path(__file__).resolve().parent
source = root / '../../DXRDemo/Shaders/DXR'
out = root / 'variants'
out.mkdir(exist_ok=True)
hlsl = (source / 'RayTrace.hlsl').read_text(encoding='utf-8-sig')
shared = (source / 'SceneData.hlsli').read_text(encoding='utf-8-sig')
(out / 'SceneData.hlsli').write_text(shared, encoding='utf-8')

def save(name, code):
    (out / (name + '.hlsl')).write_text(code, encoding='utf-8')

save('baseline', hlsl)
save('samples2', hlsl.replace('Samples    = 4', 'Samples    = 2'))
save('offset_direction', hlsl.replace('hitPos + normal * 0.001;', 'hitPos + normal * (dot(scatterDir, normal) >= 0 ? 0.001 : -0.001);'))
save('offset_none', hlsl.replace('payload.nextOrigin    = hitPos + normal * 0.001;', 'payload.nextOrigin    = hitPos;'))
save('shadow_unroll', hlsl.replace('for (int i = 0; i < 5; i++)', '[unroll] for (int i = 0; i < 5; i++)'))
fixed_rng = hlsl.replace('state = PcgHash(state);\n    return float(state >> 8) / 16777215.0;', 'uint result = PcgHash(state);\n    state = state * 747796405u + 2891336453u;\n    return float(result >> 8) / 16777215.0;')
save('rng_match_compute', fixed_rng)

constants = '''static const SphereData Spheres[5] = {
 {float3(0,1,0),1,float3(0,.9,.05),0,0,float3(0,0,0)},
 {float3(0,1,2.5),1,float3(.9,.9,.9),1,.01,float3(0,0,0)},
 {float3(0,1,-2.5),1,float3(0,0,0),2,1.5,float3(0,0,0)},
 {float3(0,1,-2.5),-.92,float3(.9,.9,.9),2,1.5,float3(0,0,0)},
 {float3(0,-1000,0),1000,float3(.9,.9,.9),0,0,float3(0,0,0)}
};'''
def literal_scene(code):
    return code.replace('#include "SceneData.hlsli"', shared.replace('StructuredBuffer<SphereData> Spheres : register(t1);', constants))
save('literal_scene', literal_scene(hlsl))
save('literal_unroll', literal_scene(hlsl).replace('for (int i = 0; i < 5; i++)', '[unroll] for (int i = 0; i < 5; i++)'))

start = hlsl.index('// ─── Ray Generation')
intersection = hlsl.index('// ─── Intersection Shader')
closest = hlsl.index('[shader("closesthit")]')
miss = hlsl.index('[shader("miss")]')
shade = hlsl[closest:miss]
shade = shade.replace('[shader("closesthit")]\nvoid ClosestHit(inout PathTracePayload payload, Attributes attr)',
    'void Shade(inout PathTracePayload payload, uint sphereIdx, float3 ro, float3 rd, float hitT)')
shade = shade.replace('    uint sphereIdx = GeometryIndex();\n', '')
shade = shade.replace('WorldRayOrigin()', 'ro').replace('WorldRayDirection()', 'rd').replace('RayTCurrent()', 'hitT')
shade_miss = hlsl[miss:].replace('[shader("miss")]\nvoid Miss(inout PathTracePayload payload)', 'void ShadeMiss(inout PathTracePayload payload, float3 rd)').replace('WorldRayDirection()', 'rd')
trace = '''
void TraceAnalytic(RayDesc ray, inout PathTracePayload payload) {
    float closest = ray.TMax;
    int best = -1;
    [unroll] for (int i=0; i<5; i++) {
        SphereData sphere = Spheres[i];
        float3 oc = ray.Origin - sphere.Center;
        float a = dot(ray.Direction,ray.Direction);
        float b = dot(oc,ray.Direction);
        float c = dot(oc,oc)-sphere.Radius*sphere.Radius;
        float d = b*b-a*c;
        if (d < 0) continue;
        float t = (-b-sqrt(d))/a;
        if (t < ray.TMin) t = (-b+sqrt(d))/a;
        if (t >= ray.TMin && t < closest) { closest=t; best=i; }
    }
    if (best >= 0) Shade(payload,best,ray.Origin,ray.Direction,closest);
    else ShadeMiss(payload,ray.Direction);
}
'''
raygen = hlsl[start:intersection].replace('TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, payload);', 'TraceAnalytic(ray, payload);')
analytic = hlsl[:start] + shade + shade_miss + trace + raygen + hlsl[intersection:]
save('analytic_raygen', analytic)
save('analytic_literal', literal_scene(analytic))
compute = hlsl[:start] + shade + shade_miss + trace + raygen
compute = compute.replace('[shader("raygeneration")]\nvoid RayGen()', '[numthreads(8,8,1)]\nvoid ComputeMain(uint3 tid : SV_DispatchThreadID)').replace('DispatchRaysIndex().xy','tid.xy').replace('DispatchRaysDimensions().xy', '(uint2)Scene.Resolution')
save('analytic.cs', compute)
save('analytic_literal.cs', literal_scene(compute))

# Preserve hardware traversal but move shading to raygen. Only hit id + distance
# cross TraceRay, instead of radiance, throughput, RNG and next-ray state.
lean_raygen = hlsl[start:intersection].replace(
    'TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, payload);',
    '''HitPayload hit; hit.sphereIdx = -1; hit.t = ray.TMax;
            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, hit);
            if (hit.sphereIdx >= 0) Shade(payload, hit.sphereIdx, ray.Origin, ray.Direction, hit.t);
            else ShadeMiss(payload, ray.Direction);''')
lean = hlsl[:start] + 'struct HitPayload { float t; int sphereIdx; };\n' + shade + shade_miss + lean_raygen + hlsl[intersection:closest] + '''
[shader("closesthit")]
void ClosestHit(inout HitPayload hit, Attributes attr) { hit.t=RayTCurrent(); hit.sphereIdx=GeometryIndex(); }
[shader("miss")]
void Miss(inout HitPayload hit) { hit.sphereIdx=-1; }
'''
save('lean_payload', lean)

inline_trace = '''
void TraceAnalytic(RayDesc ray, inout PathTracePayload payload) {
    RayQuery<RAY_FLAG_NONE> q;
    q.TraceRayInline(SceneBVH, RAY_FLAG_NONE, 0xFF, ray);
    float closestT = ray.TMax;
    while (q.Proceed()) {
        if (q.CandidateType() == CANDIDATE_PROCEDURAL_PRIMITIVE) {
            SphereData sphere = Spheres[q.CandidateGeometryIndex()];
            float3 oc = ray.Origin-sphere.Center;
            float a=dot(ray.Direction,ray.Direction), b=dot(oc,ray.Direction);
            float c=dot(oc,oc)-sphere.Radius*sphere.Radius, d=b*b-a*c;
            if (d < 0) continue;
            float t=(-b-sqrt(d))/a;
            if (t < ray.TMin) t=(-b+sqrt(d))/a;
            if (t >= ray.TMin && t < closestT) { q.CommitProceduralPrimitiveHit(t); closestT = t; }
        }
    }
    if (q.CommittedStatus() == COMMITTED_PROCEDURAL_PRIMITIVE_HIT)
        Shade(payload,q.CommittedGeometryIndex(),ray.Origin,ray.Direction,q.CommittedRayT());
    else ShadeMiss(payload,ray.Direction);
}
'''
save('inline_query.cs', compute.replace(trace, inline_trace))
print(f'Generated variants in {out}')
