Shader "Hidden/ODDGames/html2uxml/ColorAdjustFilter"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Brightness ("Brightness", Float) = 1
        _Saturation ("Saturation", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            #include "UnityUIEFilter.cginc"

            sampler2D _MainTex;
            float _Brightness;
            float _Saturation;

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(FilterVertexInput input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                float4 uvRect = GetFilterUVRect(GetFilterRectIndex(input));
                output.uv = lerp(uvRect.xy, uvRect.zw, input.uv);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv);
                float luminance = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
                color.rgb = lerp(luminance.xxx, color.rgb, _Saturation);
                color.rgb *= _Brightness;
                return color;
            }
            ENDCG
        }
    }
}
