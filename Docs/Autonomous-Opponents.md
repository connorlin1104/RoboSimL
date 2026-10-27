# RoboSimL — Autonomous Robots: Complete Plan

Planned 2026-10-26. Line numbers are from that date — check them against the code before relying on them.

## Goal
- A match is 4 robots: 1 human + 3 AI.
  - AI ally: on the human's alliance, works around the human.
  - 2 AI opponents: work as a pair; how well they coordinate is a user knob.
- AI robots are 654V_v3 only for now (the one meta robot).
- The autonomous period is deferred — handled later as its own scripted routine.

## 🔨 STILL TO BUILD — nothing below exists in the code today
- 🔨 **Score counter.** There is no scoring anywhere. Today "scored" only means
  `GoalStackMagnet.SeatedCount` went up.
  - Needed: points per alliance from goals (and Toggles, and anything else the game scores), live while the match runs.
  - The task values in assignment and the SPSA objective both depend on it.
- 🔨 **Match clock.** No timer, no match start/end.
  - Needed for "time left" in the weights and to end self-play matches.
- 🔨 **Alliances.** There is no red/blue type.
  - `MatchLoaderController.AllianceSign` guesses a side from the transform.
  - Needed: an alliance on every robot, plus which goals/loaders/Toggles belong to which alliance.
- 🔨 **Multi-robot spawning.** `RobotSpawner` spawns ONE robot.
  - Needed: 4 start poses, alliance per slot, AI vs human per slot.
  - Reuse `PlaceAndWatch` for footprint placement.
- 🔨 **Input isolation for AI robots.** Every robot binds the SAME global `<Gamepad>` actions, so a second robot obeys the player.
  - Shared actions: drive sticks, ButtonRouter's 12 buttons, `IntakePull.scoreAction`, `PneumaticActuator.toggleAction`.
  - Their OnDisable calls `action.Disable()` on the SHARED action, so disabling an AI robot's input kills the player's controls.
  - Fix: instantiate the AI robot inactive, strip the action references / ButtonRouter, then activate.
- 🔨 **Reverse Drive fix.** `ReverseDriveSettings.Reversed` is applied AFTER the manual-input branch (`RobotMotorController.cs` ~:1060), so turning it on flips AI robots too.
  - Exempt manual input without breaking the 7 existing `SetManualInput` callers (validators).
- 🔨 **AI brain** — everything in Layers 1–4 below.
- 🔨 **Stuck watchdog.** Robots beach on cups and hook on Toggles.
  - Without recovery, an AI robot that snags stays stuck all match.
- 🔨 **Difficulty knobs + UI** (Communication etc.) and presets on a settings screen.
- 🔨 **Headless self-play runner** in batch mode, for SPSA and for regression tests.
- 🔨 **Autonomous period** (deferred).

## What already exists (use it, don't rebuild it)
- Drive: `RobotMotorController.SetManualInput(throttle, turn)` (`Assets/Scripts/Drivetrain/RobotMotorController.cs:1412`).
  - Skips deadzone/expo/sensitivity but keeps the slew + brake. Centred = brake.
- Forward direction: `RobotMotorController.DriveForwardWorld` (:252). Root +Z is NOT forward on v3.
- Mechanisms: `RobotMechanisms.Find(id)`.
  - v3's ids: GroundIntake, ScoringIntake, CascadeLift, Cascade, ArmRotate.
  - `MotorActuator.SetInput(-1..1)`, `PneumaticActuator.Extend/Retract/Toggle`.
  - `IntakePull.ScoreDrop()` / `IsCarrying` / `TryCapture`, `CascadeLift.Progress` (0..1).
- Goals: `GoalStackMagnet` — `SeatedCount`, `captureRadius = 0.6u`.
- Robot-vs-robot collision works: self-collision is muted per robot root (`IgnoreRobotSelfCollision`).
- There are almost no singletons.

## Step 0 — Foundations
- Input isolation + Reverse Drive fix (see STILL TO BUILD).
  - Test: the player drives and presses every button → the AI robot doesn't move.
  - Test: the player still works after the AI robot is destroyed.
- Multi-robot spawning, alliances, match clock, score counter.
- The AI controller runs before `RobotMotorController.FixedUpdate` (`[DefaultExecutionOrder]`), or its commands land one step late.
- Check: the v3 prefab's robotId is `ryan-cascaderobot`, not `654v-v3` — intended?

## Layer 1 — Assigning tasks (Hungarian)
- Task slots: score, matchload, Toggle, goal, block-X. Repeatable tasks get several slots.
- Cost(robot, slot) = travel time (A* length / speed) − task value × weight + knob terms.
  - Task value comes from the 🔨 score counter + game state.
- Commitment bonus: never switch tasks for a small gain.
  - Replan ~2 Hz and whenever a task completes.
- AI ally:
  - Estimate the human's task from heading/velocity.
  - Put the human in as a FIXED row; take the best of what's left.
- With 2–3 robots, brute force gives the same answer; the cost-matrix setup is what matters.

## Communication knob (opponent coordination)
- Each opponent runs ITS OWN assignment.
  - Its teammate is a row filled with what it BELIEVES the teammate will do.
- Hard (comm = 1): the teammate row holds the true task.
  - Both compute the same joint assignment and never double up.
- Medium: the teammate row is a delayed, noisy guess made from watching the teammate move.
  - A don't-duplicate penalty scales with comm.
- Easy (comm = 0): no teammate row.
  - Each robot picks the best task for itself; they can chase the same cup.

## Other knobs (proposed — to cut or keep)
- Aggression: blocking weight.
- Reaction time: how stale its view is.
- Driving skill: speed cap, path sloppiness.
- Ally awareness: how well the ally reads the human.
- Presets Easy/Medium/Hard fill in all the knobs; Custom shows them.
  - Open question: are raw knobs shown to players, or only presets?

## Layer 2 — Getting there (A* + pure pursuit)
- The grid is built at RUNTIME from the loaded scene's static colliders.
  - LiteScene is not the real field layout.
  - Coarse cells. Obstacles inflated by the robot's radius.
- Cups cost extra (beaching), but the TARGET cup is excluded.
- Other robots are moving cost zones.
- Pure pursuit → throttle/turn → `SetManualInput`. Never set transforms or velocities directly.
- 🔨 Stuck watchdog: no progress for N seconds → back up, replan.

## Layer 3 — Doing the task (per-task controllers — the biggest chunk of work)
- A* gets a robot NEAR a task, not lined up with it. Each task needs its own finishing controller.
- Scoring on v3 is a state machine driven by sensors, not timers:
  1. Intake with the lift DOWN.
     - Aim the IntakeMouth (not the robot's front) at the cup's centre of mass.
     - Intake is blocked while `lift.Progress > liftRaisedThreshold` (`IntakePull.cs:332`).
  2. Handoff GroundIntake → ScoringIntake.
     - 0.35 s cooldown; wait on held counts.
  3. Align the ScoringIntake hold point over the stake axis.
     - Within 0.6u (`captureRadius`), slow, near-zero turn.
  4. THEN raise the lift.
     - A raised lift rolls in turns — never turn hard with it up.
  5. `ScoreDrop`.
     - No-op unless the lift is raised and something is held.
     - Confirm `SeatedCount` went up.
- To find out: the exact human button sequence for scoring with v3, and what ArmRotate does.
- Still to design: matchload, Toggle, goal and block controllers.

## Layer 4 — Tuning the weights (gradient descent, OFFLINE, via SPSA)
- Weights: ~10–20 numbers, each a task value × a game-state factor.
  - Factors: time left, score difference, pieces held, pieces left.
  - Needs the 🔨 score counter + 🔨 match clock.
- SPSA = gradient descent with an estimated slope:
  - Nudge all weights at once.
  - Run two batches of matches (+nudge and −nudge).
  - Step toward the better result.
  - 2 batches per step regardless of weight count; tolerates noisy scores.
- Objective: average score margin over 🔨 headless self-play matches.
- Knobs are INPUTS, not tuned.
  - Each training match draws random knob values, so the weights work at every difficulty.
  - Otherwise SPSA just learns "turn communication up".
- Ship fixed weights. Nothing is learned during a match.
- Hand-tune first; SPSA is the last layer.
- Unknown until measured: how much faster than real time a headless match runs.

## Performance
- The iPhone 13 already misses 60 fps with ONE robot.
  - LiteScene 22 ms; Serious heat → 30 fps. See `Docs/Device-Performance.md`.
- Gate 1 (slice 1): measure 2 robots.
- Gate 2 (before assignment): measure 4 v3s.
- If it doesn't fit, make the AI robots lighter BEFORE writing more AI.
  - Options: decimated meshes (see `3b9897e`) or fewer physics parts.

## Build order
1. **Slice 1 — one AI v3 scores on its own:**
   1. Input isolation + Reverse Drive fix + spawn a second v3. Test: the player is unaffected.
   2. Drive to a fixed point with pure pursuit; then add A*.
   3. Pick up one cup.
   4. Handoff → align → raise → `ScoreDrop`.
   5. Stuck watchdog.
   6. Measure 2 robots on the iPhone 13.
2. 🔨 Score counter + match clock + alliances.
3. The remaining task controllers (matchload, Toggle, goal, block).
4. 4-robot spawning + perf Gate 2.
5. Opponent assignment with the Communication knob.
6. AI ally working around the human.
7. Other knobs + presets + settings UI.
8. 🔨 Headless self-play runner → SPSA tuning.
9. Autonomous period.

## Rules
- Drive through `SetManualInput` and the actuators — the same path a player uses.
- Verify every claim here against the code before relying on it.
- Mutation-test assertions.
- Docs are nested bullets.
