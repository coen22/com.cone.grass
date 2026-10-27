Shader "Hidden/InfiniteGrass/Heads"
{
    // Flower and herb heads (GrassVariantProfile), drawn on the tips of the blades whose kind carries one. The renderer
    // copies the blade material into this shader's material, so a head rebuilds exactly the bent blade it sits on.
    // Heads never take the matched ground colour; that stays with the blades.
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "GrassHeadForward"
            Tags { "LightMode" = "GrassHeadForward" }
            Cull Off
            ZWrite On
            ZTest LEqual
            Blend Off
            AlphaToMask [_GrassAlphaToCoverage]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassHeadVertex
            #pragma fragment GrassHeadFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "../Shaders/GrassBladeCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "../Shaders/GrassForwardLighting.hlsl"

            #include "../Shaders/GrassHeadCommon.hlsl"

            struct GrassHeadVaryings
            {
                float4 positionCS : SV_POSITION;
                float4 positionWSAndFog : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                // xy: the quad's uv; z: height in head diameters above the tip (side quads).
                float3 shapeCoordinates : TEXCOORD2;
                nointerpolation half4 petal : TEXCOORD3; // rgb colour; a: 1 on a side quad
                nointerpolation half4 centre : TEXCOORD4; // rgb colour; a: GrassHeadShape
                nointerpolation float petals : TEXCOORD5;
                // 0 open, 1 a closed bud, 2 gone to seed.
                nointerpolation float stage : TEXCOORD6;
            };

            GrassHeadVaryings GrassHeadVertex(GrassHeadAttributes input, uint instanceID : SV_InstanceID)
            {
                GrassRootData root;
                GrassHeadFrame frame;
                float4 positionData;
                float rise;
                float3 normal;
                float3 positionWS = GrassCurrentHeadVertex(input, instanceID, root, frame, positionData, rise, normal);
                uint kind = root.kind;
                uint shape = frame.shape;
                bool side = input.part.x > 0.5;
                half3 normalWS = SafeNormalize(frame.right * normal.x + frame.up * normal.y + frame.forward * normal.z);
                if (side && shape != GRASS_HEAD_MESH)
                {
                    float3 toCamera = GrassCurrentView().cameraPosition - positionWS;
                    normalWS = SafeNormalize(normalWS * (dot(normalWS, toCamera) < 0.0 ? -1.0 : 1.0) + frame.up * 0.5);
                }

                // Each head picks a palette colour; palette clumping gives a drift one colour.
                float4 head = _GrassKindHead[kind];
                float4 centre = _GrassKindCentre[kind];
                uint count = (uint)clamp(centre.w, 1.0, (float)GRASS_PALETTE_COLOURS);
                float pick = frac(GrassRandom(root.seed + 13u) * (1.0 - saturate(head.w)) + GrassVariantClump(root.pivot.xz));
                uint index = min((uint)(pick * count), count - 1u);
                half3 petal = _GrassKindPalette[kind * GRASS_PALETTE_COLOURS + index].rgb *
                    (0.9 + 0.2 * GrassRandom(root.seed + 14u));

                GrassHeadVaryings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWSAndFog = float4(positionWS, ComputeFogFactor(output.positionCS.z));
                output.normalWS = normalWS;
                output.shapeCoordinates = float3(input.uv, rise);
                output.petal = half4(petal, side ? 1.0h : 0.0h);
                output.centre = half4(centre.rgb, shape);
                output.petals = GrassHeadPetals(root.seed);
                output.stage = frame.stage;
                return output;
            }

            half4 GrassHeadFragment(GrassHeadVaryings input) : SV_Target
            {
                uint shape = (uint)input.centre.a;
                uint stage = (uint)input.stage;
                bool side = input.petal.a > 0.5h;
                bool centre;
                float outline = GrassHeadOutline(shape, stage, side, input.shapeCoordinates.xy, input.shapeCoordinates.z,
                    input.petals, centre);
                half alpha = GrassHeadCoverage(outline);
                // With MSAA the edge fraction becomes sample coverage; without it the outline is solid.
                clip(alpha - (_GrassAlphaToCoverage > 0.5 ? 0.00001 : 0.5));
                if (_GrassAlphaToCoverage <= 0.5)
                    alpha = 1.0h;

                half3 albedo = centre ? input.centre.rgb : input.petal.rgb;
                // A bud is mostly green with a hint of its petals; a seed head has browned.
                if (stage == GRASS_STAGE_BUD)
                    albedo = lerp(half3(0.12h, 0.25h, 0.06h), input.petal.rgb, 0.3h);
                else if (stage == GRASS_STAGE_SEED)
                    albedo = lerp(half3(0.25h, 0.17h, 0.07h), albedo, 0.25h);
                // A puff is soft: brighter in its middle, darker toward its rim.
                if (shape == GRASS_HEAD_PUFF)
                    albedo *= 0.72h + 0.35h * saturate(outline * 4.0);
                else if (!side && !centre && stage == GRASS_STAGE_OPEN && shape != GRASS_HEAD_MESH)
                    albedo *= lerp(0.75h, 1.0h, saturate(length(input.shapeCoordinates.xy * 2.0 - 1.0) * 1.4));

                float3 positionWS = input.positionWSAndFog.xyz;
                half3 normalWS = SafeNormalize(input.normalWS);
                half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                Light mainLight = GrassMainLight(positionWS);
                // Wrapped diffuse for a soft petal, and the light through it from behind.
                half diffuse = saturate(dot(normalWS, mainLight.direction) * 0.6h + 0.4h);
                half through = saturate(dot(-viewDirectionWS, mainLight.direction));
                through *= through;
                through *= through * 0.35h;
                half3 lighting = GrassAmbientLight(normalWS) * albedo +
                    albedo * mainLight.color * (mainLight.shadowAttenuation * (diffuse + through));
                return half4(MixFog(lighting, input.positionWSAndFog.w), alpha);
            }
            ENDHLSL
        }
        // The head in the contact renderer's single-sample depth and coverage target (#90), beside the blades'
        // GrassContactDepth pass: the same head as the forward pass, so a flower casts and takes grass contacts.
        Pass
        {
            Name "GrassHeadContactDepth"
            Tags { "LightMode" = "GrassHeadContactDepth" }
            Cull Off
            ZWrite On
            ZTest LEqual
            Blend Off
            ColorMask RG
            AlphaToMask Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassHeadContactDepthVertex
            #pragma fragment GrassHeadContactDepthFragment

            #include "../Shaders/GrassHeadCommon.hlsl"

            struct GrassHeadDepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 shapeCoordinates : TEXCOORD1;
                nointerpolation uint4 shapeStageSidePetals : TEXCOORD2;
            };

            GrassHeadDepthVaryings GrassHeadContactDepthVertex(GrassHeadAttributes input, uint instanceID : SV_InstanceID)
            {
                GrassRootData root;
                GrassHeadFrame frame;
                float4 positionData;
                float rise;
                float3 normal;
                float3 positionWS = GrassCurrentHeadVertex(input, instanceID, root, frame, positionData, rise, normal);
                GrassHeadDepthVaryings output;
                output.positionCS = mul(_GrassContactViewProjection, float4(positionWS, 1.0));
                output.positionWS = positionWS;
                output.shapeCoordinates = float3(input.uv, rise);
                output.shapeStageSidePetals = uint4(frame.shape, frame.stage, input.part.x > 0.5 ? 1u : 0u,
                    (uint)GrassHeadPetals(root.seed));
                return output;
            }

            float2 GrassHeadContactDepthFragment(GrassHeadDepthVaryings input) : SV_Target
            {
                float3 cameraDelta = input.positionWS - _WorldSpaceCameraPos;
                clip(_GrassContactCasterDistance * _GrassContactCasterDistance - dot(cameraDelta, cameraDelta));
                bool centre;
                float outline = GrassHeadOutline(input.shapeStageSidePetals.x, input.shapeStageSidePetals.y,
                    input.shapeStageSidePetals.z != 0u, input.shapeCoordinates.xy, input.shapeCoordinates.z,
                    (float)input.shapeStageSidePetals.w, centre);
                // The outline's edge fraction is the head's coverage, as A2C gives it in colour; outside it, nothing.
                float coverage = GrassHeadCoverage(outline);
                clip(coverage - 0.00001);
                return float2(input.positionCS.z, coverage);
            }
            ENDHLSL
        }

        // Per-object motion for the heads (#90), beside the blades' GrassMotionVectors pass. The previous head is
        // rebuilt on the previous blade root (the same history lookup), with the previous view, wind and sun.
        Pass
        {
            Name "GrassHeadMotionVectors"
            Tags { "LightMode" = "GrassHeadMotionVectors" }
            Cull Off
            ZWrite On
            ZTest LEqual
            Blend Off
            ColorMask RG
            AlphaToMask Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassHeadMotionVertex
            #pragma fragment GrassHeadMotionFragment

            #include "../Shaders/GrassMotionVectors.hlsl"
            #include "../Shaders/GrassHeadCommon.hlsl"

            struct GrassHeadMotionVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 currentPositionWS : TEXCOORD0;
                // A head is its frame times a fixed local shape, so the previous position interpolates exactly.
                float3 previousPositionWS : TEXCOORD1;
                float3 shapeCoordinates : TEXCOORD2;
                nointerpolation uint4 shapeStageSidePetals : TEXCOORD3;
            };

            GrassHeadMotionVaryings GrassHeadMotionVertex(GrassHeadAttributes input, uint instanceID : SV_InstanceID)
            {
                GrassRootData root;
                GrassHeadFrame frame;
                float4 positionData;
                float rise;
                float3 normal;
                GrassHeadMotionVaryings output;
                output.currentPositionWS = GrassCurrentHeadVertex(input, instanceID, root, frame, positionData, rise, normal);
                // Rasterization includes the current camera jitter, exactly as the colour pass.
                output.positionCS = TransformWorldToHClip(output.currentPositionWS);
                output.shapeCoordinates = float3(input.uv, rise);
                output.shapeStageSidePetals = uint4(frame.shape, frame.stage, input.part.x > 0.5 ? 1u : 0u,
                    (uint)GrassHeadPetals(root.seed));
                output.previousPositionWS = output.currentPositionWS;
                GrassRootData previousRoot;
                float rows, previousCoverage;
                if (BuildPreviousGrassRoot(root.pivot, previousRoot, rows, previousCoverage))
                {
                    GrassHeadFrame previous = GrassBuildHeadFrame(previousRoot, previousCoverage, _GrassPreviousMainLight.xyz);
                    float previousRise;
                    float3 previousNormal;
                    float3 local = GrassHeadLocalVertex(input, previous.shape, previousRise, previousNormal);
                    output.previousPositionWS = GrassHeadWorldPosition(previous, local);
                }
                return output;
            }

            float4 GrassHeadMotionFragment(GrassHeadMotionVaryings input) : SV_Target
            {
                // URP motion is single-sample: the outline is solid at its half-coverage edge, as without A2C.
                bool centre;
                float outline = GrassHeadOutline(input.shapeStageSidePetals.x, input.shapeStageSidePetals.y,
                    input.shapeStageSidePetals.z != 0u, input.shapeCoordinates.xy, input.shapeCoordinates.z,
                    (float)input.shapeStageSidePetals.w, centre);
                clip(GrassHeadCoverage(outline) - 0.5);
                return GrassMotionVelocity(input.currentPositionWS, input.previousPositionWS);
            }
            ENDHLSL
        }
    }
}
