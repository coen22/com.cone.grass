Shader "Hidden/InfiniteGrass/ContactShadows"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "GrassContactShadows"
            Cull Off
            ZWrite Off
            ZTest Always
            // Multiply each existing color sample in place, preserving MSAA and alpha.
            Blend DstColor Zero
            ColorMask RGB

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X_FLOAT(_GrassContactSceneDepth);
            TEXTURE2D_X_FLOAT(_GrassContactGrassDepth);
            float4 _GrassContactDepthScale;
            float4 _GrassContactGrassDepthScale;
            // All projection and reconstruction use the grass depth texture's
            // normalized viewport UVs. Sampling scale/bias handles other origins.
            float4x4 _GrassContactViewProjection;
            float4x4 _GrassContactInverseViewProjection;
            float4x4 _GrassContactInverseProjection;
            float4x4 _GrassContactViewMatrix;
            // strength, ray length, bias, thickness (all lengths are world units).
            float4 _GrassContactParameters;
            // maximum camera distance, sample count, normalized edge fade width.
            float4 _GrassContactLimits;

            bool HasSurface(float rawDepth)
            {
                #if UNITY_REVERSED_Z
                    return rawDepth > 0.0 && rawDepth <= 1.0;
                #else
                    return rawDepth >= 0.0 && rawDepth < 1.0;
                #endif
            }

            float RawToClipDepth(float rawDepth)
            {
                #if UNITY_REVERSED_Z
                    return rawDepth;
                #else
                    return lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
                #endif
            }

            float ClipToRawDepth(float clipDepth)
            {
                #if UNITY_REVERSED_Z
                    return clipDepth;
                #else
                    return (clipDepth - UNITY_NEAR_CLIP_VALUE) / (1.0 - UNITY_NEAR_CLIP_VALUE);
                #endif
            }

            float EyeDepth(float2 uv, float rawDepth)
            {
                float4 positionCS = float4(uv * 2.0 - 1.0, RawToClipDepth(rawDepth), 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    positionCS.y = -positionCS.y;
                #endif
                // Two inverse-projection rows also support orthographic and
                // oblique near planes, which a near/far-only depth formula cannot.
                float viewZ = dot(_GrassContactInverseProjection[2], positionCS);
                float viewW = dot(_GrassContactInverseProjection[3], positionCS);
                return abs(viewW) > 0.0000001 ? -viewZ / viewW : FLT_MAX;
            }

            float3 ReconstructWorld(float2 uv, float rawDepth)
            {
                return ComputeWorldSpacePosition(uv, RawToClipDepth(rawDepth),
                    _GrassContactInverseViewProjection);
            }

            void SampleDepths(float2 uv, out float sceneRaw, out float grassRaw, out float grassCoverage)
            {
                sceneRaw = SAMPLE_TEXTURE2D_X_LOD(_GrassContactSceneDepth,
                    sampler_PointClamp, uv * _GrassContactDepthScale.xy + _GrassContactDepthScale.zw, 0).r;
                float2 grass = SAMPLE_TEXTURE2D_X_LOD(_GrassContactGrassDepth,
                    sampler_PointClamp, uv * _GrassContactGrassDepthScale.xy + _GrassContactGrassDepthScale.zw, 0).rg;
                grassRaw = grass.r;
                grassCoverage = saturate(grass.g);
            }

            bool GrassIsVisible(float2 uv, float sceneRaw, float grassRaw, float grassCoverage)
            {
                if (grassCoverage <= 0.0 || !HasSurface(grassRaw))
                    return false;
                if (!HasSurface(sceneRaw))
                    return true;
                float sceneEye = EyeDepth(uv, sceneRaw);
                // Accommodate depth-copy rounding, without exposing hidden grass
                // through nearer opaque objects.
                return EyeDepth(uv, grassRaw) <= sceneEye + max(0.001, sceneEye * 0.00001);
            }

            float EdgeWeight(float2 uv)
            {
                float edgeDistance = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
                return saturate(edgeDistance / _GrassContactLimits.z);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float sceneRaw, grassRaw, receiverGrassCoverage;
                SampleDepths(uv, sceneRaw, grassRaw, receiverGrassCoverage);
                bool receiverIsGrass = GrassIsVisible(uv, sceneRaw, grassRaw, receiverGrassCoverage);
                float receiverRaw = receiverIsGrass ? grassRaw : sceneRaw;
                if (!HasSurface(receiverRaw))
                    return half4(1, 1, 1, 1);

                float3 receiverWS = ReconstructWorld(uv, receiverRaw);
                if (!all(isfinite(receiverWS)))
                    return half4(1, 1, 1, 1);
                float cameraDistance = distance(receiverWS, _WorldSpaceCameraPos);
                float maxDistance = _GrassContactLimits.x;
                if (cameraDistance >= maxDistance)
                    return half4(1, 1, 1, 1);

                Light mainLight = GetMainLight();
                float3 lightDirection = SafeNormalize(mainLight.direction);
                float rayLength = _GrassContactParameters.y;
                float bias = _GrassContactParameters.z;
                float thickness = _GrassContactParameters.w;
                int steps = (int)_GrassContactLimits.y;
                float stepLength = rayLength / steps;
                float contact = 0.0;

                // Fixed midpoint samples are deterministic. No per-frame random
                // pattern is introduced into fine blades without a temporal filter.
                [loop]
                for (int sampleIndex = 0; sampleIndex < 32; sampleIndex++)
                {
                    if (sampleIndex >= steps)
                        break;

                    float travel = bias + (sampleIndex + 0.5) * stepLength;
                    float3 sampleWS = receiverWS + lightDirection * travel;
                    float4 sampleCS = mul(_GrassContactViewProjection, float4(sampleWS, 1.0));
                    if (sampleCS.w <= 0.00001)
                        break;

                    // Test the actual clipping volume, including an oblique near
                    // plane, instead of comparing eye Z with camera.nearClipPlane.
                    float sampleRaw = ClipToRawDepth(sampleCS.z / sampleCS.w);
                    if (sampleRaw < 0.0 || sampleRaw > 1.0)
                        break;
                    float sampleEye = -mul(_GrassContactViewMatrix, float4(sampleWS, 1.0)).z;

                    float2 sampleNDC = sampleCS.xy / sampleCS.w;
                    // Inverse of ComputeWorldSpacePosition's texture-UV conversion.
                    #if UNITY_UV_STARTS_AT_TOP
                        sampleNDC.y = -sampleNDC.y;
                    #endif
                    float2 sampleUV = sampleNDC * 0.5 + 0.5;
                    if (any(sampleUV <= 0.0) || any(sampleUV >= 1.0))
                        break;

                    float sampleSceneRaw, sampleGrassRaw, hitGrassCoverage;
                    SampleDepths(sampleUV, sampleSceneRaw, sampleGrassRaw, hitGrassCoverage);
                    bool hitIsGrass = GrassIsVisible(sampleUV, sampleSceneRaw, sampleGrassRaw, hitGrassCoverage);

                    // Limit the effect to contacts involving grass. Existing terrain
                    // and unrelated opaque objects do not shadow each other here.
                    if (!receiverIsGrass && !hitIsGrass)
                        continue;

                    float nearestRaw = hitIsGrass ? sampleGrassRaw : sampleSceneRaw;
                    if (!HasSurface(nearestRaw))
                        continue;

                    float depthDifference = sampleEye - EyeDepth(sampleUV, nearestRaw);
                    if (depthDifference > bias && depthDifference < thickness + bias)
                    {
                        float depthWeight = 1.0 - smoothstep(thickness * 0.5,
                            thickness, depthDifference - bias);
                        float rangeWeight = 1.0 - smoothstep(rayLength * 0.75,
                            rayLength + bias, travel);
                        float coverageWeight = (receiverIsGrass ? receiverGrassCoverage : 1.0) *
                            (hitIsGrass ? hitGrassCoverage : 1.0);
                        contact = max(contact, depthWeight * rangeWeight * EdgeWeight(sampleUV) * coverageWeight);
                    }
                }

                float distanceWeight = 1.0 - smoothstep(maxDistance * 0.8, maxDistance, cameraDistance);
                float attenuation = 1.0 - saturate(contact * _GrassContactParameters.x *
                    distanceWeight * EdgeWeight(uv));

                // This is an optional color overlay, not a replacement for URP
                // shadow maps: ambient/emissive contributions are attenuated too.
                // The single nearest-depth layer approximates fractional silhouettes;
                // it cannot reconstruct the other surfaces under covered MSAA samples.
                return half4(attenuation.xxx, 1);
            }
            ENDHLSL
        }
    }
}
