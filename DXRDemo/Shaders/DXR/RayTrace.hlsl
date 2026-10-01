// RayTrace.hlsl - DXR Path Tracer (lib_6_9)
// Ported 1:1 from ComputeSharpDemo Shaders/RayTrace/RayTraceShader.cs
// (Monte Carlo path tracer with 5 spheres: Lambertian / Metal / Glass + ground)
// Compile with: dxc -T lib_6_9 -D DXR_SHADER_MODEL_69=1 -Fo RayTrace.dxil RayTrace.hlsl
//
// Perf notes: shadow rays are resolved analytically inline (like the ComputeSharp
// original) instead of nested TraceRay calls - 1 TraceRay per bounce only.

#include "SceneData.hlsli"

// ─── Resources ───────────────────────────────────────────
RaytracingAccelerationStructure SceneBVH : register(t0);
RWTexture2D<float4>              RenderTarget : register(u0);
RWTexture2D<float4>              PrevFrame    : register(u1);
ConstantBuffer<SceneConstants>   Scene        : register(b0);

// ─── Payloads ────────────────────────────────────────────
#if DXR_SHADER_MODEL_69
// SM 6.9 requires an explicit payload ABI. Every field is carried between
// ray generation and the hit/miss shaders because this legacy path tracer
// advances the path in ray generation after each TraceRay.
struct [raypayload] PathTracePayload
{
    float3 color          : read(caller,closesthit,miss) : write(caller,closesthit,miss);
    float3 throughput     : read(caller,closesthit,miss) : write(caller,closesthit,miss);
    float3 nextOrigin     : read(caller) : write(caller,closesthit);
    float3 nextDirection  : read(caller) : write(caller,closesthit);
    uint   rngState       : read(caller,closesthit) : write(caller,closesthit);
    int    remainingBounces : read(caller,closesthit) : write(caller,closesthit,miss);
    int    hasHit          : read(caller) : write(caller,closesthit,miss);
};
#else
struct PathTracePayload
{
    float3 color;
    float3 throughput;
    float3 nextOrigin;
    float3 nextDirection;
    uint   rngState;
    int    remainingBounces;
    int    hasHit;
};
#endif

// ─── Constants (mirror RayTraceShader.cs) ─────────────────
static const float  PI         = 3.14159265359;
static const float  PI2        = 6.28318530717;
static const float  Gamma      = 2.2;
static const int    MaxWeight  = 100;
static const int    MaxBounces = 10;
static const int    Samples    = 4;
static const int    LAMB       = 0;
static const int    METAL      = 1;
static const int    DIEL       = 2;
static const float3 SunDir     = normalize(float3(1.0, 0.8, -0.5));
static const float3 SunColor   = float3(3.0, 2.8, 2.5);

// ─── Helpers (mirror RayTraceShader.cs) ───────────────────
float3 Refract(float3 i, float3 n, float eta)
{
    float dt = dot(i, n);
    float k = 1.0 - eta * eta * (1.0 - dt * dt);
    if (k < 0.0)
        return float3(0, 0, 0);
    return eta * i - (eta * dt + sqrt(k)) * n;
}

float3 GetSkyColor(float3 dir)
{
    float t = dir.y * 0.5 + 0.5;
    float3 sky = lerp(float3(0.01, 0.01, 0.04), float3(0.3, 0.5, 0.8), t);

    float sunAngle = max(dot(dir, SunDir), 0.0);
    sky += SunColor * pow(sunAngle, 200.0) * 0.5;

    return sky * 0.3;
}

uint PcgHash(uint state)
{
    uint old = state;
    state = old * 747796405u + 2891336453u;
    uint word = ((old >> ((old >> 28) + 4)) ^ old) * 277803737u;
    return (word >> 22) ^ word;
}

float RandomFloat(inout uint state)
{
    state = PcgHash(state);
    return float(state >> 8) / 16777215.0;
}

float3 RandomUnitVector(inout uint state)
{
    float theta = RandomFloat(state) * PI2;
    float z = RandomFloat(state) * 2.0 - 1.0;
    float a = sqrt(max(1.0 - z * z, 0.0));
    float3 v = float3(a * cos(theta), a * sin(theta), z);
    return v * sqrt(RandomFloat(state));
}

// Analytic sphere intersection (mirror RayTraceShader.HitSphere).
bool HitSphere(float3 ro, float3 rd, float3 center, float radius,
               float tMin, float tMax, out float hitT, out float3 hitNormal)
{
    hitT = 0;
    hitNormal = float3(0, 0, 0);

    float3 oc = ro - center;
    float a = dot(rd, rd);
    float b = dot(oc, rd);
    float c = dot(oc, oc) - radius * radius;
    float d = b * b - a * c;

    if (d <= 0.0001)
        return false;

    float t = (-b - sqrt(d)) / a;
    if (t < tMin)
        t = (-b + sqrt(d)) / a;

    if (t <= tMin || t >= tMax)
        return false;

    hitT = t;
    float3 p = ro + rd * t;
    hitNormal = (p - center) / radius;
    return true;
}

// Analytic shadow test against all 5 spheres (mirror RayTraceShader.ShadowHit).
bool ShadowHit(float3 ro, float3 rd, float tMin, float tMax)
{
    float t;
    float3 n;
    for (int i = 0; i < 5; i++)
    {
        if (HitSphere(ro, rd, Spheres[i].Center, Spheres[i].Radius, tMin, tMax, t, n))
            return true;
    }
    return false;
}

// ─── Ray Generation ──────────────────────────────────────
[shader("raygeneration")]
void RayGen()
{
    uint2 pixel = DispatchRaysIndex().xy;
    uint2 dims  = DispatchRaysDimensions().xy;

    if (pixel.x >= (uint) Scene.Resolution.x || pixel.y >= (uint) Scene.Resolution.y)
        return;

    float2 fragCoord = float2(pixel.x + 0.5, Scene.Resolution.y - (pixel.y + 0.5));
    float2 uv = fragCoord / Scene.Resolution;
    float2 pixelSize = float2(1.0, 1.0) / Scene.Resolution;

    float ratio = Scene.Resolution.x / Scene.Resolution.y;

    const float fov = 80.0;
    float halfWidth  = tan(fov * PI / 360.0);
    float halfHeight = halfWidth / ratio;

    float dist = Scene.CameraDistance;
    float2 mousePos = Scene.Mouse / Scene.Resolution;
    if (mousePos.x == 0.0 && mousePos.y == 0.0)
        mousePos = float2(0.55, 0.2);

    float cx = cos(mousePos.x * 10.0) * dist;
    float cz = sin(mousePos.x * 10.0) * dist;
    float cy = mousePos.y * 10.0;

    float3 origin = float3(cx, cy, cz);
    float3 lookAt = float3(0, 1, 0);
    float3 up     = float3(0, 1, 0);

    float3 w = normalize(origin - lookAt);
    // Faithful port: ComputeSharp does NOT normalize u (|u| = sin(angle(up, w))).
    float3 u = cross(up, w);
    float3 v = cross(w, u);

    float3 lowerLeft  = origin - u * halfWidth - v * halfHeight - w;
    float3 horizontal = u * halfWidth * 2.0;
    float3 vertical   = v * halfHeight * 2.0;

    uint rngState = pixel.x * 73856093u
                  + pixel.y * 19349663u
                  + (uint) (Scene.Time * 1000.0) * 16777619u
                  + (uint) Scene.Frame * 83492791u;

    float3 color = float3(0, 0, 0);

    for (int s = 0; s < Samples; s++)
    {
        float3 dir = lowerLeft - origin
                   + horizontal * (pixelSize.x * RandomFloat(rngState) + uv.x)
                   + vertical   * (pixelSize.y * RandomFloat(rngState) + uv.y);

        float3 rayOrigin = origin;
        float3 rayDir    = normalize(dir);
        float3 accum     = float3(0, 0, 0);
        float3 mask      = float3(1, 1, 1);

        for (int bounce = 0; bounce < MaxBounces; bounce++)
        {
            PathTracePayload payload;
            payload.color            = float3(0, 0, 0);
            payload.throughput       = mask;
            payload.nextOrigin       = float3(0, 0, 0);
            payload.nextDirection    = float3(0, 0, 0);
            payload.rngState         = rngState;
            payload.remainingBounces = MaxBounces - bounce;
            payload.hasHit           = 0;

            RayDesc ray;
            ray.Origin    = rayOrigin;
            ray.Direction = rayDir;
            ray.TMin      = 0.001;
            ray.TMax      = 5000.0;

            TraceRay(SceneBVH, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, payload);

            rngState = payload.rngState;
            accum += payload.color;

            if (payload.hasHit == 0 || payload.remainingBounces <= 0)
                break;

            rayOrigin = payload.nextOrigin;
            rayDir    = payload.nextDirection;
            mask      = payload.throughput;
        }

        color += accum;
    }

    color /= Samples;

    color = pow(max(color, 0.0), float3(1.0 / Gamma, 1.0 / Gamma, 1.0 / Gamma));

    float4 prevColor = PrevFrame[pixel];
    float weight = Scene.Frame < 1 ? 1.0 : min((float) (Scene.Frame + 1), (float) MaxWeight);
    float3 finalColor = lerp(prevColor.rgb, color, 1.0 / weight);

    RenderTarget[pixel] = float4(finalColor, 1.0);
}

// ─── Intersection Shader (Procedural Sphere) ─────────────
// GeometryIndex() (SM 6.5) selects which of the 5 AABB geometries this ray hit.
[shader("intersection")]
void IntersectionSphere()
{
    uint sphereIdx = GeometryIndex();
    SphereData sphere = Spheres[sphereIdx];

    float3 oc = WorldRayOrigin() - sphere.Center;
    float a  = dot(WorldRayDirection(), WorldRayDirection());
    float b  = dot(oc, WorldRayDirection());
    float c  = dot(oc, oc) - sphere.Radius * sphere.Radius;
    float d  = b * b - a * c;

    if (d < 0.0)
        return;

    float sqrtD = sqrt(d);
    float t = (-b - sqrtD) / a;
    if (t < RayTMin())
    {
        t = (-b + sqrtD) / a;
        if (t < RayTMin())
            return;
    }

    Attributes attr = (Attributes)0;
    ReportHit(t, 0, attr);
}

// ─── Closest Hit Shader ──────────────────────────────────
[shader("closesthit")]
void ClosestHit(inout PathTracePayload payload, Attributes attr)
{
    uint sphereIdx = GeometryIndex();
    SphereData sphere = Spheres[sphereIdx];

    float3 hitPos = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    // (p - center) / radius: unit normal, sign flips automatically for negative radius.
    float3 normal = (hitPos - sphere.Center) / sphere.Radius;

    int    matType  = sphere.MaterialType;
    float3 albedo   = sphere.Albedo;
    float  matParam = sphere.MaterialParam;
    float3 viewDir  = -normalize(WorldRayDirection());

    // ── Direct lighting: sun (analytic shadow) ──
    float ndotl = dot(normal, SunDir);
    if (ndotl > 0.0 && !ShadowHit(hitPos + normal * 0.001, SunDir, 0.001, 5000.0))
    {
        float3 halfVec = normalize(viewDir + SunDir);
        float  ndoth   = max(dot(normal, halfVec), 0.0);

        if (matType == LAMB)
        {
            payload.color += payload.throughput * albedo * SunColor
                           * ndotl * (1.0 / PI);
        }
        else if (matType == METAL)
        {
            float roughness = matParam * matParam + 0.001;
            float spec = pow(ndoth, 1.0 / roughness);
            payload.color += payload.throughput * albedo * SunColor
                           * spec * ndotl * 0.5;
        }
        else // DIEL
        {
            float fresnel = Schlick(ndoth, matParam);
            payload.color += payload.throughput * SunColor
                           * pow(ndoth, 10.0) * fresnel;
        }
    }

    // ── Environment light sampling (hemisphere + sky, analytic shadow) ──
    {
        float theta = RandomFloat(payload.rngState) * PI2;
        float phi   = acos(RandomFloat(payload.rngState));
        float3 up = abs(normal.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
        float3 x = normalize(cross(up, normal));
        float3 z = cross(normal, x);
        float3 envDir = x * sin(phi) * cos(theta)
                      + normal * cos(phi)
                      + z * sin(phi) * sin(theta);

        if (!ShadowHit(hitPos + normal * 0.001, envDir, 0.001, 5000.0))
        {
            float3 envColor = pow(GetSkyColor(envDir), float3(Gamma, Gamma, Gamma));
            float  ndotenv  = cos(phi);
            float  pdf      = 1.0 / (2.0 * PI);

            if (matType == LAMB)
            {
                payload.color += payload.throughput * albedo * envColor
                               * ndotenv * (1.0 / PI) / pdf;
            }
            else if (matType == METAL)
            {
                float3 halfVec = normalize(viewDir + envDir);
                float  ndoth   = max(dot(normal, halfVec), 0.0);
                float  roughness = matParam * matParam + 0.001;
                float  spec = pow(ndoth, 1.0 / roughness);
                payload.color += payload.throughput * albedo * envColor
                               * spec * ndotenv * 0.5 / pdf;
            }
            else // DIEL
            {
                float3 halfVec = normalize(viewDir + envDir);
                float  ndoth   = max(dot(normal, halfVec), 0.0);
                float  fresnel = Schlick(ndoth, matParam);
                payload.color += payload.throughput * envColor
                               * pow(ndoth, 10.0) * fresnel / pdf;
            }
        }
    }

    // ── Material scattering ──
    float3 scatterDir = float3(0, 0, 0);
    bool   retry      = false;

    if (matType == LAMB)
    {
        scatterDir = normal + RandomUnitVector(payload.rngState);
        payload.throughput *= albedo;
    }
    else if (matType == METAL)
    {
        float3 reflected = reflect(WorldRayDirection(), normal);
        scatterDir = reflected + RandomUnitVector(payload.rngState) * matParam;
        if (dot(scatterDir, normal) > 0.0)
            payload.throughput *= albedo;
        else
            retry = true; // ComputeSharp retries the same ray
    }
    else // DIEL
    {
        float3 reflected = reflect(WorldRayDirection(), normal);
        float3 outwardNormal;
        float  eta, reflectProb, cosine;
        float  dt = dot(WorldRayDirection(), normal);

        if (dt > 0.0) // ray exiting glass
        {
            outwardNormal = -normal;
            eta = matParam;
            cosine = eta * dt;
        }
        else // ray entering glass
        {
            outwardNormal = normal;
            eta = 1.0 / matParam;
            cosine = -dt;
        }

        float3 refracted = Refract(normalize(WorldRayDirection()), normalize(outwardNormal), eta);
        if (dot(refracted, refracted) > 0.0)
            reflectProb = Schlick(cosine, matParam);
        else
            reflectProb = 1.0; // total internal reflection

        if (RandomFloat(payload.rngState) < reflectProb)
            scatterDir = reflected;
        else
            scatterDir = refracted;
        // Glass does not attenuate throughput
    }

    // ── Push forward next ray into payload ──
    if (retry)
    {
        payload.nextOrigin    = WorldRayOrigin();
        payload.nextDirection = WorldRayDirection();
        payload.hasHit        = 1;
    }
    else if (dot(scatterDir, scatterDir) > 1e-6)
    {
        payload.nextOrigin    = hitPos + normal * 0.001;
        payload.nextDirection = normalize(scatterDir);
        payload.hasHit        = 1;
    }
    else
    {
        payload.hasHit = 0; // terminate path
    }

    payload.remainingBounces--;
}

// ─── Miss Shader (primary ray → sky) ─────────────────────
[shader("miss")]
void Miss(inout PathTracePayload payload)
{
    float3 sky = pow(GetSkyColor(normalize(WorldRayDirection())), float3(Gamma, Gamma, Gamma));

    payload.color += payload.throughput * sky;
    payload.hasHit = 0;
    payload.remainingBounces = 0;
}
