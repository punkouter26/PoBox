// The physics X-ray (XrayShapes): a MuJoCo collision shape drawn through everything as a shell of light,
// bright at its edges and nearly clear face on, with thin rings along it so it reads as a wire cage.
// Additive and unlit; drawn instanced, the colour per instance (rgb, and a = how strongly).
//
// In a Resources folder, with its material (XrayShape.mat, instancing on, so a build keeps that variant).
Shader "PoBox/XrayShape"
{
    Properties
    {
        _Power ("Edge power", Range(0.5, 6)) = 2
        _Face ("Face-on share", Range(0, 1)) = 0.12
        _Rings ("Rings a unit of length", Range(0, 40)) = 14
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Xray"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Power;
                half _Face;
                half _Rings;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(PerInstance)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Tint)
            UNITY_INSTANCING_BUFFER_END(PerInstance)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
                float bandY : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(pw);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceNormalizeViewDir(pw);
                // Rings by height in the world along the shape's own y, so a long capsule gets more of them.
                o.bandY = dot(pw - TransformObjectToWorld(float3(0, 0, 0)), normalize(TransformObjectToWorldDir(float3(0, 1, 0))));
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 tint = UNITY_ACCESS_INSTANCED_PROP(PerInstance, _Tint);
                half facing = saturate(dot(normalize(i.normalWS), normalize(i.viewWS)));
                half edge = pow(1.0 - facing, _Power);
                half ring = step(0.85, frac(i.bandY * _Rings));
                half glow = _Face + edge + ring * 0.35;
                return half4(tint.rgb * tint.a * glow, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
