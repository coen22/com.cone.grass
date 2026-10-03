#ifndef CONE_GRASS_CAPTURE_SAMPLING_INCLUDED
#define CONE_GRASS_CAPTURE_SAMPLING_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// These maps have their own fixed resolution and may be cached across camera frames.
// Keep normal texture mip filtering, without the viewing camera's upscaler mip bias.
#if defined(PLATFORM_SAMPLE_TEXTURE2D)
    #define GRASS_SAMPLE_CAPTURE_TEXTURE2D(textureName, samplerName, uv) \
        PLATFORM_SAMPLE_TEXTURE2D(textureName, samplerName, uv)
#else
    #define GRASS_SAMPLE_CAPTURE_TEXTURE2D(textureName, samplerName, uv) \
        SAMPLE_TEXTURE2D(textureName, samplerName, uv)
#endif

#endif
