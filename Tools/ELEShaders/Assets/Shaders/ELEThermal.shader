// ELE thermal view for HullCam EO/IR: drawn on a quad just in front of the camera, grabs the finished scene and turns it
// into a thermal-style image: luminance, contrast, darkened background (heat glows are drawn on top by ELE),
// white-hot (Mode 1) or black-hot (Mode 2), slight sensor noise.
Shader "ELE/Thermal"
{
    Properties
    {
        _Mode ("Mode 1 WHOT, 2 BHOT", Float) = 1
        _Contrast ("Contrast", Float) = 1.4
        _Level ("Level", Float) = 0.55
        _Noise ("Noise", Float) = 0.03
    }
    SubShader
    {
        Tags { "Queue" = "Overlay+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        ZWrite Off
        ZTest Always
        Cull Off
        GrabPass { "_ELEThermalGrab" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "ELEPalette.cginc"

            sampler2D _ELEThermalGrab;
            float _Mode, _Contrast, _Level, _Noise;

            struct v2f { float4 pos : SV_POSITION; float4 grab : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.grab = ComputeGrabScreenPos(o.pos);
                return o;
            }

            float hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.grab.xy / i.grab.w;
                float3 c = tex2D(_ELEThermalGrab, uv).rgb;
                float l = dot(c, float3(0.299, 0.587, 0.114));
                l = saturate((l - 0.5) * _Contrast + 0.5) * _Level;
                l += (hash(uv * _ScreenParams.xy + _Time.y) - 0.5) * _Noise;
                if (_Mode > 2.5) return fixed4(Ironbow(saturate(l / max(_Level, 0.01))), 1); // blue-red palette
                if (_Mode > 1.5) l = 1.0 - l;
                return fixed4(l, l, l, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
