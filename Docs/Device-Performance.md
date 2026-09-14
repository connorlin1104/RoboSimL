# Device performance check

Measuring the app on the phone. The phone can't be connected to Xcode or to Unity's profiler, so
the app measures itself: **Settings ▸ Robot ▸ Show Performance Stats** puts a thin see-through
column of numbers in the top-left corner (under L1/L2 in a game) and writes the same numbers to a
file once a second.

- What it answers — the numbers the home stage (UI Stage 3) has never had from a real device
  - How long a cold launch takes to reach the home screen, and to put the robot on the stage
  - What the turning robot costs: frame time, heat and memory, against the stage switched off
  - How the field holds up over a long drive, and how long it takes to load
- Needs a build that has it — its Settings ▸ Robot page ends in a **Performance** section
- Needs **Frame Timing Stats** on (Player Settings ▸ Other Settings; `enableFrameTimingStats: 1` in
  `ProjectSettings.asset`)
  - Off, a release build measures no CPU or GPU time, and the readout's `CPU` and `GPU` say `off`
  - `Validate Performance Overlay` fails while it's off
- The readout itself costs next to nothing: two short columns of text redrawn once a second

---

## Before each run

- **Unplug the phone**
  - Charging heats the battery, and heat is one of the things being measured
- **Low Power Mode off**
  - It caps the CPU, the GPU and the screen's refresh rate. The readout adds a `Low Power` row while
    it's on
- **Start cool** — the readout's `Heat` should say `Nominal`
  - If it says `Fair` or worse, let the phone sit for a few minutes first

## Turning it on

1. Settings ▸ Robot ▸ scroll to the bottom ▸ tick **Show Performance Stats**
   - The readout appears at once, top-left, and a new log starts
   - The button under it, **Home Screen Robot**, switches the home screen's robot between
     `Turning`, `Still` and `Off`. The runs below use it
2. Leave it ticked for the whole check
   - It stays on across launches, and a launch is only measured when the app *starts* with it on

## The runs

1. **Three cold launches**
   - Swipe the app away in the app switcher, wait ~10 s, tap the icon
   - Wait on the home screen for about 10 s after the robot appears
     - The log sums the launch up once its first 5 s are over and the robot is showing. Those 5 s
       are the window where first-time shader compiles show up
   - Each launch writes its own log
2. **10 minutes on the home screen, robot turning**
   - Home Screen Robot on `Turning`, the default. Close Settings and don't touch anything
3. **10 minutes on the home screen, robot off**
   - Settings ▸ Robot ▸ tap **Home Screen Robot** twice: `Turning` → `Still` → `Off`. Close Settings
   - `Off` shows the chassis mark and draws nothing. The difference from run 2 is what the turning
     robot costs
   - `Still` draws the robot once, as a picture — worth a couple of minutes if `Off` shows the robot
     is expensive
   - It's a setting, so it stays: tap it back to `Turning` when you're done
4. **10 minutes driving**
   - Settings ▸ Robot ▸ Lite Field on, then Drive (or the full field, if that's the one in question)
   - Drive the way you normally would. The log also records how long the field took to load

## Reading the readout

- `FPS 60` — frames in the last second. The app caps itself at 60, so a frame has 16.7 ms
- `CPU 6.1 ms`, `GPU 4.4 ms` — the average time the main thread and the GPU spent on one frame. The
  rest of the 16.7 ms is headroom
- `Worst 21 ms` — the slowest frame in the last second: a hitch shows up here
- `Heat Nominal` — iOS's own heat level
  - It goes `Nominal` → `Fair` → `Serious` → `Critical`. From `Serious` iOS slows the phone down to
    cool it, and every frame time after that point measures a throttled phone
- `RAM 412 MB` — the memory iOS counts against the app. How much more it may take before iOS closes
  it is in the log (`headroom_mb`)
- `Low Power` — only there while Low Power Mode is on
- Long times read `1.2 s` and big memory `1.4 GB`, so the column never gets wider
- Launch and load times aren't on screen. They're in the log: the `launch` row, and each scene's
  `first_frame` row

## Getting the logs to the Mac

- Files ▸ On My iPhone ▸ RoboSimL ▸ **Performance**
  - One file per session, named for when it started
- Select them ▸ Share ▸ AirDrop to the Mac
- Hand them to Claude to read, or open them in Numbers
- Delete them from Files afterwards — a 10-minute session is under 100 KB, but they pile up

## What's in a log

- One row a second with `kind` = `tick`:

  | column | what |
  | --- | --- |
  | `t_s` | seconds since the app's process started (since pressing Play, in the editor) |
  | `scene` | `HomeScene`, `LiteScene`, `SampleScene` |
  | `fps`, `frame_ms`, `worst_frame_ms` | frames that second, their average and the slowest, as felt |
  | `cpu_main_ms`, `cpu_render_ms`, `gpu_ms`, `worst_gpu_ms` | Frame Timing Stats' averages for that second |
  | `heat`, `low_power` | `Nominal`/`Fair`/`Serious`/`Critical`, and `1` for Low Power Mode |
  | `footprint_mb`, `headroom_mb` | memory iOS counts against the app, and what's left before it closes it |
  | `battery_pct`, `power` | battery level, and `Discharging` / `Charging` / `Full` |
  | `stage` | the home stage: `Drift` (`Turning` in Settings), `Still`, `Off`, or `hidden` behind a full-screen page |

  - An empty cell is "not measured" — the editor has no heat or memory figures, for one
- Rows with `kind` = `event` in between, with `event` and `detail` filled in:
  - `session` — the app version, phone model, iOS version, screen size, refresh rate, frame cap
  - `first_frame` — each scene's first frame, with `load_s` when a load was asked for
  - `stage_robot_shown` — a robot up on the stage: `built` or `cached`, `build_ms` (the hitch of
    building it) and `ready_ms` (from picking it to seeing it)
  - `launch` — once a launch's first 5 s are over and its robot is showing: its first frame, robot
    and slowest frame
  - `stage_mode` — Home Screen Robot switched in Settings
  - `load_requested`, `heat`, `low_power`, `low_memory`, `background`, `foreground`, `quit`,
    `stats_off`

## Turning it off

- Untick Show Performance Stats. The readout goes and the log closes
