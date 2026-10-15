// The shockwave of a punch out of the ordinary (ImpactVfx): one quad laid on the surface that was hit, a
// ring drawn across it that spreads from the middle to the edge and thins and fades as it goes. Additive,
// unlit, no grab of the screen: a ring of light, not a ripple in the picture.
//
//   _Progress   0 at the hit, 1 gone
//   _Strength   how far out of the ordinary the punch was, 0.5..1.4 (over 1 it blooms)
//
// In a Resources folder so a build carries it although nothing in a scene refers to it.
Shader "PoBox/Shockwave"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.92, 0.8, 1)
        _Progress ("Progress", Range(0, 1)) = 0
        _Strength ("Strength", Range(0, 2)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Shockwave"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Progress;
                half _Strength;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv * 2.0 - 1.0;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half t = saturate(_Progress);
                half r = length(i.uv);
                // Out from a tenth of the way to the edge; a thick band at first, a thin line at the end.
                half width = lerp(0.22, 0.05, t);
                half ring = saturate(1.0 - abs(r - lerp(0.1, 0.92, t)) / width);
                half fade = (1.0 - t) * (1.0 - t);
                return half4(_Color.rgb * ring * ring * fade * _Strength, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
