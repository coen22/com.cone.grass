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
| Clumping | 0 sprinkles the kind evenly. 1 gathers all of it into clusters inside drifts; in between, that fraction goes there and the rest stays everywhere. |
| Drift Area | Fraction of the ground the kind's drifts cover, on the profile's **Clump Size** (drift diameter, 12 m by default). |
| Cluster Area | Fraction of a drift its clusters cover, on the profile's **Cluster Size** (0.7 m): clumps of a few to a few dozen plants with bare ground between. 1 fills the whole drift. |
| Moisture, Slope | Habitat: -1 prefers dry or flat ground, 1 wet ground or slopes, 0 anywhere. |

Real meadows grow in drifts, not as a sprinkle: a species gathers where its own low-frequency noise is high, and inside
a drift in clusters on a smaller scale. A drift is where the kind's drift noise lies in the top **Drift Area** of its
values, a cluster where its cluster noise lies in the top **Cluster Area** of its values; each threshold is a quantile
of that noise's measured distribution, so drifts and clusters cover those fractions of the ground and the kind's share
stays its weight however strong the clumping. Unclumped kinds fill what the clusters leave, in proportion to their
weights. A cluster never holds more than every root: the cluster area, then the drift area, is raised until
`clumping * weight / (driftArea * clusterArea)` is at most one. Where the clusters of two heavy clumped kinds overlap they
would hold more than every root; they then share it, which trims their shares slightly (in the tests, weights 0.3 and
0.1 both clumped come out about 0.02 low). In the meadow preset the flowers clump fully, so none stands alone between
the clusters; a typical daisy cluster holds 6 to 20 heads.

Habitat sorts the kinds: a shared moisture field (smooth noise on the profile's **Habitat Size**, 40 m) and the ground's
slope (measured from two height samples half a metre from the root, only when a kind has a slope preference; 0 flat, 1 at
about 35 degrees) scale each kind's weight by `(1 + moisture * (2 m - 1)) * (1 + slope * (2 s - 1))` before the kinds
share the root. Habitat makes the shares local: buttercups crowd the wet hollows, knapweed and yarrow the dry slopes.

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

## Blades, stems and colour

**Height Scale**, **Width Scale** and **Curving Scale** scale the material's blade for that kind; the material's
randomness still applies. **Stem Width** keeps part of the blade's width to its tip: 0 is a pointed grass blade, the
meadow's flowers use 0.6, a visible stem the head sits on and that bends with the blade in wind and interaction.
**Tint** multiplies the blade colour. **Instance Variation** moves each plant's colour toward **Variation Tint** by its own
random number, **Clump Variation** by its clump's smooth noise, so neighbouring plants drift together. The root still
blends to the matched ground colour (**Ground Color Strength**, terrain splat roots and **Ground Colour Along Whole Blade**
behave as before, and the kind's tint multiplies the result); heads never take the ground colour.

The canopy occlusion's leaf area index follows the mix's mean blade area (`MeanBladeAreaScale`), and frustum culling
bounds every root by the tallest, widest and most curved kind and its largest head.

## Heads

**Head** selects a quad-cross head drawn on the kind's stem tip: **Daisy** (a ring of narrow petals round a disc),
**Cup** (five broad petals, a buttercup or poppy), **Umbel** (a flat cluster of florets, yarrow), **Spike** (an upright
spike, knapweed or lavender) or **Clover** (a three-lobed leaf: a herb, not a flower). **Mesh** uses the profile's
**Head Mesh** (about one unit across, pivot at the stem, +Y up), tinted from the palette.

The quad-cross is one flat face and two crossed upright quads (12 vertices, 6 triangles). The head shader cuts each
outline procedurally, so no texture is needed, and with MSAA the outline edge becomes sample coverage. A head rebuilds
the bent blade it sits on, wind and interaction included, so it stays on its stem. Its face turns from the stem's tip
toward the sun (a leaf less), with each head's own jitter. Each head's size varies by **Head Size Randomness** either way
(±30 % in the meadow). **Bud Fraction** of the heads are closed buds (small, mostly green) and **Seed Fraction** have gone
to seed (petals gone, a browned disc or florets). Each head picks a **Palette** colour; **Palette Clumping** gives a drift
one colour. **Centre** colours the disc or florets.

At range a head would alias into a white speck. It has no pixel floor: it shrinks away once it covers under about three
pixels, and over the last **Head Fade** of **Head Draw Distance** (60 m). As it goes, the top of its stem takes the mean
petal colour (**Distant Tip Colour**), so a cluster reads as a soft coloured patch at 10 to 100 m rather than speckles.

Heads use their own queues after the three LOD ranges of the position buffer: one for quad-cross heads and one for mesh
heads, each **Max Head Count** (renderer, millions, default 0.25: 4 MiB of positions) when a kind uses it and absent
otherwise. A full queue drops heads and counts them as overflow.

### Known gaps

- Heads have no contact-depth pass: contact shadows do not include them.
- Heads have no motion-vector pass: with motion vectors on they carry no per-object motion, so keep TAA off, as for the
  baseline.

## Data layout

`_GrassPositions.w` is `coverage + 2 * kind` (`GrassPackKind`); coverage stays in (0, 1], so kind zero is the earlier
layout. Custom blade shaders should call `GrassUnpackCoverage` and `GrassUnpackKind` from `GrassVariants.hlsl`. Motion
history stores `16 * kind + rows + coverage / 2`. The counter buffer has six entries (three LOD counts, overflow, two
head counts) and the indirect argument buffer five (three LODs, two heads); with any head queue both head counters and
arguments are written, with none the compute leaves the extra entries untouched.

## Validation

EditMode: `GrassVariantSelectionTests` (deterministic placement from the seed, weights, drift area, clumping keeping its
share, cluster sizes and bare ground between them, habitat, area weights, packing and the neutral table) and
`GrassVariantGpuGenerationTests` (the shipped kernel against the CPU twin, head queues and area weights; needs a
compute-capable device). Cost of the heads, the extra selection work and the weight capture belongs to the #28
measurement at 1080p with the variants on and off.
