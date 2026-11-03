# Version 1.2 — open items

What 1.1 shipped with, knowingly. 1.1 went to review 2026-09-17; nothing here blocked it.

Ranked by what a player actually feels.

---

## The cause, found 2026-09-17

**Nothing that ships has ever been decimated.** Both GPU findings are that one fact.

Measured by `Tools ▸ RoboSim ▸ Validate ▸ Geometry Census`. "Drawn" counts every renderer every frame,
which is the GPU's bill; "unique" counts each mesh once, which is the memory bill. A phone frame is
normally budgeted around **300,000** triangles.

| | draws | **triangles drawn** | unique meshes | mesh memory |
| --- | --- | --- | --- | --- |
| LiteScene, the field alone | 752 | **2,700,248** | 717 | 249 MB |
| SampleScene, the full field | 1,390 | 6,203,690 | 1,355 | 568 MB |
| 654V v3, drivable | 769 | **9,371,311** | 150 | 226 MB |
| 654V v2, drivable | 1,639 | 12,900,907 | 220 | 246 MB |
| 654V v1, drivable | 800 | 5,514,143 | 183 | 228 MB |
| 360 RPM Drivetrain, drivable | 521 | 3,074,202 | 39 | 39 MB |
| 654V v3 showcase (home screen) | 576 | **9,131,013** | 99 | 218 MB |
| 654V v2 showcase | 737 | 10,816,607 | 147 | 230 MB |

- **Driving is the field plus the robot on top of it: ~12.1 million triangles a frame.** Forty times
  what a phone frame is meant to carry, and 226 + 249 MB of mesh before anything else loads
- **The home screen draws 9.1 million of them thirty times a second** for a robot that only turns
- The references say why. 767 of 654V v3's 769 meshes point straight into `Ryan_CascadeRobot.fbx`
  (103 MB), and 710 of LiteScene's 752 point straight into `OverrideFieldVersion3.fbx` (205 MB).
  Raw CAD, imported and shipped
- The project already knows this is wrong and already has the tool. `ReduceRobotMeshes` /
  `MeshDecimator` were written for it, and `ReduceRobotMeshes`'s own header says one 654V is "~2.8
  million triangles and ~226 MB of runtime mesh data" and that keeping 0.08 of it "takes 2.8M down to
  about 220k". **It was never run on the four robots that ship, or on the field.**

### What it was NOT

Worth writing down, because it was the first answer and it was wrong. The four robots carry 361-737
parts each, built from two material *assets*, which reads like a draw-call problem. It is not:

- Merging each showcase to one draw per material works — 576 draws become 18, 737 become 42 — and
  moves the triangle count by exactly nothing. The merge is implemented (`BuildShowcasePrefabs`),
  and guarded so it cannot bake until the geometry comes down
- The tell was baking it: merged copies of undecimated meshes came to **2.5 GB of assets** for four
  robots. That is the same geometry, written out where its size is visible
- Counting distinct material *assets* undercounts badly. An FBX holds its materials as sub-assets of
  one file, so three guids across the project are really 8 to 54 materials

### The order to do it in

1. **Decimate the robots**, one at a time — `Tools ▸ RoboSim ▸ Robot ▸ Reduce Robot Meshes`, or
   `-executeMethod ReduceRobotMeshes.RunBatch -robot <name> -keep 0.08`. It replaces the render
   meshes and **does not touch colliders**, so it cannot change how a robot drives. Doing them one at
   a time with the result in front of you is the tool's own instruction, and the ratio is a
   judgement about how that robot looks
   - It fixes both screens at once: a showcase is baked from its robot, so a decimated robot bakes a
     decimated showcase
   - At 0.08, 654V v3 goes from 9.37M drawn triangles to roughly 750k, and 226 MB to roughly 18 MB
2. **Re-run run 2 on the phone** (10 min, robot `Turning`). The `gpu` number is the whole
   experiment: 15.84 ms against 1.63 ms with the stage off
3. **Decimate the field — DONE 2026-11-02** (`Tools ▸ RoboSim ▸ Field & Pieces ▸ Reduce Field Meshes`)
   - SampleScene 6.20M → **4.11M** drawn triangles (−34 %), LiteScene 2.70M → **1.27M** (−53 %).
     168 reduced meshes in `Assets/FieldMeshes`, 104 MB of LFS
   - Physics untouched: colliders keep their own FBX mesh, and each reduced mesh carries its source's
     bounds, which is what PieceGeometry / ClawGrab read
   - The decimator now **carries the import's normals** (`keepSourceNormals`). Rebuilt by angle,
     even an unreduced corner piece shaded 12 % different from its import, so 3.35M triangles could
     never pass the look check. Carried, the unreduced rebuild matches the import exactly
   - The field is held to a **stricter look** than a robot, because a player drives right up to it:
     each part judged at the closest chase-camera density (~200 px per world unit), pixel tolerance
     24, limit 0.5 %, one more gentle step at 0.7. Meshes under 2,000 triangles are left alone: a
     goal's engraved rings are separate small meshes, the check forgives anything under 3 px wide,
     and they came out a circle instead of a hexagon. All meshes under 2,000 together were 43k
   - Checked by rendering both scenes before and after: overview, corners and goals, at 4 units
     (the closest the camera gets) and closer. At 4 units 0.2–0.4 % of pixels move by more than
     2/255 and nothing is visible; closer than the camera can get, a goal's top rim softens slightly
4. **Then decide about merging.** After decimation the draws are the only thing left, and merging
   costs a duplicated copy of the mesh data — cheap once the meshes are small, and possibly not worth
   it at all. Measure before spending it
5. **Re-run run 4** and see where `Serious` lands. Heat is the work: cut the work and the wall
   moves out

### Free while you are in there

- **Post-processing — CORRECTED 2026-11-02, the first read was wrong.** The field scenes' Volume
  points at `SampleSceneProfile`, not `DefaultVolumeProfile`, and it is NOT neutral: Neutral
  tonemapping, Bloom 0.25 (threshold 1), Vignette 0.2
  - Turning post and HDR off was tried and rendered: the field comes out ~7 % brighter with the
    walls and tape blown to white. Not the same picture, so post and HDR stay on
  - **Bloom is off** (`SampleSceneProfile.asset`). Rendered A/B: 0.24 % of pixels move by more than
    2/255, all of it a faint glow along the bright tape lines. Bloom is the multi-pass part of the
    chain (prefilter plus a down/up-sample pyramid), so it is the one worth paying nothing for
  - Tonemapping and vignette stay: both run inside the one uber pass the chain needs anyway, and
    tonemapping needs the HDR buffer. Render scale 0.8 means there is an intermediate buffer either way
- All 752 LiteScene renderers have `m_ReceiveShadows: 1`, including ones nothing can shadow

## 1. The field drops to 30 fps after four minutes of driving

**Measured** (`Device-Performance.md`, run 4): heat `Nominal` → `Fair` at 2:45 → `Serious` at 4:10
of continuous driving from a cold phone. From `Serious` iOS pins the app to exactly 30.0 fps and
holds it there for the rest of the session — it never recovers while you keep driving.

- This is the one a player notices: a practice session is 4 minutes at ~46 fps and then half that
- It is not a thermal accident, it is the budget. Driving costs **22–25 ms of GPU per frame on a
  cool phone**, against a 16.7 ms budget for 60 fps
  - So LiteScene can never reach 60 on an iPhone 13, throttled or not. It was running at 46–49
  - The CPU is not the problem: `cpu_main` 6.0 ms cool, and it only climbs to 10.8 because the
    throttle slows the CPU too
- Render scale is already 0.8 and MSAA is already off (`Mobile_RPAsset.asset:28-29`), so the easy
  resolution lever is spent. The cost is shading, overdraw or draw calls
- **Cause found** — see *The cause* above: the field draws 2.70M triangles and the robot on
  top of it 9.37M, against a ~300k phone budget. Undecimated CAD
- Target: 16.7 ms cool. That buys 60 fps *and* pushes `Serious` out past a realistic session

## 2. The home screen runs at 48 fps because of the turntable

**Measured** (run 2 vs run 3, the same phone, back to back, both `Nominal` throughout):

| home screen | fps | frame | `cpu_main` | **`gpu`** | memory |
| --- | --- | --- | --- | --- | --- |
| robot `Turning` | **47.8** | 21.0 ms | 1.8 ms | **15.84 ms** | 661 MB |
| robot `Off` | **60.1** | 16.64 ms | 0.8 ms | **1.63 ms** | 667 MB |

- The turntable costs **~14.2 ms of GPU per frame, averaged**. It draws at 30 Hz
  (`RobotStageView.driftRenderRate`), so a frame it draws on costs roughly **28–30 ms of GPU** —
  about twice the whole 16.7 ms budget. Every draw frame is a dropped frame, which is the 48
- For scale: one decorative robot on a plain background costs about what **an entire field frame**
  costs, and at lower resolution. That gap is the thing to chase, not the cadence
- The stage deliberately renders at native resolution, not the pipeline's 0.8:
  `RobotStageView.EnsureTexture()` computes `longest / RenderScale()` and caps at
  `maxTextureSize = 1536`
  - Texture size is worth logging, but it is not the main cost — see *The cause* above: the
    showcase is 9.1 million triangles, thirty times a phone frame's budget
- Do **not** just lower `driftRenderRate`. It lowers the average and leaves the hitch
- Battery, same two runs (the phone reports in 5% steps, so directional only):
  - `Turning` — 40% → 35% inside 10 minutes
  - `Off` — 35% held flat across 20 minutes
  - So the turntable is most of the idle drain on the home screen, which is where the app sits

## 3. The performance readout sits on top of L1 and L2 — FIXED 2026-11-02

Connor, on the phone, 2026-09-17.

- **Fixed:** in a game the panel now reads where the controls really are, once a second, and takes the
  first spot none of them touch (`PerfOverlay.ClearSpot`): under L1/L2, the top-left corner, top
  middle, top-right, under R1/R2, then the side middles. Every button in the scene counts, plus the
  six movable groups by name (that covers the joysticks). Nowhere clear: the least-covered spot
  - `Validate Performance Overlay` checks the default layout still gives the old spot, that L1/L2
    dragged over it moves the panel, and that a fully covered screen does not just take the first spot
- What it was, for the record:
  - `PerfOverlay.cs:45-46` placed it by hand: `HomePosition (40, -40)`, `FieldPosition (40, -228)`.
    The 228 was meant to clear L1/L2, and could not for everyone: button positions are
    **player-configurable** (Controls Layout: "drag every button where your thumbs actually are")
  - Of the three options listed then (a fixed safe corner, read the live controls, a draggable
    panel), reading the live controls is the one that works for every layout

## 4. Pneumatic stroke classes are wrong — FIXED 2026-11-02

- **Fixed:** the four sources say 25 / 50 / 75, and the four baked values moved to the nearest real
  class: v1's two 90s are 75, v2's flip and clamp 20s are 25 (edited in the prefabs, which is what a
  builder re-run writes for these fields). Nothing to re-bundle
- What it was: the code asserted 20 / 50 / 90 in `PneumaticBuilder.cs` (`CylinderSize` enum, now
  `Large75`/`Regular50`/`Small25`, its switch, `SizeFromStroke`, defaults), `ClawRig.cs:192-193` and
  `ClawBuilder.cs` (presets, defaults, the stroke error message)
- **Cosmetic only.** `strokeMm` drives the visible rod slide through `PneumaticCylinderFollower`;
  the real motion comes from `retractedDeg` / `extendedDeg`

## 5. Scene reload hitches 186–283 ms

- Run 4 reloaded LiteScene three times. Two of them hitched: 186 ms at t=307, 283 ms at t=493
- Warm reloads are otherwise fast — `load_s` 0.17 / 0.21 / 0.30 against 2.47 s for the first load
- Low priority: it is a hitch on a screen the player just asked to change

---

## Not a problem — measured, leave it alone

- **Memory.** 570–890 MB footprint across every run, headroom never below 1.2 GB, no `low_memory`
  event in any session
- **Cold launch.** Engine ready and first frame at 3.06 / 3.53 / 3.20 s across three cold launches,
  robot on the stage 46–78 ms later. The 2.3–2.7 s "worst frame" in each launch window *is* the
  first frame, not a hitch during use
- **Building the showcase robot.** `build_ms` 1.7–2.4, and `cached` on every later show
