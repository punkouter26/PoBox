// The crowd: one mesh of camera-facing quads, one draw call for the whole hall.
//
// Every quad carries its pivot in POSITION, its corner (-0.5..0.5, 0..1) in TEXCOORD1, a random phase and
// energy in TEXCOORD2, and its shirt colour in COLOR. The vertex shader turns each quad to face the camera
// about the vertical and moves it with the fight: an idle sway, a bounce whose height and rate follow
// _PoBoxCrowdMood (the excitement reading) and a jump on _PoBoxCrowdBurst (a clean heavy hit, a knockdown).
// The figure itself is drawn in the fragment shader as a head and shoulders, so there is no texture to
// licence or to keep in memory.
//
// Three more things it does with what ArenaMood sends it:
//   _PoBoxCrowdFlash   camera flashes. On a big moment a share of the figures each fire a flash, a different
//                      share every fourteenth of a second, bright enough to bloom.
//   _PoBoxCrowdPhones  phone lights. During the walk-on, a count and the result a share of the figures hold
//                      up a small steady light that sways with them.
//   _PoBoxCrowdSide    who just scored: -1 red, +1 blue. Each half of the hall sits behind its own corner,
//                      two figures in five there wear its colour, and they are the ones who jump highest
//                      when their fighter lands.
Shader "PoBox/Crowd"
{
    Properties
    {
        _Width ("Figure width (m)", Float) = 0.62
        _Height ("Figure height (m)", Float) = 0.95
        _Brightness ("Brightness", Range(0, 2)) = 0.55
        _SkinColor ("Skin", Color) = (0.72, 0.55, 0.44, 1)
        _RedCorner ("Red corner's colour", Color) = (0.89, 0.24, 0.24, 1)
        _BlueCorner ("Blue corner's colour", Color) = (0.20, 0.49, 0.94, 1)
        _FlashGain ("Flash brightness", Range(0, 20)) = 9
        _PhoneGain ("Phone light brightness", Range(0, 10)) = 1.6
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
                half4 _RedCorner;
                half4 _BlueCorner;
                float _FlashGain;
                float _PhoneGain;
            CBUFFER_END

            // Set every frame by ArenaMood.
            float _PoBoxCrowdMood;
            float _PoBoxCrowdBurst;
            float _PoBoxCrowdFlash;
            float _PoBoxCrowdPhones;
            float _PoBoxCrowdSide;

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
                // x: this figure's flash is firing, y: it is holding up a phone, z: where across the quad the phone is
                float3 lights : TEXCOORD3;
            };

            float hash2(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pivot = TransformObjectToWorld(v.positionOS.xyz);

                float3 toCam = _WorldSpaceCameraPos - pivot;
                toCam.y = 0;
                float3 fwd = normalize(toCam + float3(0, 0, 1e-4));
                float3 right = normalize(cross(float3(0, 1, 0), fwd));

                // Which corner this seat is behind: red is at (-x, -z), blue at (+x, +z).
                float side = dot(normalize(pivot.xz + float2(1e-4, 0)), float2(0.7071, 0.7071));
                float blueSide = smoothstep(-0.15, 0.15, side);
                float fan = step(hash2(v.seed * 31.7 + 4.2), 0.4);
                // +1 when this figure's own fighter has just scored, -1 when the other has.
                float cheer = _PoBoxCrowdSide * (blueSide * 2 - 1);

                float mood = saturate(_PoBoxCrowdMood);
                float burst = saturate(_PoBoxCrowdBurst) * (1.0 + 0.5 * cheer * fan);
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
                // The fans wear their corner's colour.
                half3 cornerColour = lerp(_RedCorner.rgb, _BlueCorner.rgb, blueSide);
                o.color = float4(lerp(v.color.rgb, cornerColour * 0.8, fan * 0.8), 1);
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.lift = saturate(bounce * 6 + jump * 3);

                // A flash is one figure in about twelve at the peak, a different twelfth every 1/14 s.
                float tick = floor(t * 14.0);
                float fire = step(hash2(v.seed * 97.0 + tick * 0.618), saturate(_PoBoxCrowdFlash) * 0.085);
                float phone = step(hash2(v.seed * 53.0 + 9.1), saturate(_PoBoxCrowdPhones) * 0.22);
                o.lights = float3(fire, phone, 0.25 + 0.5 * hash2(v.seed * 11.0));
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Head: a circle near the top. Shoulders: an ellipse below it, cut off flat at the seat.
                float2 p = i.uv;
                float head = length((p - float2(0.5, 0.78)) / float2(0.17, 0.18));
                float body = length((p - float2(0.5, 0.18)) / float2(0.46, 0.52));
                float inside = min(head, body);

                // The flash is held in front of the face; the phone above one shoulder.
                float flash = saturate(1.0 - length((p - float2(0.5, 0.72)) / float2(0.20, 0.13))) * i.lights.x;
                float phone = saturate(1.0 - length((p - float2(i.lights.z, 0.93)) / float2(0.05, 0.034))) * i.lights.y;
                clip(max(1.0 - inside, max(flash, phone) - 0.02));

                half isHead = step(head, 1.0);
                half3 shirt = i.color.rgb;
                half3 col = lerp(shirt, _SkinColor.rgb, isHead);
                // Lit from the ring: brighter towards the top of the figure, and brighter when on its feet.
                half shade = _Brightness * (0.55 + 0.45 * p.y) * (1.0 + 0.5 * i.lift);
                col *= shade * step(inside, 1.0);
                col = MixFog(col, i.fog);
                // Lights are not fogged: they are what is seen through it.
                col += half3(1.0, 0.97, 0.92) * flash * flash * _FlashGain;
                col += half3(0.85, 0.92, 1.0) * phone * _PhoneGain;
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
