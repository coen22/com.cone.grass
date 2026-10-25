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

            #define GRASS_HEAD_DAISY 1
            #define GRASS_HEAD_CUP 2
            #define GRASS_HEAD_UMBEL 3
            #define GRASS_HEAD_SPIKE 4
            #define GRASS_HEAD_CLOVER 5
            #define GRASS_HEAD_MESH 6
            #define GRASS_HEAD_PUFF 7

            struct GrassHeadAttributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                // x: 0 for the flat face, 1 for the two crossed side quads.
                float2 part : TEXCOORD1;
            };

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

            #define GRASS_STAGE_OPEN 0
            #define GRASS_STAGE_BUD 1
            #define GRASS_STAGE_SEED 2

            // Height of the side quads and of the face, in head diameters: scale and offset of the quad's -0.5..0.5,
            // then the face's offset above the tip.
            float3 GrassHeadExtent(uint shape)
            {
                if (shape == GRASS_HEAD_CUP) return float3(0.5, -0.15, 0.0);
                if (shape == GRASS_HEAD_UMBEL) return float3(0.3, 0.05, 0.05);
                if (shape == GRASS_HEAD_SPIKE) return float3(1.7, 0.75, 1.55);
                if (shape == GRASS_HEAD_CLOVER) return float3(0.1, 0.0, 0.0);
                if (shape == GRASS_HEAD_PUFF) return float3(0.8, 0.05, 0.1);
                return float3(0.4, 0.05, 0.02);
            }

            GrassHeadVaryings GrassHeadVertex(GrassHeadAttributes input, uint instanceID : SV_InstanceID)
            {
                GrassViewParameters view = GrassCurrentView();
                float4 positionData = _GrassPositions[(uint)_GrassInstanceOffset + instanceID];
                GrassRootData root = BuildGrassRootForView(positionData, view);
                uint kind = root.kind;
                float4 head = _GrassKindHead[kind];
                uint shape = (uint)head.z;

                float3 tip = GrassCenterAtHeight(root, 1.0);
                // The face turns from the stem's tip toward the sun, with each head's own jitter; a leaf turns less.
                float3 stemUp = GrassTangentAtHeight(root, 1.0);
                float3 toSun = float3(_MainLightPosition.x, 0.0, _MainLightPosition.z);
                float2 jitter = float2(GrassRandom(root.seed + 17u), GrassRandom(root.seed + 18u)) - 0.5;
                float turn = shape == GRASS_HEAD_CLOVER ? 0.15 : 0.45;
                float3 up = SafeNormalize(stemUp + toSun * turn + float3(jitter.x, 0.0, jitter.y) * 0.5);
                up = SafeNormalize(float3(up.x, max(up.y, 0.35), up.z));
                // Some heads face sideways, nodding over on their stems.
                if (GrassRandom(root.seed + 19u) < _GrassKindStem[kind].z)
                {
                    float nod = GrassRandom(root.seed + 20u) * 6.2831853;
                    up = SafeNormalize(float3(cos(nod), 0.35, sin(nod)));
                }
                float yaw = GrassRandom(root.seed + 12u) * 6.2831853;
                float3 around = float3(cos(yaw), 0.0, sin(yaw));
                float3 right = SafeNormalize(around - up * dot(around, up));
                float3 forward = cross(right, up);

                // A few heads are closed buds and a few have gone to seed; a clover leaf is always open.
                float stageRandom = GrassRandom(root.seed + 16u);
                float stage = shape == GRASS_HEAD_CLOVER || shape == GRASS_HEAD_MESH ? GRASS_STAGE_OPEN :
                    stageRandom < _GrassVariantParams2.z ? GRASS_STAGE_BUD :
                    stageRandom < _GrassVariantParams2.z + _GrassVariantParams2.w ? GRASS_STAGE_SEED : GRASS_STAGE_OPEN;
                // Each head's size varies either way by the kind's randomness; buds and seed heads are smaller.
                float size = head.x * (1.0 + saturate(head.y) * (2.0 * GrassRandom(root.seed + 11u) - 1.0)) *
                    (stage == GRASS_STAGE_BUD ? 0.55 : stage == GRASS_STAGE_SEED ? 0.75 : 1.0);
                // No pixel floor: under about three pixels, or near its draw distance, the head shrinks away and the
                // stem's top carries its colour instead (GrassHeadVisibility); it also follows a density transition.
                size *= GrassHeadVisibility(size, root.cameraDistance, root.worldUnitsPerPixel) *
                    sqrt(GrassUnpackCoverage(positionData.w));

                float3 local = input.positionOS;
                float3 normal;
                bool side = input.part.x > 0.5;
                float rise = 0.0;
                if (shape == GRASS_HEAD_MESH)
                    normal = input.normalOS;
                else
                {
                    float3 extent = GrassHeadExtent(shape);
                    rise = side ? local.y * extent.x + extent.y : extent.z;
                    local.y = rise;
                    // A slight dome on the face; the side quads face the camera, a little toward the sky.
                    normal = side ? input.normalOS : float3(local.x * 0.8, 1.0, local.z * 0.8);
                }
                float3 positionWS = tip + (right * local.x + up * local.y + forward * local.z) * size;
                half3 normalWS = SafeNormalize(right * normal.x + up * normal.y + forward * normal.z);
                if (side && shape != GRASS_HEAD_MESH)
                {
                    float3 toCamera = view.cameraPosition - positionWS;
                    normalWS = SafeNormalize(normalWS * (dot(normalWS, toCamera) < 0.0 ? -1.0 : 1.0) + up * 0.5);
                }

                // Each head picks a palette colour; palette clumping gives a drift one colour.
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
                output.petals = 11.0 + floor(GrassRandom(root.seed + 15u) * 6.0);
                output.stage = stage;
                return output;
            }

            // Signed distance-like value, positive inside the head's outline, in units of its quad.
            float GrassHeadOutline(uint shape, uint stage, bool side, float2 uv, float rise, float petals, out bool centre)
            {
                centre = false;
                if (shape == GRASS_HEAD_MESH)
                    return 1.0;
                if (stage == GRASS_STAGE_BUD)
                {
                    // A closed bud: a small egg, seen from above or the side.
                    float x = uv.x * 2.0 - 1.0;
                    return side ? 1.0 - sqrt(x * x / 0.09 + (rise / 0.13) * (rise / 0.13)) : 0.35 - length(uv * 2.0 - 1.0);
                }
                if (stage == GRASS_STAGE_SEED && (shape == GRASS_HEAD_DAISY || shape == GRASS_HEAD_CUP))
                {
                    // Petals gone: the disc alone, a low dome.
                    float x = uv.x * 2.0 - 1.0;
                    centre = true;
                    return side ? 1.0 - sqrt(x * x / 0.1 + (rise / 0.14) * (rise / 0.14)) : 0.32 - length(uv * 2.0 - 1.0);
                }
                if (side)
                {
                    float x = uv.x * 2.0 - 1.0;
                    if (shape == GRASS_HEAD_CUP)
                    {
                        float bottom = -0.3 * (1.0 - x * x / 0.72);
                        return min(min(rise - bottom, 0.03 - rise), 0.85 - abs(x));
                    }
                    if (shape == GRASS_HEAD_UMBEL)
                    {
                        // A shallow dome of separate florets, not a solid band.
                        float floret = 0.32 - abs(frac(x * 4.5) - 0.5);
                        return min(min(0.05 - abs(rise - 0.08 * (1.0 - x * x)), floret * 0.25), 0.92 - abs(x));
                    }
                    if (shape == GRASS_HEAD_SPIKE)
                    {
                        float t = (rise - 0.75) / 0.75;
                        float width = 0.32 * sqrt(saturate(1.0 - t * t)) * (0.85 + 0.15 * sin(rise * 28.0));
                        return width - abs(x);
                    }
                    if (shape == GRASS_HEAD_CLOVER)
                        return -1.0;
                    if (shape == GRASS_HEAD_PUFF)
                    {
                        // A soft ball of florets: a slightly ragged round outline.
                        float wobble = 0.04 * sin(x * 23.0 + rise * 17.0);
                        return 1.0 - sqrt(x * x / 0.18 + ((rise - 0.1) / 0.3) * ((rise - 0.1) / 0.3)) + wobble;
                    }
                    // Daisy: petals edge-on, with the disc as a low dome.
                    float dome = 1.0 - (x * x / 0.08 + (rise / 0.12) * (rise / 0.12));
                    centre = dome > 0.0 && rise > 0.0;
                    return max(min(0.045 + 0.03 * (1.0 - x * x) - abs(rise), 1.0 - abs(x)), dome * 0.1);
                }
                float2 q = uv * 2.0 - 1.0;
                float r = length(q);
                float a = atan2(q.y, q.x);
                if (shape == GRASS_HEAD_CUP)
                {
                    centre = r < 0.18;
                    return 0.8 + 0.2 * cos(5.0 * a) - r;
                }
                if (shape == GRASS_HEAD_UMBEL)
                {
                    // Florets on a grid cut to a disc, with gaps between them.
                    float2 cell = frac(q * 3.5) - 0.5;
                    centre = length(cell) < 0.1;
                    return min(0.36 - length(cell), 0.9 - r);
                }
                if (shape == GRASS_HEAD_SPIKE)
                {
                    centre = r < 0.15;
                    return 0.45 - r;
                }
                if (shape == GRASS_HEAD_PUFF)
                    return 0.42 + 0.04 * cos(a * 11.0) - r;
                if (shape == GRASS_HEAD_CLOVER)
                {
                    centre = r < 0.07;
                    return 0.3 + 0.7 * pow(abs(cos(1.5 * a)), 0.6) - r;
                }
                centre = r < 0.27;
                return 0.3 + 0.7 * smoothstep(0.1, 0.9, abs(cos(a * petals * 0.5))) - r;
            }

            half4 GrassHeadFragment(GrassHeadVaryings input) : SV_Target
            {
                uint shape = (uint)input.centre.a;
                uint stage = (uint)input.stage;
                bool side = input.petal.a > 0.5h;
                bool centre;
                float outline = GrassHeadOutline(shape, stage, side, input.shapeCoordinates.xy, input.shapeCoordinates.z,
                    input.petals, centre);
                half alpha = saturate(outline / max(fwidth(outline), 1e-4) + 0.5);
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
    }
}
