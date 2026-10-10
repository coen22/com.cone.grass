#ifndef CONE_GRASS_BLADE_COMMON_INCLUDED
#define CONE_GRASS_BLADE_COMMON_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
    half4 _Color;
    half4 _AOColor;
    float _GrassWidth;
    float _GrassHeight;
    float _GrassCurving;
    float _GrassWidthRandomness;
    float _GrassHeightRandomness;
    float _ExpandDistantGrassWidth;
    float4 _ExpandDistantGrassRange;
    float4 _WindTexture_ST;
    float _WindStrength;
    float4 _WindScroll;
    half _RandomNormal;
    half _GroundBlendStrength;
    float _GroundBlendHeight;
    half _GroundBlendFloor;
    half _GroundAlbedoAlongBlade;
    half _TerrainSplatAtRoots;
    half _CanopyOcclusion;
    float _GrassSpacing;
    float _UseGroundNormal;
    float _UseAdditionalLights;
    float _SpecularFadeStart;
    float _SpecularFadeEnd;
    float _MinimumPixelWidth;
    float _GrassAlphaToCoverage;
    float _MaxSubdivision;
    float _SubdivisionDistance;
    float _SubdivisionHeightBoost;
    float _SubdivisionBumpWidth;
    float _FullDensityDistance;
    float _DensityFalloffExponent;
CBUFFER_END

// Renderer-owned resources and constants belong outside UnityPerMaterial.
// xyz = stable world pivot; w = continuous coverage, never camera distance.
StructuredBuffer<float4> _GrassPositions;
int _GrassInstanceOffset;
int _GrassMainLightShadowCascades;
int _GrassUseExplicitTime;
float _GrassTime;
float _GrassContactCasterDistance;
float4x4 _GrassContactViewProjection;
float4 _GrassSHAr;
float4 _GrassSHAg;
float4 _GrassSHAb;
float4 _GrassSHBr;
float4 _GrassSHBg;
float4 _GrassSHBb;
float4 _GrassSHC;
float2 _CenterPos;
float _DrawDistance;
float _TextureUpdateThreshold;
float4 _GrassHeightMapRT_TexelSize;
// The painted terrain under the camera, bound by the renderer: xy origin and zw size in world XZ
// (zw zero when none), its control map's texel size, and per layer the world-to-UV scale and
// offset, TerrainLit's diffuse remap scale with the normal scale in w, and the diffuse width.
float4 _GrassSplatTerrain;
float4 _GrassSplatControl_TexelSize;
float4 _GrassSplatTile[4];
float4 _GrassSplatRemap[4];
// Per layer: TerrainLit's smoothness constant, its source and metallic.
float4 _GrassSplatSurface[4];
float4 _GrassSplatWidths;

TEXTURE2D(_WindTexture);
SAMPLER(sampler_WindTexture);
TEXTURE2D(_GrassColorRT);
SAMPLER(sampler_GrassColorRT);
TEXTURE2D(_GrassSlopeRT);
SAMPLER(sampler_GrassSlopeRT);
TEXTURE2D(_GrassGroundColorRT);
SAMPLER(sampler_GrassGroundColorRT);
TEXTURE2D(_GrassHeightMapRT);
SAMPLER(sampler_GrassHeightMapRT);
// The authored surface density the compute thinned the population with, whether it applies, and whether
// it shortens blades instead.
TEXTURE2D(_GrassDensityRT);
int _AuthoredAreas;
int _DensityShortensBlades;
TEXTURE2D(_GrassSplatControl);
TEXTURE2D(_GrassSplatDiffuse0);
TEXTURE2D(_GrassSplatDiffuse1);
TEXTURE2D(_GrassSplatDiffuse2);
TEXTURE2D(_GrassSplatDiffuse3);
TEXTURE2D(_GrassSplatNormal0);
TEXTURE2D(_GrassSplatNormal1);
TEXTURE2D(_GrassSplatNormal2);
TEXTURE2D(_GrassSplatNormal3);
SAMPLER(sampler_linear_clamp);
SAMPLER(sampler_trilinear_repeat);

struct GrassAttributes
{
    float3 positionOS : POSITION;
    float2 uv : TEXCOORD0;
};

struct GrassVertexData
{
    float3 positionWS;
    float3 pivotWS;
    half3 normalWS;
    half3 stableNormalWS;
    // The blade's direction at its root and at this height, curvature and wind included.
    half3 rootTangentWS;
    half3 tangentWS;
    float physicalPixelWidth;
    float2 mapUV;
    float2 shapeCoordinates;
    float height;
    float cameraDistance;
    float coverage;
    float worldUnitsPerPixel;
    float2 silhouette;
    uint seed;
};

// The current and previous frames evaluate the same deformation. Keeping these
// inputs explicit also lets motion vectors use the previous camera-facing width.
struct GrassShapeParameters
{
    float4 dimensions; // width, height, width randomness, height randomness
    float4 shaping; // curvature, distant width, subdivision boost, bump width
    float4 ranges; // distant width start/end, subdivision distance, minimum pixels
    float4 windST;
    float4 windMotion; // scroll XY, strength, time
};

struct GrassViewParameters
{
    float3 cameraPosition;
    float3 cameraForward;
    float3 cameraRight;
    float viewTranslationZ;
    float3 projection; // render height in pixels, abs(projection.m11), orthographic
};

struct GrassRootData
{
    float3 pivot;
    float3 slopeDirection;
    float2 wind;
    float2 curvature;
    float2 mapUV;
    float width;
    float tipWidth;
    float physicalPixelWidth;
    float height;
    float coverage;
    float worldUnitsPerPixel;
    float cameraDistance;
    // The drawn silhouette's base and tip widths over the physical triangle's base: what a view ray meets.
    float2 silhouette;
    uint seed;
};

uint GrassHash(uint value)
{
    value ^= value >> 16;
    value *= 0x85ebca6bu;
    value ^= value >> 13;
    value *= 0xc2b2ae35u;
    value ^= value >> 16;
    return value;
}

float GrassRandom(uint seed)
{
    return (GrassHash(seed) & 0x00ffffffu) * (1.0 / 16777216.0);
}

float2 GrassWorldToMapUV(float3 positionWS)
{
    float extent = max(_DrawDistance + _TextureUpdateThreshold, 0.001);
    return (positionWS.xz - _CenterPos) / (2.0 * extent) + 0.5;
}

float GrassDistanceDensity(float cameraDistance)
{
    float fadeRange = max(_DrawDistance - _FullDensityDistance, 0.001);
    float fade = saturate((_DrawDistance - cameraDistance) / fadeRange);
    return pow(fade, max(_DensityFalloffExponent, 0.001));
}

GrassShapeParameters GrassCurrentShape()
{
    GrassShapeParameters shape;
    shape.dimensions = float4(_GrassWidth, _GrassHeight, _GrassWidthRandomness, _GrassHeightRandomness);
    shape.shaping = float4(_GrassCurving, _ExpandDistantGrassWidth, _SubdivisionHeightBoost, _SubdivisionBumpWidth);
    shape.ranges = float4(_ExpandDistantGrassRange.xy, _SubdivisionDistance, _MinimumPixelWidth);
    shape.windST = _WindTexture_ST;
    shape.windMotion = float4(_WindScroll.xy, _WindStrength,
        _GrassUseExplicitTime != 0 ? _GrassTime : _Time.y);
    return shape;
}

GrassViewParameters GrassCurrentView()
{
    GrassViewParameters view;
    view.cameraPosition = _WorldSpaceCameraPos;
    view.cameraForward = -UNITY_MATRIX_V[2].xyz;
    view.cameraRight = UNITY_MATRIX_V[0].xyz;
    view.viewTranslationZ = UNITY_MATRIX_V[2].w;
    view.projection = float3(_ScaledScreenParams.y, abs(UNITY_MATRIX_P._m11), unity_OrthoParams.w);
    return view;
}

float GrassWorldUnitsPerPixel(float3 positionWS, GrassViewParameters view)
{
    float eyeDepth = max(abs(dot(positionWS, -view.cameraForward) + view.viewTranslationZ), 0.001);
    float depthScale = lerp(eyeDepth, 1.0, view.projection.z);
    if (!all(isfinite(view.projection)) || view.projection.y <= 0.0 ||
        !isfinite(depthScale) || depthScale <= 0.0)
        return 0.0;
    // A valid orthographic projection can have an arbitrarily small positive
    // scale. Flooring it changes the requested pixel width in large views.
    // Divide in stages so render height * projection scale cannot overflow.
    float worldUnitsPerPixel = (depthScale / max(view.projection.x, 1.0)) / view.projection.y * 2.0;
    return isfinite(worldUnitsPerPixel) ? max(worldUnitsPerPixel, 0.0) : 0.0;
}

// The authored surface density at a root, as a fraction of full density. With the distance fade it is
// the population the compute thinned the blades to, or with _DensityShortensBlades the fraction of their
// height the blades keep; a surviving blade's own coverage stays near one, so it cannot tell a sparse edge
// from the dense interior. Either way the canopy's area follows it.
float GrassAuthoredDensity(float2 mapUV)
{
    float authored = 1.0;
    if (_AuthoredAreas != 0)
    {
        authored = saturate(SAMPLE_TEXTURE2D_LOD(_GrassDensityRT, sampler_LinearClamp, mapUV, 0).r);
        // Only the first surface's map is bound. A blade stands only where its own surface's density
        // is positive, so zero here is another surface's map; that blade keeps the full canopy.
        if (authored <= 0.0)
            authored = 1.0;
    }
    return authored;
}

GrassRootData BuildGrassRoot(float4 positionData, GrassShapeParameters shape,
    GrassViewParameters view, float4 slope, float2 wind, float2 mapUV)
{
    GrassRootData root;
    root.pivot = positionData.xyz;
    root.seed = GrassHash(asuint(root.pivot.x)) ^ GrassHash(asuint(root.pivot.z) + 0x9e3779b9u);
    root.cameraDistance = distance(view.cameraPosition, root.pivot);
    root.mapUV = mapUV;
    float width = max(0.0, shape.dimensions.x) *
        (1.0 - GrassRandom(root.seed + 1u) * saturate(shape.dimensions.z));
    float widthRange = max(shape.ranges.y - shape.ranges.x, 0.001);
    width += saturate((root.cameraDistance - shape.ranges.x) / widthRange) * max(0.0, shape.shaping.y);

    // Keep lighting detail tied to physical width before minimum-pixel expansion.
    float originalFullWidth = width * 0.5;
    float worldUnitsPerPixel = GrassWorldUnitsPerPixel(root.pivot, view);
    root.physicalPixelWidth = worldUnitsPerPixel > 0.0 ? originalFullWidth / worldUnitsPerPixel : 0.0;
    if (!isfinite(root.physicalPixelWidth)) root.physicalPixelWidth = 0.0;
    float minimumFullWidth = max(0.0, shape.ranges.w) * worldUnitsPerPixel;
    // Unrepresentable expansion must not turn an otherwise finite blade into
    // infinite vertices. root.width stores twice this full geometric width.
    if (!isfinite(minimumFullWidth) || minimumFullWidth > 0.5 * FLT_MAX)
        minimumFullWidth = 0.0;
    float expandedFullWidth = max(originalFullWidth, minimumFullWidth);
    // Preserve the ratio at small world scales too. A fixed denominator floor
    // would thin even an unexpanded blade that projects to several full pixels.
    float widthCoverage = expandedFullWidth > 0.0 ? originalFullWidth / expandedFullWidth : 0.0;
    // A2C can represent the fractional area compensation. An opaque single
    // sample cannot: dithering it back into holes defeats its sampling floor.
    // Retain the widened silhouette there, while density fade stays separate.
    // A zero physical width must still produce no grass in either path.
    if (_GrassAlphaToCoverage <= 0.5)
        widthCoverage = originalFullWidth > 0.0 ? 1.0 : 0.0;
    float densityCoverage = saturate(positionData.w);
    root.worldUnitsPerPixel = worldUnitsPerPixel;
    root.coverage = densityCoverage * saturate(widthCoverage);
    root.width = expandedFullWidth * 2.0;
    root.tipWidth = 0.0;
    // A2C covers only the physical fraction of a widened blade.
    root.silhouette = float2(1.0, 0.0);
    float opaqueDensityScale = 1.0;
    if (_GrassAlphaToCoverage <= 0.5)
    {
        // Preserve a solid blade during the compute population transition.
        // Scale the already expanded silhouette, not its physical width before
        // the floor, so width and height both fade instead of puncturing fragment cells.
        // The last part of that fade may become subpixel; zero remains absent.
        opaqueDensityScale = sqrt(densityCoverage);
        float minimumPixels = max(0.0, shape.ranges.w);
        if (minimumFullWidth > 0.0 && originalFullWidth > 0.0 && minimumPixels > 0.0)
        {
            float resolved = smoothstep(minimumPixels, minimumPixels * 2.0, root.physicalPixelWidth);
            root.tipWidth = minimumFullWidth * (1.0 - resolved);
        }
        // The opaque floor widens the base and blunts the tip of the physical triangle.
        if (originalFullWidth > 0.0)
            root.silhouette = float2(expandedFullWidth, root.tipWidth) / originalFullWidth;
        root.width *= opaqueDensityScale;
        root.tipWidth *= opaqueDensityScale;
        // Lighting resolution follows the physically shortened transition blade.
        root.physicalPixelWidth *= opaqueDensityScale;
        root.coverage = densityCoverage > 0.0 && originalFullWidth > 0.0 ? 1.0 : 0.0;
    }
    root.height = max(0.0, shape.dimensions.y) *
        (1.0 - GrassRandom(root.seed + 2u) * saturate(shape.dimensions.w));
    float bump = saturate(1.0 - abs(root.cameraDistance - shape.ranges.z) / max(shape.shaping.w, 0.001));
    root.height *= max(0.0, 1.0 + shape.shaping.z * bump) * opaqueDensityScale;
    if (_DensityShortensBlades != 0)
        root.height *= GrassAuthoredDensity(mapUV);

    // Decode the captured direction before strength interpolation. Clear pixels
    // contain zero RG and alpha; their neutral signed direction must remain zero.
    float2 slopeRG = slope.a > 0.0001 ? saturate(slope.rg / slope.a) : float2(0.5, 0.5);
    float2 slopeXZ = slopeRG * 2.0 - 1.0;
    float3 slopeDirection = SafeNormalize(float3(slopeXZ.x,
        1.0 - max(abs(slopeXZ.x), abs(slopeXZ.y)) * 0.5, slopeXZ.y));
    root.slopeDirection = SafeNormalize(lerp(float3(0, 1, 0), slopeDirection, saturate(slope.a)));
    root.wind = wind * shape.windMotion.z * (1.0 - saturate(slope.a));
    root.curvature = (float2(GrassRandom(root.seed + 3u), GrassRandom(root.seed + 4u)) * 2.0 - 1.0) * shape.shaping.x * opaqueDensityScale;
    return root;
}

float3 GrassDirectionAtHeight(GrassRootData root, float height)
{
    float3 direction = root.slopeDirection;
    direction.xz += root.wind * height;
    return SafeNormalize(direction);
}

float3 GrassCenterAtHeight(GrassRootData root, float height)
{
    float3 center = root.pivot + GrassDirectionAtHeight(root, height) * height * root.height;
    center.xz += height * height * root.curvature;
    return center;
}

// The blade's unit direction at a fraction of its height: the slope of its centre line.
half3 GrassTangentAtHeight(GrassRootData root, float height)
{
    float low = saturate(height - 0.01), high = saturate(height + 0.01);
    float3 tangent = GrassCenterAtHeight(root, high) - GrassCenterAtHeight(root, low);
    return dot(tangent, tangent) > 1e-12 ? half3(normalize(tangent)) : half3(GrassDirectionAtHeight(root, height));
}

void EvaluateGrassRow(GrassRootData root, GrassViewParameters view, float height,
    out float3 center, out float3 halfSpan)
{
    height = saturate(height);
    float3 bladeDirection = GrassDirectionAtHeight(root, height);
    center = GrassCenterAtHeight(root, height);
    // Perspective rays vary across the image. Facing every blade along the
    // camera's central ray can backface-cull visible roots behind the camera in
    // world XZ when looking down. Orthographic rays remain parallel instead.
    float3 viewDirection = view.projection.z > 0.5
        ? view.cameraForward : SafeNormalize(center - view.cameraPosition);
    float3 rightTangent = cross(bladeDirection, viewDirection);
    if (dot(rightTangent, rightTangent) < 0.00001)
        rightTangent = view.cameraRight;
    rightTangent = SafeNormalize(rightTangent);
    // A continuously vanishing cap replaces only physically unresolved tips.
    // Resolved and A2C blades keep the same pointed silhouette (tipWidth0).
    halfSpan = rightTangent * 0.5 * lerp(root.width * 0.5, root.tipWidth, height);
}

float3 EvaluateGrassPosition(GrassRootData root, GrassViewParameters view, float2 uv)
{
    float3 center, halfSpan;
    EvaluateGrassRow(root, view, uv.y, center, halfSpan);
    return center + (uv.x * 2.0 - 1.0) * halfSpan;
}

GrassRootData BuildGrassRootForView(float4 positionData, GrassViewParameters view)
{
    GrassShapeParameters shape = GrassCurrentShape();
    float2 mapUV = GrassWorldToMapUV(positionData.xyz);
    float4 slope = SAMPLE_TEXTURE2D_LOD(_GrassSlopeRT, sampler_GrassSlopeRT, mapUV, 0);
    float2 windUV = positionData.xz * shape.windST.xy + shape.windST.zw + shape.windMotion.xy * shape.windMotion.w;
    float2 wind = SAMPLE_TEXTURE2D_LOD(_WindTexture, sampler_WindTexture, windUV, 0).rg * 2.0 - 1.0;
    return BuildGrassRoot(positionData, shape, view, slope, wind, mapUV);
}

GrassRootData BuildCurrentGrassRoot(float4 positionData)
{
    return BuildGrassRootForView(positionData, GrassCurrentView());
}

GrassVertexData BuildGrassVertex(GrassAttributes input, uint instanceID)
{
    GrassRootData root = BuildCurrentGrassRoot(_GrassPositions[(uint)_GrassInstanceOffset + instanceID]);
    GrassViewParameters view = GrassCurrentView();
    GrassVertexData blade;
    blade.positionWS = EvaluateGrassPosition(root, view, input.uv);
    blade.pivotWS = root.pivot;
    blade.worldUnitsPerPixel = root.worldUnitsPerPixel;
    blade.silhouette = root.silhouette;
    blade.seed = root.seed;
    blade.height = saturate(input.uv.y);
    blade.cameraDistance = root.cameraDistance;
    blade.mapUV = root.mapUV;
    blade.coverage = root.coverage;
    blade.physicalPixelWidth = root.physicalPixelWidth;

    float3 normalJitter = float3(GrassRandom(blade.seed + 5u) * 2.0 - 1.0,
        0.0, GrassRandom(blade.seed + 6u) * 2.0 - 1.0);
    float3 stableNormal = GrassDirectionAtHeight(root, blade.height) - view.cameraForward * 0.5;
    blade.stableNormalWS = SafeNormalize(stableNormal);
    blade.normalWS = SafeNormalize(stableNormal + _RandomNormal * normalJitter);
    blade.rootTangentWS = GrassTangentAtHeight(root, 0.0);
    blade.tangentWS = GrassTangentAtHeight(root, blade.height);
    // This coordinate remains linear across the tapered mesh, including its final
    // triangle, so the same analytic edge coverage works for every geometry LOD.
    blade.shapeCoordinates = float2((input.uv.x - 0.5) * (1.0 - blade.height), blade.height);
    return blade;
}

// One-sided blade area per square metre of ground (leaf area index) at a root: blades per square
// metre at this spacing and local density, times the mean blade's triangle of physical base width
// (half the width parameter, before pixel widening) and height, each after its mean randomness.
float GrassCanopyAreaIndex(float density)
{
    float spacing = max(_GrassSpacing, 0.001);
    float baseWidth = 0.5 * max(_GrassWidth, 0.0) * (1.0 - 0.5 * saturate(_GrassWidthRandomness));
    float height = max(_GrassHeight, 0.0) * (1.0 - 0.5 * saturate(_GrassHeightRandomness));
    return saturate(density) * 0.5 * baseWidth * height / (spacing * spacing);
}

// Blade area above a fraction of blade height: a triangle keeps (1 - h) squared of its area above h.
float GrassCanopyAreaAbove(float areaIndex, float bladeHeight)
{
    float above = 1.0 - saturate(bladeHeight);
    return areaIndex * above * above;
}

// Optical depth per unit of blade area toward a direction (Beer-Lambert): near-vertical blades project
// (2/pi) sin(theta) of their area toward a direction at zenith angle theta, and the ray crosses the canopy,
// a layer on the ground, along 1 / (n . d) of its depth for the ground normal n; (2/pi) tan(theta) on level
// ground. The transmission is the fraction of light from that direction passing the blades above a point.
float GrassCanopyDepth(float3 direction, float3 groundNormal)
{
    float across = dot(groundNormal, direction);
    return across > 0.0001 ? 0.6366198 * sqrt(saturate(1.0 - direction.y * direction.y)) / across : 1e4;
}

// Blades turn to face the camera, so a view ray meets each one's full width: pi/2 times the depth toward
// a light, which meets them at every azimuth.
float GrassCanopyViewDepth(float3 viewDirection, float3 groundNormal)
{
    return 1.5707963 * GrassCanopyDepth(viewDirection, groundNormal);
}

half GrassCanopyTransmission(float areaAbove, float3 direction, float3 groundNormal)
{
    return areaAbove > 0.0 ? exp(-GrassCanopyDepth(direction, groundNormal) * areaAbove) : 1.0h;
}

// Turns a normal by the rotation that takes unit direction a to unit direction b (Rodrigues).
// A blade never folds back onto itself, so a and b are never opposite.
half3 GrassTurn(half3 normal, half3 a, half3 b)
{
    float3 axis = cross(a, b);
    float cosine = dot(a, b);
    return SafeNormalize(normal * cosine + cross(axis, normal) + axis * (dot(axis, normal) / max(1.0 + cosine, 1e-4)));
}

// The cosine-weighted mean of that transmission over the sky: the ambient a point still receives.
// Four-point Gauss-Legendre quadrature over cos(theta); it is exact without blades.
// Four-point Gauss-Legendre quadrature on [0, 1].
static const float4 kGrassQuadratureNode = float4(0.0694318, 0.3300095, 0.6699905, 0.9305682);
static const float4 kGrassQuadratureWeight = float4(0.1739274, 0.3260726, 0.3260726, 0.1739274);
// Over the sky's cos(theta): the weights times 2 cos(theta), and the optical depth (2/pi) tan(theta) at each node.
static const float4 kGrassSkyCosine = kGrassQuadratureNode;
static const float4 kGrassSkyWeight = 2.0 * kGrassQuadratureWeight * kGrassSkyCosine;
static const float4 kGrassSkyDepth = 0.6366198 * sqrt(1.0 - kGrassSkyCosine * kGrassSkyCosine) / kGrassSkyCosine;

half GrassCanopySkyVisibility(float areaAbove)
{
    return dot(kGrassSkyWeight, exp(-kGrassSkyDepth * areaAbove));
}

// The canopy shows its shaded ground and blade bases from above and only its lit tops along the ground.
// The grass divides its light by the bare ground's light as this view sees it under the canopy on average,
// so the grass and the ground between its blades average the bare terrain beside them from every view and
// distance while the canopy's depth stays as contrast within the grass.
//
// A view ray meets the drawn silhouettes: trapezoids of base and tip widths (b, t) over the physical
// triangle's base, so with optical depth k per unit of physical area it has crossed k A ((b - t) x^2 + 2 t x)
// by (1 - x) of the blade height, while the light there is shaded by the physical area above, A x^2. Over
// the probability u of having met a blade, x follows in closed form; four Gauss-Legendre nodes in u give
// the physical area above each, and the ground under all of A takes the probability left.

float4 GrassCanopySeenAbove(float drawnArea, float viewDepth, float2 silhouette, out float reach)
{
    float perDepth = max(viewDepth * drawnArea, 1e-6);
    reach = 1.0 - exp(-perDepth * (silhouette.x + silhouette.y));
    float4 crossed = -log(1.0 - reach * kGrassQuadratureNode) / perDepth;
    float taper = silhouette.x - silhouette.y, tip = 2.0 * silhouette.y;
    float4 x = abs(taper) > 1e-4 ?
        (sqrt(tip * tip + 4.0 * taper * crossed) - tip) / (2.0 * taper) : crossed / max(tip, 1e-6);
    x = saturate(x);
    return drawnArea * x * x;
}

// The mean of a canopy term exp(-c a) over what the view sees (GrassCanopySeenAbove).
float GrassCanopySeenTerm(float4 above, float reach, float c, float drawnArea)
{
    return reach * dot(kGrassQuadratureWeight, exp(-c * above)) + (1.0 - reach) * exp(-c * drawnArea);
}

// The sky's (x) and a light's (y) seen means; both are one without blades.
half2 GrassCanopySeen(float drawnArea, float viewDepth, float2 silhouette, float lightDepth)
{
    if (drawnArea <= 0.0)
        return 1.0h;
    float reach;
    float4 above = GrassCanopySeenAbove(drawnArea, viewDepth, silhouette, reach);
    float sky = 0.0;
    UNITY_UNROLL for (int i = 0; i < 4; i++)
        sky += kGrassSkyWeight[i] * GrassCanopySeenTerm(above, reach, kGrassSkyDepth[i], drawnArea);
    return half2(sky, GrassCanopySeenTerm(above, reach, lightDepth, drawnArea));
}

// TerrainLit's albedo and tangent-space normal for the bound terrain's first four layers at a
// root, filtered at the mip the terrain itself uses at that distance, so a blade takes the colour
// and the detail of the ground it stands in. Returns false outside that terrain.
void GrassSplatLayer(TEXTURE2D_PARAM(diffuseMap, diffuseSampler), TEXTURE2D_PARAM(normalMap, normalSampler),
    float4 tile, float4 remap, float4 surface, float width, half weight, float3 positionWS, float worldUnitsPerPixel,
    inout half3 albedo, inout half3 normalTS, inout half2 smoothnessMetallic)
{
    float2 layerUV = positionWS.xz * tile.xy + tile.zw;
    float lod = max(0.0, log2(max(worldUnitsPerPixel * width * tile.x, 0.000001)));
    half4 diffuse = SAMPLE_TEXTURE2D_LOD(diffuseMap, diffuseSampler, layerUV, lod);
    albedo += weight * diffuse.rgb * remap.rgb;
    normalTS += weight * UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(normalMap, normalSampler, layerUV, lod), remap.a);
    // TerrainLit's sources: the constant times the diffuse alpha, the diffuse alpha alone, or the constant.
    half smoothness = surface.y < 0.5 ? diffuse.a * surface.x : (surface.y < 1.5 ? diffuse.a : surface.x);
    smoothnessMetallic += weight * half2(smoothness, surface.z);
}

bool GrassTerrainSplat(float3 positionWS, float worldUnitsPerPixel, out half3 albedo, out half3 normalTS,
    out half2 smoothnessMetallic)
{
    albedo = 0.0h;
    normalTS = 0.0h;
    smoothnessMetallic = 0.0h;
    if (_GrassSplatTerrain.z <= 0.0 || _GrassSplatTerrain.w <= 0.0)
        return false;
    float2 terrainUV = (positionWS.xz - _GrassSplatTerrain.xy) / _GrassSplatTerrain.zw;
    if (any(terrainUV < 0.0) || any(terrainUV > 1.0))
        return false;
    // TerrainLit maps normalized terrain UVs to the control map's texel centres.
    float2 controlUV = (terrainUV * (_GrassSplatControl_TexelSize.zw - 1.0) + 0.5) * _GrassSplatControl_TexelSize.xy;
    half4 control = SAMPLE_TEXTURE2D_LOD(_GrassSplatControl, sampler_linear_clamp, controlUV, 0);
    control /= max(dot(control, 1.0h), 0.0001h);
    GrassSplatLayer(TEXTURE2D_ARGS(_GrassSplatDiffuse0, sampler_trilinear_repeat),
        TEXTURE2D_ARGS(_GrassSplatNormal0, sampler_trilinear_repeat), _GrassSplatTile[0], _GrassSplatRemap[0],
        _GrassSplatSurface[0], _GrassSplatWidths.x, control.r, positionWS, worldUnitsPerPixel, albedo, normalTS,
        smoothnessMetallic);
    GrassSplatLayer(TEXTURE2D_ARGS(_GrassSplatDiffuse1, sampler_trilinear_repeat),
        TEXTURE2D_ARGS(_GrassSplatNormal1, sampler_trilinear_repeat), _GrassSplatTile[1], _GrassSplatRemap[1],
        _GrassSplatSurface[1], _GrassSplatWidths.y, control.g, positionWS, worldUnitsPerPixel, albedo, normalTS,
        smoothnessMetallic);
    GrassSplatLayer(TEXTURE2D_ARGS(_GrassSplatDiffuse2, sampler_trilinear_repeat),
        TEXTURE2D_ARGS(_GrassSplatNormal2, sampler_trilinear_repeat), _GrassSplatTile[2], _GrassSplatRemap[2],
        _GrassSplatSurface[2], _GrassSplatWidths.z, control.b, positionWS, worldUnitsPerPixel, albedo, normalTS,
        smoothnessMetallic);
    GrassSplatLayer(TEXTURE2D_ARGS(_GrassSplatDiffuse3, sampler_trilinear_repeat),
        TEXTURE2D_ARGS(_GrassSplatNormal3, sampler_trilinear_repeat), _GrassSplatTile[3], _GrassSplatRemap[3],
        _GrassSplatSurface[3], _GrassSplatWidths.w, control.a, positionWS, worldUnitsPerPixel, albedo, normalTS,
        smoothnessMetallic);
    normalTS.z += 0.00001h;
    normalTS = normalize(normalTS);
    return true;
}

// TerrainLit's tangent frame on a surface with normal n: tangent along +X and bitangent along +Z
// on level ground.
half3 GrassTerrainNormalToWorld(half3 normalTS, half3 normal)
{
    half3 vertexTangent = cross(half3(0.0h, 0.0h, 1.0h), normal);
    half3 bitangent = cross(normal, vertexTangent);
    return SafeNormalize(-vertexTangent * normalTS.x + bitangent * normalTS.y + normal * normalTS.z);
}

half GrassGroundBlend(float bladeHeight, half mapStrength)
{
    float rootFalloff = 1.0 - smoothstep(0.0, max(_GroundBlendHeight, 0.0001), bladeHeight);
    float heightWeight = lerp(saturate(_GroundBlendFloor), 1.0, rootFalloff);
    return saturate(mapStrength * _GroundBlendStrength) * heightWeight * step(0.0001, _GroundBlendHeight);
}

float GrassGroundHeight(float2 uv, float fallback)
{
    float2 captured = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv, 0).rg;
    if (!all(isfinite(captured)) || captured.g <= 0.0)
        return fallback;
    if (captured.g >= 1.0)
        return captured.r;

    // Height is not premultiplied by eligibility. Exclude clear/invalid texels
    // from bilinear height weights without dividing a vertex-color mask into Y.
    int2 size = max(int2(_GrassHeightMapRT_TexelSize.zw), int2(1, 1));
    float2 pixel = uv * size - 0.5;
    int2 basePixel = int2(floor(pixel));
    float2 fraction = frac(pixel);
    float height = 0.0;
    float validWeight = 0.0;
    [unroll]
    for (uint corner = 0u; corner < 4u; ++corner)
    {
        int2 offset = int2(corner & 1u, corner >> 1u);
        int2 coordinate = clamp(basePixel + offset, int2(0, 0), size - 1);
        float2 neighbor = LOAD_TEXTURE2D(_GrassHeightMapRT, coordinate).rg;
        float2 weightXY = lerp(1.0 - fraction, fraction, float2(offset));
        float weight = weightXY.x * weightXY.y;
        if (neighbor.g > 0.0 && all(isfinite(neighbor)))
        {
            height += neighbor.r * weight;
            validWeight += weight;
        }
    }
    return validWeight > 0.0 ? height / validWeight : fallback;
}

half3 GrassGroundNormal(float2 uv)
{
    float2 texel = _GrassHeightMapRT_TexelSize.xy;
    if (any(texel <= 0.0) || any(uv < texel) || any(uv > 1.0 - texel))
        return half3(0, 1, 0);

    float2 center = SAMPLE_TEXTURE2D_LOD(_GrassHeightMapRT, sampler_GrassHeightMapRT, uv, 0).rg;
    if (!all(isfinite(center)) || center.g <= 0.0)
        return half3(0, 1, 0);
    float height = GrassGroundHeight(uv, center.r);
    // A missing neighbor contributes the valid center height rather than a
    // false cliff down to the capture's clear world height of zero.
    float left = GrassGroundHeight(uv - float2(texel.x, 0), height);
    float right = GrassGroundHeight(uv + float2(texel.x, 0), height);
    float down = GrassGroundHeight(uv - float2(0, texel.y), height);
    float up = GrassGroundHeight(uv + float2(0, texel.y), height);

    float2 sampleDistance = max(texel * (2.0 * (_DrawDistance + _TextureUpdateThreshold)), 0.0001);
    return SafeNormalize(float3((left - right) / (2.0 * sampleDistance.x),
        1.0, (down - up) / (2.0 * sampleDistance.y)));
}

half GrassFragmentCoverageWithMode(float2 shapeCoordinates, float instanceCoverage, uint seed, bool alphaToCoverage)
{
    float coverage = saturate(instanceCoverage);
    // The single-sample contact pass stores the analytic silhouette beside
    // depth when color uses A2C. In the opaque path the rasterizer already
    // owns the tapered mesh edge: randomly clipping that edge a second time
    // adds holes without providing fractional color coverage.
    if (alphaToCoverage)
    {
        float edgeDistance = (1.0 - shapeCoordinates.y) * 0.5 - abs(shapeCoordinates.x);
        float edgeFootprint = max(fwidth(shapeCoordinates.x) + 0.5 * fwidth(shapeCoordinates.y), 0.0001);
        coverage *= saturate(edgeDistance / edgeFootprint + 0.5);
        clip(coverage - 0.00001);
        return coverage;
    }
    clip(coverage - 0.00001);
    // Single-sample geometry already carries density through its solid area.
    // Motion on an MSAA camera still uses the legacy fractional sample estimate.
    if (_GrassAlphaToCoverage <= 0.5)
        return 1.0h;

    // Quantize in blade coordinates and hash its stable world pivot. Camera motion
    // and time never reseed the pattern. Without MSAA the contact pass clips the
    // identical fragments and records coverage 1 for the surviving samples.
    float across = shapeCoordinates.x / max(1.0 - shapeCoordinates.y, 0.0001) + 0.5;
    uint2 cell = (uint2)floor(saturate(float2(across, shapeCoordinates.y)) * float2(4.0, 16.0));
    float threshold = GrassRandom(seed ^ GrassHash(cell.x + cell.y * 17u));
    clip(coverage - max(threshold, 0.00001));
    return 1.0h;
}

half GrassFragmentCoverage(float2 shapeCoordinates, float instanceCoverage, uint seed)
{
    return GrassFragmentCoverageWithMode(shapeCoordinates, instanceCoverage, seed, _GrassAlphaToCoverage > 0.5);
}

half GrassForwardCoverage(float2 shapeCoordinates, float instanceCoverage, uint seed)
{
    if (_GrassAlphaToCoverage > 0.5)
    {
        // The tapered mesh is the blade silhouette. Hardware MSAA already
        // covers its geometric edges; A2C is ANDed with that sample mask.
        // Export only density/width compensation here. Applying the analytic
        // edge fraction as well would attenuate the same silhouette twice.
        half coverage = saturate(instanceCoverage);
        clip(coverage - 0.00001);
        return coverage;
    }
    return GrassFragmentCoverageWithMode(shapeCoordinates, instanceCoverage, seed, false);
}

#endif
