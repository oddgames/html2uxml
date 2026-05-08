Shader "Hidden/ODDGames/html2uxml/ScreenBackdrop"
{
    Properties
    {
        _MainTex ("Backdrop", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _UvScaleBias ("UV Scale Bias", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Tint;
            float4 _UvScaleBias;

            struct Attributes
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                fixed4 color : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.screenPos = ComputeScreenPos(output.position);
                output.color = input.color;
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.screenPos.xy / max(0.0001, input.screenPos.w);
                uv = uv * _UvScaleBias.xy + _UvScaleBias.zw;
                fixed4 color = tex2D(_MainTex, uv);
                return color * input.color * _Tint;
            }
            ENDCG
        }
    }
}
