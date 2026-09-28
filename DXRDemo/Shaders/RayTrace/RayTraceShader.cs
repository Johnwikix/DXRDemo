using ComputeSharp;

namespace DXRDemo.Shaders.RayTrace;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct RayTraceShader(
    float iTime,
    Float2 iMouse,
    Float2 iResolution,
    int frame,
    float iDist,
    bool isHdrEnabled,
    float sdrWhiteLevelInNits,
    float maxLuminanceInNits,
    int maxBounces,
    int samples,
    IReadWriteNormalizedTexture2D<Float4> normalTexture) : IComputeShader<Float4>
{
    private const float PI = 3.14159265359f;
    private const float PI2 = 6.28318530717f;

    private const int LAMB = 0;
    private const int METAL = 1;
    private const int DIEL = 2;

    private const float Gamma = 2.2f;

    // Sun lighting
    private static readonly Float3 SunDir = Hlsl.Normalize(new Float3(1.0f, 0.8f, -0.5f));
    private static readonly Float3 SunColor = new(3.0f, 2.8f, 2.5f);

    // Sphere 0 — green lambertian
    private static readonly Float3 S0C = new(0, 1, 0);
    private static readonly float S0R = 1.0f;
    private static readonly int S0T = 0;
    private static readonly Float3 S0A = new(0, 0.9f, 0.05f);
    private static readonly float S0P = 0;

    // Sphere 1 — silver metal
    private static readonly Float3 S1C = new(0, 1, 2.5f);
    private static readonly float S1R = 1.0f;
    private static readonly int S1T = 1;
    private static readonly Float3 S1A = new(0.9f, 0.9f, 0.9f);
    private static readonly float S1P = 0.01f;

    // Sphere 2 — glass
    private static readonly Float3 S2C = new(0, 1, -2.5f);
    private static readonly float S2R = 1.0f;
    private static readonly int S2T = 2;
    private static readonly Float3 S2A = new(0, 0, 0);
    private static readonly float S2P = 1.5f;

    // Sphere 3 — ground plane
    private static readonly Float3 S4C = new(0, -1000, 0);
    private static readonly float S4R = 1000.0f;
    private static readonly int S4T = 0;
    private static readonly Float3 S4A = new(0.9f, 0.9f, 0.9f);
    private static readonly float S4P = 0;

    // Prism — upright triangular glass prism for visible refraction
    private static readonly Float3 PrismCenter = new(3.0f, 0.3464f, -2.5f);
    private static readonly float PrismSide = 1.2f;
    private static readonly float PrismDepth = 1.8f;
    private static readonly float PrismAngle = 0.0f;
    private static readonly int PrismT = 2;
    private static readonly float PrismP = 1.52f;

    private static bool ShadowHit(Float3 ro, Float3 rd, float tMin, float tMax)
    {
        float t;
        Float3 n;

        if (HitSphere(ro, rd, S0C, S0R, tMin, tMax, out t, out n)) return true;
        if (HitSphere(ro, rd, S1C, S1R, tMin, tMax, out t, out n)) return true;
        if (HitSphere(ro, rd, S2C, S2R, tMin, tMax, out t, out n)) return true;
        if (HitSphere(ro, rd, S4C, S4R, tMin, tMax, out t, out n)) return true;

        Float3 p2;
        if (HitPrism(ro, rd, tMin, out t, out p2, out n)) return true;

        return false;
    }

    private static Float2 Scale(Float2 v, float s) => new(v.X * s, v.Y * s);
    private static Float3 Scale(Float3 v, float s) => new(v.X * s, v.Y * s, v.Z * s);
    private static Float4 Scale(Float4 v, float s) => new(v.X * s, v.Y * s, v.Z * s, v.W * s);

    private static uint Rng(ref uint state)
    {
        uint old = state;
        state = old * 747796405u + 2891336453u;
        uint word = ((old >> ((int)(old >> 28) + 4)) ^ old) * 277803737u;
        return (word >> 22) ^ word;
    }

    private static float Random(ref uint state)
    {
        return (float)(Rng(ref state) >> 8) / 16777215.0f;
    }

    private static Float3 RandomUnitVector(ref uint state)
    {
        float theta = Random(ref state) * PI2;
        float z = Random(ref state) * 2.0f - 1.0f;
        float a = Hlsl.Sqrt(1.0f - z * z);
        Float3 vector = new(a * Hlsl.Cos(theta), a * Hlsl.Sin(theta), z);
        return Scale(vector, Hlsl.Sqrt(Random(ref state)));
    }

    private static Float3 RayPointAt(Float3 origin, Float3 dir, float t)
    {
        return origin + Scale(dir, t);
    }

    private static float Schlick(float cosine, float ior)
    {
        float r0 = (1.0f - ior) / (1.0f + ior);
        r0 *= r0;
        return r0 + (1.0f - r0) * Hlsl.Pow(1.0f - cosine, 5.0f);
    }

    private static bool HitSphere(Float3 ro, Float3 rd,
        Float3 center, float radius,
        float tMin, float tMax,
        out float hitT, out Float3 hitNormal)
    {
        hitT = 0;
        hitNormal = Float3.Zero;

        Float3 oc = ro - center;
        float a = Hlsl.Dot(rd, rd);
        float b = Hlsl.Dot(oc, rd);
        float c = Hlsl.Dot(oc, oc) - radius * radius;
        float d = b * b - a * c;

        if (d <= 0.0001f)
            return false;

        float t = (-b - Hlsl.Sqrt(d)) / a;
        if (t < tMin)
            t = (-b + Hlsl.Sqrt(d)) / a;

        if (t <= tMin || t >= tMax)
            return false;

        hitT = t;
        Float3 p = RayPointAt(ro, rd, t);
        hitNormal = (p - center) / radius;
        return true;
    }

    private static Float3 RotateZ(Float3 v, float a)
    {
        float cosA = Hlsl.Cos(a);
        float sinA = Hlsl.Sin(a);
        return new Float3(cosA * v.X - sinA * v.Y, sinA * v.X + cosA * v.Y, v.Z);
    }

    private static bool PlaneHit(Float3 ro, Float3 rd, Float3 n, Float3 p0, float tMin, out float t)
    {
        t = 0;
        float denom = Hlsl.Dot(rd, n);
        if (Hlsl.Abs(denom) < 0.000001f)
            return false;
        t = Hlsl.Dot(p0 - ro, n) / denom;
        return t > tMin;
    }

    private static bool InPrismTriangle(float x, float y, float halfSide, float h, float baseY)
    {
        if (y < baseY)
            return false;
        return y <= baseY + h * (1.0f - Hlsl.Abs(x) / halfSide);
    }

    // Triangular prism in local space: centroid at origin, extruded along Z.
    // Works both from outside and from inside (returns min positive t).
    private static bool HitPrism(Float3 ro, Float3 rd, float tMin,
        out float hitT, out Float3 hitPos, out Float3 hitNormal)
    {
        hitT = 1000000.0f;
        hitPos = Float3.Zero;
        hitNormal = Float3.Zero;

        float halfSide = PrismSide * 0.5f;
        float halfDepth = PrismDepth * 0.5f;
        float h = PrismSide * 0.8660254f;
        float baseY = -h / 3.0f;

        Float3 lro = RotateZ(ro - PrismCenter, -PrismAngle);
        Float3 lrd = RotateZ(rd, -PrismAngle);

        Float3 nBase = new Float3(0, -1, 0);
        Float3 nLeft = new Float3(-h, halfSide, 0) / PrismSide;
        Float3 nRight = new Float3(h, halfSide, 0) / PrismSide;

        bool found = false;
        float t;
        Float3 p;
        Float3 bestN = Float3.Zero;
        Float3 bestP = Float3.Zero;

        // Base face (y = baseY)
        if (PlaneHit(lro, lrd, nBase, new Float3(0, baseY, 0), tMin, out t) && t < hitT)
        {
            p = lro + lrd * t;
            if (p.X >= -halfSide && p.X <= halfSide && p.Z >= -halfDepth && p.Z <= halfDepth)
            { found = true; hitT = t; bestN = nBase; bestP = p; }
        }

        // Left slant face (through A and apex C)
        if (PlaneHit(lro, lrd, nLeft, new Float3(-halfSide, baseY, 0), tMin, out t) && t < hitT)
        {
            p = lro + lrd * t;
            if (p.X >= -halfSide && p.X <= 0 && p.Z >= -halfDepth && p.Z <= halfDepth)
            { found = true; hitT = t; bestN = nLeft; bestP = p; }
        }

        // Right slant face (through B and apex C)
        if (PlaneHit(lro, lrd, nRight, new Float3(halfSide, baseY, 0), tMin, out t) && t < hitT)
        {
            p = lro + lrd * t;
            if (p.X >= 0 && p.X <= halfSide && p.Z >= -halfDepth && p.Z <= halfDepth)
            { found = true; hitT = t; bestN = nRight; bestP = p; }
        }

        // Cap faces
        if (PlaneHit(lro, lrd, new Float3(0, 0, -1), new Float3(0, 0, -halfDepth), tMin, out t) && t < hitT)
        {
            p = lro + lrd * t;
            if (InPrismTriangle(p.X, p.Y, halfSide, h, baseY))
            { found = true; hitT = t; bestN = new Float3(0, 0, -1); bestP = p; }
        }

        if (PlaneHit(lro, lrd, new Float3(0, 0, 1), new Float3(0, 0, halfDepth), tMin, out t) && t < hitT)
        {
            p = lro + lrd * t;
            if (InPrismTriangle(p.X, p.Y, halfSide, h, baseY))
            { found = true; hitT = t; bestN = new Float3(0, 0, 1); bestP = p; }
        }

        if (!found)
            return false;

        hitPos = RotateZ(bestP, PrismAngle) + PrismCenter;
        hitNormal = RotateZ(bestN, PrismAngle);
        return true;
    }

    private static bool HitScene(Float3 ro, Float3 rd, float tMin, float tMax,
        out Float3 position, out Float3 normal,
        out int matType, out Float3 matAlbedo, out float matParam)
    {
        float closest = tMax;
        bool hit = false;
        position = Float3.Zero;
        normal = Float3.Zero;
        matType = 0;
        matAlbedo = Float3.Zero;
        matParam = 0;

        float t;
        Float3 n;

        if (HitSphere(ro, rd, S0C, S0R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S0T; matAlbedo = S0A; matParam = S0P; }

        if (HitSphere(ro, rd, S1C, S1R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S1T; matAlbedo = S1A; matParam = S1P; }

        if (HitSphere(ro, rd, S2C, S2R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S2T; matAlbedo = S2A; matParam = S2P; }

        if (HitSphere(ro, rd, S4C, S4R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S4T; matAlbedo = S4A; matParam = S4P; }

        Float3 p2;
        if (HitPrism(ro, rd, tMin, out t, out p2, out n) && t < closest)
        { closest = t; hit = true; position = p2; normal = n; matType = PrismT; matAlbedo = Float3.Zero; matParam = PrismP; }

        return hit;
    }

    private static Float3 Refract(Float3 i, Float3 n, float eta)
    {
        float dt = Hlsl.Dot(i, n);
        float k = 1.0f - eta * eta * (1.0f - dt * dt);
        if (k < 0.0f)
            return Float3.Zero;
        return Scale(i * eta - n * (eta * dt + Hlsl.Sqrt(k)), 1.0f);
    }

    private static Float3 GetSkyColor(Float3 dir)
    {
        float t = dir.Y * 0.5f + 0.5f;
        Float3 bottom = new(0.01f, 0.01f, 0.04f);
        Float3 top = new(0.3f, 0.5f, 0.8f);
        Float3 sky = Hlsl.Lerp(bottom, top, t);

        float sunAngle = Hlsl.Max(Hlsl.Dot(dir, SunDir), 0.0f);
        sky += SunColor * Hlsl.Pow(sunAngle, 200.0f) * 0.5f;

        return sky * 0.3f;
    }

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;
        if (xy.X >= iResolution.X || xy.Y >= iResolution.Y)
            return Float4.Zero;

        Float2 fragCoord = new(xy.X + 0.5f, iResolution.Y - (xy.Y + 0.5f));
        Float2 uv = fragCoord / iResolution;
        Float2 pixelSize = new Float2(1.0f, 1.0f) / iResolution;

        float ratio = iResolution.X / iResolution.Y;

        const float fov = 80.0f;
        float halfWidth = Hlsl.Tan(fov * PI / 360.0f);
        float halfHeight = halfWidth / ratio;

        float dist = iDist;
        Float2 mousePos = iMouse / iResolution;
        if (mousePos.X == 0.0f && mousePos.Y == 0.0f)
            mousePos = new Float2(0.55f, 0.2f);

        float cx = Hlsl.Cos(mousePos.X * 10.0f) * dist;
        float cz = Hlsl.Sin(mousePos.X * 10.0f) * dist;
        float cy = mousePos.Y * 10.0f;

        Float3 origin = new(cx, cy, cz);
        Float3 lookAt = new(0.0f, 1.0f, 0.0f);
        Float3 upVector = new(0.0f, 1.0f, 0.0f);

        Float3 w = Hlsl.Normalize(origin - lookAt);
        Float3 u = Hlsl.Cross(upVector, w);
        Float3 v = Hlsl.Cross(w, u);

        Float3 lowerLeft = origin - Scale(u, halfWidth) - Scale(v, halfHeight) - w;
        Float3 horizontal = Scale(u, halfWidth * 2.0f);
        Float3 vertical = Scale(v, halfHeight * 2.0f);

        uint rngState = (uint)(xy.X * 73856093 + xy.Y * 19349663 + (int)(iTime * 1000.0f) * 16777619 + frame * 83492791);

        Float3 color = Float3.Zero;
        float hitDistEnc = 0;
        Float3 primaryNormal = Float3.Zero;
        float primaryMat = 3.0f;

        for (int s = 0; s < samples; s++)
        {
            Float3 dir = lowerLeft - origin;
            dir += Scale(horizontal, pixelSize.X * Random(ref rngState) + uv.X);
            dir += Scale(vertical, pixelSize.Y * Random(ref rngState) + uv.Y);

            Float3 position, normal, matAlbedo;
            int matType;
            float matParam;
            float firstT = -1;
            Float3 sampleNormal = Float3.Zero;
            float sampleMat = 3.0f;

            // Inline trace loop
            Float3 rayRo = origin;
            Float3 rayRd = dir;
            Float3 accum = Float3.Zero;
            Float3 mask = new(1.0f, 1.0f, 1.0f);

            for (int b = 0; b < maxBounces; b++)
            {
                if (HitScene(rayRo, rayRd, 0.001f, 5000.0f,
                    out position, out normal,
                    out matType, out matAlbedo, out matParam))
                {
                    if (b == 0)
                    {
                        firstT = Hlsl.Length(position - rayRo);
                        sampleNormal = normal;
                        sampleMat = matType;
                    }

                    float ndotl = Hlsl.Dot(normal, SunDir);
                    if (ndotl > 0.0f)
                    {
                        Float3 shadowRo = position + normal * 0.001f;
                        if (!ShadowHit(shadowRo, SunDir, 0.001f, 5000.0f))
                        {
                            if (matType == LAMB)
                            {
                                accum += mask * matAlbedo * SunColor * ndotl * (1.0f / PI);
                            }
                            else if (matType == METAL)
                            {
                                Float3 viewDir = -Hlsl.Normalize(rayRd);
                                Float3 halfVec = Hlsl.Normalize(viewDir + SunDir);
                                float ndoth = Hlsl.Max(Hlsl.Dot(normal, halfVec), 0.0f);
                                float roughness = matParam * matParam + 0.001f;
                                float spec = Hlsl.Pow(ndoth, 1.0f / roughness);
                                accum += mask * matAlbedo * SunColor * spec * ndotl * 0.5f;
                            }
                            else if (matType == DIEL)
                            {
                                Float3 viewDir = -Hlsl.Normalize(rayRd);
                                Float3 halfVec = Hlsl.Normalize(viewDir + SunDir);
                                float ndoth = Hlsl.Max(Hlsl.Dot(normal, halfVec), 0.0f);
                                float fresnel = Schlick(ndoth, matParam);
                                accum += mask * SunColor * Hlsl.Pow(ndoth, 10.0f) * fresnel;
                            }
                        }
                    }

                    // Environment light sampling: sample hemisphere + evaluate sky
                    {
                        float theta = Random(ref rngState) * PI2;
                        float phi = Hlsl.Acos(Random(ref rngState));
                        Float3 up = Hlsl.Abs(normal.Y) < 0.99f ? new Float3(0, 1, 0) : new Float3(1, 0, 0);
                        Float3 x = Hlsl.Normalize(Hlsl.Cross(up, normal));
                        Float3 z = Hlsl.Cross(normal, x);
                        Float3 envDir = x * Hlsl.Sin(phi) * Hlsl.Cos(theta) + normal * Hlsl.Cos(phi) + z * Hlsl.Sin(phi) * Hlsl.Sin(theta);

                        Float3 envRo = position + normal * 0.001f;
                        if (!ShadowHit(envRo, envDir, 0.001f, 5000.0f))
                        {
                            Float3 envColor = GetSkyColor(envDir);
                            envColor = Hlsl.Pow(envColor, new Float3(Gamma, Gamma, Gamma));
                            float ndotenv = Hlsl.Cos(phi);
                            float pdf = 1.0f / (2.0f * PI);

                            if (matType == LAMB)
                            {
                                accum += mask * matAlbedo * envColor * ndotenv * (1.0f / PI) / pdf;
                            }
                            else if (matType == METAL)
                            {
                                Float3 viewDir = -Hlsl.Normalize(rayRd);
                                Float3 halfVec = Hlsl.Normalize(viewDir + envDir);
                                float ndoth = Hlsl.Max(Hlsl.Dot(normal, halfVec), 0.0f);
                                float roughness = matParam * matParam + 0.001f;
                                float spec = Hlsl.Pow(ndoth, 1.0f / roughness);
                                accum += mask * matAlbedo * envColor * spec * ndotenv * 0.5f / pdf;
                            }
                            else if (matType == DIEL)
                            {
                                Float3 viewDir = -Hlsl.Normalize(rayRd);
                                Float3 halfVec = Hlsl.Normalize(viewDir + envDir);
                                float ndoth = Hlsl.Max(Hlsl.Dot(normal, halfVec), 0.0f);
                                float fresnel = Schlick(ndoth, matParam);
                                accum += mask * envColor * Hlsl.Pow(ndoth, 10.0f) * fresnel / pdf;
                            }
                        }
                    }

                    if (matType == LAMB)
                    {
                        Float3 d = normal + RandomUnitVector(ref rngState);
                        rayRo = position;
                        rayRd = d;
                        mask *= matAlbedo;
                    }
                    else if (matType == METAL)
                    {
                        Float3 reflected = Hlsl.Reflect(rayRd, normal);
                        Float3 d = Scale(RandomUnitVector(ref rngState), matParam) + reflected;

                        if (Hlsl.Dot(d, normal) > 0.0f)
                        {
                            rayRo = position;
                            rayRd = d;
                            mask *= matAlbedo;
                        }
                    }
                    else if (matType == DIEL)
                    {
                        Float3 reflected = Hlsl.Reflect(rayRd, normal);
                        Float3 attenuation = new(1.0f, 1.0f, 1.0f);

                        Float3 refracted, outwardNormal;
                        float eta, reflectProb, cosine;

                        float dt = Hlsl.Dot(rayRd, normal);

                        if (dt > 0.0f)
                        {
                            outwardNormal = -normal;
                            eta = matParam;
                            cosine = eta * dt / Hlsl.Length(rayRd);
                        }
                        else
                        {
                            outwardNormal = normal;
                            eta = 1.0f / matParam;
                            cosine = -dt / Hlsl.Length(rayRd);
                        }

                        refracted = Refract(Hlsl.Normalize(rayRd), Hlsl.Normalize(outwardNormal), eta);
                        if (refracted.X != 0.0f || refracted.Y != 0.0f || refracted.Z != 0.0f)
                            reflectProb = Schlick(cosine, matParam);
                        else
                            reflectProb = 1.0f;

                        if (Random(ref rngState) < reflectProb)
                        {
                            rayRo = position;
                            rayRd = reflected;
                        }
                        else
                        {
                            rayRo = position;
                            rayRd = refracted;
                        }

                        mask *= attenuation;
                    }
                }
                else
                {
                    Float3 skyColor = GetSkyColor(Hlsl.Normalize(rayRd));
                    skyColor = Hlsl.Pow(skyColor, new Float3(Gamma, Gamma, Gamma));
                    accum += mask * skyColor;
                    break;
                }
            }

            color += accum;
            hitDistEnc += firstT < 0 ? 1.0f : firstT / (firstT + 1.0f);

            if (s == 0)
            {
                primaryNormal = sampleNormal;
                primaryMat = sampleMat;
            }
        }

        color /= samples;
        hitDistEnc /= samples;

        // Encode the linear radiance for the current display: PQ for HDR10, sRGB gamma for SDR.
        // The denoiser pipeline (temporal accumulation + spatial filter) runs in this same
        // encoded space, so the intermediate buffers can stay in a normalized UNORM format.
        if (isHdrEnabled)
        {
            Float3 nits = Hlsl.Min(color * sdrWhiteLevelInNits, maxLuminanceInNits);

            color = PqEncode(nits);
        }
        else
        {
            color = Hlsl.Pow(Hlsl.Max(color, Float3.Zero), new Float3(1.0f / Gamma, 1.0f / Gamma, 1.0f / Gamma));
        }

        // World-space normal (RGB, stored as n*0.5+0.5) and material id (A) of the primary hit.
        // Sky pixels store a zero normal and material id 3.
        // Pack the integer id into UNORM8 instead of clamping all ids above 1 together.
        normalTexture[xy] = new Float4(primaryNormal * 0.5f + 0.5f, primaryMat / 255.0f);

        return new Float4(color, hitDistEnc);
    }

    // ST 2084 (PQ) inverse EOTF, mapping linear luminance in nits to [0, 1] signal values
    private static Float3 PqEncode(Float3 linearNits)
    {
        linearNits = Hlsl.Max(linearNits, Float3.Zero);

        Float3 n = linearNits / 10000.0f;
        Float3 y = Hlsl.Pow(n, new Float3(0.1593017578125f, 0.1593017578125f, 0.1593017578125f));
        Float3 num = 0.8359375f + 18.8515625f * y;
        Float3 den = 1.0f + 18.6875f * y;

        return Hlsl.Pow(num / den, new Float3(78.84375f, 78.84375f, 78.84375f));
    }
}
