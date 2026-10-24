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
            };

            // Height of the side quads and of the face, in head diameters: scale and offset of the quad's -0.5..0.5,
            // then the face's offset above the tip.
            float3 GrassHeadExtent(uint shape)
            {
                if (shape == GRASS_HEAD_CUP) return float3(0.5, -0.15, 0.0);
                if (shape == GRASS_HEAD_UMBEL) return float3(0.3, 0.05, 0.05);
                if (shape == GRASS_HEAD_SPIKE) return float3(1.7, 0.75, 1.55);
                if (shape == GRASS_HEAD_CLOVER) return float3(0.1, 0.0, 0.0);
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
                float3 up = GrassTangentAtHeight(root, 1.0);
                float yaw = GrassRandom(root.seed + 12u) * 6.2831853;
                float3 around = float3(cos(yaw), 0.0, sin(yaw));
                float3 right = SafeNormalize(around - up * dot(around, up));
                float3 forward = cross(right, up);

                // The head shrinks away toward its draw distance, where the blade tip takes its colour, and with the
                // blade through a density transition. At least a pixel wide while it lasts, so it does not sparkle.
                float fade = 1.0 - smoothstep(_GrassVariantParams.w, _GrassVariantParams.z, root.cameraDistance);
                float size = max(head.x * (1.0 - saturate(head.y) * GrassRandom(root.seed + 11u)), root.worldUnitsPerPixel);
                size *= fade * sqrt(GrassUnpackCoverage(positionData.w));

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
                return output;
            }

            // Signed distance-like value, positive inside the head's outline, in units of its quad.
            float GrassHeadOutline(uint shape, bool side, float2 uv, float rise, float petals, out bool centre)
            {
                centre = false;
                if (shape == GRASS_HEAD_MESH)
                    return 1.0;
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
                bool side = input.petal.a > 0.5h;
                bool centre;
                float outline = GrassHeadOutline(shape, side, input.shapeCoordinates.xy, input.shapeCoordinates.z,
                    input.petals, centre);
                half alpha = saturate(outline / max(fwidth(outline), 1e-4) + 0.5);
                // With MSAA the edge fraction becomes sample coverage; without it the outline is solid.
                clip(alpha - (_GrassAlphaToCoverage > 0.5 ? 0.00001 : 0.5));
                if (_GrassAlphaToCoverage <= 0.5)
                    alpha = 1.0h;

                half3 albedo = centre ? input.centre.rgb : input.petal.rgb;
                if (!side && !centre && shape != GRASS_HEAD_MESH)
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
