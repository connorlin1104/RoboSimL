# Version 1.2 — open items

What 1.1 shipped with, knowingly. 1.1 went to review 2026-09-17; nothing here blocked it.

Ranked by what a player actually feels.

---

## The cause, found 2026-09-17

Both GPU findings are **one problem: draw calls.** Every robot is imported CAD, and every CAD part
is still its own `MeshRenderer` — hundreds of them, all sharing two materials.

| | renderers | distinct materials | things that actually move |
| --- | --- | --- | --- |
| LiteScene (the field alone) | 752 | 3 | 15 Rigidbodies |
| `654V_v3.prefab` (drivable) | 769 | 2 | 28 ArticulationBodies |
| `ryan-cascaderobot_Showcase` | 576 | 2 | **none** |
| `654v-v2_Showcase` | 737 | 2 | none |
| `654v-v1_Showcase` | 612 | 2 | none |
| `360rpm-drivetrain_Showcase` | 361 | 2 | none |

- **A driving frame is field 752 + robot 769 = ~1,520 draw calls, drawn from three materials.** The
  home screen draws 576 for one robot that does nothing but turn
- They are not separate for any reason that survives inspection:
  - the showcase prefabs have **no ArticulationBody, no Rigidbody and no collider** — they are
    pictures, and 576 of their parts are welded to each other by definition
  - the drivable robot has 769 renderers hanging off **28 links**. Nothing inside a link can move
    relative to anything else inside it
  - the field has 752 renderers and **15** Rigidbodies, so ~737 of them never move at all — and
    only 72 of the scene's 1,825 objects carry any static flag
- Merged by material, those numbers become: showcase **2**, robot **~56** (28 links x 2 materials),
  field a few dozen if merged per region. Call it 60 instead of 1,520
- This also explains the shape of the measurements. It is not fill rate: render scale is already
  0.8 and MSAA is off, and the same ~1,500 draws cost the same whether the phone is cool or hot.
  Tiny CAD triangles rasterise at a 2x2 quad minimum, so hundreds of small draws waste the GPU
  twice over — per-draw overhead and wasted rasteriser

### The order to do it in

1. **Merge the showcase prefabs.** Biggest win for the least risk: static, no physics, two
   materials, and `Build Home Screen` already generates them, so the merge is a bake step.
   576 -> 2
2. **Re-run run 2 on the phone** (10 min, robot `Turning`). The `gpu` number is the whole
   experiment: 15.84 ms should collapse. If it does not, this diagnosis is wrong and the field work
   below is not worth starting
3. **Merge the field's static structure**, per region rather than into one mesh — one giant mesh
   cannot be frustum-culled. The 15 Rigidbodies stay as they are
4. **Merge each robot link's visual meshes**, one per material per link. Colliders and the
   ArticulationBody hierarchy stay exactly where they are — only the renderers combine
5. **Re-run run 4** and see where `Serious` lands. Heat is the work: cut the work and the wall
   moves out

### Free while you are in there

- `LiteScene`'s camera has `m_RenderPostProcessing: 1` (`LiteScene.unity:2899`) and the scene's
  Volume points at `DefaultVolumeProfile` — where **every effect is neutral**: Tonemapping `None`,
  Bloom intensity 0, DoF `Off`, every other intensity 0
  - So the app pays for a post chain that does nothing: an intermediate colour buffer instead of
    rendering straight to the backbuffer (expensive on a tile GPU), and a full-screen blit
  - `m_SupportsHDR: 1` makes that intermediate FP16, doubling its bandwidth, for effects that are off
  - Turn both off and check a screenshot is identical. It should be
- All 752 LiteScene renderers have `m_ReceiveShadows: 1`, including ones nothing can shadow
- Watch the vertex count when merging — over 65,535 the combined mesh needs `IndexFormat.UInt32`

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
- **Cause found** — see *The cause* above: ~1,520 draw calls a frame from three materials
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
    showcase draws 576 times for one static robot built from two materials
- Do **not** just lower `driftRenderRate`. It lowers the average and leaves the hitch
- Battery, same two runs (the phone reports in 5% steps, so directional only):
  - `Turning` — 40% → 35% inside 10 minutes
  - `Off` — 35% held flat across 20 minutes
  - So the turntable is most of the idle drain on the home screen, which is where the app sits

## 3. The performance readout sits on top of L1 and L2

Connor, on the phone, 2026-09-17.

- `PerfOverlay.cs:45-46` places it by hand: `HomePosition (40, -40)`, `FieldPosition (40, -228)`
  - The 228 was meant to clear L1/L2 in a game. It does not — and it cannot, because button
    positions are **player-configurable** (Controls Layout: "drag every button where your thumbs
    actually are"). No fixed offset is safe for everyone
- Options, cheapest first:
  - move it to a corner the controls never occupy, and check that against `ControlsLayout`'s bounds
  - read the live L1/L2 `RectTransform`s and sit clear of them
  - make the panel draggable, like the controls themselves
- Diagnostics-only, so it never reached a player — it is in the way of *testing*, which is why it
  matters now that the perf runs are a routine

## 4. Pneumatic stroke classes are wrong

- The real classes are **25 / 50 / 75 mm** (Connor, 2026-09-17). The code asserts 20 / 50 / 90
  - `Assets/Scripts/Editor/Mechanisms/PneumaticBuilder.cs` — `CylinderSize` enum
    (`Large90`/`Regular50`/`Small20`), the switch, `SizeFromStroke`, and the defaults
  - `Assets/Scripts/Mechanisms/ClawRig.cs:192` tooltip and `:193` default
  - `Assets/Scripts/Editor/Mechanisms/ClawBuilder.cs:79` default
- Every shipped pneumatic is therefore off-class: v1 ×2 at 90, v2 flip + clamp at 20
- **Cosmetic only.** `strokeMm` drives the visible rod slide through `PneumaticCylinderFollower`;
  the real motion comes from `retractedDeg` / `extendedDeg`
- Fix the four sources, then re-run the builders on v1 and v2 to re-bake the four values
  - Both robots are `remote: 0`, so **no re-bundle and no re-upload**

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
