# Plant kinds: blades, herbs and flowers (#88)

A **Grass Variant Profile** (`Assets > Create > Cone > Grass > Variant Profile`) lets one Infinite Grass Renderer grow
several kinds of plant: short and tall blades, broad herbs, clover and flowers with heads. Assign it to the renderer's
**Plant Kinds > Variants**. A new profile starts as a meadow (short grass, tall grass, a broad herb, clover, daisy,
buttercup, yarrow and knapweed); **Reset** on the asset restores it. Without a profile the renderer grows its one
material blade exactly as before: every per-kind value is stored relative to that blade, and the root layout is
unchanged for kind zero.

## How a root chooses its kind

Every generation cell keeps its stable root, jitter, density and LOD decision. The kind is one more stable decision,
from the cell's seed and the profile's **Seed**: changing the seed re-deals the kinds without moving a root, and the same
seed always grows the same meadow.

| Setting | Meaning |
|---|---|
| Weight | The kind's share of the roots, relative to the other kinds. |
| Clumping | 0 sprinkles the kind evenly. 1 gathers all of it into drifts; in between, that fraction goes to drifts and the rest stays everywhere. |
| Drift Area | Fraction of the ground the kind's drifts cover. Smaller drifts are denser. |
| Clump Size (profile) | Diameter of a drift or colour clump, in metres. |

A drift is where the kind's own smooth noise lies in the top **Drift Area** of its values. The threshold is a quantile of
that noise's measured distribution, so the drifts cover that fraction of the ground and the kind's share stays its weight
whatever the clumping. Unclumped kinds fill what the drifts leave in proportion to their weights. Where the drifts of two
heavy clumped kinds overlap they would hold more than every root; they then share it, which trims their shares slightly
(in the tests, weights 0.3 and 0.1 both clumped come out about 0.02 low). A drift never holds more than every root: the
drift area is raised to at least `clumping * weight / (1 - weight * (1 - clumping))`.

`GrassVariantSelection` is the CPU twin of the compute's choice, in the same float32 order, for tests and tools.

## Placement Areas reweight the kinds

A Placement Area's **Plant Kinds > Override Variant Weights** scales each kind's weight inside that area, in the profile's
order, from 0 (none here) to 1. A lawn sets the flowers to 0; a flower bank lowers the grasses. The kinds an area keeps
share its roots in proportion. Overlapping areas take each kind's largest multiplier, as density takes the largest
coverage. Ground the areas do not reach, or an area whose multipliers are all zero, grows the profile's own mix. From a
script, call `SetVariantWeights(...)` and `ClearVariantWeights()`.

The multipliers are captured beside the ground colour into two RGBA8 maps at a quarter of the **Capture Resolution**
(256² at the default 1024: 0.5 MiB), only while a profile mixes two or more kinds in authored areas. They recapture
with the ground colour: an area edit, a move or a capture recentre.

## Blades and colour

**Height Scale**, **Width Scale** and **Curving Scale** scale the material's blade for that kind; the material's
randomness still applies. **Tint** multiplies the blade colour. **Instance Variation** moves each plant's colour toward
**Variation Tint** by its own random number, **Clump Variation** by its clump's smooth noise, so neighbouring plants
drift together. The root still blends to the matched ground colour (**Ground Color Strength**, terrain splat roots and
**Ground Colour Along Whole Blade** behave as before, and the kind's tint multiplies the result); heads never take the
ground colour.

The canopy occlusion's leaf area index follows the mix's mean blade area (`MeanBladeAreaScale`), and frustum culling
bounds every root by the tallest, widest and most curved kind and its head.

## Heads

**Head** selects a quad-cross head drawn on the kind's blade tip: **Daisy** (a ring of narrow petals round a disc),
**Cup** (five broad petals, a buttercup or poppy), **Umbel** (a flat cluster of florets, yarrow), **Spike** (an upright
spike, knapweed or lavender) or **Clover** (a three-lobed leaf: a herb, not a flower). **Mesh** uses the profile's
**Head Mesh** (about one unit across, pivot at the stem, +Y up), tinted from the palette.

The quad-cross is one flat face and two crossed upright quads (12 vertices, 6 triangles). The head shader cuts each
outline procedurally, so no texture is needed, and with MSAA the outline edge becomes sample coverage. A head rebuilds
the bent blade it sits on, wind and interaction included, so it stays on the stem. It shrinks away over the last
**Head Fade** of **Head Draw Distance**; from there a flower's blade tip takes its mean petal colour (**Distant Tip
Colour**), so a meadow still reads as flowering at 100 m without drawing heads. Each head picks a **Palette** colour;
**Palette Clumping** gives a drift one colour. **Centre** colours the disc or florets.

Heads use their own queues after the three LOD ranges of the position buffer: one for quad-cross heads and one for mesh
heads, each **Max Head Count** (renderer, millions, default 0.25: 4 MiB of positions) when a kind uses it and absent
otherwise. A full queue drops heads and counts them as overflow. Heads have no contact-depth or motion-vector pass:
contact shadows do not include them, and with motion vectors on they carry no per-object motion (keep TAA off, as for
the baseline).

## Data layout

`_GrassPositions.w` is `coverage + 2 * kind` (`GrassPackKind`); coverage stays in (0, 1], so kind zero is the earlier
layout. Custom blade shaders should call `GrassUnpackCoverage` and `GrassUnpackKind` from `GrassVariants.hlsl`. Motion
history stores `16 * kind + rows + coverage / 2`. The counter buffer has six entries (three LOD counts, overflow, two
head counts) and the indirect argument buffer five (three LODs, two heads); with no head queue the compute leaves the
extra entries untouched.

## Validation

EditMode: `GrassVariantSelectionTests` (deterministic placement from the seed, weights, drift area, clumping, area
weights, packing and the neutral table) and `GrassVariantGpuGenerationTests` (the shipped kernel against the CPU twin,
head queues and area weights; needs a compute-capable device). Cost of the heads, the extra selection work and the
weight capture belongs to the #28 measurement at 1080p with the variants on and off.
