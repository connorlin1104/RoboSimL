using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Checks for the home screen's robot stage: the parts of it nobody would notice breaking until it had shipped.
//
// The caption's chips, the turntable's maths and the selection event are checked headless, and what the chips
// say about the shipped robots against those robots' own rigs. The strip is run against the robot that
// carries NonSupportingLink. The baked showcases are checked as assets —
// nothing left on them that could wake, every part on the stage layer with no shadow work, the cull
// keeping exactly the parts it should, and the robot inside the camera's view at every angle of the turn.
// The built HomeScene is checked for the settings no structural check can see, above all the two cameras'
// render order: get that wrong and the whole UI vanishes on every frame the stage draws.
//
// Usage: Tools > RoboSim > Validate > Validate Home Stage, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod HomeSceneValidation.RunBatchValidate
public static class HomeSceneValidation
{
    private const string Title = "Validate Home Stage";

    // The two screen shapes the home screen is built against: a 6.7" phone and a 13" iPad.
    private static readonly Vector2[] Screens = { new Vector2(2778f, 1284f), new Vector2(2752f, 2064f) };

    private struct StageSettings
    {
        public float fov;
        public float pitch;
        public float margin;
        public float[] viewAspects;
    }

    [MenuItem("Tools/RoboSim/Validate/Validate Home Stage", false, 55)]
    private static void RunInteractive()
    {
        // It opens HomeScene over whatever is open.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive(Title, Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch(Title, Run);

    private static string Run()
    {
        int checks = 0;
        checks += Caption();
        checks += RigHighlights(out string chips);
        checks += TurntableMaths();
        checks += SelectionEvent();
        checks += StripLeavesNothingThatWakes();
        checks += BuiltScene(out StageSettings stage);
        checks += BakedShowcases(stage, out string counts);
        return $"{Title}: PASSED ({checks} checks). Showcase renderers: {counts}. Chips: {chips}.";
    }

    // --- The caption ---

    private static int Caption()
    {
        int checks = 0;

        // Configure Controller still names every mechanism, through this.
        void Pretty(string raw, string expected)
        {
            string actual = MechanismNames.Pretty(raw);
            ValidationUtil.Assert(actual == expected, $"MechanismNames.Pretty(\"{raw}\") gave \"{actual}\", expected \"{expected}\".");
            checks++;
        }
        Pretty("CascadeLift", "Cascade Lift");
        Pretty("LeftSideToggle", "Left Side Toggle");
        Pretty("PlasticClaw Flip", "Plastic Claw Flip"); // already half spaced: no double space
        Pretty("DR4B Lift", "DR4B Lift");                // never split after a digit
        Pretty("DRLift", "DR Lift");                     // a word after a run of capitals
        Pretty("Doinker", "Doinker");
        Pretty("   ", string.Empty);

        // The chips: what each kind of robot shows, in the order the stage shows it.
        void Chips(RobotModelCatalog.Highlights highlights, string expected, string what)
        {
            string actual = Describe(highlights.Chips());
            ValidationUtil.Assert(actual == expected, $"{what}: the chips read \"{actual}\", expected \"{expected}\".");
            checks++;
        }
        Chips(new RobotModelCatalog.Highlights { driveWatts = 66f },
              "66 W Drive", "a drivetrain and nothing else");
        Chips(new RobotModelCatalog.Highlights { driveWatts = 44f, liftWatts = 22f, rigLift = RobotModelCatalog.LiftKind.DR4B, rigFloatingIntake = true },
              "44 W Drive | 22 W DR4B | Floating Intake", "a DR4B robot with a floor intake");
        Chips(new RobotModelCatalog.Highlights { driveWatts = 44f, liftWatts = 22f, rigLift = RobotModelCatalog.LiftKind.Cascade, rigClaw = true },
              "44 W Drive | 22 W Cascade | Claw", "a cascade robot with a claw");
        Chips(new RobotModelCatalog.Highlights(),
              string.Empty, "a robot with nothing typed and nothing rigged, whose row hides");
        Chips(new RobotModelCatalog.Highlights { rigLift = RobotModelCatalog.LiftKind.Cascade },
              "Cascade", "a lift with no watts typed, named without a number");
        Chips(new RobotModelCatalog.Highlights { liftWatts = 22f },
              "22 W Lift", "watts typed for a lift no builder made");
        Chips(new RobotModelCatalog.Highlights { driveWatts = 5.5f, liftWatts = 16.5f, rigLift = RobotModelCatalog.LiftKind.DR4B },
              "5.5 W Drive | 16.5 W DR4B", "half-motor watts, which keep their decimal");
        // Each setting against what the rig says: Never hides a label the rig shows, Always shows one it doesn't.
        Chips(new RobotModelCatalog.Highlights { rigClaw = true, clawLabel = RobotModelCatalog.LabelSetting.Never },
              string.Empty, "a claw the rig found, set to Never");
        Chips(new RobotModelCatalog.Highlights { floatingIntakeLabel = RobotModelCatalog.LabelSetting.Always },
              "Floating Intake", "an intake the rig didn't find, set to Always");
        Chips(new RobotModelCatalog.Highlights { clampLabel = RobotModelCatalog.LabelSetting.Always },
              "Clamp", "a clamp, which only its setting can show until something rigs one");
        Chips(Widest(), "82.5 W Drive | 82.5 W Cascade | Floating Intake | Claw | Clamp", "every chip at once");

        // The colour goes on the watts and nothing else. Describe takes the tags out, so this is the one check that
        // sees them.
        var accent = new Color32(0x0E, 0xA5, 0xE9, 0xFF);
        string tinted = RobotStageView.ChipText(new RobotModelCatalog.Highlights.Chip { label = "Drive", watts = 44f }, accent);
        ValidationUtil.Assert(tinted == "<color=#0EA5E9>44 W</color> Drive",
            $"the stage writes a 44 W drivetrain chip as \"{tinted}\"; the watts should lead, in the accent colour, and the label follow.");
        checks++;

        int most = Widest().Chips().Count;
        ValidationUtil.Assert(most <= BuildHomeScene.StageChipSlots,
            $"a robot can show {most} chips, but the stage has {BuildHomeScene.StageChipSlots} slots — the rest would be dropped.");
        return checks + 1;
    }

    // Every chip at once, each carrying the widest watts a chip can: two digits and a half. No robot has all of
    // it — the two numbers alone come to more than the 88 W of motors 654V v3 carries. It is the row the stage
    // has to have room for.
    private static RobotModelCatalog.Highlights Widest() => new RobotModelCatalog.Highlights
    {
        driveWatts = 82.5f,
        liftWatts = 82.5f,
        rigLift = RobotModelCatalog.LiftKind.Cascade,
        rigFloatingIntake = true,
        rigClaw = true,
        clampLabel = RobotModelCatalog.LabelSetting.Always,
    };

    // The chips as a player reads them: what the stage writes into each label, with the colour tags taken out.
    private static string Describe(List<RobotModelCatalog.Highlights.Chip> chips)
    {
        var parts = new List<string>(chips.Count);
        foreach (RobotModelCatalog.Highlights.Chip chip in chips)
            parts.Add(Regex.Replace(RobotStageView.ChipText(chip, Color.white), "<[^>]*>", string.Empty));
        return string.Join(" | ", parts);
    }

    // --- What the shipped robots' rigs say ---

    private struct Expected
    {
        public RobotModelCatalog.LiftKind lift;
        public bool claw;
        public int intakes;
        public int floorIntakes;
        public int elevenWatt;
        public int fiveWatt;
    }

    // Measured separately — straight off the prefab files, not through RobotHighlightDetection — so a regression
    // in the detection shows up here as a robot with the wrong label or the wrong motor count. A robot re-rigged
    // on purpose needs its line updated.
    private static readonly Dictionary<string, Expected> ShippedRobots = new Dictionary<string, Expected>
    {
        // A drivetrain alone: six 11 W motors.
        ["360rpm-drivetrain"] = new Expected { lift = RobotModelCatalog.LiftKind.None, elevenWatt = 6 },
        // Its one intake rides the DR4B down to 0.09 below the wheels' centres.
        ["654v-v1"] = new Expected { lift = RobotModelCatalog.LiftKind.DR4B, intakes = 1, floorIntakes = 1, elevenWatt = 6, fiveWatt = 2 },
        // A claw, and no intake at all.
        ["654v-v2"] = new Expected { lift = RobotModelCatalog.LiftKind.Cascade, claw = true, elevenWatt = 6, fiveWatt = 4 },
        // The robot the floor rule exists for: one intake level with its wheels, and a scoring intake 1.17 units
        // above them that must NOT count.
        ["ryan-cascaderobot"] = new Expected { lift = RobotModelCatalog.LiftKind.Cascade, intakes = 2, floorIntakes = 1, elevenWatt = 7, fiveWatt = 2 },
    };

    private static int RigHighlights(out string summary)
    {
        RobotModelCatalog catalog = RoboSimPaths.LoadRobotCatalog();
        ValidationUtil.Assert(catalog != null, $"no RobotModelCatalog at {RoboSimPaths.RobotModelCatalog}.");

        int checks = 0, shipped = 0;
        var lines = new List<string>();
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || entry.prefab == null) continue;
            string who = $"'{entry.displayName}'";
            RobotHighlightDetection.Rig rig = RobotHighlightDetection.Detect(entry.prefab);
            RobotHighlightDetection.Motors motors = RobotHighlightDetection.CountMotors(entry.prefab);

            if (ShippedRobots.TryGetValue(entry.id, out Expected expected))
            {
                ValidationUtil.Assert(rig.lift == expected.lift, $"{who}'s lift reads as {rig.lift}; it has a {expected.lift}.");
                ValidationUtil.Assert(rig.claw == expected.claw,
                    expected.claw ? $"{who}'s claw wasn't found." : $"{who} reads as having a claw, and it has none.");
                ValidationUtil.Assert(rig.intakes == expected.intakes && rig.floorIntakes == expected.floorIntakes,
                    $"{rig.floorIntakes} of {who}'s {rig.intakes} intake(s) read as at the floor; it has {expected.floorIntakes} of {expected.intakes}.");
                ValidationUtil.Assert(motors.elevenWatt == expected.elevenWatt && motors.fiveWatt == expected.fiveWatt,
                    $"{who}'s CAD counts as {motors.elevenWatt} x 11 W and {motors.fiveWatt} x 5.5 W; it has " +
                    $"{expected.elevenWatt} and {expected.fiveWatt}.");
                shipped++;
                checks += 4;
            }

            // What the catalog carries is what the rig says NOW — or the stage goes on showing a robot's old labels.
            RobotModelCatalog.Highlights highlights = entry.highlights;
            ValidationUtil.Assert(highlights != null && highlights.rigLift == rig.lift &&
                                  highlights.rigFloatingIntake == rig.floatingIntake && highlights.rigClaw == rig.claw,
                $"{who}'s chips are out of date with its robot. Run Build Home Screen.");
            string problem = RobotHighlightDetection.WattsProblem(highlights, motors);
            ValidationUtil.Assert(problem == null, $"{who}: {problem} (Tools > RoboSim > Robot > Model Catalog.)");
            checks += 2;
            lines.Add($"{entry.displayName} [{Describe(highlights.Chips())}]");
        }
        // Found by id, so a renamed id would otherwise skip every check that matters without a word.
        ValidationUtil.Assert(shipped == ShippedRobots.Count,
            $"only {shipped} of the {ShippedRobots.Count} shipped robots were found in the catalog by id.");
        summary = string.Join(", ", lines);
        return checks + 1;
    }

    // --- The turntable ---

    private static int TurntableMaths()
    {
        const float Drift = 12f, Rate = 3f, Fling = 540f;
        int checks = 0;

        // One second of a hard flick, stepped at 30 fps, at 60, and in one go. Integrated exactly, all three
        // land on the same angle and speed. A plain Euler step lands degrees apart — which is how a
        // regression to `yaw += velocity * dt` shows up here.
        float y30 = 0f, v30 = Fling;
        for (int i = 0; i < 30; i++) RobotStage.Step(ref y30, ref v30, Drift, Rate, 1f / 30f);
        float y60 = 0f, v60 = Fling;
        for (int i = 0; i < 60; i++) RobotStage.Step(ref y60, ref v60, Drift, Rate, 1f / 60f);
        float y1 = 0f, v1 = Fling;
        RobotStage.Step(ref y1, ref v1, Drift, Rate, 1f);
        ValidationUtil.Near(y30, y60, 0.01f, "a flick must come to the same angle at 30 fps as at 60");
        ValidationUtil.Near(y60, y1, 0.01f, "sixty small steps must land where one big step does");
        ValidationUtil.Near(v30, v60, 0.001f, "a flick must slow the same way at 30 fps as at 60");
        checks += 3;

        // It settles back to the drift in about two seconds: within 10% by 2.1 s...
        float y = 0f, v = Fling;
        RobotStage.Step(ref y, ref v, Drift, Rate, 2.1f);
        ValidationUtil.Assert(Mathf.Abs(v - Drift) <= 0.1f * Drift, $"a hard flick should be back to the drift by 2.1 s; it is turning at {v:F1} deg/s.");
        // ...but not at once: a second in, the flick must still be plainly visible.
        y = 0f; v = Fling;
        RobotStage.Step(ref y, ref v, Drift, Rate, 1f);
        ValidationUtil.Assert(v > 2f * Drift, $"a flick should still be spinning well above the drift after 1 s; it is at {v:F1} deg/s.");
        // A flick BACKWARDS runs down through zero and comes back to the drift going forwards.
        y = 0f; v = -Fling;
        RobotStage.Step(ref y, ref v, Drift, Rate, 3f);
        ValidationUtil.Assert(v > 0.5f * Drift, $"a backwards flick should come round to the forward drift by 3 s; it is at {v:F1} deg/s.");
        checks += 3;

        // The framing distance fits a cylinder in view — and is TIGHT: a regression to the loose closed-form
        // bound would still fit, and would stand every robot about a third smaller on the stage.
        foreach (Vector3 c in new[] { new Vector3(5f, 3f, 1.8f), new Vector3(5f, 9f, 1.8f), new Vector3(5f, 3f, 0.98f), new Vector3(2f, 9f, 0.98f) })
        {
            float radius = c.x, height = c.y, aspect = c.z;
            float distance = RobotStage.FrameDistance(radius, height, 26f, aspect, 18f);
            List<Vector3> rims = CylinderRims(radius, height, 720);
            ValidationUtil.Assert(InView(rims, Vector3.zero, 0f, distance * 1.005f, 26f, aspect, 18f),
                $"a {radius}x{height} cylinder should fit a {aspect} view at the distance FrameDistance gives.");
            ValidationUtil.Assert(!InView(rims, Vector3.zero, 0f, distance * 0.97f, 26f, aspect, 18f),
                $"FrameDistance stands a {radius}x{height} cylinder further back than it needs to be.");
            checks += 2;
        }
        return checks;
    }

    private static List<Vector3> CylinderRims(float radius, float height, int samples)
    {
        var points = new List<Vector3>(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            float a = i * (2f * Mathf.PI / samples);
            points.Add(new Vector3(radius * Mathf.Cos(a), 0.5f * height, radius * Mathf.Sin(a)));
            points.Add(new Vector3(radius * Mathf.Cos(a), -0.5f * height, radius * Mathf.Sin(a)));
        }
        return points;
    }

    // Unity's own camera maths rather than RobotStage's: the stage camera on a pivot at `pivot` turned by
    // `yaw`, `distance` back along a view pitched down by `pitch`. Every point must land inside the frustum.
    // worldToCameraMatrix is the inverse of the camera's pose with Z flipped, because camera space looks down -Z.
    private static bool InView(List<Vector3> points, Vector3 pivot, float yaw, float distance, float fov, float aspect, float pitch)
    {
        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        Quaternion look = Quaternion.Euler(pitch, 0f, 0f);
        Vector3 position = pivot + turn * (look * new Vector3(0f, 0f, -distance));
        Matrix4x4 worldToCamera = Matrix4x4.TRS(position, turn * look, new Vector3(1f, 1f, -1f)).inverse;
        Matrix4x4 toClip = Matrix4x4.Perspective(fov, aspect, 0.001f, 100000f) * worldToCamera;
        foreach (Vector3 p in points)
        {
            Vector4 clip = toClip * new Vector4(p.x, p.y, p.z, 1f);
            if (clip.w <= 0f) return false;
            if (Mathf.Abs(clip.x) > clip.w * 1.0005f || Mathf.Abs(clip.y) > clip.w * 1.0005f) return false;
        }
        return true;
    }

    // --- The selection event ---

    // Its own catalog and ids, and the two real PlayerPrefs keys it has to use (they ARE the storage) put
    // back afterwards, so a run never disturbs this machine's selection or held codes.
    private static int SelectionEvent()
    {
        string savedSelection = PlayerPrefs.GetString(RobotModelCatalog.SelectedModelPrefKey, string.Empty);
        string savedCodes = PlayerPrefs.GetString(RobotOwnerSettings.CodesPrefKey, string.Empty);
        RobotModelCatalog catalog = ScriptableObject.CreateInstance<RobotModelCatalog>();
        try
        {
            PlayerPrefs.DeleteKey(RobotOwnerSettings.CodesPrefKey);
            catalog.models.Add(new RobotModelCatalog.Entry { id = "stage-a", displayName = "A" });
            catalog.models.Add(new RobotModelCatalog.Entry { id = "stage-b", displayName = "B" });
            catalog.models.Add(new RobotModelCatalog.Entry
            {
                id = "stage-p", displayName = "P",
                visibility = RobotModelCatalog.Visibility.Private, ownerCode = "STAGETEST-1",
            });
            var heard = new List<string>();
            catalog.SelectionChanged += entry => heard.Add(entry != null ? entry.id : "(none)");

            catalog.SelectedModelId = "stage-b";
            Heard(heard, "stage-b", "choosing a robot must announce it");
            catalog.SelectedModelId = "stage-b";
            Heard(heard, null, "choosing the SAME robot again must announce nothing — the stage would rebuild it for no reason");

            RobotOwnerSettings.AddCode("STAGETEST-1");
            catalog.NotifySelectionMayHaveChanged();
            Heard(heard, null, "unlocking a robot that isn't the selected one changes nothing, so must announce nothing");
            catalog.SelectedModelId = "stage-p";
            Heard(heard, "stage-p", "choosing the private robot once it is unlocked must announce it");

            // The case a listener on the setter alone misses: forgetting the code moves the selection off the
            // private robot with NO write to the setter — the getter falls back to the first visible robot.
            RobotOwnerSettings.RemoveCode("STAGETEST-1");
            catalog.NotifySelectionMayHaveChanged();
            Heard(heard, "stage-a", "forgetting the code moves the selection to the first visible robot, and that must be " +
                                    "announced — or the stage goes on showing a robot Drive no longer loads");
        }
        finally
        {
            Object.DestroyImmediate(catalog);
            if (string.IsNullOrEmpty(savedSelection)) PlayerPrefs.DeleteKey(RobotModelCatalog.SelectedModelPrefKey);
            else PlayerPrefs.SetString(RobotModelCatalog.SelectedModelPrefKey, savedSelection);
            if (string.IsNullOrEmpty(savedCodes)) PlayerPrefs.DeleteKey(RobotOwnerSettings.CodesPrefKey);
            else PlayerPrefs.SetString(RobotOwnerSettings.CodesPrefKey, savedCodes);
            PlayerPrefs.Save();
        }
        return 5;
    }

    private static void Heard(List<string> heard, string expected, string why)
    {
        if (expected == null)
            ValidationUtil.Assert(heard.Count == 0, $"{why} (heard: {string.Join(", ", heard)}).");
        else
            ValidationUtil.Assert(heard.Count == 1 && heard[0] == expected, $"{why} (heard: {(heard.Count == 0 ? "nothing" : string.Join(", ", heard))}).");
        heard.Clear();
    }

    // --- The strip ---

    // Run against the robot that carries NonSupportingLink — the component whose OnEnable would install a
    // process-wide physics callback from the menu.
    //
    // The property asserted is that nothing which could wake survives. NonSupportingLinkModel's
    // RegisteredColliderCount would look like the natural thing to check and is not: in edit mode OnEnable
    // never runs, so it reads zero whether or not the strip worked, and a check that passes either way
    // tests nothing.
    private static int StripLeavesNothingThatWakes()
    {
        GameObject subject = null;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponentInChildren<NonSupportingLink>(true) == null) continue;
            subject = prefab;
            break;
        }
        ValidationUtil.Assert(subject != null, "no robot prefab carries a NonSupportingLink, so there is nothing to prove the " +
                                               "strip against (it was 654V_v3).");

        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var holder = new GameObject("StripProbe");
            SceneManager.MoveGameObjectToScene(holder, preview);
            holder.SetActive(false);
            GameObject copy = Object.Instantiate(subject, holder.transform);

            // Before: prove the copy has what the strip is meant to remove, or "none left" proves nothing.
            int renderers = copy.GetComponentsInChildren<MeshRenderer>(true).Length;
            ValidationUtil.Assert(copy.GetComponentsInChildren<NonSupportingLink>(true).Length > 0 &&
                                  copy.GetComponentsInChildren<Collider>(true).Length > 0 &&
                                  copy.GetComponentsInChildren<ArticulationBody>(true).Length > 0,
                $"'{subject.name}' should arrive with scripts, colliders and articulations for the strip to remove.");

            RobotShowcase.StripToRenderers(copy);

            ValidationUtil.Assert(copy.GetComponentsInChildren<MonoBehaviour>(true).Length == 0, $"scripts survived the strip on '{subject.name}'.");
            ValidationUtil.Assert(copy.GetComponentsInChildren<Collider>(true).Length == 0, $"colliders survived the strip on '{subject.name}'.");
            ValidationUtil.Assert(copy.GetComponentsInChildren<ArticulationBody>(true).Length == 0 &&
                                  copy.GetComponentsInChildren<Rigidbody>(true).Length == 0 &&
                                  copy.GetComponentsInChildren<Joint>(true).Length == 0, $"bodies or joints survived the strip on '{subject.name}'.");
            ValidationUtil.Assert(!RobotShowcase.NeedsStrip(copy), $"something outside the allowlist survived the strip on '{subject.name}'.");
            ValidationUtil.Assert(copy.GetComponentsInChildren<MeshRenderer>(true).Length == renderers,
                $"the strip removed renderers from '{subject.name}' — it must only ever remove what isn't one.");
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
        return 6;
    }

    // --- The built scene ---

    private static int BuiltScene(out StageSettings settings)
    {
        int checks = 0;

        // The gate Build Home Screen uses to skip a rebuild. Every check in it must MATCH what the builder
        // writes: one that never matches turns into a silent rebuild on every run, and one that matches too
        // easily lets a stale scene ship. So it has to call the scene the builder wrote valid.
        ValidationUtil.Assert(BuildHomeScene.HomeSceneIsValid(),
            "HomeSceneIsValid calls the built HomeScene stale — either it needs Rebuild Home Screen (Force), or one " +
            "of its checks doesn't match what the builder writes.");
        checks++;

        Scene scene = EditorSceneManager.OpenScene(RoboSimPaths.HomeScene, OpenSceneMode.Single);
        ValidationUtil.Assert(BuildHomeScene.FindDescendantRect(scene, BuildHomeScene.HomeSceneStamp) != null,
            $"HomeScene carries no {BuildHomeScene.HomeSceneStamp} — the builder changed and the scene wasn't rebuilt.");

        Camera main = null;
        foreach (Camera camera in Components<Camera>(scene))
            if (camera.CompareTag("MainCamera")) main = camera;
        Transform stageCameraTransform = BuildHomeScene.FindDescendantTransform(scene, "StageCamera");
        Camera stageCamera = stageCameraTransform != null ? stageCameraTransform.GetComponent<Camera>() : null;
        ValidationUtil.Assert(main != null && stageCamera != null, "HomeScene needs its Main Camera and the StageCamera.");

        // THE one. URP draws this overlay canvas on the last base camera that renders to the screen; a camera
        // with a target texture doesn't, so if the stage camera sorted last NEITHER would draw the UI. Sorted
        // on (int)depth, so a fractional gap is no gap.
        ValidationUtil.Assert((int)stageCamera.depth < (int)main.depth,
            $"the stage camera (depth {stageCamera.depth}) must render before the Main Camera (depth {main.depth}) by a whole " +
            "number, or the home screen's UI disappears on every frame the stage draws.");
        ValidationUtil.Assert((main.cullingMask & (1 << RobotShowcase.LayerIndex)) == 0,
            "the Main Camera draws the showcase layer — the robot would be drawn twice, every frame, behind the backdrop.");
        ValidationUtil.Assert(stageCamera.cullingMask == 1 << RobotShowcase.LayerIndex, "the stage camera must draw the showcase layer and nothing else.");
        ValidationUtil.Assert(stageCamera.clearFlags == CameraClearFlags.SolidColor && stageCamera.backgroundColor.a == 0f,
            "the stage camera must clear to a transparent colour, or the robot sits in an opaque box.");
        ValidationUtil.Assert(stageCamera.targetTexture == null,
            "the stage camera has a texture saved in the scene — it must be made at runtime, sized to the device.");
        ValidationUtil.Assert(!stageCamera.enabled, "the stage camera must be saved switched off; the view turns it on for the frames it draws.");
        UniversalAdditionalCameraData stageData = stageCamera.GetComponent<UniversalAdditionalCameraData>();
        ValidationUtil.Assert(stageData != null && !stageData.renderPostProcessing && !stageData.renderShadows,
            "the stage camera must render without post-processing and without shadows.");
        checks += 8;

        Transform pivot = BuildHomeScene.FindDescendantTransform(scene, "StagePivot");
        List<Light> lights = Components<Light>(scene);
        ValidationUtil.Assert(lights.Count == 3, $"HomeScene should have the stage's three lights and no others; it has {lights.Count}.");
        foreach (Light light in lights)
        {
            ValidationUtil.Assert(pivot != null && light.transform.parent == pivot,
                $"'{light.name}' must ride the stage pivot, or the robot turns under a fixed light and one side goes dark.");
            ValidationUtil.Assert(light.shadows == LightShadows.None && light.type == LightType.Directional,
                $"'{light.name}' must be a shadowless directional light — a shadowed one puts the shadow pass back.");
        }
        ValidationUtil.Assert(RenderSettings.ambientMode == AmbientMode.Trilight && RenderSettings.skybox == null,
            "HomeScene must light its ambient from the palette with no sky — the default is a daylight blue.");
        ValidationUtil.Assert(Components<AudioListener>(scene).Count == 1, "HomeScene must have exactly one AudioListener.");
        ValidationUtil.Assert(LayerMask.LayerToName(RobotShowcase.LayerIndex) == RobotShowcase.LayerName,
            $"layer {RobotShowcase.LayerIndex} must be named '{RobotShowcase.LayerName}'.");
        checks += 4 + lights.Count * 2;

        RobotStageView view = FirstComponent<RobotStageView>(scene);
        RobotStage rig = FirstComponent<RobotStage>(scene);
        ValidationUtil.Assert(view != null && rig != null, "HomeScene needs the RobotStageView and the RobotStage.");
        RawImage image = view.GetComponent<RawImage>();
        ValidationUtil.Assert(image.raycastTarget, "the robot's window must take raycasts — it is the surface a finger spins it on.");
        ValidationUtil.Assert(!image.enabled, "the robot's window must be saved switched off: with no texture yet it would draw a white box.");
        checks += 2;

        // The chips: every slot wired and saved switched off (the view turns on as many as the robot has), and
        // room for the widest row a robot can have — measured with the chip labels' own font, so a bigger size
        // or a longer label fails here, on both screen shapes below, rather than on an iPad.
        SerializedObject viewSo = new SerializedObject(view);
        var chipRow = viewSo.FindProperty("chipRow").objectReferenceValue as GameObject;
        SerializedProperty slots = viewSo.FindProperty("chipLabels");
        ValidationUtil.Assert(chipRow != null && slots != null && slots.arraySize == BuildHomeScene.StageChipSlots,
            $"the stage view needs its chip row and {BuildHomeScene.StageChipSlots} chip labels.");
        TMP_Text chipLabel = null;
        for (int i = 0; i < slots.arraySize; i++)
        {
            var label = slots.GetArrayElementAtIndex(i).objectReferenceValue as TMP_Text;
            ValidationUtil.Assert(label != null && label.transform.parent != null && label.transform.parent.parent == chipRow.transform,
                $"chip slot {i + 1} is not a label inside a chip in the chip row.");
            ValidationUtil.Assert(!label.transform.parent.gameObject.activeSelf, $"chip {i + 1} must be saved switched off.");
            if (chipLabel == null) chipLabel = label;
        }
        float widestRow = WidestRowWidth(chipLabel, viewSo.FindProperty("wattsColor").colorValue,
            chipRow.GetComponent<HorizontalLayoutGroup>(), chipLabel.transform.parent.GetComponent<HorizontalLayoutGroup>());
        checks += 1 + slots.arraySize * 2;

        // Layout, worked out from the saved anchors at both screen shapes: the window sits inside the stage,
        // clear of the title above it and the caption below it.
        RectTransform homeStage = BuildHomeScene.FindDescendantRect(scene, "HomeStage");
        RectTransform stageRegion = BuildHomeScene.FindDescendantRect(scene, "StageRegion");
        RectTransform caption = BuildHomeScene.FindDescendantRect(scene, "StageCaption");
        RectTransform title = BuildHomeScene.FindDescendantRect(scene, "Title");
        var viewRect = (RectTransform)view.transform;
        ValidationUtil.Assert(homeStage != null && stageRegion != null && caption != null && title != null,
            "HomeScene is missing HomeStage, StageRegion, StageCaption or Title.");

        settings = new StageSettings
        {
            fov = stageCamera.fieldOfView,
            pitch = rig.pitch,
            margin = rig.framingMargin,
            viewAspects = new float[Screens.Length],
        };
        for (int i = 0; i < Screens.Length; i++)
        {
            float scale = Mathf.Sqrt(Screens[i].x / 1920f * (Screens[i].y / 1080f));
            var canvas = new Rect(0f, 0f, Screens[i].x / scale, Screens[i].y / scale);
            Rect home = Resolve(homeStage, canvas);
            Rect stage = Resolve(stageRegion, home);
            Rect window = Resolve(viewRect, stage);
            Rect band = Resolve(caption, stage);
            Rect titleBand = Resolve(title, home);
            string where = $"on a {Screens[i].x}x{Screens[i].y} screen";
            ValidationUtil.Assert(window.width > 100f && window.height > 100f, $"the robot's window has no room {where}.");
            ValidationUtil.Assert(Inside(window, stage) && Inside(band, stage), $"the robot's window or caption leaves the stage {where}.");
            ValidationUtil.Assert(window.yMin >= band.yMax - 0.5f, $"the robot's window overlaps its caption {where}.");
            ValidationUtil.Assert(window.yMax <= titleBand.yMin + 0.5f, $"the robot's window runs up under the title {where}.");
            ValidationUtil.Assert(widestRow <= band.width,
                $"the widest row of chips ({widestRow:F0} units) is wider than the caption ({band.width:F0}) {where}.");
            settings.viewAspects[i] = window.width / window.height;
            checks += 5;
        }
        return checks;
    }

    // --- The baked showcases ---

    private static int BakedShowcases(StageSettings stage, out string counts)
    {
        RobotModelCatalog catalog = RoboSimPaths.LoadRobotCatalog();
        ValidationUtil.Assert(catalog != null, $"no RobotModelCatalog at {RoboSimPaths.RobotModelCatalog}.");

        int checks = 0;
        var summary = new List<string>();
        var corners = new List<Vector3>();
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || entry.prefab == null) continue;
            string who = $"'{entry.displayName}'";

            GameObject showcase = entry.showcasePrefab;
            ValidationUtil.Assert(showcase != null, $"{who} has no showcase — the stage would show only its name. Run Build Home Screen.");
            ValidationUtil.Assert(AssetDatabase.GetAssetPath(showcase).StartsWith(RoboSimPaths.ShowcaseFolder + "/", StringComparison.Ordinal),
                $"{who}'s showcase must live in {RoboSimPaths.ShowcaseFolder} — anywhere under Assets/Robots and Delete Robot lists it as an orphan.");
            ValidationUtil.Assert(entry.showcaseSource == BuildShowcasePrefabs.SourceStamp(entry.prefab),
                $"{who}'s showcase is out of date with its robot. Run Build Home Screen.");

            // Nothing on it that could wake, and nothing physical.
            ValidationUtil.Assert(showcase.GetComponentsInChildren<MonoBehaviour>(true).Length == 0, $"{who}'s showcase carries scripts.");
            ValidationUtil.Assert(showcase.GetComponentsInChildren<Collider>(true).Length == 0 &&
                                  showcase.GetComponentsInChildren<ArticulationBody>(true).Length == 0 &&
                                  showcase.GetComponentsInChildren<Rigidbody>(true).Length == 0 &&
                                  showcase.GetComponentsInChildren<Joint>(true).Length == 0, $"{who}'s showcase carries physics.");
            ValidationUtil.Assert(!RobotShowcase.NeedsStrip(showcase), $"{who}'s showcase carries something other than renderers.");

            // The cull kept exactly the parts it should: the count a correct bake of the robot would keep.
            MeshRenderer[] renderers = showcase.GetComponentsInChildren<MeshRenderer>(true);
            int expected = BuildShowcasePrefabs.ExpectedRenderers(entry.prefab);
            ValidationUtil.Assert(renderers.Length == expected,
                $"{who}'s showcase has {renderers.Length} renderers, but its robot has {expected} that aren't fasteners or hidden.");
            ValidationUtil.Assert(renderers.Length <= BuildShowcasePrefabs.RendererLimit,
                $"{who}'s showcase has {renderers.Length} renderers, over the limit of {BuildShowcasePrefabs.RendererLimit}.");

            foreach (MeshRenderer renderer in renderers)
            {
                string part = $"{who}, part '{renderer.name}'";
                ValidationUtil.Assert(renderer.shadowCastingMode == ShadowCastingMode.Off && !renderer.receiveShadows,
                    $"{part} still does shadow work.");
                ValidationUtil.Assert(renderer.lightProbeUsage == LightProbeUsage.Off && renderer.reflectionProbeUsage == ReflectionProbeUsage.Off,
                    $"{part} still samples probes the home scene doesn't have.");
                ValidationUtil.Assert(renderer.gameObject.layer == RobotShowcase.LayerIndex, $"{part} is not on the showcase layer, so the stage camera can't see it.");
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                ValidationUtil.Assert(filter != null && filter.sharedMesh != null, $"{part} has no mesh.");
                foreach (Material material in renderer.sharedMaterials)
                {
                    ValidationUtil.Assert(material != null && material.shader != null && material.shader.name != "Hidden/InternalErrorShader",
                        $"{part} has a missing material or shader — it would draw magenta on the first screen of the app.");
                }
            }

            // Placement: at the origin (every robot prefab root is 12-16 units off it), in its own orientation.
            ValidationUtil.Assert(showcase.transform.localPosition == Vector3.zero, $"{who}'s showcase root is not at the origin.");
            ValidationUtil.Assert(Quaternion.Angle(showcase.transform.localRotation, entry.prefab.transform.localRotation) < 0.01f,
                $"{who}'s showcase is not in its robot's own orientation.");

            // In view at every angle of the turn, on both screen shapes — every corner of every part.
            RobotShowcase.Frame frame = RobotShowcase.ComputeFrame(showcase, null);
            ValidationUtil.Assert(frame.IsValid, $"{who}'s showcase measures as having no size.");
            corners.Clear();
            RobotShowcase.CollectCorners(showcase, null, corners);
            foreach (float aspect in stage.viewAspects)
            {
                float distance = RobotStage.FrameDistance(frame.radius, frame.height, stage.fov, aspect, stage.pitch) * stage.margin;
                for (int yaw = 0; yaw < 360; yaw += 15)
                {
                    ValidationUtil.Assert(InView(corners, frame.center, yaw, distance, stage.fov, aspect, stage.pitch, 1f),
                        $"{who} leaves the stage's view at {yaw} degrees of the turn on a {aspect:F2}-wide view.");
                }
            }

            summary.Add($"{entry.displayName} {renderers.Length}");
            checks += 11 + renderers.Length;
        }
        counts = string.Join(", ", summary);
        return checks;
    }

    // The same test as above, for real geometry: `slack` is exact here, because the corners are what the
    // cylinder was measured from.
    private static bool InView(List<Vector3> points, Vector3 pivot, float yaw, float distance, float fov, float aspect, float pitch, float slack) =>
        InView(points, pivot, yaw, distance * slack, fov, aspect, pitch);

    // How wide the widest row of chips lays out: each chip is its label plus its padding, with the row's spacing
    // between. The labels are measured by a copy of a real one — same font, size and style — in a scene of its
    // own: a label saved switched off has never been set up to measure anything, and switching one on would
    // leave HomeScene marked as changed.
    private static float WidestRowWidth(TMP_Text model, Color wattsColor, HorizontalLayoutGroup row, HorizontalLayoutGroup chip)
    {
        ValidationUtil.Assert(row != null && chip != null, "the chip row and each chip must lay themselves out with a HorizontalLayoutGroup.");
        List<RobotModelCatalog.Highlights.Chip> chips = Widest().Chips();
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var probeObject = new GameObject("ChipProbe", typeof(RectTransform));
            SceneManager.MoveGameObjectToScene(probeObject, preview);
            TextMeshProUGUI probe = probeObject.AddComponent<TextMeshProUGUI>();
            probe.font = model.font;
            probe.fontSize = model.fontSize;
            probe.fontStyle = model.fontStyle;
            probe.characterSpacing = model.characterSpacing;
            probe.textWrappingMode = TextWrappingModes.NoWrap;

            float width = row.spacing * (chips.Count - 1);
            int characters = 0;
            foreach (RobotModelCatalog.Highlights.Chip c in chips)
            {
                width += chip.padding.horizontal + probe.GetPreferredValues(RobotStageView.ChipText(c, wattsColor)).x;
                characters += c.label.Length + (c.watts > 0f ? 7 : 0);
            }
            // A measurement that came back empty would pass the fit on any screen. Text like this averages over
            // half an em a character, so one under a third of an em is a measurement that isn't working.
            ValidationUtil.Assert(width > characters * model.fontSize / 3f,
                $"the chip labels measured {width:F0} units for {characters} characters — the measurement isn't working.");
            return width;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    // --- Helpers ---

    private static Rect Resolve(RectTransform rect, Rect parent)
    {
        Vector2 min = parent.min + Vector2.Scale(rect.anchorMin, parent.size) + rect.offsetMin;
        Vector2 max = parent.min + Vector2.Scale(rect.anchorMax, parent.size) + rect.offsetMax;
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static bool Inside(Rect inner, Rect outer) =>
        inner.xMin >= outer.xMin - 0.5f && inner.xMax <= outer.xMax + 0.5f &&
        inner.yMin >= outer.yMin - 0.5f && inner.yMax <= outer.yMax + 0.5f;

    private static List<T> Components<T>(Scene scene) where T : Component
    {
        var found = new List<T>();
        foreach (GameObject root in scene.GetRootGameObjects()) found.AddRange(root.GetComponentsInChildren<T>(true));
        return found;
    }

    private static T FirstComponent<T>(Scene scene) where T : Component
    {
        List<T> found = Components<T>(scene);
        return found.Count > 0 ? found[0] : null;
    }
}
