// Blue (cold) -> purple -> red -> orange -> yellow-white (hot)
inline float3 Ironbow(float t)
{
    t = saturate(t);
    float3 c0 = float3(0.02, 0.02, 0.10);
    float3 c1 = float3(0.10, 0.10, 0.65);
    float3 c2 = float3(0.55, 0.05, 0.60);
    float3 c3 = float3(0.90, 0.10, 0.15);
    float3 c4 = float3(1.00, 0.55, 0.05);
    float3 c5 = float3(1.00, 0.97, 0.80);
    if (t < 0.2) return lerp(c0, c1, t / 0.2);
    if (t < 0.4) return lerp(c1, c2, (t - 0.2) / 0.2);
    if (t < 0.6) return lerp(c2, c3, (t - 0.4) / 0.2);
    if (t < 0.8) return lerp(c3, c4, (t - 0.6) / 0.2);
    return lerp(c4, c5, (t - 0.8) / 0.2);
}
