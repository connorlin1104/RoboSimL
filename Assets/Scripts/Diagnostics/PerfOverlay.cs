using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The performance readout that Settings > Robot > Show Performance Stats turns on. It shows how hard the phone is
// working in the corner of every screen, and writes the same numbers to a file once a second — because the phone this
// app is tested on can't be connected to Xcode or to Unity's profiler, so the app has to measure itself.
// Docs/Device-Performance.md is the test it exists for.
//
// Every second, one row: frames per second and the slowest frame; how long the main thread, the render thread and the
// GPU spent on an average frame (FrameTimingManager, which needs Player Settings > Frame Timing Stats); how hot the phone
// is and whether Low Power Mode is on; the memory iOS counts against the app and how much more it may take before iOS
// closes it (both from Assets/Plugins/iOS); the battery; and what the home stage is doing. Between the rows, events: the
// launch reaching its first frame, the first robot on the stage, each robot swap and what it cost, each scene load,
// heat changes, memory warnings, the app going to the background and coming back.
//
// The file goes in the app's Documents folder, under Performance/, which the Files app shows as
// On My iPhone > RoboSimL > Performance (IosPlistPostProcessor is what makes that folder visible at all).
//
// A switch in Settings rather than a secret gesture: App Review treats a feature hidden from the reviewer as a
// guideline 2.3.1 problem, and the TestFlight build IS the build that gets submitted.
//
// Built in code at runtime, not in a scene, because it has to follow the app through every scene: it lives under
// DontDestroyOnLoad. With the switch off, none of it exists.
public class PerfOverlay : MonoBehaviour
{
    // The folder under Documents the logs go in. A folder of its own, so a log can never be mistaken for a robot file:
    // Submit a Robot lists only the top of Documents (RobotFilePicker).
    public const string LogFolder = "Performance";

    public const string HomeSceneName = "HomeScene";

    // Where the panel sits, in canvas units: the same 1920x1080, match-0.5 canvas every screen here uses, so these line
    // up with BuildDriveControls' and BuildHomeScene's numbers. A thin column in the home screen's top-left corner, 40 in
    // from both edges. In a game that corner holds L1 and L2 (40 in, two 72-unit buttons 20 apart, ending 204 down), so
    // there it steps straight down under them. It is thin because of the home title, which slides into the corner's row
    // as Settings opens: on a 4:3 iPad, the narrowest screen, its first letter stops about 232 in, and the panel ends at
    // 200. Validate Performance Overlay works both out from the scenes.
    public static readonly Vector2 HomePosition = new Vector2(40f, -40f);
    public static readonly Vector2 FieldPosition = new Vector2(40f, -228f);
    public const float PanelWidth = 160f;

    private const float FontSize = 22f;
    private const float Padding = 10f;
    private const int UiLayer = 5;
    // Above everything the app draws: the loading overlay, the sub-screens, a field's controls.
    private const int SortingOrder = 32767;
    private const double TickSeconds = 1.0;
    // A launch's slowest frame is looked for in the five seconds after its first frame, which is where the stutter of
    // shaders compiling for the first time shows up.
    private const double LaunchWindowSeconds = 5.0;
    // How long the launch's summary waits for the first robot to show before it is written without one.
    private const double LaunchRobotWaitSeconds = 30.0;
    private const DeviceStats.Thermal NoHeatYet = (DeviceStats.Thermal)(-2);
    private const int NoLowPowerYet = -2;

    // 60% navy, so what's behind shows through; at 85% it sat on the field as a black box. The worst case for the text is
    // the field's light tiles: this project blends UI in linear light, where a see-through dark panel comes out lighter
    // than the same alpha looks in an image editor.
    private static readonly Color PanelColor = new Color32(0x0B, 0x14, 0x2E, 0x99);
    private static readonly Color TextColor = new Color32(0xE8, 0xEE, 0xFB, 0xFF);

    private static PerfOverlay instance;
    // Unity's clock plus this is the time since the process started, which is when the player tapped the icon. Unity's
    // own clock starts later, once the engine is up, and would miss the start of every launch. Off a device the process's
    // age is unknown, and times count from this Play instead — in the editor Unity's clock runs from the editor's start.
    private static double clockOffset;
    private static double engineReadyAt = -1;

    public struct Parts
    {
        public Canvas canvas;
        public RectTransform panel;
        public TextMeshProUGUI labels;   // the left column: what each row is
        public TextMeshProUGUI values;   // the right column, right-aligned so the numbers line up
    }

    // Everything the panel prints, so the text can be made — and checked against the font — without a running app.
    public struct Reading
    {
        public double fps, cpuMainMs, gpuMs;
        public bool frameTiming;
        public DeviceStats.Thermal heat;
        public int lowPower;
        public double footprintMb;
    }

    // One row of the panel: what it is on the left, its value on the right.
    public struct Row
    {
        public string label, value;

        public Row(string label, string value)
        {
            this.label = label;
            this.value = value;
        }
    }

    private Parts parts;
    private readonly PerfWindow window = new PerfWindow();
    private readonly FrameTiming[] timings = new FrameTiming[1];
    private bool frameTiming;
    private StreamWriter writer;
    private bool sessionWritten;
    private bool startedAtLaunch;
    private bool quitting;
    private double nextTick;
    private bool skipFrame;
    private string sceneName = string.Empty;
    private string firstFramePending;
    private Scene currentScene;
    private RobotStageView stageView;

    private double launchFirstFrame = -1;
    private double launchRobot = -1;
    private double launchWorstMs;
    private double launchWindowEnd = -1;
    private bool launchWritten;
    private double loadRequestedAt = -1;
    private DeviceStats.Thermal lastHeat = NoHeatYet;
    private int lastLowPower = NoLowPowerYet;

    private static double Now => Time.realtimeSinceStartupAsDouble + clockOffset;

    // The Settings switch. On starts a new log and puts the panel up; off closes the log and takes the panel away.
    public static void SetShown(bool shown)
    {
        if (shown && instance == null) Create(atLaunch: false);
        else if (!shown && instance != null) Destroy(instance.gameObject);
    }

    public static bool IsShown => instance != null;

    // The store screenshot tool hides the panel while it captures, so no screenshot can carry it.
    public static bool HiddenForCapture
    {
        get => instance != null && instance.parts.canvas != null && !instance.parts.canvas.enabled;
        set
        {
            if (instance != null && instance.parts.canvas != null) instance.parts.canvas.enabled = !value;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlay()
    {
        instance = null;
        clockOffset = 0;
        engineReadyAt = -1;
    }

    // As early as the app runs, so a launch is measured from its very first frame when the switch was left on.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void StartWithApp()
    {
        double processAge = DeviceStats.SecondsSinceProcessStart;
        clockOffset = (processAge >= 0 ? processAge : 0) - Time.realtimeSinceStartupAsDouble;
        engineReadyAt = Now;
        if (PerformanceStatsSettings.Show) Create(atLaunch: true);
    }

    private static void Create(bool atLaunch)
    {
        var go = new GameObject("PerfOverlay");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<PerfOverlay>();
        instance.Begin(atLaunch);
    }

    private void Begin(bool atLaunch)
    {
        startedAtLaunch = atLaunch;
        parts = BuildUi(transform);
        frameTiming = FrameTimingManager.IsFeatureEnabled();
        nextTick = Time.realtimeSinceStartupAsDouble + TickSeconds;
        OpenLog();
        // Switched on from Settings, the scene it sits in is already running: entered now, without a first frame.
        Scene active = SceneManager.GetActiveScene();
        if (!atLaunch && active.IsValid() && active.isLoaded)
        {
            currentScene = active;
            EnterScene(active);
        }
    }

    void OnEnable()
    {
        PerfLog.Reported += OnReported;
        Application.lowMemory += OnLowMemory;
    }

    void OnDisable()
    {
        PerfLog.Reported -= OnReported;
        Application.lowMemory -= OnLowMemory;
    }

    void OnApplicationQuit()
    {
        quitting = true;
        Event("quit", string.Empty);
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (!quitting) Event("stats_off", string.Empty);
        CloseLog();
    }

    void OnApplicationPause(bool paused)
    {
        // Unity reports coming to the foreground as it starts up, before there is anything to measure.
        if (!sessionWritten) return;
        Event(paused ? "background" : "foreground", string.Empty);
        if (paused) return;
        // The first frame back carries all the time the app spent away. It isn't a slow frame.
        skipFrame = true;
        window.Clear();
        nextTick = Time.realtimeSinceStartupAsDouble + TickSeconds;
    }

    void Update()
    {
        // A new scene is noticed by looking, not through SceneManager.sceneLoaded: in the editor the scene Play starts in
        // never raises that event (seen 2026-09-13), and a load's first frame is this frame either way.
        Scene active = SceneManager.GetActiveScene();
        if (active != currentScene && active.isLoaded)
        {
            currentScene = active;
            EnterScene(active);
            firstFramePending = active.name;
        }

        if (!sessionWritten)
        {
            // Written on the first frame rather than at creation: by now every other launch-time setup has run, the
            // frame-rate cap included.
            sessionWritten = true;
            Event("session", SessionDetail());
        }

        double dt = Time.unscaledDeltaTime;
        if (skipFrame) skipFrame = false;
        else
        {
            window.AddFrame(dt);
            // Counted when the frame STARTED inside the window: the one that runs past its end is often the slowest.
            if (launchWindowEnd > 0 && Now - dt <= launchWindowEnd) launchWorstMs = Math.Max(launchWorstMs, dt * 1000.0);
        }

        if (frameTiming)
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
            {
                FrameTiming t = timings[0];
                window.AddTiming(t.frameStartTimestamp, t.cpuMainThreadFrameTime, t.cpuRenderThreadFrameTime, t.gpuFrameTime);
            }
        }

        if (Time.realtimeSinceStartupAsDouble >= nextTick) Tick();
    }

    void LateUpdate()
    {
        if (firstFramePending == null) return;
        string scene = firstFramePending;
        firstFramePending = null;
        FirstFrame(scene);
    }

    private void EnterScene(Scene scene)
    {
        sceneName = scene.name;
        parts.panel.anchoredPosition = sceneName == HomeSceneName ? HomePosition : FieldPosition;
        // For the log's stage column. Settings > Robot > Performance is where the stage is switched.
        stageView = FindAnyObjectByType<RobotStageView>(FindObjectsInactive.Include);
        Layout();
    }

    private void FirstFrame(string scene)
    {
        double now = Now;
        var detail = new StringBuilder("scene=").Append(scene);
        if (loadRequestedAt >= 0)
        {
            detail.Append(" load_s=").Append(Fmt(now - loadRequestedAt, "0.000"));
            loadRequestedAt = -1;
        }
        if (startedAtLaunch && launchFirstFrame < 0)
        {
            launchFirstFrame = now;
            launchWindowEnd = now + LaunchWindowSeconds;
        }
        Event("first_frame", detail.ToString());
    }

    private void OnReported(string name, string detail)
    {
        if (name == PerfLog.LoadRequested) loadRequestedAt = Now;
        else if (name == PerfLog.StageRobotShown && startedAtLaunch && launchRobot < 0) launchRobot = Now;
        Event(name, detail);
    }

    private void OnLowMemory() =>
        Event("low_memory", "footprint_mb=" + Fmt(Megabytes(DeviceStats.FootprintBytes), "0"));

    private void Tick()
    {
        nextTick = Time.realtimeSinceStartupAsDouble + TickSeconds;

        DeviceStats.Thermal heat = DeviceStats.ThermalState;
        if (heat != lastHeat)
        {
            if (lastHeat != NoHeatYet) Event("heat", HeatName(heat));
            lastHeat = heat;
        }
        int lowPower = DeviceStats.LowPowerMode;
        if (lowPower != lastLowPower)
        {
            if (lastLowPower != NoLowPowerYet) Event("low_power", lowPower == 1 ? "on" : "off");
            lastLowPower = lowPower;
        }
        double footprint = Megabytes(DeviceStats.FootprintBytes);
        double headroom = Megabytes(DeviceStats.AvailableBytes);

        // Summed up once the window has closed and the robot is up — or given up on: a launch that isn't on the home
        // screen, or a robot still not showing half a minute on.
        bool robotDone = launchRobot >= 0 || sceneName != HomeSceneName || Now > launchFirstFrame + LaunchRobotWaitSeconds;
        if (startedAtLaunch && !launchWritten && launchWindowEnd > 0 && Now > launchWindowEnd && robotDone)
        {
            launchWritten = true;
            Event("launch", $"first_frame_s={Fmt(launchFirstFrame, "0.000")} robot_s={(launchRobot >= 0 ? Fmt(launchRobot, "0.000") : "-")} " +
                            $"worst_frame_ms={Fmt(launchWorstMs, "0.0")} engine_ready_s={Fmt(engineReadyAt, "0.000")}");
        }

        Write(PerfCsv.TickRow(new PerfCsv.Tick
        {
            t = Now,
            scene = sceneName,
            fps = window.Fps,
            frameMs = window.AverageFrameMs,
            worstFrameMs = window.WorstFrameMs,
            cpuMainMs = window.CpuMainMs,
            cpuRenderMs = window.CpuRenderMs,
            gpuMs = window.GpuMs,
            worstGpuMs = window.WorstGpuMs,
            heat = heat == DeviceStats.Thermal.Unknown ? string.Empty : heat.ToString(),
            lowPower = lowPower,
            footprintMb = footprint,
            headroomMb = headroom,
            batteryPct = SystemInfo.batteryLevel * 100.0,
            power = SystemInfo.batteryStatus == BatteryStatus.Unknown ? string.Empty : SystemInfo.batteryStatus.ToString(),
            stage = StageName(),
        }));

        List<Row> rows = ReadoutRows(new Reading
        {
            fps = window.Fps,
            cpuMainMs = window.CpuMainMs,
            gpuMs = window.GpuMs,
            frameTiming = frameTiming,
            heat = heat,
            lowPower = lowPower,
            footprintMb = footprint,
        });
        parts.labels.text = Column(rows, values: false);
        parts.values.text = Column(rows, values: true);
        Layout();
        window.Clear();
    }

    // --- The home stage ---

    // What the stage was doing that second, for the log's stage column. Settings > Robot > Performance switches it
    // (HomeStageSettings) and reports each change through PerfLog.StageModeChanged.
    private string StageName()
    {
        if (stageView == null) return string.Empty;
        return stageView.isActiveAndEnabled ? stageView.mode.ToString() : "hidden";
    }

    // --- The panel ---

    private void Layout()
    {
        float textHeight = Mathf.Ceil(Mathf.Max(parts.labels.preferredHeight, parts.values.preferredHeight));
        var column = new Vector2(PanelWidth - Padding * 2f, textHeight);
        parts.labels.rectTransform.sizeDelta = column;
        parts.values.rectTransform.sizeDelta = column;
        parts.panel.sizeDelta = new Vector2(PanelWidth, Padding * 2f + textHeight);
    }

    // The TextMesh Pro default, LiberationSans, whose digits are all one width — the numbers change every second and
    // shouldn't shuffle sideways as they do. Its glyphs are baked in advance, so the panel prints ASCII only.
    public static TMP_FontAsset Font => TMP_Settings.defaultFontAsset;

    public static Parts BuildUi(Transform root)
    {
        var parts = new Parts();
        GameObject go = root.gameObject;
        go.layer = UiLayer;
        parts.canvas = go.AddComponent<Canvas>();
        parts.canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        parts.canvas.sortingOrder = SortingOrder;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        // No GraphicRaycaster: nothing on this canvas can take a touch, so the panel never gets in the way of what it sits
        // over — a field's controls, the home screen's robot, Settings.

        parts.panel = NewRect("PerfPanel", go.transform);
        parts.panel.anchorMin = parts.panel.anchorMax = parts.panel.pivot = new Vector2(0f, 1f);
        parts.panel.anchoredPosition = HomePosition;
        parts.panel.sizeDelta = new Vector2(PanelWidth, 200f);
        Image background = parts.panel.gameObject.AddComponent<Image>();
        background.color = PanelColor;
        background.raycastTarget = false;

        parts.labels = NewText(NewColumn("PerfLabels", parts.panel), TextAlignmentOptions.TopLeft);
        parts.values = NewText(NewColumn("PerfValues", parts.panel), TextAlignmentOptions.TopRight);
        parts.labels.text = "Measuring...";
        parts.values.text = string.Empty;
        return parts;
    }

    // One of the panel's two columns. Both fill the same inset box; one text is aligned left and the other right.
    private static RectTransform NewColumn(string name, RectTransform panel)
    {
        RectTransform rect = NewRect(name, panel);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(Padding, -Padding);
        rect.sizeDelta = new Vector2(PanelWidth - Padding * 2f, 150f);
        return rect;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = UiLayer;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private static TextMeshProUGUI NewText(RectTransform rect, TextAlignmentOptions alignment)
    {
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = Font;
        text.fontSize = FontSize;
        text.color = TextColor;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        return text;
    }

    // --- What it says ---

    // A row a stat, short label left and value right, so it fits the thin column: how smoothly it runs, what a frame
    // costs the CPU and the GPU, how hot the phone is, the memory the app uses, and a Low Power row while that mode is
    // on. The labels are short because the column is: a longer one would push the panel into the home title's way on an
    // iPad. Everything else is in the log (2026-09-13). So is the slowest frame of each second (worst_frame_ms): its
    // row, Worst, went the same day, because a label that says what it measures doesn't fit, and without one it meant
    // nothing.
    public static List<Row> ReadoutRows(Reading r)
    {
        var rows = new List<Row>
        {
            new Row("FPS", Fmt(r.fps, "0")),
            new Row("CPU", r.frameTiming ? Duration(r.cpuMainMs) : "off"),
            new Row("GPU", r.frameTiming ? Duration(r.gpuMs) : "off"),
            new Row("Heat", r.heat == DeviceStats.Thermal.Unknown ? "n/a" : HeatName(r.heat)),
            new Row("RAM", r.footprintMb >= 0 ? Memory(r.footprintMb) : "n/a"),
        };
        if (r.lowPower == 1) rows.Add(new Row("Low Power", string.Empty));
        return rows;
    }

    // One of the panel's columns: every row's label, or every row's value, a line each.
    public static string Column(List<Row> rows, bool values)
    {
        var text = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) text.Append('\n');
            text.Append(values ? rows[i].value : rows[i].label);
        }
        return text.ToString();
    }

    // A time never more than seven characters, so it always fits its column: 6.1 ms, 123 ms, 1.2 s, 123 s. The decimal
    // goes at three digits, in every unit.
    public static string Duration(double ms)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0) return "-";
        if (ms < 99.95) return ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        if (ms < 999.5) return ms.ToString("0", CultureInfo.InvariantCulture) + " ms";
        double seconds = ms / 1000.0;
        return seconds.ToString(seconds < 99.95 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
    }

    // The same for memory: 412 MB, 1.4 GB, 121 GB.
    public static string Memory(double mb)
    {
        if (mb < 999.5) return mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
        double gb = mb / 1024.0;
        return gb.ToString(gb < 99.95 ? "0.0" : "0", CultureInfo.InvariantCulture) + " GB";
    }

    public static string HeatName(DeviceStats.Thermal heat) =>
        heat == DeviceStats.Thermal.Unknown ? string.Empty : heat.ToString();

    private static string Fmt(double value, string format) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString(format, CultureInfo.InvariantCulture);

    private static double Megabytes(long bytes) => bytes < 0 ? -1 : bytes / (1024.0 * 1024.0);

    // --- The file ---

    private void OpenLog()
    {
        try
        {
            string folder = Path.Combine(Application.persistentDataPath, LogFolder);
            Directory.CreateDirectory(folder);
            string stem = "perf-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, stem + ".csv");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}-{n}.csv");
            writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine(PerfCsv.Header());
            writer.Flush();
        }
        catch (Exception e)
        {
            CloseLog();
            Debug.LogWarning("[PerfOverlay] Not recording to a file: " + e.Message);
        }
    }

    private string SessionDetail() =>
        $"app={Application.version} device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} " +
        $"screen={Screen.width}x{Screen.height} refresh_hz={Fmt(Screen.currentResolution.refreshRateRatio.value, "0")} " +
        $"frame_cap={Application.targetFrameRate} frame_timing={(frameTiming ? "on" : "off")} " +
        $"started={(startedAtLaunch ? "at_launch" : "from_settings")} engine_ready_s={Fmt(engineReadyAt, "0.000")}";

    private void Event(string name, string detail) => Write(PerfCsv.EventRow(Now, sceneName, name, detail));

    // Flushed on every line: iOS closes a backgrounded app without a word, and a crash is exactly the run worth having.
    private void Write(string line)
    {
        if (writer == null) return;
        try
        {
            writer.WriteLine(line);
            writer.Flush();
        }
        catch (Exception e)
        {
            CloseLog();
            Debug.LogWarning("[PerfOverlay] Not recording to a file: " + e.Message);
        }
    }

    private void CloseLog()
    {
        if (writer == null) return;
        try { writer.Dispose(); }
        catch (Exception) { /* the file is as complete as it is going to get */ }
        writer = null;
    }
}
