Shader "Hidden/ODDGames/html2uxml/CssGradient"
{
    Properties
    {
        _FallbackColor ("Fallback Color", Color) = (0, 0, 0, 0)
        _DitherStrength ("Dither Strength", Float) = 1
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

            #define MAX_STOPS 8

            fixed4 _FallbackColor;
            float _DitherStrength;

            float _LinearEnabled;
            float _LinearAngle;
            float _LinearCount;
            fixed4 _LinearColor0, _LinearColor1, _LinearColor2, _LinearColor3;
            fixed4 _LinearColor4, _LinearColor5, _LinearColor6, _LinearColor7;
            float _LinearPos0, _LinearPos1, _LinearPos2, _LinearPos3;
            float _LinearPos4, _LinearPos5, _LinearPos6, _LinearPos7;

            float _Radial1Enabled;
            float _Radial1Count;
            float4 _Radial1Center;
            float4 _Radial1Radius;
            fixed4 _Radial1Color0, _Radial1Color1, _Radial1Color2, _Radial1Color3;
            fixed4 _Radial1Color4, _Radial1Color5, _Radial1Color6, _Radial1Color7;
            float _Radial1Pos0, _Radial1Pos1, _Radial1Pos2, _Radial1Pos3;
            float _Radial1Pos4, _Radial1Pos5, _Radial1Pos6, _Radial1Pos7;

            float _Radial2Enabled;
            float _Radial2Count;
            float4 _Radial2Center;
            float4 _Radial2Radius;
            fixed4 _Radial2Color0, _Radial2Color1, _Radial2Color2, _Radial2Color3;
            fixed4 _Radial2Color4, _Radial2Color5, _Radial2Color6, _Radial2Color7;
            float _Radial2Pos0, _Radial2Pos1, _Radial2Pos2, _Radial2Pos3;
            float _Radial2Pos4, _Radial2Pos5, _Radial2Pos6, _Radial2Pos7;

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

            fixed4 StopColor(int setId, int index)
            {
                if (setId == 0)
                {
                    if (index == 0) return _LinearColor0;
                    if (index == 1) return _LinearColor1;
                    if (index == 2) return _LinearColor2;
                    if (index == 3) return _LinearColor3;
                    if (index == 4) return _LinearColor4;
                    if (index == 5) return _LinearColor5;
                    if (index == 6) return _LinearColor6;
                    return _LinearColor7;
                }
                if (setId == 1)
                {
                    if (index == 0) return _Radial1Color0;
                    if (index == 1) return _Radial1Color1;
                    if (index == 2) return _Radial1Color2;
                    if (index == 3) return _Radial1Color3;
                    if (index == 4) return _Radial1Color4;
                    if (index == 5) return _Radial1Color5;
                    if (index == 6) return _Radial1Color6;
                    return _Radial1Color7;
                }
                if (index == 0) return _Radial2Color0;
                if (index == 1) return _Radial2Color1;
                if (index == 2) return _Radial2Color2;
                if (index == 3) return _Radial2Color3;
                if (index == 4) return _Radial2Color4;
                if (index == 5) return _Radial2Color5;
                if (index == 6) return _Radial2Color6;
                return _Radial2Color7;
            }

            float StopPos(int setId, int index)
            {
                if (setId == 0)
                {
                    if (index == 0) return _LinearPos0;
                    if (index == 1) return _LinearPos1;
                    if (index == 2) return _LinearPos2;
                    if (index == 3) return _LinearPos3;
                    if (index == 4) return _LinearPos4;
                    if (index == 5) return _LinearPos5;
                    if (index == 6) return _LinearPos6;
                    return _LinearPos7;
                }
                if (setId == 1)
                {
                    if (index == 0) return _Radial1Pos0;
                    if (index == 1) return _Radial1Pos1;
                    if (index == 2) return _Radial1Pos2;
                    if (index == 3) return _Radial1Pos3;
                    if (index == 4) return _Radial1Pos4;
                    if (index == 5) return _Radial1Pos5;
                    if (index == 6) return _Radial1Pos6;
                    return _Radial1Pos7;
                }
                if (index == 0) return _Radial2Pos0;
                if (index == 1) return _Radial2Pos1;
                if (index == 2) return _Radial2Pos2;
                if (index == 3) return _Radial2Pos3;
                if (index == 4) return _Radial2Pos4;
                if (index == 5) return _Radial2Pos5;
                if (index == 6) return _Radial2Pos6;
                return _Radial2Pos7;
            }

            fixed4 SampleStops(int setId, float count, float t)
            {
                t = saturate(t);
                if (count <= 1.5)
                    return StopColor(setId, 0);

                fixed4 previousColor = StopColor(setId, 0);
                float previousPos = StopPos(setId, 0);
                [unroll]
                for (int i = 1; i < MAX_STOPS; i++)
                {
                    if (i >= count)
                        break;
                    fixed4 nextColor = StopColor(setId, i);
                    float nextPos = StopPos(setId, i);
                    if (t <= nextPos)
                    {
                        float span = max(0.0001, nextPos - previousPos);
                        return lerp(previousColor, nextColor, saturate((t - previousPos) / span));
                    }
                    previousColor = nextColor;
                    previousPos = nextPos;
                }
                return previousColor;
            }

            fixed4 AlphaOver(fixed4 src, fixed4 dst)
            {
                float outA = src.a + dst.a * (1.0 - src.a);
                float3 outRgb = src.rgb * src.a + dst.rgb * dst.a * (1.0 - src.a);
                outRgb = outA > 0.0001 ? outRgb / outA : 0;
                return fixed4(outRgb, outA);
            }

            // Element pixel size — needed to match CSS gradient line geometry,
            // which is computed in pixel space, not unit-UV space. Without
            // these, non-square elements interpolate at the wrong points along
            // the gradient axis (visible color drift away from the corners).
            float _LinearWidth;
            float _LinearHeight;

            fixed4 SampleLinear(float2 uv)
            {
                float rad = radians(_LinearAngle);
                float2 dir = float2(sin(rad), -cos(rad));
                float W = max(1.0, _LinearWidth);
                float H = max(1.0, _LinearHeight);
                float2 px = float2(uv.x * W, uv.y * H);
                // Project the four element corners into the gradient direction
                // in PIXEL space (matches CSS gradient line length =
                // |W*sin(a)| + |H*cos(a)|).
                float p0 = 0;
                float p1 = W * dir.x;
                float p2 = W * dir.x + H * dir.y;
                float p3 = H * dir.y;
                float pMin = min(min(p0, p1), min(p2, p3));
                float pMax = max(max(p0, p1), max(p2, p3));
                float t = (dot(px, dir) - pMin) / max(0.0001, pMax - pMin);
                return SampleStops(0, _LinearCount, t);
            }

            fixed4 SampleRadial(float2 uv, int setId)
            {
                float2 center = setId == 1 ? _Radial1Center.xy : _Radial2Center.xy;
                float2 radius = max(setId == 1 ? _Radial1Radius.xy : _Radial2Radius.xy, 0.0001);
                float t = length((uv - center) / radius);
                return SampleStops(setId, setId == 1 ? _Radial1Count : _Radial2Count, t);
            }

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                fixed4 color = _FallbackColor;
                if (_LinearEnabled > 0.5)
                    color = AlphaOver(SampleLinear(input.uv), color);
                if (_Radial2Enabled > 0.5)
                    color = AlphaOver(SampleRadial(input.uv, 2), color);
                if (_Radial1Enabled > 0.5)
                    color = AlphaOver(SampleRadial(input.uv, 1), color);

                float dither = (Hash(input.position.xy) - 0.5) * (_DitherStrength / 255.0);
                color.rgb = saturate(color.rgb + dither);
                return color * input.color;
            }
            ENDCG
        }
    }
}
