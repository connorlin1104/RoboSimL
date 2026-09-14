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
using UnityEngine.EventSystems;
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
//   - The panel is a thin column of five short rows (FPS, CPU, GPU, Heat, RAM) and a Low Power row only while that
//     mode is on; every row fits its column, and all of it is in the font, whose glyphs are baked in advance.
//   - Nothing on the panel takes a touch, so it never blocks the controls under it. In the home screen's corner it ends
//     before the title's first letter with the title docked on the narrowest screen, and in a game it starts below L1
//     and L2 in both field scenes. It measures in the same canvas units as the home screen, so its positions mean what
//     they say.
//   - The switch is on Settings > Robot, wired, and off, with the home robot's Turning / Still / Off button under it.
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
        HomeScreen home = Switches(checks);
        Panel(checks, home);
        // Last: it opens the field scenes, and the HomeScene objects above go with the scene they came from.
        FieldClearance(checks);

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

    private static readonly string[] RowLabels = { "FPS", "CPU", "GPU", "Heat", "RAM" };
    private const string LowPowerLabel = "Low Power";
    private const float MinRowGap = 8f;   // canvas units between a row's label and its value

    private static void Readout(ValidationUtil.Checks checks)
    {
        TMP_FontAsset font = PerfOverlay.Font;
        checks.That(font != null, "TextMesh Pro has no default font asset, so the panel has nothing to draw with");
        if (font == null) return;

        var readings = new List<PerfOverlay.Reading>();
        foreach (DeviceStats.Thermal heat in Enum.GetValues(typeof(DeviceStats.Thermal)))
            foreach (bool timing in new[] { true, false })
                foreach (int lowPower in new[] { -1, 0, 1 })
                    readings.Add(new PerfOverlay.Reading
                    {
                        fps = timing ? 59.6 : double.NaN, cpuMainMs = timing ? 6.1 : double.NaN,
                        gpuMs = 12.25, frameTiming = timing, heat = heat, lowPower = lowPower, footprintMb = lowPower < 0 ? -1 : 412.3,
                    });
        // The widest each value gets before it changes unit, and far past that: what the column has to hold.
        readings.Add(new PerfOverlay.Reading
        {
            fps = 120, cpuMainMs = 99.94, gpuMs = 999.4, frameTiming = true,
            heat = DeviceStats.Thermal.Critical, lowPower = 1, footprintMb = 999.4,
        });
        readings.Add(new PerfOverlay.Reading
        {
            fps = 1000, cpuMainMs = 123456, gpuMs = 99.96, frameTiming = true,
            heat = DeviceStats.Thermal.Nominal, lowPower = 1, footprintMb = 123456,
        });

        var root = new GameObject("PerfReadoutCheck") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            PerfOverlay.Parts parts = PerfOverlay.BuildUi(root.transform);
            foreach (PerfOverlay.Reading reading in readings)
            {
                List<PerfOverlay.Row> rows = PerfOverlay.ReadoutRows(reading);
                string problem = RowsProblem(reading, rows);
                checks.That(problem == null, problem);
                foreach (bool values in new[] { false, true })
                {
                    string column = PerfOverlay.Column(rows, values);
                    string missing = Missing(font, column);
                    checks.That(missing == null, $"the panel can print \"{column.Replace('\n', '|')}\", but its font has no glyph for {missing}");
                }
                string wide = WidthProblem(parts, rows);
                checks.That(wide == null, wide);
            }

            // All three checks have to be able to fail: a row the column can't hold (the label the slowest frame would
            // need to say what it is, which is why the panel doesn't show it), a row a player has no use for, and a
            // character the font doesn't have.
            List<PerfOverlay.Row> tooLong = PerfOverlay.ReadoutRows(readings[0]);
            tooLong[3] = new PerfOverlay.Row("Slowest frame", "1199 ms");
            checks.Refuses(() => Throw(WidthProblem(parts, tooLong)), "a row wider than the column");
            List<PerfOverlay.Row> extra = PerfOverlay.ReadoutRows(readings[0]);
            extra.Add(new PerfOverlay.Row("Log", "perf-20260913-143210.csv"));
            checks.Refuses(() => Throw(RowsProblem(readings[0], extra)), "a row the panel shouldn't have");
            checks.Refuses(() => Throw(Missing(font, "Heat 中")), "a line with a character the font doesn't have");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // Null when the rows are the panel's six, in order, and then Low Power when, and only when, that mode is on.
    private static string RowsProblem(PerfOverlay.Reading reading, List<PerfOverlay.Row> rows)
    {
        var expected = new List<string>(RowLabels);
        if (reading.lowPower == 1) expected.Add(LowPowerLabel);
        var labels = new List<string>();
        foreach (PerfOverlay.Row row in rows) labels.Add(row.label);
        return string.Join("|", labels) == string.Join("|", expected) ? null
            : $"the panel's rows should be {string.Join(", ", expected)}; they are {string.Join(", ", labels)}";
    }

    // Null when every row's label and value fit side by side in the column, measured by TextMesh Pro in the panel's own
    // font and size; else the first row that doesn't.
    private static string WidthProblem(PerfOverlay.Parts parts, List<PerfOverlay.Row> rows)
    {
        float column = parts.labels.rectTransform.sizeDelta.x;
        foreach (PerfOverlay.Row row in rows)
        {
            float label = parts.labels.GetPreferredValues(row.label).x;
            float value = string.IsNullOrEmpty(row.value) ? 0f : parts.values.GetPreferredValues(row.value).x;
            if (label + MinRowGap + value > column)
                return $"the row \"{row.label}  {row.value}\" needs {label + MinRowGap + value:0} units but the column is {column:0}";
        }
        return null;
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

    // --- The switches in Settings ---

    // What the panel's own checks need from HomeScene: its canvas scaler, and the title the panel has to stay clear of.
    private struct HomeScreen
    {
        public CanvasScaler scaler;
        public RectTransform title;
    }

    private static HomeScreen Switches(ValidationUtil.Checks checks)
    {
        Scene scene = EditorSceneManager.OpenScene(RoboSimPaths.HomeScene, OpenSceneMode.Single);
        var home = new HomeScreen();
        HomeScreenController controller = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (controller == null) controller = root.GetComponentInChildren<HomeScreenController>(true);
            Canvas canvas = root.GetComponent<Canvas>();
            if (home.scaler == null && canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                home.scaler = root.GetComponent<CanvasScaler>();
            TitleDock dock = root.GetComponentInChildren<TitleDock>(true);
            if (home.title == null && dock != null) home.title = (RectTransform)dock.transform;
        }
        checks.That(controller != null, "HomeScene has no HomeScreenController");
        if (controller == null) return home;

        var so = new SerializedObject(controller);
        var toggle = so.FindProperty("performanceStatsToggle").objectReferenceValue as Toggle;
        checks.That(toggle != null, "HomeScreenController's performanceStatsToggle isn't wired. Run Tools > RoboSim > Scenes > Build Home Screen");
        if (toggle == null) return home;

        TMP_Text label = toggle.GetComponentInChildren<TMP_Text>(true);
        checks.That(label != null && label.text == "Show Performance Stats",
            $"the switch should read \"Show Performance Stats\"; it reads \"{(label != null ? label.text : null)}\"");
        checks.That(OnRobotPage(toggle.transform), "the switch isn't on Settings > Robot, which is where Docs/Device-Performance.md sends you");
        checks.That(!PerformanceStatsSettings.DefaultShow && !toggle.isOn,
            "the switch must ship off: the readout is for measuring the app, not for playing it");

        HomeStageButton(checks, so, toggle);
        return home;
    }

    // The home robot's button: right under the switch, pointed at the stage it switches, saying what the stage does before
    // anyone has pressed it, and stepping through every mode.
    private static void HomeStageButton(ValidationUtil.Checks checks, SerializedObject so, Toggle performanceSwitch)
    {
        var button = so.FindProperty("homeStageButton").objectReferenceValue as Button;
        var view = so.FindProperty("stageView").objectReferenceValue as RobotStageView;
        checks.That(button != null && view != null,
            "HomeScreenController's homeStageButton or stageView isn't wired, so the home robot can't be switched. " +
            "Run Tools > RoboSim > Scenes > Build Home Screen");
        if (button == null) return;

        checks.That(button.transform.parent == performanceSwitch.transform.parent &&
                    button.transform.GetSiblingIndex() == performanceSwitch.transform.GetSiblingIndex() + 1,
            "the home robot's button should sit right under Show Performance Stats");

        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        string first = HomeStageSettings.ButtonText(HomeStageSettings.DefaultMode);
        checks.That(label != null && label.text == first,
            $"the home robot's button should read \"{first}\" in the scene; it reads \"{(label != null ? label.text : null)}\"");

        var modes = (RobotStageView.StageMode[])Enum.GetValues(typeof(RobotStageView.StageMode));
        foreach (RobotStageView.StageMode mode in modes)
        {
            string text = HomeStageSettings.ButtonText(mode);
            string missing = label != null ? Missing(label.font, text) : null;
            checks.That(missing == null, $"the home robot's button can say \"{text}\", but its font has no glyph for {missing}");
            checks.That(HomeStageSettings.Parse(mode.ToString()) == mode,
                $"a stored {mode} reads back as {HomeStageSettings.Parse(mode.ToString())}");
        }
        checks.That(HomeStageSettings.DefaultMode == RobotStageView.StageMode.Drift, "the home robot should turn until someone changes it");
        // Only a name written by the setting itself counts: not a number, not the wrong case, not a combination of flags.
        foreach (string unreadable in new[] { string.Empty, "1", "off", "Drift, Off" })
            checks.That(HomeStageSettings.Parse(unreadable) == HomeStageSettings.DefaultMode,
                $"a stored \"{unreadable}\" should read as {HomeStageSettings.DefaultMode}; it reads as {HomeStageSettings.Parse(unreadable)}");

        // Presses go Turning, Still, Off and back: every mode once, then the first again.
        var seen = new HashSet<RobotStageView.StageMode>();
        RobotStageView.StageMode at = HomeStageSettings.DefaultMode;
        for (int i = 0; i < modes.Length; i++)
        {
            seen.Add(at);
            at = HomeStageSettings.Next(at);
        }
        checks.That(at == HomeStageSettings.DefaultMode && seen.Count == modes.Length,
            $"{modes.Length} presses of the home robot's button should visit every mode once and come back to the first");
    }

    private static bool OnRobotPage(Transform t)
    {
        for (; t != null; t = t.parent)
            if (t.name == "SettingsPage_Robot") return true;
        return false;
    }

    // --- The panel ---

    private static void Panel(ValidationUtil.Checks checks, HomeScreen home)
    {
        var root = new GameObject("PerfOverlayCheck") { hideFlags = HideFlags.HideAndDontSave };
        var leaky = new GameObject("PerfOverlayLeak") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            PerfOverlay.Parts parts = PerfOverlay.BuildUi(root.transform);

            string touch = TouchProblem(root);
            checks.That(touch == null, touch);
            // The check has to be able to fail: the same panel, given back the raycaster it used to have.
            PerfOverlay.BuildUi(leaky.transform);
            leaky.AddComponent<GraphicRaycaster>();
            checks.Refuses(() => Throw(TouchProblem(leaky)), "a panel whose canvas can take a touch");

            string missing = Missing(PerfOverlay.Font, parts.labels.text);
            checks.That(missing == null, $"the panel's first words need glyphs its font doesn't have: {missing}");

            checks.That(parts.panel.anchoredPosition == PerfOverlay.HomePosition && parts.panel.pivot == new Vector2(0f, 1f) &&
                        parts.panel.anchorMin == new Vector2(0f, 1f) && parts.panel.anchorMax == new Vector2(0f, 1f) &&
                        Mathf.Approximately(parts.panel.sizeDelta.x, PerfOverlay.PanelWidth),
                "the panel should start as a PanelWidth-wide column hung from the top-left corner at PerfOverlay.HomePosition");
            string title = TitleProblem(PerfOverlay.HomePosition, PerfOverlay.PanelWidth, home.title, home.scaler);
            checks.That(title == null, title);
            if (home.title != null && home.scaler != null)
                checks.Refuses(() => Throw(TitleProblem(PerfOverlay.HomePosition, 560f, home.title, home.scaler)),
                    "the old 560-wide panel in the corner, which the docked title slid under");

            // Its position is written in the home screen's canvas units, so its canvas has to measure the same way.
            CanvasScaler own = root.GetComponent<CanvasScaler>();
            checks.That(home.scaler != null, "HomeScene has no ScreenSpaceOverlay canvas with a CanvasScaler to compare against");
            if (home.scaler != null)
                checks.That(own.uiScaleMode == home.scaler.uiScaleMode && own.referenceResolution == home.scaler.referenceResolution &&
                            Mathf.Approximately(own.matchWidthOrHeight, home.scaler.matchWidthOrHeight),
                    $"the panel's canvas scales as {own.referenceResolution} match {own.matchWidthOrHeight}, the home screen's as " +
                    $"{home.scaler.referenceResolution} match {home.scaler.matchWidthOrHeight}, so its position would land somewhere else");
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(leaky);
        }
    }

    // Null when nothing under root can take a touch, else what can. A canvas with no raycaster takes none at all; a
    // Selectable or a raycast-target graphic is listed too, because it would start taking them the moment a raycaster
    // came back.
    private static string TouchProblem(GameObject root)
    {
        var takers = new List<string>();
        foreach (BaseRaycaster raycaster in root.GetComponentsInChildren<BaseRaycaster>(true))
            takers.Add($"a {raycaster.GetType().Name} on {raycaster.name}");
        foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>(true))
            takers.Add($"a {selectable.GetType().Name} ({selectable.name})");
        foreach (Graphic graphic in root.GetComponentsInChildren<Graphic>(true))
            if (graphic.raycastTarget) takers.Add($"{graphic.name}, a raycast target");
        return takers.Count == 0 ? null
            : "nothing on the panel may take a touch, since it sits over a field's controls, but these can: " + string.Join(", ", takers);
    }

    // The screen shape on which the docked title reaches furthest into the panel's corner: the narrowest, a 4:3 iPad.
    private const float NarrowestAspect = 4f / 3f;
    private const float TitleMargin = 12f;

    // Null when a panel `width` wide at `position` ends at least TitleMargin short of the home title's first letter, with
    // the title docked over the stage on the narrowest screen: that is where it comes closest, sliding into the corner's
    // row as Settings opens. Worked out in canvas units from the scene: the title's docked anchor span, its centred text
    // at the size TextMesh Pro gives it (GetPreferredValues measures an auto-sized text at its largest size), and the
    // canvas width a 4:3 screen has at the home canvas's scaler settings.
    private static string TitleProblem(Vector2 position, float width, RectTransform title, CanvasScaler scaler)
    {
        if (title == null || scaler == null) return "HomeScene has no docking title or canvas scaler to measure the panel against";
        var text = title.GetComponent<TMP_Text>();
        var dock = title.GetComponent<TitleDock>();
        var stage = title.parent as RectTransform;
        if (text == null || dock == null || stage == null || stage.parent == null || stage.parent.GetComponent<Canvas>() == null ||
            stage.anchorMin.x != 0f || stage.anchorMax.x != 1f || stage.offsetMin.x != 0f || stage.offsetMax.x != 0f ||
            text.horizontalAlignment != HorizontalAlignmentOptions.Center)
            return "the title or HomeStage changed shape (HomeStage should fill the canvas's width, the title be centred in " +
                   "its span), so the check can't say where the title's first letter lands";
        float canvasWidth = CanvasWidth(NarrowestAspect, scaler);
        float left = title.anchorMin.x * canvasWidth + title.anchoredPosition.x - title.sizeDelta.x * title.pivot.x;
        float right = dock.dockedAnchorMaxX * canvasWidth + title.anchoredPosition.x + title.sizeDelta.x * (1f - title.pivot.x);
        float letters = Mathf.Min(text.GetPreferredValues(text.text).x, right - left);
        float firstLetter = (left + right) / 2f - letters / 2f;
        float panelRight = position.x + width;
        return panelRight + TitleMargin <= firstLetter ? null
            : $"with Settings open on a 4:3 screen the title's first letter lands {firstLetter:0} in, but the panel reaches " +
              $"{panelRight:0} (it has to stop {TitleMargin:0} short), so the title would slide under it";
    }

    // How wide the canvas is on a screen of this shape: CanvasScaler's MatchWidthOrHeight mixes the two scale factors in
    // log2 space.
    private static float CanvasWidth(float aspect, CanvasScaler scaler)
    {
        float height = 1000f, width = aspect * height;
        float logWidth = Mathf.Log(width / scaler.referenceResolution.x, 2f);
        float logHeight = Mathf.Log(height / scaler.referenceResolution.y, 2f);
        return width / Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, scaler.matchWidthOrHeight));
    }

    // --- In a game ---

    private const string ShoulderCluster = "ShoulderButtonsLeft";   // BuildDriveControls' L1 + L2 group

    // In a game the panel steps down under L1 and L2. Read off both field scenes rather than copied from
    // BuildDriveControls, so moving the buttons fails here instead of in someone's hands.
    private static void FieldClearance(ValidationUtil.Checks checks)
    {
        foreach (string path in new[] { RoboSimPaths.LiteScene, RoboSimPaths.MainScene })
        {
            string problem = ClusterProblem(path, PerfOverlay.FieldPosition, PerfOverlay.PanelWidth);
            checks.That(problem == null, problem);
        }
        // The check has to be able to fail: a panel left at the home screen's spot, on top of L1.
        checks.Refuses(() => Throw(ClusterProblem(RoboSimPaths.LiteScene, PerfOverlay.HomePosition, PerfOverlay.PanelWidth)),
            "a panel that stayed in the home screen's corner, on top of L1");
    }

    // Null when a panel at `position` is clear of the scene's L1/L2 cluster: wholly below it or wholly beside it.
    private static string ClusterProblem(string scenePath, Vector2 position, float width)
    {
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        RectTransform cluster = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (RectTransform rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.name != ShoulderCluster) continue;
                cluster = rect;
                break;
            }
            if (cluster != null) break;
        }
        if (cluster == null) return $"{scene.name} has no {ShoulderCluster}, so there is nothing to keep the panel off";
        var scaler = cluster.parent != null ? cluster.parent.GetComponent<CanvasScaler>() : null;
        if (scaler == null || scaler.referenceResolution != new Vector2(1920f, 1080f) || !Mathf.Approximately(scaler.matchWidthOrHeight, 0.5f) ||
            cluster.anchorMin != new Vector2(0f, 1f) || cluster.anchorMax != new Vector2(0f, 1f) || cluster.pivot != new Vector2(0f, 1f))
            return $"{scene.name}'s {ShoulderCluster} is no longer pinned to the top-left corner of a 1920x1080, match-0.5 canvas like " +
                   "the panel's, so this can't say where L2 ends";
        float bottom = cluster.anchoredPosition.y - cluster.sizeDelta.y;
        float right = cluster.anchoredPosition.x + cluster.sizeDelta.x;
        bool below = position.y <= bottom;
        bool beside = position.x >= right || position.x + width <= cluster.anchoredPosition.x;
        return below || beside ? null
            : $"in {scene.name} the panel at ({position.x:0}, {position.y:0}) sits on L1/L2, which reach {-bottom:0} down";
    }
}
