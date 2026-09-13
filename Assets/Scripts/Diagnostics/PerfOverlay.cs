using System;
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
    // up with BuildDriveControls' numbers. The home screen's top-left corner is empty. On a field that corner holds L1
    // and L2 — 40 in from the corner, two 72-unit buttons 20 apart — so there the panel starts under them.
    public static readonly Vector2 HomePosition = new Vector2(40f, -40f);
    public static readonly Vector2 FieldPosition = new Vector2(40f, -228f);
    public const float PanelWidth = 560f;

    private const float FontSize = 24f;
    private const float Padding = 14f;
    private const float ButtonWidth = 240f;
    private const float ButtonHeight = 56f;
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

    private static readonly Color PanelColor = new Color32(0x0B, 0x14, 0x2E, 0xD9);
    private static readonly Color TextColor = new Color32(0xE8, 0xEE, 0xFB, 0xFF);
    private static readonly Color ButtonColor = new Color32(0x0E, 0xA5, 0xE9, 0xFF);

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
        public TextMeshProUGUI readout;
        public Button stageButton;
        public TextMeshProUGUI stageLabel;
    }

    // Everything the panel prints, so the text can be made — and checked against the font — without a running app.
    public struct Reading
    {
        public double fps, worstFrameMs, cpuMainMs, gpuMs;
        public bool frameTiming;
        public DeviceStats.Thermal heat;
        public int lowPower;
        public double footprintMb, headroomMb;
        public string launchLine, loadLine, logName, logError;
    }

    private Parts parts;
    private readonly PerfWindow window = new PerfWindow();
    private readonly FrameTiming[] timings = new FrameTiming[1];
    private bool frameTiming;
    private StreamWriter writer;
    private string logName = string.Empty;
    private string logError = string.Empty;
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
    private string loadLine = string.Empty;
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
        parts.stageButton.onClick.AddListener(CycleStageMode);
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
        stageView = FindAnyObjectByType<RobotStageView>(FindObjectsInactive.Include);
        RefreshStageButton();
        Layout();
    }

    private void FirstFrame(string scene)
    {
        double now = Now;
        var detail = new StringBuilder("scene=").Append(scene);
        if (loadRequestedAt >= 0)
        {
            double seconds = now - loadRequestedAt;
            detail.Append(" load_s=").Append(Fmt(seconds, "0.000"));
            loadLine = FormatLoad(scene, seconds);
            loadRequestedAt = -1;
        }
        else
        {
            loadLine = string.Empty;
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

        parts.readout.text = FormatReadout(new Reading
        {
            fps = window.Fps,
            worstFrameMs = window.WorstFrameMs,
            cpuMainMs = window.CpuMainMs,
            gpuMs = window.GpuMs,
            frameTiming = frameTiming,
            heat = heat,
            lowPower = lowPower,
            footprintMb = footprint,
            headroomMb = headroom,
            launchLine = sceneName == HomeSceneName && startedAtLaunch && launchFirstFrame >= 0
                ? FormatLaunch(launchFirstFrame, launchRobot, launchWorstMs, Now > launchWindowEnd)
                : string.Empty,
            loadLine = loadLine,
            logName = logName,
            logError = logError,
        });
        RefreshStageButton();
        Layout();
        window.Clear();
    }

    // --- The home stage ---

    private void RefreshStageButton()
    {
        bool shown = stageView != null && stageView.isActiveAndEnabled;
        if (parts.stageButton.gameObject.activeSelf != shown) parts.stageButton.gameObject.SetActive(shown);
        if (shown) parts.stageLabel.text = StageButtonText(stageView.mode);
    }

    // Drift, Still, Off and round again. What the stage costs is the difference between the rows logged in each.
    private void CycleStageMode()
    {
        if (stageView == null) return;
        RobotStageView.StageMode next = stageView.mode switch
        {
            RobotStageView.StageMode.Drift => RobotStageView.StageMode.Still,
            RobotStageView.StageMode.Still => RobotStageView.StageMode.Off,
            _ => RobotStageView.StageMode.Drift,
        };
        stageView.SetMode(next);
        Event("stage_mode", next.ToString());
        RefreshStageButton();
    }

    private string StageName()
    {
        if (stageView == null) return string.Empty;
        return stageView.isActiveAndEnabled ? stageView.mode.ToString() : "hidden";
    }

    // --- The panel ---

    private void Layout()
    {
        float textHeight = Mathf.Ceil(parts.readout.preferredHeight);
        parts.readout.rectTransform.sizeDelta = new Vector2(PanelWidth - Padding * 2f, textHeight);
        float height = Padding * 2f + textHeight;
        if (parts.stageButton.gameObject.activeSelf) height += Padding + ButtonHeight;
        parts.panel.sizeDelta = new Vector2(PanelWidth, height);
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
        go.AddComponent<GraphicRaycaster>();

        parts.panel = NewRect("PerfPanel", go.transform);
        parts.panel.anchorMin = parts.panel.anchorMax = parts.panel.pivot = new Vector2(0f, 1f);
        parts.panel.anchoredPosition = HomePosition;
        parts.panel.sizeDelta = new Vector2(PanelWidth, 200f);
        Image background = parts.panel.gameObject.AddComponent<Image>();
        background.color = PanelColor;
        // A touch on the panel goes through to whatever is under it. Only the stage button takes one.
        background.raycastTarget = false;

        RectTransform text = NewRect("PerfReadout", parts.panel);
        text.anchorMin = text.anchorMax = text.pivot = new Vector2(0f, 1f);
        text.anchoredPosition = new Vector2(Padding, -Padding);
        text.sizeDelta = new Vector2(PanelWidth - Padding * 2f, 150f);
        parts.readout = NewText(text, TextAlignmentOptions.TopLeft);
        parts.readout.text = "Measuring...";

        RectTransform button = NewRect("PerfStageButton", parts.panel);
        button.anchorMin = button.anchorMax = button.pivot = new Vector2(0f, 0f);
        button.anchoredPosition = new Vector2(Padding, Padding);
        button.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
        Image buttonImage = button.gameObject.AddComponent<Image>();
        buttonImage.color = ButtonColor;
        parts.stageButton = button.gameObject.AddComponent<Button>();
        parts.stageButton.targetGraphic = buttonImage;

        RectTransform label = NewRect("PerfStageLabel", button);
        label.anchorMin = Vector2.zero;
        label.anchorMax = Vector2.one;
        label.offsetMin = label.offsetMax = Vector2.zero;
        parts.stageLabel = NewText(label, TextAlignmentOptions.Center);
        parts.stageLabel.color = Color.white;
        parts.stageLabel.text = StageButtonText(RobotStageView.StageMode.Drift);
        button.gameObject.SetActive(false);
        return parts;
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

    public static string StageButtonText(RobotStageView.StageMode mode) => "Stage: " + mode;

    public static string FormatReadout(Reading r)
    {
        var text = new StringBuilder();
        text.Append(Fmt(r.fps, "0")).Append(" fps");
        if (r.frameTiming)
            text.Append("   CPU ").Append(Fmt(r.cpuMainMs, "0.0")).Append(" ms   GPU ").Append(Fmt(r.gpuMs, "0.0")).Append(" ms");
        else
            text.Append("   CPU and GPU: Frame Timing Stats is off");

        text.Append('\n').Append("Slowest frame ").Append(Fmt(r.worstFrameMs, "0")).Append(" ms   Heat ")
            .Append(r.heat == DeviceStats.Thermal.Unknown ? "n/a" : HeatName(r.heat));
        if (r.lowPower == 1) text.Append("   Low Power Mode");

        text.Append('\n');
        if (r.footprintMb >= 0) text.Append("Memory ").Append(Fmt(r.footprintMb, "0")).Append(" MB");
        else text.Append("Memory: measured on a phone only");
        if (r.headroomMb >= 0) text.Append(", ").Append(Fmt(r.headroomMb, "0")).Append(" MB to spare");

        if (!string.IsNullOrEmpty(r.launchLine)) text.Append('\n').Append(r.launchLine);
        if (!string.IsNullOrEmpty(r.loadLine)) text.Append('\n').Append(r.loadLine);
        text.Append('\n').Append(string.IsNullOrEmpty(r.logError)
            ? "Recording " + Ascii(r.logName)
            : "Not recording: " + Ascii(r.logError));
        return text.ToString();
    }

    public static string FormatLaunch(double firstFrame, double robot, double worstMs, bool windowClosed)
    {
        var line = new StringBuilder("Launch ").Append(Fmt(firstFrame, "0.00")).Append(" s");
        if (robot >= 0) line.Append("   robot ").Append(Fmt(robot, "0.00")).Append(" s");
        if (windowClosed) line.Append("   slowest ").Append(Fmt(worstMs, "0")).Append(" ms");
        return line.ToString();
    }

    public static string FormatLoad(string scene, double seconds) =>
        "Loaded " + Ascii(scene) + " in " + Fmt(seconds, "0.00") + " s";

    public static string HeatName(DeviceStats.Thermal heat) =>
        heat == DeviceStats.Thermal.Unknown ? string.Empty : heat.ToString();

    private static string Fmt(double value, string format) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString(format, CultureInfo.InvariantCulture);

    private static double Megabytes(long bytes) => bytes < 0 ? -1 : bytes / (1024.0 * 1024.0);

    // Anything outside the font's baked glyphs would draw as an empty box: an error message in the phone's own
    // language, a file name with an accent in it.
    public static string Ascii(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = new StringBuilder(value.Length);
        foreach (char c in value) text.Append(c >= ' ' && c <= '~' ? c : '?');
        return text.ToString();
    }

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
            logName = Path.GetFileName(path);
            writer.WriteLine(PerfCsv.Header());
            writer.Flush();
        }
        catch (Exception e)
        {
            CloseLog();
            logError = e.Message;
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
            logError = e.Message;
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
