# grass #88 plant kinds: stills and walk

Procedural 512 m meadow (lane GS clone-only driver), gym blade settings (0.5 m x 6.5 cm, terrain colour along the blade,
splat roots, canopy, ground normal), spacing 0.08 m, draw distance 200 m, 1920x1080, D3D11, RTX 3090, Unity 6000.6.0f1.
Same cameras before and after: eye 1.1 m looking 1 m ahead; eye 1.8 m looking 10 m and 100 m ahead.

- before: main dfbc796 (today's blades). after: a761199 with the meadow preset (GrassVariantProfile.ResetToMeadow).
- `*-before-after.jpg`: before above, after below. `*-after.png`: the after still at full quality.
- `walk-30s-before-after.mp4`: 30 s at 1.4 m/s, camera 1.8 m up and 4 m behind the walker, both sides the same path.
- `walk-30s-after-720p.mp4`: the after walk alone.
Wind animates from editor time, which differs between launches, so the blades' sway is not frame-matched.
