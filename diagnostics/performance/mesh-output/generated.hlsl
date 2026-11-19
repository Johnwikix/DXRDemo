#define __GroupSize__get_X 8
#define __GroupSize__get_Y 8
#define __GroupSize__get_Z 1

static float Random(inout uint state);

static bool BoxHit(float3 ro, float3 inv, float3 lo, float3 hi, float tMax);

bool HitMesh(float3 ro, float3 rd, float tMax, bool anyHit, out float bestT, out int bestId);

static float3 Sky(float3 rd);

cbuffer _ : register(b0)
{
    uint __x;
    uint __y;
    uint __z;
    int width;
    int height;
    int samples;
    int maxBounces;
    int frame;
    int diagnostic;
}

StructuredBuffer<float4> triangles : register(t0);

StructuredBuffer<float4> nodes : register(t1);

RWTexture2D<float4> output : register(u0);

static float Random(inout uint state)
{
    uint old = state;
    state = old * 747796405u + 2891336453u;
    uint word = ((old >> ((int)(old >> 28) + 4)) ^ old) * 277803737u;
    return ((word >> 22) ^ word) * (1.0 / 4.2949673E+09);
}

static bool BoxHit(float3 ro, float3 inv, float3 lo, float3 hi, float tMax)
{
    float3 a = (lo - ro) * inv;
    float3 b = (hi - ro) * inv;
    float3 near = min(a, b), far = max(a, b);
    float enter = max(max(near.x, near.y), max(near.z, 0.0001));
    float exit = min(min(far.x, far.y), min(far.z, tMax));
    return enter <= exit;
}

bool HitMesh(float3 ro, float3 rd, float tMax, bool anyHit, out float bestT, out int bestId)
{
    bestT = tMax;
    bestId = -1;
    float3 inv = float3(abs(rd.x) < 1E-20 ? (rd.x < 0 ? -1E+20 : 1E+20) : 1.0 / rd.x, abs(rd.y) < 1E-20 ? (rd.y < 0 ? -1E+20 : 1E+20) : 1.0 / rd.y, abs(rd.z) < 1E-20 ? (rd.z < 0 ? -1E+20 : 1E+20) : 1.0 / rd.z);
    int node = 0;
    int previous = -1;
    while (node >= 0)
    {
        float4 lo = nodes[node * 3];
        float4 hi = nodes[node * 3 + 1];
        float4 info = nodes[node * 3 + 2];
        int parent = (int)info.z;
        int count = (int)hi.w;
        int axis = (int)info.w;
        bool reverse = (axis == 0 ? rd.x : (axis == 1 ? rd.y : rd.z)) < 0;
        int first = reverse ? (int)info.y : node + 1;
        int second = reverse ? node + 1 : (int)info.y;
        if (previous != parent)
        {
            int next = previous == first ? second : parent;
            previous = node;
            node = next;
            continue;
        }

        if (!BoxHit(ro, inv, lo.xyz, hi.xyz, bestT))
        {
            previous = node;
            node = parent;
            continue;
        }

        if (count == 0)
        {
            previous = node;
            node = first;
            continue;
        }

        int start = (int)lo.w;
        for (int i = 0; i < count; i++)
        {
            int id = start + i;
            float3 v0 = triangles[id * 3].xyz;
            float3 e1 = triangles[id * 3 + 1].xyz;
            float3 e2 = triangles[id * 3 + 2].xyz;
            float3 p = cross(rd, e2);
            float det = dot(e1, p);
            if (abs(det) < 1E-10)
                continue;
            float __reserved__rcp = 1.0 / det;
            float3 s = ro - v0;
            float u = dot(s, p) * __reserved__rcp;
            if (u < 0 || u > 1)
                continue;
            float3 q = cross(s, e1);
            float v = dot(rd, q) * __reserved__rcp;
            if (v < 0 || u + v > 1)
                continue;
            float t = dot(e2, q) * __reserved__rcp;
            if (t >= 0.0001 && t < bestT)
            {
                bestT = t;
                bestId = id;
                if (anyHit)
                    return true;
            }
        }

        previous = node;
        node = parent;
    }

    return bestId >= 0;
}

static float3 Sky(float3 rd)
{
    return lerp(float3(0.45, 0.55, 0.7), float3(0.1, 0.2, 0.4), saturate(rd.y));
}

[NumThreads(__GroupSize__get_X, __GroupSize__get_Y, __GroupSize__get_Z)]
void Execute(uint3 ThreadIds : SV_DispatchThreadID)
{
    if (ThreadIds.x < __x && ThreadIds.y < __y && ThreadIds.z < __z)
    {
        int2 pixel = ThreadIds.xy;
        if (pixel.x >= width || pixel.y >= height)
            return;
        float3 origin = float3(0, 1.35, 2.7);
        float3 forward = normalize(float3(0, 0.9, 0) - origin);
        float3 right = normalize(cross(forward, float3(0, 1, 0)));
        float3 up = cross(right, forward);
        float3 sun = normalize(float3(-1, 2, 1));
        uint rng = (uint)(pixel.x * 73856093 + pixel.y * 19349663 + frame * 83492791 + 12345);
        float3 sum = (float3)0;
        for (int __reserved__sample = 0; __reserved__sample < samples; __reserved__sample++)
        {
            float sx = ((pixel.x + Random(rng)) / width * 2 - 1) * (width / (float)height) * 0.5;
            float sy = (1 - (pixel.y + Random(rng)) / height * 2) * 0.5;
            float3 ro = origin;
            float3 rd = normalize(forward + right * sx + up * sy);
            float3 throughput = float3(1, 1, 1);
            float3 color = (float3)0;
            for (int bounce = 0; bounce < maxBounces; bounce++)
            {
                float meshT;
                int id;
                bool mesh = HitMesh(ro, rd, 1000, false, meshT, id);
                if (diagnostic != 0)
                {
                    if (mesh)
                    {
                        float3 gn = normalize(cross(triangles[id * 3 + 1].xyz, triangles[id * 3 + 2].xyz));
                        output[pixel] = float4(gn * 0.5 + 0.5, meshT);
                    }
                    else
                        output[pixel] = float4(0, 0, 0, -1);
                    return;
                }

                float groundT = rd.y < -1E-08 ? -ro.y / rd.y : 1000;
                bool ground = groundT > 0.0001 && groundT < meshT;
                if (!mesh && !ground)
                {
                    color += throughput * Sky(rd);
                    break;
                }

                float t = ground ? groundT : meshT;
                float3 normal = ground ? float3(0, 1, 0) : normalize(cross(triangles[id * 3 + 1].xyz, triangles[id * 3 + 2].xyz));
                if (dot(normal, rd) > 0)
                    normal = -normal;
                float3 albedo = ground ? float3(0.7, 0.7, 0.7) : float3(0.65, 0.3, 0.12);
                float3 __reserved__point = ro + rd * t;
                ro = __reserved__point + normal * 0.0002;
                float ndotl = max(dot(normal, sun), 0);
                float shadowT;
                int shadowId;
                if (ndotl > 0 && !HitMesh(ro, sun, 1000, true, shadowT, shadowId))
                    color += throughput * albedo * float3(3, 2.8, 2.5) * (ndotl / 3.1415927);
                float r = sqrt(Random(rng));
                float phi = Random(rng) * 6.2831855;
                float3 tangent = normalize(cross(abs(normal.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0), normal));
                float3 bitangent = cross(normal, tangent);
                rd = normalize(tangent * (r * cos(phi)) + bitangent * (r * sin(phi)) + normal * sqrt(max(0, 1 - r * r)));
                throughput *= albedo;
            }

            sum += color;
        }

        output[pixel] = float4(sum / samples, 1);
    }
}