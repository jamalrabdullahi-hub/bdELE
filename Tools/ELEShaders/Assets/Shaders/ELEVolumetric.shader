// ELE volumetric explosion / smoke volume.
// Drawn on a unit sphere mesh scaled to the cloud's radius. Each pixel marches a ray through the sphere, sampling
// fractal noise for density (boiling with time), lit by the sun with a short shadow march, glowing from a hot core.
// Built-in render pipeline, Unity 2019.4 (KSP 1.12). Cost is controlled from C#: _Steps (8-32) and only a few volumes live.
Shader "ELE/Volumetric"
{
    Properties
    {
        _Density ("Density", Float) = 1.5
        _Steps ("Steps", Float) = 20
        _NoiseScale ("Noise scale", Float) = 2.2
        _Boil ("Boil speed", Float) = 0.35
        _Seed ("Seed", Float) = 0
        _SmokeColor ("Smoke colour", Color) = (0.32, 0.30, 0.28, 1)
        _FireColor ("Fire colour", Color) = (1.0, 0.55, 0.15, 1)
        _Heat ("Heat 0..1", Float) = 1
        _Fade ("Fade 0..1", Float) = 1
        _SunDir ("Sun direction (object space)", Vector) = (0, 1, 0, 0)
        _SunColor ("Sun colour", Color) = (1, 0.97, 0.9, 1)
        _Ambient ("Ambient", Float) = 0.35
        _Flatten ("Flatten bottom", Float) = 0
        _Shape ("Shape (0 cloud, 1 vapor cone)", Float) = 0
        _Thickness ("Cone shell thickness", Float) = 0.09
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Front          // render the inside faces so it still works with the camera inside the cloud
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float _Density, _Steps, _NoiseScale, _Boil, _Seed, _Heat, _Fade, _Ambient, _Flatten, _Shape, _Thickness;
            float4 _SmokeColor, _FireColor, _SunDir, _SunColor;

            struct v2f { float4 pos : SV_POSITION; float3 objPos : TEXCOORD0; float3 objCam : TEXCOORD1; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.objPos = v.vertex.xyz;
                o.objCam = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                return o;
            }

            // Cheap value noise + fbm (no textures needed)
            float hash(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
            float noise(float3 x)
            {
                float3 i = floor(x), f = frac(x); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(hash(i), hash(i + float3(1,0,0)), f.x), lerp(hash(i + float3(0,1,0)), hash(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(hash(i + float3(0,0,1)), hash(i + float3(1,0,1)), f.x), lerp(hash(i + float3(0,1,1)), hash(i + float3(1,1,1)), f.x), f.y), f.z);
            }
            float fbm(float3 p) { float a = 0.5, s = 0; for (int i = 0; i < 4; i++) { s += a * noise(p); p *= 2.03; a *= 0.5; } return s; }

            // Density in the unit sphere: radial falloff * billowing noise
            // Transonic vapor cone: a thin bell-shaped shell around the +y (flight) axis, densest just behind the front,
            // thinning toward the tail, with fine streaky noise (condensation in the low-pressure region).
            float coneDensity(float3 p)
            {
                float y = p.y;                                  // +1 = nose side, -1 = tail side
                if (y > 0.55 || y < -1.0) return 0;
                float rad = length(p.xz);
                float R = 0.18 + (0.55 - y) * 0.42;             // widens toward the tail
                float shell = exp(-pow((rad - R) / _Thickness, 2));
                float along = saturate((0.55 - y) * 3.0) * saturate((y + 1.0) * 1.2); // fade in at the front edge, out at the tail
                float3 q = float3(atan2(p.z, p.x) * 3.0, y * 6.0 - _Time.y * 4.0, rad * 8.0) + _Seed;
                float n = 0.55 + 0.45 * noise(q * 1.7) + 0.25 * noise(q * 4.1);
                return shell * along * n * _Density;
            }

            float density(float3 p)
            {
                if (_Shape > 0.5) return coneDensity(p);
                float r = length(p);
                float shape = saturate(1.0 - r);
                if (_Flatten > 0) shape *= saturate((p.y + 1.0 - _Flatten) * 3.0); // sits on the ground
                if (shape <= 0) return 0;
                float3 q = p * _NoiseScale + float3(_Seed, _Seed * 0.7, _Seed * 1.3);
                q.y -= _Time.y * _Boil;
                float n = fbm(q + fbm(q * 0.5) * 0.8);   // domain warp for billows
                return saturate((n - (1.0 - shape) * 0.9) * 2.2) * _Density;
            }

            // Ray / unit sphere: entry and exit distances
            float2 sphere(float3 ro, float3 rd)
            {
                float b = dot(ro, rd), c = dot(ro, ro) - 1.0, h = b * b - c;
                if (h < 0) return float2(1, -1);
                h = sqrt(h);
                return float2(max(-b - h, 0), -b + h);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 ro = i.objCam;
                float3 rd = normalize(i.objPos - i.objCam);
                float2 t = sphere(ro, rd);
                if (t.x > t.y) discard;
                int steps = (int)clamp(_Steps, 6, 40);
                float dt = (t.y - t.x) / steps;
                float3 sun = normalize(_SunDir.xyz);
                float3 col = 0; float trans = 1;
                float jitter = hash(i.objPos * 91.7 + _Time.y) * dt; // breaks up banding
                for (int s = 0; s < 40; s++)
                {
                    if (s >= steps || trans < 0.02) break;
                    float3 p = ro + rd * (t.x + jitter + dt * s);
                    float d = density(p);
                    if (d <= 0.001) continue;
                    // 3-step shadow toward the sun
                    float sh = 0;
                    for (int k = 1; k <= 3; k++) sh += density(p + sun * 0.18 * k);
                    float light = exp(-sh * 0.6) * (1 - _Ambient) + _Ambient;
                    // Hot core: fire where it's dense and near the centre, cooling with _Heat
                    float core = _Shape > 0.5 ? 0 : saturate((1.0 - length(p) * 1.6) * _Heat * 1.5) * saturate(d * 1.5);
                    float3 c = _SmokeColor.rgb * _SunColor.rgb * light + _FireColor.rgb * core * 3.0;
                    float a = 1.0 - exp(-d * dt * 4.0);
                    col += trans * a * c;
                    trans *= 1.0 - a;
                }
                float alpha = (1.0 - trans) * _Fade;
                return fixed4(col * _Fade, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
