// ELE thermal palette for the targeting pod (FLIR ball) image: maps brightness to a blue-to-red ("ironbow"-style) scale.
// Used with Graphics.Blit on the pod's render texture.
Shader "ELE/ThermalBlit"
{
    Properties
    {
        _MainTex ("Image", 2D) = "white" {}
        _Contrast ("Contrast", Float) = 1.3
    }
    SubShader
    {
        ZWrite Off ZTest Always Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "ELEPalette.cginc"

            sampler2D _MainTex;
            float _Contrast;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float3 c = tex2D(_MainTex, i.uv).rgb;
                float l = dot(c, float3(0.299, 0.587, 0.114));
                l = saturate((l - 0.5) * _Contrast + 0.5);
                return fixed4(Ironbow(l), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
