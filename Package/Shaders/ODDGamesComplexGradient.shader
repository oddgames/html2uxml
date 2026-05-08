Shader "Hidden/ODDGames/html2uxml/ComplexGradient"
{
    Properties
    {
        _ColorA ("Color A", Color) = (1, 1, 1, 1)
        _ColorB ("Color B", Color) = (0, 0, 0, 1)
        _ColorC ("Color C", Color) = (1, 0.65, 0, 1)
        _Center ("Center", Vector) = (0.5, 0.5, 0, 0)
        _Scale ("Scale", Float) = 1
        _Twist ("Twist", Float) = 0
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

            fixed4 _ColorA;
            fixed4 _ColorB;
            fixed4 _ColorC;
            float4 _Center;
            float _Scale;
            float _Twist;

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                float2 p = (input.uv - _Center.xy) * max(0.001, _Scale);
                float angle = atan2(p.y, p.x) / 6.28318530718 + 0.5 + _Twist;
                float radius = saturate(length(p));
                fixed4 angular = lerp(_ColorA, _ColorB, smoothstep(0.0, 1.0, frac(angle)));
                fixed4 color = lerp(angular, _ColorC, smoothstep(0.1, 1.0, radius));
                return color * input.color;
            }
            ENDCG
        }
    }
}
