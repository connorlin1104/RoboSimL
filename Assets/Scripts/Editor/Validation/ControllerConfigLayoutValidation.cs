using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Configure Controller has to fit every screen the app ships to: no text is drawn over or under a button that
// isn't its own, no text runs into other text, none spills out of its box, and the assignment popup sits
// between the title and the bottom row with the rest of the screen dimmed under it. Checked on the saved
// HomeScene at both App Store screenshot sizes: a 6.5" iPhone (the tight one, its canvas 979 units tall) and a
// 13" iPad. Every catalog robot is laid out with its shipped layout, plus a made-up worst case, a robot whose
// every button drives more long-named functions than a caption has lines for.
//
// Why it exists: on 2026-09-14, on a phone, the popup cut through the title with the Back row showing under
// it, the Control Style rows wrapped out of their boxes, R1's second caption line was drawn under R2, and the
// caption under Up ran into Left and Right. Each of those is one rectangle overlapping another, so this
// measures rectangles: TMP's own bounds of the text as laid out, after the layout groups, the diagram's
// fit-to-screen scale and TMP's auto-sizing have run, in canvas units.
//
// Usage: Tools > RoboSim > Validate > Validate Controller Config Layout, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod ControllerConfigLayoutValidation.RunBatchValidate
public static class ControllerConfigLayoutValidation
{
    private const string Title = "Validate Controller Config Layout";

    // The two App Store screenshot sizes, which are also the widest and the squarest screens the app runs on.
    private static readonly (string Label, int Width, int Height)[] Screens =
    {
        ("6.5\" iPhone", 2778, 1284),
        ("13\" iPad", 2752, 2064),
    };

    // Overlaps this small are anti-aliasing, not layout.
    private const float Slack = 1f;

    [MenuItem("Tools/RoboSim/Validate/Validate Controller Config Layout", false, 58)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive(Title, Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch(Title, Run);

    private static string Run()
    {
        var checks = new ValidationUtil.Checks();
        string previousScene = SceneManager.GetActiveScene().path;
        int layouts = 0;
        try
        {
            Scene scene = EditorSceneManager.OpenScene(RoboSimPaths.HomeScene, OpenSceneMode.Single);
            Canvas canvas = null;
            ControllerConfigScreen screen = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Canvas c in root.GetComponentsInChildren<Canvas>(true))
                    if (canvas == null && c.isRootCanvas) canvas = c;
                if (screen == null) screen = root.GetComponentInChildren<ControllerConfigScreen>(true);
            }
            ValidationUtil.Assert(canvas != null && screen != null,
                "HomeScene has no root canvas or no ControllerConfigScreen — rebuild it (Tools > RoboSim > Scenes > Build Home Screen)");

            // Laid out in world space, at the size the CanvasScaler gives each screen, so nothing needs a
            // screen or a render: scale = sqrt(w/refW * h/refH), its match of 0.5. Held to that match, or the
            // sizes below would describe some other canvas.
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            ValidationUtil.Assert(scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize &&
                                  Mathf.Approximately(scaler.matchWidthOrHeight, 0.5f),
                "the home canvas no longer scales with the screen at a width/height match of 0.5, so this " +
                "validator's canvas sizes are wrong — update Settle's formula to the scaler's new rule");
            Vector2 reference = scaler.referenceResolution;
            scaler.enabled = false;
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = (RectTransform)canvas.transform;

            var so = new SerializedObject(screen);
            var robots = new List<RobotModelCatalog.Entry>();
            foreach (RobotModelCatalog.Entry entry in RoboSimPaths.LoadRobotCatalog().models)
                if (entry != null && !string.IsNullOrEmpty(entry.id)) robots.Add(entry);
            robots.Add(WorstCase());

            foreach ((string label, int width, int height) in Screens)
            {
                float scale = Mathf.Sqrt(width / reference.x * (height / reference.y));
                canvasRect.localScale = Vector3.one;
                canvasRect.sizeDelta = new Vector2(width / scale, height / scale);
                foreach (RobotModelCatalog.Entry robot in robots)
                {
                    string where = $"{robot.displayName}, {label}";
                    ButtonMap map = ControllerMapSettings.Clone(robot.defaultButtonMap);
                    bool hasMechanisms = robot.mechanisms != null && robot.mechanisms.Count > 0;

                    screen.ShowForLayoutCheck(robot, map);
                    Settle(canvas);
                    CheckDiagram(checks, so, robot, map, canvasRect, where);

                    screen.OnDiagramButtonPressed((int)ControllerButton.R1);
                    Settle(canvas);
                    CheckPopup(checks, so, canvasRect, $"{where}, Assign R1 open", hasMechanisms, false);

                    // The Control Style button is hidden for a robot with nothing to style.
                    if (hasMechanisms)
                    {
                        screen.OnControlStylePressed();
                        Settle(canvas);
                        CheckPopup(checks, so, canvasRect, $"{where}, Control Style open", true, true);
                    }
                    layouts++;
                }
            }
        }
        finally
        {
            // Nothing here was saved: put back the scene that was open, from disk.
            if (!string.IsNullOrEmpty(previousScene)) EditorSceneManager.OpenScene(previousScene, OpenSceneMode.Single);
        }

        if (checks.Failures.Count == 0)
            return $"{Title}: PASSED ({checks.Count} checks over {layouts} robot layouts at {Screens.Length} screen sizes).";
        var report = new StringBuilder($"{checks.Failures.Count} of {checks.Count} checks failed:");
        foreach (string failure in checks.Failures.Take(60)) report.Append("\n  - ").Append(failure);
        if (checks.Failures.Count > 60) report.Append($"\n  ... and {checks.Failures.Count - 60} more");
        throw new InvalidOperationException(report.ToString());
    }

    // A robot nobody would build but anyone could upload: long mechanism names, and every button driving more
    // functions than a caption has lines for, so the "+N" fold and the shrink-then-ellipsis both run on every
    // button at both sizes, and the title has a robot name long enough to need shrinking.
    private static RobotModelCatalog.Entry WorstCase()
    {
        string[] names =
        {
            "Right Side Scoring Intake Roller", "Left Side Double Reverse Four Bar", "Plastic Claw Clamp Flipper",
            "Endgame Hang Hook Release", "Wide Floor Intake Pivot Piston", "Mobile Goal Tilter Piston",
        };
        var entry = new RobotModelCatalog.Entry
        {
            id = "layout-check-worst-case",
            displayName = "Layout Check Robot With A Very Long Name",
        };
        for (int i = 0; i < names.Length; i++)
        {
            entry.mechanisms.Add(new RobotModelCatalog.MechanismInfo
            {
                id = "mechanism" + i,
                displayName = names[i],
                type = i < 4 ? RobotMechanisms.TypeMotor : RobotMechanisms.TypePneumatic,
            });
        }
        foreach (ControllerButton button in Enum.GetValues(typeof(ControllerButton)))
        {
            for (int k = 0; k < 4; k++)
            {
                int m = ((int)button + k) % names.Length;
                string mode = m >= 4 ? ControllerMapSettings.ModeToggle
                    : k % 2 == 0 ? ControllerMapSettings.ModeForward : ControllerMapSettings.ModeReverse;
                entry.defaultButtonMap.assignments.Add(new ButtonAssignment
                {
                    button = button.ToString(), mechanismId = "mechanism" + m, mode = mode,
                });
            }
        }
        return entry;
    }

    // Everything the game's own layout pass has done by the time the screen is looked at: the layout groups,
    // the diagram's fit-to-screen scale (sent on a rect change, which edit mode never sends), and TMP's
    // auto-sizing, which needs the final rects.
    private static void Settle(Canvas canvas)
    {
        var root = (RectTransform)canvas.transform;
        for (int pass = 0; pass < 2; pass++)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(root);
            foreach (ScaleToFitParent fit in canvas.GetComponentsInChildren<ScaleToFitParent>(true)) fit.Apply();
        }
        foreach (TMP_Text text in canvas.GetComponentsInChildren<TMP_Text>(false)) text.ForceMeshUpdate();
    }

    private static void CheckDiagram(ValidationUtil.Checks checks, SerializedObject so, RobotModelCatalog.Entry robot,
        ButtonMap map, RectTransform canvas, string where)
    {
        var panel = (GameObject)so.FindProperty("panel").objectReferenceValue;
        var overlay = (GameObject)so.FindProperty("assignmentPanel").objectReferenceValue;
        checks.That(panel != null && panel.activeInHierarchy, $"{where}: the config panel isn't showing");
        checks.That(overlay != null && !overlay.activeInHierarchy, $"{where}: the popup's dim is up with no popup open");
        if (panel == null) return;

        Transform diagram = panel.transform.Find("ConfigDiagramArea/ControllerDiagram");
        checks.That(diagram != null, $"{where}: there is no ConfigDiagramArea/ControllerDiagram to measure");
        if (diagram == null) return;

        List<TMP_Text> texts = Showing(panel.transform);
        CheckTexts(checks, texts, panel.GetComponentsInChildren<Selectable>(false).ToList(), canvas, where);

        // On the diagram's dark backdrop, not poking off it onto the panel.
        Rect backdrop = InCanvas((RectTransform)diagram, canvas);
        foreach (TMP_Text text in texts.Where(t => t.transform.IsChildOf(diagram)))
        {
            float off = Spill(Ink(text, canvas), backdrop);
            checks.That(off <= Slack, $"{where}: {Name(text)} pokes {off:0} units off the diagram");
        }

        // Every button that drives something says so, and none that doesn't: otherwise the checks above ran on
        // less than the screen shows.
        SerializedProperty buttons = so.FindProperty("buttons");
        SerializedProperty captions = so.FindProperty("assignmentLabels");
        var ids = new HashSet<string>(robot.mechanisms.Where(m => m != null).Select(m => m.id));
        for (int i = 0; i < ControllerMapSettings.ButtonCount; i++)
        {
            var caption = (TMP_Text)captions.GetArrayElementAtIndex(i).objectReferenceValue;
            bool drives = ControllerMapSettings.FindAll(map, (ControllerButton)i).Any(a => ids.Contains(a.mechanismId));
            checks.That(caption != null && drives == !string.IsNullOrWhiteSpace(caption.text),
                $"{where}: {(ControllerButton)i} {(drives ? "drives something but its caption is empty" : "drives nothing but has a caption")}");
            // Every line it was given is drawn. TMP's own ellipsis cuts a caption at its first line too wide
            // for the box and drops every line after it, which no overlap or spill would ever show.
            if (caption != null && !string.IsNullOrWhiteSpace(caption.text))
            {
                int given = caption.text.Split('\n').Length;
                checks.That(caption.textInfo.lineCount == given,
                    $"{where}: {(ControllerButton)i}'s caption draws {caption.textInfo.lineCount} of its {given} lines");
            }
        }
        for (int i = (int)ControllerButton.Up; i < ControllerMapSettings.ButtonCount; i++)
            CheckRound(checks, (Button)buttons.GetArrayElementAtIndex(i).objectReferenceValue, canvas, where);
    }

    private static void CheckPopup(ValidationUtil.Checks checks, SerializedObject so, RectTransform canvas, string where,
        bool expectRows, bool styleRows)
    {
        var panel = (GameObject)so.FindProperty("panel").objectReferenceValue;
        var overlay = (GameObject)so.FindProperty("assignmentPanel").objectReferenceValue;
        checks.That(overlay != null && overlay.activeInHierarchy, $"{where}: the popup didn't open");
        if (overlay == null || !overlay.activeInHierarchy) return;

        // The dim covers the whole screen, so nothing outside the popup looks tappable.
        Rect screenBox = InCanvas(canvas, canvas);
        Rect dim = InCanvas((RectTransform)overlay.transform, canvas);
        checks.That(dim.xMin <= screenBox.xMin + Slack && dim.xMax >= screenBox.xMax - Slack &&
                    dim.yMin <= screenBox.yMin + Slack && dim.yMax >= screenBox.yMax - Slack,
            $"{where}: the dim covers {dim.width:0}x{dim.height:0} of a {screenBox.width:0}x{screenBox.height:0} screen");

        RectTransform popup = overlay.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(r => r.name == "AssignmentPanel");
        checks.That(popup != null, $"{where}: there is no AssignmentPanel under the dim");
        if (popup == null) return;
        Rect box = InCanvas(popup, canvas);
        checks.That(Spill(box, screenBox) <= Slack, $"{where}: the popup runs {Spill(box, screenBox):0} units off the screen");

        // Clear of the title above it and the bottom row below it: nothing half-hidden under it.
        var header = (TMP_Text)so.FindProperty("headerLabel").objectReferenceValue;
        checks.That(!Overlap(box, Ink(header, canvas), out Rect covered),
            $"{where}: the popup covers {covered.height:0} units of the title '{header.text}'");
        Transform bottomRow = panel.transform.Find("ConfigBottomRow");
        checks.That(bottomRow != null, $"{where}: there is no ConfigBottomRow to keep the popup clear of");
        if (bottomRow != null)
        {
            foreach (Transform button in bottomRow)
            {
                if (!button.gameObject.activeInHierarchy) continue;
                checks.That(!Overlap(box, InCanvas((RectTransform)button, canvas), out Rect under),
                    $"{where}: the popup covers {under.height:0} units of {button.name}");
            }
        }

        CheckTexts(checks, Showing(popup), popup.GetComponentsInChildren<Selectable>(false).ToList(), canvas, where);

        List<Button> rows = popup.GetComponentsInChildren<Button>(false).Where(b => b.name.StartsWith("Row_")).ToList();
        if (expectRows) checks.That(rows.Count > 0, $"{where}: the popup lists no rows");
        foreach (Button row in rows)
        {
            Transform detail = row.transform.Find(ControllerConfigScreen.RowDetailName);
            bool second = detail != null && detail.gameObject.activeSelf;
            checks.That(second == styleRows, $"{where}: row {row.name} {(styleRows ? "has no second line for its style" : "shows a second line")}");
        }
    }

    // Text that is drawn, and every button: no text runs into a button that isn't its own or into other text,
    // and none spills out of its own box. What a scroll list clips is measured as clipped.
    private static void CheckTexts(ValidationUtil.Checks checks, List<TMP_Text> texts, List<Selectable> buttons,
        RectTransform canvas, string where)
    {
        var inks = texts.Select(t => Clip(Ink(t, canvas), t.transform, canvas)).ToList();
        var boxes = buttons.Select(b => Clip(InCanvas((RectTransform)b.transform, canvas), b.transform, canvas)).ToList();
        for (int i = 0; i < texts.Count; i++)
        {
            TMP_Text text = texts[i];
            Rect whole = Ink(text, canvas);
            checks.That(whole.width > 0f && whole.height > 0f, $"{where}: {Name(text)} drew nothing to measure");
            if (whole.width <= 0f || whole.height <= 0f) continue;
            float spill = Spill(whole, InCanvas(text.rectTransform, canvas));
            checks.That(spill <= Slack, $"{where}: {Name(text)} spills {spill:0} units out of its box");

            if (inks[i].width <= 0f || inks[i].height <= 0f) continue; // scrolled out of view
            var hits = new List<string>();
            for (int b = 0; b < buttons.Count; b++)
                if (!text.transform.IsChildOf(buttons[b].transform) && Overlap(inks[i], boxes[b], out Rect x))
                    hits.Add($"button {buttons[b].name} ({x.width:0}x{x.height:0})");
            for (int j = i + 1; j < texts.Count; j++)
                if (Overlap(inks[i], inks[j], out Rect x)) hits.Add($"{Name(texts[j])} ({x.width:0}x{x.height:0})");
            checks.That(hits.Count == 0, $"{where}: {Name(text)} runs into {string.Join(", ", hits)}");
        }
    }

    // A diamond button's arrow or letter sits inside its circle, not merely inside the square around it.
    private static void CheckRound(ValidationUtil.Checks checks, Button button, RectTransform canvas, string where)
    {
        if (button == null) { checks.That(false, $"{where}: a diamond button is missing"); return; }
        Rect circle = InCanvas((RectTransform)button.transform, canvas);
        float radius = Mathf.Min(circle.width, circle.height) * 0.5f;
        int marks = 0;
        foreach (Graphic mark in button.GetComponentsInChildren<Graphic>(false))
        {
            // A sub-mesh is TMP drawing part of its parent label from another atlas (the bold face's), on a
            // rect that is the whole label's box: the label's own ink below is what the letters cover.
            if (mark.gameObject == button.gameObject || mark is TMP_SubMeshUI) continue;
            marks++;
            Rect r = mark is TMP_Text text ? Ink(text, canvas) : InCanvas(mark.rectTransform, canvas);
            float reach = new[] { new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax) }
                .Max(p => (p - circle.center).magnitude) - radius;
            checks.That(reach <= Slack, $"{where}: {button.name}'s {mark.name} reaches {reach:0.#} units past its circle");
        }
        checks.That(marks > 0, $"{where}: {button.name} has no arrow or letter on it");
    }

    private static List<TMP_Text> Showing(Transform under) =>
        under.GetComponentsInChildren<TMP_Text>(false).Where(t => t.enabled && !string.IsNullOrWhiteSpace(t.text)).ToList();

    private static string Name(TMP_Text text)
    {
        string s = text.text.Replace("\n", " / ");
        return $"'{(s.Length > 50 ? s.Substring(0, 47) + "..." : s)}' ({text.name})";
    }

    // The text as drawn: TMP's bounds of the laid-out characters, not the box it was given.
    private static Rect Ink(TMP_Text text, RectTransform canvas)
    {
        Bounds b = text.textBounds;
        if (b.size.x <= 0f || b.size.y <= 0f) return Rect.zero;
        Transform t = text.transform;
        return Around(canvas, t.TransformPoint(new Vector3(b.min.x, b.min.y)), t.TransformPoint(new Vector3(b.max.x, b.max.y)),
            t.TransformPoint(new Vector3(b.min.x, b.max.y)), t.TransformPoint(new Vector3(b.max.x, b.min.y)));
    }

    private static Rect InCanvas(RectTransform rect, RectTransform canvas)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return Around(canvas, corners);
    }

    private static Rect Around(RectTransform canvas, params Vector3[] world)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (Vector3 w in world)
        {
            Vector3 p = canvas.InverseTransformPoint(w);
            x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y);
            x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
        }
        return Rect.MinMaxRect(x0, y0, x1, y1);
    }

    // What a scrolling list actually shows of something inside it.
    private static Rect Clip(Rect r, Transform t, RectTransform canvas)
    {
        foreach (RectMask2D mask in t.GetComponentsInParent<RectMask2D>())
            r = Intersect(r, InCanvas(mask.rectTransform, canvas));
        return r;
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        float x0 = Mathf.Max(a.xMin, b.xMin), y0 = Mathf.Max(a.yMin, b.yMin);
        float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
        return x1 > x0 && y1 > y0 ? Rect.MinMaxRect(x0, y0, x1, y1) : Rect.zero;
    }

    private static bool Overlap(Rect a, Rect b, out Rect overlap)
    {
        overlap = Intersect(a, b);
        return overlap.width > Slack && overlap.height > Slack;
    }

    // How far a rect reaches past the box it should stay inside; zero or less when it stays in.
    private static float Spill(Rect r, Rect box) =>
        Mathf.Max(Mathf.Max(box.xMin - r.xMin, r.xMax - box.xMax), Mathf.Max(box.yMin - r.yMin, r.yMax - box.yMax));
}
