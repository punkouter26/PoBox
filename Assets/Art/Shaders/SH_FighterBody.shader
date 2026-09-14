// The fighter body shader.
//
// WHY THIS EXISTS. Read the problem off a phone screen: the game is portrait
// 9:16, the drama camera frames a whole ring, and a fighter is a small pale
// shape against a dark arena floor. Stock URP/Lit gives that shape no edge, so
// a limb in front of the torso and a limb behind it are the same flat colour
// and the pose — the entire subject of an active-ragdoll boxing game — is hard
// to read. A rim light is the cheapest fix there is: it draws the silhouette of
// every part from the camera's own point of view, for one dot product.
//
// It also carries STRAIN. Systems_FighterShading feeds _Strain from
// Systems_Stamina.JointLoad01 — the same number the heatmap draws and stamina
// drains from, never the action vector, which is a request rather than a force
// and reads as full effort from a policy pinned against a limit. A fighter
// working at its ceiling warms and glows at the edges; one standing easy does
// not. That makes visible, on the body itself, the thing the colour commentary
// otherwise has to say out loud.
//
// COLOUR IDENTITY IS NOT NEGOTIABLE (project rule). Red means "no brain,
// hand-written"; green means "the standard policy on the standard body". So the
// strain tint is applied as a MULTIPLY toward a warm value rather than as a
// replacement: a strained red bot goes deeper red and a strained green fighter
// goes amber-green. Neither can ever be mistaken for the other, at any strain.
//
// SRP BATCHER COMPATIBLE. Every property lives in one UnityPerMaterial CBUFFER
// in every pass, which is what the batcher checks. The one thing that opts a
// renderer out is a MaterialPropertyBlock, which the fighters already carry for
// the copy wash — so this costs nothing that was not already being paid.
Shader "PoBox/FighterBody"
{
    Properties
    {
        [MainColor] _BaseColor("Base Colour", Color) = (1,1,1,1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        _Smoothness("Smoothness", Range(0,1)) = 0.35
        _Metallic("Metallic", Range(0,1)) = 0.0

        [Header(Silhouette)]
        _RimColor("Rim Colour", Color) = (0.65,0.80,1.0,1)
        _RimPower("Rim Power", Range(0.5,8)) = 3.0
        _RimStrength("Rim Strength", Range(0,3)) = 0.9

        [Header(Strain)]
        // Driven per fighter by Systems_FighterShading through a property
        // block. Authored at 0 so a material dropped on anything else looks
        // like an ordinary lit surface.
        _Strain("Strain", Range(0,1)) = 0.0
        _StrainColor("Strain Multiply", Color) = (1.0,0.45,0.30,1)
        _StrainRim("Strain Rim Boost", Range(0,4)) = 1.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _Smoothness;
            half   _Metallic;
            half4  _RimColor;
            half   _RimPower;
            half   _RimStrength;
            half   _Strain;
            half4  _StrainColor;
            half   _StrainRim;
        CBUFFER_END
        ENDHLSL

        // ------------------------------------------------------------------
        // Forward lit. The only pass that does anything interesting.
        // ------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            // Lighting. Kept to the set this project's two renderers actually
            // use: the mobile asset has additional-light SHADOWS off entirely
            // (Assets/Settings/Mobile_RPAsset.asset), so the variants for them
            // would compile a permutation nothing on a phone can reach.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                float  fogCoord    : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = normals.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.shadowCoord = GetShadowCoord(positions);
                output.fogCoord = ComputeFogFactor(positions.positionCS.z);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // Multiply toward the strain colour — never replace. See the
                // colour-identity note in the file header.
                half3 strained = lerp(albedo.rgb, albedo.rgb * _StrainColor.rgb, saturate(_Strain));

                float3 normalWS = normalize(input.normalWS);
                float3 viewWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewWS;
                inputData.shadowCoord = input.shadowCoord;
                inputData.fogCoord = input.fogCoord;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = strained;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion = 1.0h;
                surfaceData.alpha = 1.0h;
                surfaceData.normalTS = half3(0, 0, 1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                // The silhouette. Fresnel against the view direction, so the
                // edge is drawn wherever the surface turns away from the
                // camera — which is exactly the outline of the limb, from
                // wherever the drama camera happens to be.
                half fresnel = pow(saturate(1.0h - saturate(dot(normalWS, viewWS))), _RimPower);
                half rimGain = _RimStrength + _Strain * _StrainRim;
                half3 rim = _RimColor.rgb * fresnel * rimGain;
                // Warmed by strain as well as brightened, so a fighter at its
                // force ceiling reads hot at the edges rather than merely
                // brighter.
                rim = lerp(rim, rim * _StrainColor.rgb, saturate(_Strain));

                color.rgb += rim;
                color.rgb = MixFog(color.rgb, input.fogCoord);
                return half4(color.rgb, 1.0h);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Shadows. Without this pass the fighters cast none at all, which is
        // the single strongest cue that a body is standing ON something.
        // ------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowVertex(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                output.positionCS = positionCS;
                return output;
            }

            half4 ShadowFragment(ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Depth and depth-normals. The PC renderer runs screen-space ambient
        // occlusion (Assets/Settings/PC_Renderer.asset) and a surface missing
        // these passes is simply absent from it — the fighters would be the
        // only things in the arena with no contact darkening.
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthVertex(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFragment(DepthVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            struct NormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            NormalsVaryings DepthNormalsVertex(NormalsAttributes input)
            {
                NormalsVaryings output = (NormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DepthNormalsFragment(NormalsVaryings input) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    // A fighter that fails to compile here should still be a fighter, not a
    // magenta shape — the fallback is the stock lit surface this replaces.
    FallBack "Universal Render Pipeline/Lit"
}
