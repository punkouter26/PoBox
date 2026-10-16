// The telemetry a fighter wears.
//
// Drawn over the lit body: on the stand-in as a second material on every link's renderer, on a trained
// fighter as a second skinned renderer over the owner's own mesh. The body underneath keeps its own
// shader (so it takes the ring lights and casts shadows like everything else); this pass adds what the
// simulation is saying about the fighter, fed per renderer through a property block:
//
//   _Bruise        accumulated damage on the whole part, 0..1 (the stand-in, one renderer a link)
//   _BruiseMarks   up to eight marks where punches landed: xyz in the world, w how bad, 0..1 (trained fighters)
//   _Sweat         1 - stamina: a tight wet highlight from the key light, and a sheen that grows with it
//   _Flush         the face going red as health goes: xyz the middle of the head in the world, w how much, 0..1
//   _RimColor      the corner colour, as a fresnel edge so the two fighters separate from the crowd
//   _Ripple        a punch just landed: xyz where, w seconds since; a ring of light runs out over the skin from it
//   _RippleStrength how hard it was, 0..1.5
//   _Load          up to twelve joints: xyz where, w how hard its motors work (0..1, Fighter's stress); the
//                  working ones glow amber through the skin, the hardest-worked hottest
//
// Blend One OneMinusSrcAlpha: rgb is added light, alpha is how much of the body underneath is taken away,
// which is what lets one pass both light an edge and darken a bruise.
Shader "PoBox/FighterOverlay"
{
    Properties
    {
        _RimColor ("Rim colour", Color) = (1, 1, 1, 1)
        _RimPower ("Rim power", Range(0.5, 8)) = 3
        _RimIntensity ("Rim intensity", Range(0, 3)) = 0.55
        _Bruise ("Bruise 0..1", Range(0, 1)) = 0
        _Sweat ("Sweat 0..1", Range(0, 1)) = 0
        _SweatGain ("Sweat highlight gain", Range(0, 2)) = 1
        _BruiseGain ("Bruise gain", Range(0, 2)) = 1
        _BruiseRadius ("Bruise radius (m)", Range(0.02, 0.3)) = 0.09
        _LoadGain ("Load glow gain", Range(0, 2)) = 1
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
            ZTest LEqual
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
                half _Bruise;
                half _Sweat;
                half _SweatGain;
                half _BruiseGain;
                half _BruiseRadius;
                half _LoadGain;
            CBUFFER_END

            // Per renderer, from FighterSkin. Outside the material's buffer: an array in it would stop the
            // batcher, and a renderer with a property block is not batched anyway.
            float4 _BruiseMarks[8];
            float _BruiseCount;
            float4 _Flush;
            float4 _Ripple;
            float _RippleStrength;
            float4 _Load[12];
            float _LoadCount;

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
                float3 positionWS : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(pw);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceNormalizeViewDir(pw);
                o.positionWS = pw;
                return o;
            }

            // A cheap, stable noise for the mottling of a bruise and the beading of sweat.
            float hash3(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 n = normalize(i.normalWS);
                half3 v = normalize(i.viewWS);
                half facing = saturate(dot(n, v));
                half fresnel = pow(1 - facing, _RimPower);

                half3 rim = _RimColor.rgb * fresnel * _RimIntensity;

                // Sweat: a tight highlight off the key light, beaded, and a sheen at grazing angles, both
                // growing as the fighter tires.
                half sweat = saturate(_Sweat);
                half3 h = normalize(_MainLightPosition.xyz + v);
                half bead = 0.55 + 0.9 * step(0.72, hash3(floor(i.positionWS * 260.0)));
                half spec = pow(saturate(dot(n, h)), 90) * sweat * 1.6 * _SweatGain * bead;
                half3 wet = _MainLightColor.rgb * (spec + fresnel * sweat * 0.10 * _SweatGain);

                // Bruises: the whole part's, and the marks where punches landed.
                half bruise = saturate(_Bruise);
                half3 tint = half3(0.16, 0.02, 0.05) * bruise;
                int count = (int)min(_BruiseCount, 8);
                for (int k = 0; k < count; k++)
                {
                    float d = distance(i.positionWS, _BruiseMarks[k].xyz);
                    // A dark middle and a redder, wider edge: what a day-old knock looks like.
                    half core = saturate(1.0 - d / (_BruiseRadius * 0.6));
                    half halo = saturate(1.0 - d / _BruiseRadius);
                    half mottle = 0.75 + 0.5 * hash3(floor(i.positionWS * 120.0));
                    half amount = _BruiseMarks[k].w * mottle;
                    bruise = max(bruise, saturate(core * core * amount * 1.2));
                    tint += half3(0.20, 0.03, 0.07) * halo * halo * amount * 0.3;
                }
                bruise = saturate(bruise * _BruiseGain);

                // The flush: the face and neck reddening, strongest at the cheeks, gone a hand's width below the jaw.
                half flushed = saturate(1.0 - distance(i.positionWS, _Flush.xyz) / 0.17) * saturate(_Flush.w);
                flushed *= flushed;
                half3 blood = half3(0.30, 0.04, 0.03) * flushed;

                // The ripple: a thin ring of light running out from where the punch landed at 1.2 m/s, gone in 0.35 s.
                half3 ripple = 0;
                if (_Ripple.w < 0.35)
                {
                    float d = distance(i.positionWS, _Ripple.xyz);
                    half ring = saturate(1.0 - abs(d - _Ripple.w * 1.2) / 0.03) * (1.0 - _Ripple.w / 0.35);
                    ripple = half3(1.0, 0.86, 0.72) * ring * ring * saturate(_RippleStrength) * 0.8;
                }

                // Load: a joint whose motors are past half their limit glows amber round it, a hand's width out.
                half load = 0;
                int joints = (int)min(_LoadCount, 12);
                for (int j = 0; j < joints; j++)
                {
                    half near = saturate(1.0 - distance(i.positionWS, _Load[j].xyz) / 0.15);
                    load = max(load, near * near * saturate((_Load[j].w - 0.45) / 0.55));
                }
                half3 glow = half3(1.0, 0.42, 0.06) * load * (0.35 + 0.65 * fresnel) * 0.6 * _LoadGain;

                half3 colour = rim + wet + tint * _BruiseGain + blood + ripple + glow;
                return half4(colour, max(bruise * 0.55, flushed * 0.22));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
