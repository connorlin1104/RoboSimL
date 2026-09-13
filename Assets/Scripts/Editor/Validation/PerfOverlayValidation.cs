using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Checks for the performance overlay (PerfOverlay): the parts nobody would see break until a TestFlight build came back
// with a log that won't open, a log with no GPU times in it, or a build that wouldn't link at all.
//
//   - Player Settings > Frame Timing Stats is on. Off, a release build measures no CPU or GPU time.
//   - Every native call DeviceStats makes is a function in RoboSimDeviceStats.mm, of the same type, and the plugin builds
//     for iOS alone. A missing name fails in Xcode, twenty minutes into an archive; a wrong type returns garbage.
//   - A second's frames come out as the numbers they should, a frame reported twice counting once.
//   - Every row has one cell per column, whatever language the phone is in: German writes 7.2 as "7,2".
//   - Every line the panel can print is in its font, whose glyphs are baked in advance: anything else is an empty box.
//   - Only the stage button takes a touch, so the panel never blocks the controls under it; and it measures in the same
//     canvas units as the home screen, so the positions it is placed at mean what they say.
//   - The switch is on Settings > Robot, wired, and off.
//
// Each check that could pass by not looking also has to refuse a mistake made on purpose (ValidationUtil.Checks.Refuses).
//
// Usage: Tools > RoboSim > Validate > Validate Performance Overlay, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod PerfOverlayValidation.RunBatchValidate
public static class PerfOverlayValidation
{
    private const string Title = "Validate Performance Overlay";
    private const string NativePath = "Assets/Plugins/iOS/RoboSimDeviceStats.mm";
    private const string BridgePath = "Assets/Scripts/Diagnostics/DeviceStats.cs";

    [MenuItem("Tools/RoboSim/Validate/Validate Performance Overlay", false, 56)]
    private static void RunInteractive()
    {
        // It opens HomeScene over whatever is open.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive(Title, Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch(Title, Run);

    private static string Run()
    {
        var checks = new ValidationUtil.Checks();
        FrameTimingIsOn(checks);
        NativeBridge(checks);
        Window(checks);
        Csv(checks);
        Readout(checks);
        CanvasScaler homeScaler = SettingsSwitch(checks);
        Panel(checks, homeScaler);

        if (checks.Failures.Count == 0) return $"{Title}: PASSED ({checks.Count} checks).";
        var report = new StringBuilder($"{checks.Failures.Count} of {checks.Count} checks failed:");
        foreach (string failure in checks.Failures) report.Append("\n  - ").Append(failure);
        throw new InvalidOperationException(report.ToString());
    }

    // --- Frame Timing Stats ---

    private static void FrameTimingIsOn(ValidationUtil.Checks checks) =>
        checks.That(PlayerSettings.enableFrameTimingStats,
            "Player Settings > Other Settings > Frame Timing Stats is off, so a release build measures no CPU or GPU time " +
            "and the overlay can only say so. Turn it on (ProjectSettings.asset: enableFrameTimingStats: 1).");

    // --- The native half ---

    private static readonly Regex Import =
        new Regex(@"\[DllImport\(""__Internal""\)\]\s*private\s+static\s+extern\s+(\w+)\s+(\w+)\s*\(\s*\)\s*;");
    private static readonly Regex Native =
        new Regex(@"^\s*(int|int64_t|double)\s+(RoboSim\w+)\s*\(\s*void\s*\)", RegexOptions.Multiline);

    // What each C# return type is called on the C side.
    private static readonly Dictionary<string, string> CType = new Dictionary<string, string>
    {
        { "int", "int" }, { "long", "int64_t" }, { "double", "double" },
    };

    // Whether every C# call has its native function, of the type C# expects: null when they agree, else what's wrong.
    private static string BridgeProblem(string csharp, string native)
    {
        var functions = new Dictionary<string, string>();
        foreach (Match match in Native.Matches(native)) functions[match.Groups[2].Value] = match.Groups[1].Value;
        MatchCollection imports = Import.Matches(csharp);
        if (imports.Count == 0) return "DeviceStats.cs declares no native calls, so there was nothing to compare";

        var problems = new List<string>();
        foreach (Match match in imports)
        {
            string type = match.Groups[1].Value, name = match.Groups[2].Value;
            if (!functions.TryGetValue(name, out string nativeType))
                problems.Add($"{name} is called from C# but isn't in RoboSimDeviceStats.mm, so Xcode would fail to link");
            else if (!CType.TryGetValue(type, out string expected) || expected != nativeType)
                problems.Add($"{name} returns {nativeType} in RoboSimDeviceStats.mm but {type} in DeviceStats.cs");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static void NativeBridge(ValidationUtil.Checks checks)
    {
        string csharp = File.ReadAllText(BridgePath);
        string native = File.ReadAllText(NativePath);
        string problem = BridgeProblem(csharp, native);
        checks.That(problem == null, problem);

        // The comparison has to catch each kind of mistake it claims to.
        checks.Refuses(() => Throw(BridgeProblem(csharp, native.Replace("RoboSimThermalState", "RoboSimThermal"))),
            "a C# call whose native function was renamed");
        checks.Refuses(() => Throw(BridgeProblem(csharp.Replace("extern long RoboSimFootprintBytes", "extern int RoboSimFootprintBytes"), native)),
            "a C# call of the wrong type");
        checks.Refuses(() => Throw(BridgeProblem(string.Empty, native)), "a bridge the parse found nothing in");

        var importer = AssetImporter.GetAtPath(NativePath) as PluginImporter;
        checks.That(importer != null, $"{NativePath} isn't imported as a plugin");
        if (importer == null) return;
        checks.That(importer.GetCompatibleWithPlatform(BuildTarget.iOS),
            "RoboSimDeviceStats.mm isn't set to build for iOS, so the functions DeviceStats calls would be missing from the app");
        checks.That(!importer.GetCompatibleWithEditor() && !importer.GetCompatibleWithAnyPlatform(),
            "RoboSimDeviceStats.mm is set to build for more than iOS");
    }

    private static void Throw(string problem)
    {
        if (problem != null) throw new InvalidOperationException(problem);
    }

    // --- A second's frames ---

    private static void Window(ValidationUtil.Checks checks)
    {
        var window = new PerfWindow();
        // 59 frames at 60 fps and one 100 ms hitch: 60 frames in 1.08333 s.
        for (int i = 0; i < 59; i++) window.AddFrame(1.0 / 60.0);
        window.AddFrame(0.1);
        Near(checks, window.Fps, 55.3846, 0.001, "frames per second over 59 quick frames and one slow one");
        Near(checks, window.AverageFrameMs, 18.0556, 0.001, "the average frame over the same second");
        Near(checks, window.WorstFrameMs, 100.0, 1e-6, "the slowest frame");

        // FrameTimingManager hands back the latest finished frame each time it is asked, often the same one again.
        window.AddTiming(1000, 6.0, 4.0, 5.0);
        window.AddTiming(1000, 6.0, 4.0, 5.0);
        window.AddTiming(2000, 8.0, 0.0, 9.0); // no render-thread timer on this one
        Near(checks, window.CpuMainMs, 7.0, 1e-9, "the main thread's average, the repeated frame counted once");
        Near(checks, window.CpuRenderMs, 4.0, 1e-9, "the render thread's average, a missing timer left out rather than counted as 0");
        Near(checks, window.GpuMs, 7.0, 1e-9, "the GPU's average");
        Near(checks, window.WorstGpuMs, 9.0, 1e-9, "the GPU's slowest frame");

        window.Clear();
        checks.That(double.IsNaN(window.Fps) && double.IsNaN(window.WorstFrameMs) && double.IsNaN(window.GpuMs),
            "an empty second must read as unknown, not as 0");
        window.AddTiming(2000, 8.0, 3.0, 9.0);
        checks.That(double.IsNaN(window.CpuMainMs),
            "the frame handed over just before a clear must not count again just after it");
        window.AddFrame(double.NaN);
        window.AddFrame(-1.0);
        checks.That(window.Frames == 0, "a NaN or negative frame time must not count as a frame");
    }

    private static void Near(ValidationUtil.Checks checks, double actual, double expected, double tolerance, string what) =>
        checks.That(!double.IsNaN(actual) && Math.Abs(actual - expected) <= tolerance, $"{what}: expected {expected}, got {actual}");

    // --- The log's rows ---

    private static void Csv(ValidationUtil.Checks checks)
    {
        // A culture that writes numbers the way German and French do, made here rather than looked up, so the check
        // doesn't depend on which cultures this Mono carries.
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        comma.NumberFormat.NumberGroupSeparator = ".";

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            foreach (CultureInfo culture in new[] { CultureInfo.InvariantCulture, comma })
            {
                CultureInfo.CurrentCulture = culture;
                string name = culture == comma ? "a decimal-comma language" : "English";

                string tick = PerfCsv.TickRow(new PerfCsv.Tick
                {
                    t = 12.345, scene = "HomeScene", fps = 59.6, frameMs = 16.78, worstFrameMs = 33.3, cpuMainMs = 6.12,
                    cpuRenderMs = 4.5, gpuMs = 5.25, worstGpuMs = 9.75, heat = "Fair", lowPower = 0, footprintMb = 412.3,
                    headroomMb = 1433.9, batteryPct = 87, power = "Discharging", stage = "Drift",
                });
                checks.That(RowProblem(tick) == null, $"in {name}, a tick row: {RowProblem(tick)}");
                List<string> cells = Parse(tick);
                if (cells.Count == PerfCsv.Columns.Length)
                    checks.That(cells[Col("fps")] == "59.6" && cells[Col("cpu_main_ms")] == "6.12" && cells[Col("footprint_mb")] == "412.3",
                        $"in {name}, numbers must be written with a full stop; the row reads {tick}");

                const string detail = "app=1.0.0 device=iPhone14,2 os=\"iOS 17.5.1\"";
                string row = PerfCsv.EventRow(3.5, "HomeScene", "session", detail);
                checks.That(RowProblem(row) == null, $"in {name}, an event row: {RowProblem(row)}");
                cells = Parse(row);
                if (cells.Count == PerfCsv.Columns.Length)
                    checks.That(cells[Col("detail")] == detail && cells[Col("event")] == "session",
                        $"a detail with a comma and quotes in it must come back whole; it came back as {cells[Col("detail")]}");

                string unknown = PerfCsv.TickRow(new PerfCsv.Tick
                {
                    t = 1, scene = "LiteScene", fps = double.NaN, frameMs = double.NaN, worstFrameMs = double.NaN,
                    cpuMainMs = double.NaN, cpuRenderMs = double.NaN, gpuMs = double.NaN, worstGpuMs = double.NaN,
                    lowPower = -1, footprintMb = -1, headroomMb = -1, batteryPct = -100,
                });
                cells = Parse(unknown);
                checks.That(cells.Count == PerfCsv.Columns.Length && cells[Col("gpu_ms")] == string.Empty &&
                            cells[Col("footprint_mb")] == string.Empty && cells[Col("battery_pct")] == string.Empty &&
                            cells[Col("low_power")] == string.Empty,
                    $"what wasn't measured must be an empty cell, never 0 or -1; the row reads {unknown}");
            }
            checks.That(RowProblem(PerfCsv.Header()) == null, $"the header: {RowProblem(PerfCsv.Header())}");

            // The row check has to be able to fail: one number written the decimal-comma way splits its row.
            CultureInfo.CurrentCulture = comma;
            var split = new string[PerfCsv.Columns.Length];
            split[0] = 12.5.ToString("0.0");
            checks.Refuses(() => Throw(RowProblem(string.Join(",", split))), "a row with a number written as 12,5");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // Null when the row reads back as one cell per column, else what's wrong.
    private static string RowProblem(string row)
    {
        int count = Parse(row).Count;
        return count == PerfCsv.Columns.Length ? null : $"{count} cells for {PerfCsv.Columns.Length} columns: {row}";
    }

    // Reads a row back the way a spreadsheet would: commas split cells, a quoted cell may hold commas, "" is a quote.
    private static List<string> Parse(string row)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < row.Length && row[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    private static int Col(string name) => Array.IndexOf(PerfCsv.Columns, name);

    // --- What the panel prints ---

    private static void Readout(ValidationUtil.Checks checks)
    {
        TMP_FontAsset font = PerfOverlay.Font;
        checks.That(font != null, "TextMesh Pro has no default font asset, so the panel has nothing to draw with");
        if (font == null) return;

        var lines = new List<string>();
        foreach (DeviceStats.Thermal heat in Enum.GetValues(typeof(DeviceStats.Thermal)))
        {
            foreach (bool timing in new[] { true, false })
            {
                foreach (int lowPower in new[] { -1, 0, 1 })
                {
                    lines.Add(PerfOverlay.FormatReadout(new PerfOverlay.Reading
                    {
                        fps = timing ? 59.6 : double.NaN,
                        worstFrameMs = 183.2,
                        cpuMainMs = timing ? 6.1 : double.NaN,
                        gpuMs = 12.25,
                        frameTiming = timing,
                        heat = heat,
                        lowPower = lowPower,
                        footprintMb = lowPower < 0 ? -1 : 412.3,
                        headroomMb = lowPower < 0 ? -1 : 1433.9,
                        launchLine = PerfOverlay.FormatLaunch(2.41, lowPower == 1 ? -1 : 2.95, 183.0, timing),
                        loadLine = PerfOverlay.FormatLoad("LiteScene", 3.2),
                        logName = "perf-20260913-143210.csv",
                        // An error in the phone's own language, which the panel must not print as boxes.
                        logError = lowPower == 0 ? "ディスクがいっぱいです (Größe)" : string.Empty,
                    }));
                }
            }
        }
        foreach (RobotStageView.StageMode mode in Enum.GetValues(typeof(RobotStageView.StageMode)))
            lines.Add(PerfOverlay.StageButtonText(mode));

        foreach (string line in lines)
        {
            string missing = Missing(font, line);
            checks.That(missing == null, $"the panel can print \"{line.Replace('\n', '|')}\", but its font has no glyph for {missing}");
        }

        // The check has to be able to fail: a character the font doesn't have.
        checks.Refuses(() => Throw(Missing(font, "Heat 中")), "a line with a character the font doesn't have");
    }

    // The characters in `text` the font has no glyph for, line breaks aside; null when it has them all. TMP answers
    // false with no list when it couldn't read its own table, and that counts as missing everything.
    private static string Missing(TMP_FontAsset font, string text)
    {
        if (font.HasCharacters(text.Replace("\n", string.Empty), out uint[] missing, false, false)) return null;
        if (missing == null || missing.Length == 0) return "any character: the font's table couldn't be read";
        var names = new List<string>();
        foreach (uint c in missing) names.Add($"U+{c:X4}");
        return string.Join(", ", names);
    }

    // --- The switch ---

    private static CanvasScaler SettingsSwitch(ValidationUtil.Checks checks)
    {
        Scene scene = EditorSceneManager.OpenScene(RoboSimPaths.HomeScene, OpenSceneMode.Single);
        HomeScreenController controller = null;
        CanvasScaler scaler = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (controller == null) controller = root.GetComponentInChildren<HomeScreenController>(true);
            Canvas canvas = root.GetComponent<Canvas>();
            if (scaler == null && canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                scaler = root.GetComponent<CanvasScaler>();
        }
        checks.That(controller != null, "HomeScene has no HomeScreenController");
        if (controller == null) return scaler;

        var toggle = new SerializedObject(controller).FindProperty("performanceStatsToggle").objectReferenceValue as Toggle;
        checks.That(toggle != null, "HomeScreenController's performanceStatsToggle isn't wired. Run Tools > RoboSim > Scenes > Build Home Screen");
        if (toggle == null) return scaler;

        TMP_Text label = toggle.GetComponentInChildren<TMP_Text>(true);
        checks.That(label != null && label.text == "Show Performance Stats",
            $"the switch should read \"Show Performance Stats\"; it reads \"{(label != null ? label.text : null)}\"");
        bool onRobotPage = false;
        for (Transform t = toggle.transform; t != null; t = t.parent)
            if (t.name == "SettingsPage_Robot") onRobotPage = true;
        checks.That(onRobotPage, "the switch isn't on Settings > Robot, which is where Docs/Device-Performance.md sends you");
        checks.That(!PerformanceStatsSettings.DefaultShow && !toggle.isOn,
            "the switch must ship off: the readout is for measuring the app, not for playing it");
        return scaler;
    }

    // --- The panel ---

    private static void Panel(ValidationUtil.Checks checks, CanvasScaler home)
    {
        var root = new GameObject("PerfOverlayCheck") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            PerfOverlay.Parts parts = PerfOverlay.BuildUi(root.transform);

            var takers = new List<string>();
            foreach (Graphic graphic in root.GetComponentsInChildren<Graphic>(true))
                if (graphic.raycastTarget) takers.Add(graphic.name);
            checks.That(takers.Count == 1 && parts.stageButton.targetGraphic != null && takers[0] == parts.stageButton.targetGraphic.name,
                "only the stage button may take a touch — the panel sits over a field's controls — but these do: " +
                string.Join(", ", takers));

            string missing = Missing(PerfOverlay.Font, parts.readout.text + parts.stageLabel.text);
            checks.That(missing == null, $"the panel's first words need glyphs its font doesn't have: {missing}");

            // Its positions are written in the home screen's canvas units, so its canvas has to measure the same way.
            CanvasScaler own = root.GetComponent<CanvasScaler>();
            checks.That(home != null, "HomeScene has no ScreenSpaceOverlay canvas with a CanvasScaler to compare against");
            if (home != null)
                checks.That(own.uiScaleMode == home.uiScaleMode && own.referenceResolution == home.referenceResolution &&
                            Mathf.Approximately(own.matchWidthOrHeight, home.matchWidthOrHeight),
                    $"the panel's canvas scales as {own.referenceResolution} match {own.matchWidthOrHeight}, the home screen's as " +
                    $"{home.referenceResolution} match {home.matchWidthOrHeight}, so its positions would land somewhere else");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }
}
