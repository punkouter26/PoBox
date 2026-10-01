// The telemetry a fighter wears.
//
// A second material on every fighter renderer, drawn over the lit body. The body underneath stays the
// stock URP Lit shader (so it takes the ring lights and casts shadows like everything else); this pass adds
// what the simulation is saying about the link it is on, fed per renderer through a property block:
//
//   _Stress   drive torque over the joint's limit, 0..1: amber to red, bright enough to bloom near the limit
//   _Bruise   accumulated damage on the part, 0..1: darkens and reddens the skin
//   _Sweat    1 - stamina: a tight wet highlight from the key light
//   _RimColor the corner colour, as a fresnel edge so the two fighters separate from the crowd
//
// Blend One OneMinusSrcAlpha: rgb is added light, alpha is how much of the body underneath is taken away,
// which is what lets one pass both glow and bruise.
Shader "PoBox/FighterOverlay"
{
    Properties
    {
        _RimColor ("Rim colour", Color) = (1, 1, 1, 1)
        _RimPower ("Rim power", Range(0.5, 8)) = 3
        _RimIntensity ("Rim intensity", Range(0, 3)) = 0.55
        _Stress ("Joint stress 0..1", Range(0, 1)) = 0
        _StressGain ("Stress glow gain", Range(0, 8)) = 2.2
        _Bruise ("Bruise 0..1", Range(0, 1)) = 0
        _Sweat ("Sweat 0..1", Range(0, 1)) = 0
        _SweatGain ("Sweat highlight gain", Range(0, 2)) = 1
        _BruiseGain ("Bruise gain", Range(0, 2)) = 1
        // LEqual (4) draws on the surface of the body it sits on. Always (8) draws through whatever is
        // in front: used for the stress shapes inside a skinned fighter's limbs, which glow through the skin.
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+5" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Overlay"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _RimColor;
                half _RimPower;
                half _RimIntensity;
                half _Stress;
                half _StressGain;
                half _Bruise;
                half _Sweat;
                half _SweatGain;
                half _BruiseGain;
                half _ZTest;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(pw);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceNormalizeViewDir(pw);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 n = normalize(i.normalWS);
                half3 v = normalize(i.viewWS);
                half facing = saturate(dot(n, v));
                half fresnel = pow(1 - facing, _RimPower);

                half3 rim = _RimColor.rgb * fresnel * _RimIntensity;

                // Nothing below half the limit, so holding a guard does not light the whole body up;
                // amber as the joint starts to work, red at the limit.
                half s = smoothstep(0.5, 1.0, saturate(_Stress));
                half3 heat = lerp(half3(1.0, 0.62, 0.08), half3(1.0, 0.10, 0.03), s);
                half3 glow = heat * s * _StressGain * (0.45 + 0.55 * facing);

                // A tight highlight off the key light, growing as the fighter tires.
                half3 h = normalize(_MainLightPosition.xyz + v);
                half spec = pow(saturate(dot(n, h)), 90) * _Sweat * 1.6 * _SweatGain;
                half3 wet = _MainLightColor.rgb * spec;

                half bruise = saturate(_Bruise) * _BruiseGain;
                half3 colour = rim + glow + wet + half3(0.16, 0.02, 0.05) * bruise;
                return half4(colour, bruise * 0.45);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
