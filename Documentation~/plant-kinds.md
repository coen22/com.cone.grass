# Plant kinds and flowers

A **Grass Variant Profile** lets one Infinite Grass Renderer grow several kinds of plant: short and tall grasses, broad herbs, clover, and flowers with heads. Without a profile the renderer grows its one material blade.

## Set up

1. **Assets > Create > Cone > Grass > Variant Profile**. A new profile starts as the [meadow preset](#the-meadow-preset): a flowering lawn.
2. Assign it to the Infinite Grass Renderer's **Plant Kinds > Variants**.
3. Edit the kinds in the profile. Changes show at once.

**Reset** in the profile's context menu restores the meadow preset.

## Kinds

A profile holds up to **eight kinds**; entries beyond the eighth are ignored. Each kind scales the material's blade (**Height Scale**, **Width Scale**, **Curving Scale**), tints it, and can carry a head. Every value is relative to the material, so one material serves the whole mix. See the [kind reference](reference.md#kind) for every setting.

The blade colour of a kind:

- **Tint** multiplies the blade colour.
- **Instance Variation** moves each plant toward **Variation Tint** by its own random number.
- **Clump Variation** moves it by a smooth clump noise, so neighbouring plants drift together.

Blades of every kind keep the ground colour match at their roots, and the kind's tint multiplies the result. Heads never take the ground colour.

## The mix

Each root picks one kind from a stable random number, so the meadow is the same every time you open the scene. **Weight** is a kind's share of the roots relative to the other kinds. The profile's **Seed** re-deals the kinds without moving a single root.

### Clumping and drifts

Real meadows grow in drifts, not as an even sprinkle. Each kind has its own drift pattern:

| Setting | Effect |
|---|---|
| Clumping | 0 sprinkles the kind evenly. 1 puts all of it into clusters inside drifts. In between, that fraction goes into drifts and the rest stays everywhere. |
| Drift Area | Fraction of the ground the kind's drifts cover. Drift size is the profile's **Clump Size** (12 m by default). |
| Cluster Area | Fraction of a drift that its clusters cover. Cluster size is the profile's **Cluster Size** (0.7 m). Clusters hold a few to a few dozen plants with bare ground between them. 1 fills the whole drift. |

Clumping moves a kind's plants together but keeps its share: a kind with weight 0.03 is still 3 % of the roots, however strongly clumped. Unclumped kinds fill the gaps the clusters leave. Where the clusters of two heavily clumped kinds overlap, they share the space, which lowers both shares slightly.

### Habitats

**Moisture** and **Slope** sort kinds by ground:

- **Moisture** follows a smooth moisture field over the profile's **Habitat Size** (40 m). 1 crowds a kind into the wet hollows; -1 onto the dry ground.
- **Slope** follows the terrain: 0 is flat, 1 is about 35 degrees. 1 prefers slopes; -1 flat ground. Slope is measured only when some kind has a slope preference.

Habitat changes the shares locally; it does not change which kinds exist.

## Heads

**Head** puts a head on the kind's stem tip:

| Head | Looks like |
|---|---|
| Daisy | A ring of narrow petals round a disc: daisy, chamomile. |
| Cup | Five broad petals in a shallow cup: buttercup, poppy. |
| Umbel | A flat cluster of small florets: yarrow, cow parsley. |
| Spike | An upright spike of florets: lavender, knapweed, plantain. |
| Puff | A small soft round flower: clover blossom, a pompom. |
| Clover | A three-lobed leaf held flat: a herb, not a flower. |
| Mesh | The profile's **Head Mesh**, tinted from the palette. |

Procedural heads are a small cross of three quads whose outline the shader cuts, so they need no texture. With MSAA, the outline edge becomes sample coverage. A head sits on the bent tip of its blade, so it sways with the wind and bends with interaction. Heads face the sun with some jitter; **Sideways** nods a fraction of them over to the side. They appear in [contact shadows](reference.md#optional-contact-shadows) and [motion vectors](reference.md#motion-vectors) like blades.

Give a flower a stem: set **Stem Width** to about 0.5 and a narrow **Width Scale**. A stem keeps its width to the tip and hides in the grass.

**Head Size** is the diameter in metres; **Head Size Randomness** varies it. The profile's **Bud Fraction** of heads are closed buds and **Seed Fraction** have gone to seed.

### Palettes

Each head picks one colour from its kind's **Palette**. Up to **four colours** are used. **Palette Clumping** 0 picks per head; 1 gives a whole drift the same colour. **Centre** colours the disc or florets.

### Distance fade

A head would alias into a white speck at range, so it never widens to a minimum pixel size. Instead it shrinks away once it covers fewer than about three pixels, and over the last **Head Fade** of **Head Draw Distance**. As it goes, the stem tip takes the head's mean petal colour by **Distant Tip Colour**, so far flowers become faint dots of colour in the lawn rather than flickering specks.

### Head capacity

Heads use their own queues: one for procedural heads and one for mesh heads. Each holds **Max Head Count** (renderer, in millions, default 0.25) and exists only when some kind with weight above zero uses it. A full queue drops heads and counts them as overflow.

## Per-area weights

A Placement Area can change the mix inside it. Turn on **Plant Kinds > Override Variant Weights**, then set **Variant Weights**, one per kind in the profile's order, from 0 (none here) to 1 (the profile's weight). The inspector lists the kind names next to the weights.

- **Only areas with Override Variant Weights affect the kinds.** An area without an override does not take part: inside it, the profile's own mix grows, except where an area with an override also covers the ground, whose weights then apply.
- Where areas with overrides overlap, each kind takes the **larger** of their weights.
- The kinds an area keeps share its roots in proportion. A lawn sets the flowers to 0; a flower bank lowers the grasses.
- If every weight in an area is 0, it grows the **first kind only**.
- Per-area weights apply in **Authored Areas** mode only.

From a script, call `area.SetVariantWeights(1f, 1f, 0f, 0f, 0f, 0f, 0f)` (missing entries stay 1) and `area.ClearVariantWeights()`.

The weights are captured at a quarter of the renderer's **Capture Resolution**, only while the profile mixes two or more kinds. A weight edge therefore blends over about one weight texel: about 2.4 m at the default capture settings.

## The meadow preset

A new profile is a short, lush flowering lawn: small, sparse, single flower heads standing at the height of the grass tips. The look to aim for is a mown meadow seen at eye level, where flowers just reach the top of the lawn and dot it with colour, never a field of tall stalks.

| Kind | Weight | Notes |
|---|---|---|
| Lawn grass | 0.62 | Height 0.62 of the material, broad (1.5×), bright tint. |
| Undercoat | 0.3 | Short (0.35×), broad (1.8×), darker tint. Gives the lawn depth. |
| Broad herb | 0.06 | Low (0.3×), very broad (3×), half clumped. |
| Clover | 0.015 | Clover leaves, clumped, slight preference for wet ground. |
| Meadow puff | 0.003 | Puff heads in pink, lilac, yellow and white. |
| Daisy | 0.0012 | White and pale pink, slight preference for flat ground. |
| Buttercup | 0.0012 | Yellow cups, slight preference for wet ground. |

The preset's choices, and why:

- **Flowers at grass-tip height.** The lawn's tips stand at 0.62 of the material height times 0.5 to 1 (the material's height randomness). Flowers stand at 0.62 with **Height Randomness** 0.1, so their heads sit level with the tallest lawn tips: visible over the grass round them, never above it.
- **Small, single heads.** Heads are about 6 cm across, vary by only ±10 %, and are unclumped, so flowers stand alone rather than in patches.
- **Sparse.** The three flowers together are about 0.5 % of the roots: roughly one flower per 1.2 m² at a **Spacing** of 0.08 m, or one per 1.9 m² at the default 0.1 m.
- **Fade to dots.** Heads draw to 50 m and fade over the last 40 %. **Distant Tip Colour** 0.35 leaves only faint colour at range.
- Few buds (5 %) and seed heads (2 %).

To make it your own, change the palettes and weights first. Keep the flowers' **Height Scale** equal to the lawn's and their **Height Randomness** low if you want the heads to stay at the tips.

## Limits

- Up to 8 kinds per profile and 4 palette colours per kind.
- Heads are lit by the main light (with its shadows) and ambient light only. They do not receive additional lights, even when blades do.
- Heads never take the ground colour.
- One profile per renderer. Areas can reweight its kinds but not add new ones.

## Custom blade shaders

With a profile, each root's packed position stores the kind beside the coverage. Custom blade shaders should read them with `GrassUnpackCoverage` and `GrassUnpackKind` from `GrassVariants.hlsl`. Kind 0 uses the same layout as a renderer without a profile.
