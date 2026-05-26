Shader "Hidden/ODDGames/html2uxml/BoxShadowFilter"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _OffsetX ("Offset X", Float) = 0
        _OffsetY ("Offset Y", Float) = 0
        _Blur ("Blur", Float) = 0
        _ShadowColor ("Shadow Color", Color) = (0, 0, 0, 0.35)
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
            float4 _MainTex_TexelSize;
            float _OffsetX;
            float _OffsetY;
            float _Blur;
            fixed4 _ShadowColor;

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

            float SampleShadowAlpha(float2 uv, float radius)
            {
                if (radius <= 0.001)
                    return tex2D(_MainTex, uv).a;

                float2 texel = _MainTex_TexelSize.xy;
                float alpha = 0.0;
                float weightSum = 0.0;

                [unroll]
                for (int y = -3; y <= 3; y++)
                {
                    [unroll]
                    for (int x = -3; x <= 3; x++)
                    {
                        float2 p = float2(x, y) / 3.0;
                        float dist2 = dot(p, p);
                        float w = exp(-dist2 * 2.25);
                        alpha += tex2D(_MainTex, uv + p * radius * texel).a * w;
                        weightSum += w;
                    }
                }

                return alpha / max(weightSum, 0.0001);
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, input.uv);

                float2 offset = float2(_OffsetX, -_OffsetY) * _MainTex_TexelSize.xy;
                float radius = max(0.0, _Blur);
                float shadowAlpha = SampleShadowAlpha(input.uv - offset, radius);

                fixed4 shadow = _ShadowColor;
                shadow.a *= shadowAlpha;
                shadow.rgb *= shadow.a;

                return src + shadow * (1.0 - src.a);
            }
            ENDCG
        }
    }
}
