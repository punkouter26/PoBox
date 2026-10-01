// The crowd: one mesh of camera-facing quads, one draw call for the whole hall.
//
// Every quad carries its pivot in POSITION, its corner (-0.5..0.5, 0..1) in TEXCOORD1, a random phase and
// energy in TEXCOORD2, and its shirt colour in COLOR. The vertex shader turns each quad to face the camera
// about the vertical and moves it with the fight: an idle sway, a bounce whose height and rate follow
// _PoBoxCrowdMood (the excitement reading) and a jump on _PoBoxCrowdBurst (a clean heavy hit, a knockdown).
// The figure itself is drawn in the fragment shader as a head and shoulders, so there is no texture to
// licence or to keep in memory.
Shader "PoBox/Crowd"
{
    Properties
    {
        _Width ("Figure width (m)", Float) = 0.62
        _Height ("Figure height (m)", Float) = 0.95
        _Brightness ("Brightness", Range(0, 2)) = 0.55
        _SkinColor ("Skin", Color) = (0.72, 0.55, 0.44, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Width;
                float _Height;
                float _Brightness;
                half4 _SkinColor;
            CBUFFER_END

            // Set every frame by ArenaMood.
            float _PoBoxCrowdMood;
            float _PoBoxCrowdBurst;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float2 corner : TEXCOORD1;
                float2 seed : TEXCOORD2;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float fog : TEXCOORD1;
                float lift : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pivot = TransformObjectToWorld(v.positionOS.xyz);

                float3 toCam = _WorldSpaceCameraPos - pivot;
                toCam.y = 0;
                float3 fwd = normalize(toCam + float3(0, 0, 1e-4));
                float3 right = normalize(cross(float3(0, 1, 0), fwd));

                float mood = saturate(_PoBoxCrowdMood);
                float burst = saturate(_PoBoxCrowdBurst);
                float phase = v.seed.x * 6.2831853;
                float energy = 0.4 + 0.6 * v.seed.y;
                float t = _Time.y;
                float bob = sin(t * 1.7 + phase) * 0.015 * (0.3 + mood);
                float bounce = max(0, sin(t * (4.0 + 2.0 * mood) + phase)) * 0.10 * mood * energy;
                float jump = max(0, sin(t * 9.0 + phase)) * 0.30 * burst * energy;
                float lean = sin(t * 1.3 + phase * 1.7) * 0.05 * (0.2 + mood);

                float cy = v.corner.y;
                float3 pos = pivot
                           + right * (v.corner.x * _Width + lean * cy)
                           + float3(0, 1, 0) * (cy * _Height + bob + bounce + jump);

                o.positionCS = TransformWorldToHClip(pos);
                o.uv = v.uv;
                o.color = v.color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.lift = saturate(bounce * 6 + jump * 3);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Head: a circle near the top. Shoulders: an ellipse below it, cut off flat at the seat.
                float2 p = i.uv;
                float head = length((p - float2(0.5, 0.78)) / float2(0.17, 0.18));
                float body = length((p - float2(0.5, 0.18)) / float2(0.46, 0.52));
                float inside = min(head, body);
                clip(1.0 - inside);

                half isHead = step(head, 1.0);
                half3 shirt = i.color.rgb;
                half3 col = lerp(shirt, _SkinColor.rgb, isHead);
                // Lit from the ring: brighter towards the top of the figure, and brighter when on its feet.
                half shade = _Brightness * (0.55 + 0.45 * p.y) * (1.0 + 0.5 * i.lift);
                col *= shade;
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
