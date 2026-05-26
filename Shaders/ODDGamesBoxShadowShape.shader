Shader "Hidden/ODDGames/html2uxml/BoxShadowShape"
{
    Properties
    {
        _OverlaySize ("Overlay Size", Vector) = (1, 1, 0, 0)
        _SourceRect ("Source Rect", Vector) = (0, 0, 1, 1)
        _Radius ("Radius", Float) = 0
        _LayerCount ("Layer Count", Float) = 0
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

            #define MAX_SHADOWS 8

            float4 _OverlaySize;
            float4 _SourceRect;
            float _Radius;
            float _LayerCount;
            float4 _Shadow0, _Shadow1, _Shadow2, _Shadow3;
            float4 _Shadow4, _Shadow5, _Shadow6, _Shadow7;
            fixed4 _ShadowColor0, _ShadowColor1, _ShadowColor2, _ShadowColor3;
            fixed4 _ShadowColor4, _ShadowColor5, _ShadowColor6, _ShadowColor7;

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

            float4 ShadowData(int index)
            {
                if (index == 0) return _Shadow0;
                if (index == 1) return _Shadow1;
                if (index == 2) return _Shadow2;
                if (index == 3) return _Shadow3;
                if (index == 4) return _Shadow4;
                if (index == 5) return _Shadow5;
                if (index == 6) return _Shadow6;
                return _Shadow7;
            }

            fixed4 ShadowColor(int index)
            {
                if (index == 0) return _ShadowColor0;
                if (index == 1) return _ShadowColor1;
                if (index == 2) return _ShadowColor2;
                if (index == 3) return _ShadowColor3;
                if (index == 4) return _ShadowColor4;
                if (index == 5) return _ShadowColor5;
                if (index == 6) return _ShadowColor6;
                return _ShadowColor7;
            }

            float RoundedRectDistance(float2 p, float4 rect, float radius)
            {
                float2 halfSize = max(rect.zw * 0.5, 0.0001);
                radius = clamp(radius, 0.0, min(halfSize.x, halfSize.y));
                float2 center = rect.xy + halfSize;
                float2 q = abs(p - center) - (halfSize - radius);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }

            fixed4 AlphaOver(fixed4 src, fixed4 dst)
            {
                float outA = src.a + dst.a * (1.0 - src.a);
                float3 outRgb = src.rgb * src.a + dst.rgb * dst.a * (1.0 - src.a);
                outRgb = outA > 0.0001 ? outRgb / outA : 0;
                return fixed4(outRgb, outA);
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                float2 p = input.uv * max(_OverlaySize.xy, 0.0001);
                fixed4 result = fixed4(0, 0, 0, 0);

                [unroll]
                for (int i = 0; i < MAX_SHADOWS; i++)
                {
                    if (i >= _LayerCount)
                        break;
                    float4 s = ShadowData(i);
                    fixed4 c = ShadowColor(i);
                    if (c.a <= 0.0001)
                        continue;

                    // Browser box-shadow blur has a finite falloff that reads
                    // slightly tighter than the raw CSS blur radius when drawn
                    // as an SDF transition in UI Toolkit pixels.
                    float blur = max(0.0, s.z * 0.9);
                    float spread = s.w;
                    float4 r = _SourceRect;
                    r.xy += s.xy - spread.xx;
                    r.zw += spread.xx * 2.0;
                    float radius = max(0.0, _Radius + spread);
                    float dist = RoundedRectDistance(p, r, radius);
                    float alpha = blur <= 0.001
                        ? step(dist, 0.0)
                        : 1.0 - smoothstep(-blur, blur, dist);
                    c.a *= saturate(alpha);
                    result = AlphaOver(c, result);
                }

                return result * input.color;
            }
            ENDCG
        }
    }
}
