Shader "Hidden/ODDGames/html2uxml/GaussianBlur"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Direction ("Direction", Vector) = (1, 0, 0, 0)
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

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _Direction;

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                float2 stepUv = _MainTex_TexelSize.xy * _Direction.xy;
                fixed4 color = tex2D(_MainTex, input.uv) * 0.2270270270;
                color += tex2D(_MainTex, input.uv + stepUv * 1.3846153846) * 0.3162162162;
                color += tex2D(_MainTex, input.uv - stepUv * 1.3846153846) * 0.3162162162;
                color += tex2D(_MainTex, input.uv + stepUv * 3.2307692308) * 0.0702702703;
                color += tex2D(_MainTex, input.uv - stepUv * 3.2307692308) * 0.0702702703;
                return color;
            }
            ENDCG
        }
    }
}
