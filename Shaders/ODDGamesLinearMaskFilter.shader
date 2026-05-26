Shader "Hidden/ODDGames/html2uxml/LinearMaskFilter"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Angle ("Angle", Float) = 180
        _StartAlpha ("Start Alpha", Float) = 1
        _EndAlpha ("End Alpha", Float) = 0
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
            float _Angle;
            float _StartAlpha;
            float _EndAlpha;

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 localUv : TEXCOORD1;
            };

            Varyings Vert(FilterVertexInput input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                float4 uvRect = GetFilterUVRect(GetFilterRectIndex(input));
                output.uv = lerp(uvRect.xy, uvRect.zw, input.uv);
                output.localUv = input.uv;
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, input.uv);
                float rad = radians(_Angle);
                float2 dir = float2(sin(rad), cos(rad));
                float t = dot(input.localUv - 0.5, dir) + 0.5;
                float alpha = lerp(_StartAlpha, _EndAlpha, saturate(t));
                src.rgb *= alpha;
                src.a *= alpha;
                return src;
            }
            ENDCG
        }
    }
}
