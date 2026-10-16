// The picture rippling under a punch out of the ordinary (Fx/ScreenRipple): a ring that runs out from where the punch
// landed on screen and bends the picture as it passes, thinning and fading as it goes. A full-screen pass
// (URP's FullScreenPassRendererFeature, after post-processing) that is switched on only while a ripple runs.
//
//   _PoBoxRipple        xy the middle (0..1 across the screen), z progress 0..1, w how far it bends, in screen heights
//   _PoBoxRippleAspect  the screen's width over its height
Shader "PoBox/ScreenRipple"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "ScreenRipple"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _PoBoxRipple;
            float _PoBoxRippleAspect;

            half4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float2 d = uv - _PoBoxRipple.xy;
                d.x *= _PoBoxRippleAspect;
                float r = length(d);
                float front = _PoBoxRipple.z * 0.55;             // the ring reaches half a screen height out
                const float width = 0.05;
                float x = (r - front) / width;
                float band = saturate(1.0 - abs(x));
                float bend = _PoBoxRipple.w * band * band * (1.0 - _PoBoxRipple.z) * sin(x * 3.14159);
                float2 dir = r > 1e-4 ? d / r : float2(0, 0);
                dir.x /= _PoBoxRippleAspect;
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + dir * bend);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
