// ELE screen distortion: heat haze (fires, exhaust) and the refracting shockwave shell.
// Drawn on a sphere mesh. GrabPass copies the screen once per frame (shared name, so many distortions cost one grab).
//  Mode 0 heat haze: wobbling noise offset, strongest at the centre of the sphere's silhouette, rising with time.
//  Mode 1 shockwave: a thin ring at the silhouette edge (fresnel) bends the scene outward, fading with _Fade.
Shader "ELE/Distort"
{
    Properties
    {
        _Strength ("Strength", Float) = 0.02
        _Mode ("Mode (0 haze, 1 shock)", Float) = 0
        _Fade ("Fade", Float) = 1
        _Speed ("Speed", Float) = 1.5
        _RingWidth ("Ring width", Float) = 0.15
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        ZWrite Off
        Cull Back
        GrabPass { "_ELEGrab" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _ELEGrab;
            float _Strength, _Mode, _Fade, _Speed, _RingWidth;

            struct v2f { float4 pos : SV_POSITION; float4 grab : TEXCOORD0; float3 n : TEXCOORD1; float3 v : TEXCOORD2; float3 wp : TEXCOORD3; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.grab = ComputeGrabScreenPos(o.pos);
                o.n = UnityObjectToWorldNormal(v.normal);
                o.wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.v = normalize(_WorldSpaceCameraPos - o.wp);
                return o;
            }

            float hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }
            float noise(float2 x) { float2 i = floor(x), f = frac(x); f = f * f * (3 - 2 * f);
                return lerp(lerp(hash(i), hash(i + float2(1,0)), f.x), lerp(hash(i + float2(0,1)), hash(i + float2(1,1)), f.x), f.y); }

            fixed4 frag(v2f i) : SV_Target
            {
                float facing = saturate(dot(normalize(i.n), i.v));   // 1 at the centre, 0 at the edge
                float2 uv = i.grab.xy / i.grab.w;
                float2 off;
                if (_Mode < 0.5)
                {
                    float2 q = i.wp.xz * 0.6 + float2(0, -_Time.y * _Speed);
                    off = (float2(noise(q), noise(q + 17.3)) - 0.5) * 2.0 * _Strength * facing * facing;
                }
                else
                {
                    float ring = saturate(1.0 - abs(facing - _RingWidth) / _RingWidth);
                    float2 dir = normalize(mul((float3x3)UNITY_MATRIX_V, i.n).xy + 1e-5);
                    off = dir * ring * _Strength;
                }
                off *= _Fade;
                return tex2D(_ELEGrab, uv + off);
            }
            ENDCG
        }
    }
    Fallback Off
}
