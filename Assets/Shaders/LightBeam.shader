// A ring light's beam through the hall's haze (Fx/ArenaMood): a cone from the lamp down to the canvas, added light,
// brightest at the lamp and fading towards the floor, soft at its edges, where the cone is seen side-on.
//
//   _BeamColor   the light's colour times how bright the beam is (ArenaMood follows its light's intensity)
//
// The cone's mesh has v = 0 at the lamp and 1 at the floor (Assets/Settings/LightBeam.asset, PoBox/Do All/Graphics).
Shader "PoBox/LightBeam"
{
    Properties
    {
        _BeamColor ("Beam colour", Color) = (0.05, 0.05, 0.05, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Beam"
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
                half4 _BeamColor;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 viewWS : TEXCOORD1; float v : TEXCOORD2; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                float3 pw = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(pw);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.viewWS = GetWorldSpaceNormalizeViewDir(pw);
                o.v = i.uv.y;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Seen through the middle of the cone there is more haze in the way than at its edge.
                half facing = abs(dot(normalize(i.normalWS), normalize(i.viewWS)));
                half along = lerp(0.2, 1.0, pow(saturate(1.0 - i.v), 1.4));     // some of it reaches the canvas
                half edge = smoothstep(0.0, 0.6, facing);
                return half4(_BeamColor.rgb * along * edge * 0.5, 0);      // both faces are drawn: half each
            }
            ENDHLSL
        }
    }
    Fallback Off
}
