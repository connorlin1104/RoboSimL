using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Captures App Store screenshots. One press takes the moment on screen at both sizes this app's listing needs,
// renders each size for real, saves them without an alpha channel and numbers them as a pair.
//
// No device or Xcode simulator is needed, because nothing in this project reads Screen.safeArea. The UI is a single
// ScaleWithScreenSize canvas (1920x1080 reference, match 0.5), so what it lays out is a pure function of the render
// resolution — set the Game view to 2752x2064 and you get the pixels an iPad renders. That is a real difference, not a
// stretch: at 4:3 the match-0.5 scaler picks a different scale factor than at the phone's 19.5:9, so a phone shot
// resized to iPad dimensions would show a layout the app never draws. That is why each size is rendered, never resized.
//
// What it can't reproduce is the OS drawing rounded corners and the home indicator over the app. Apple doesn't want
// those in a screenshot, so it doesn't matter.
//
// IF THAT SAFE-AREA ASSUMPTION EVER STOPS HOLDING — someone adds Screen.safeArea handling, or a notch inset — the Game
// view stops matching the device and these shots become wrong in a way that still looks plausible. Capture on hardware
// at that point.
//
// One press:
//   1. Freezes the game where it is. Time.timeScale 0 stops physics and everything else that runs on game time, and the
//      home stage is held Still so its robot stops turning. A paused editor is let run on, still frozen at time scale 0,
//      because a new size is only laid out on a frame that runs.
//   2. For each size, picks the Game view's own entry of that size and waits for the view to report it, then for a few
//      frames more: the canvas lays itself out again, and the home stage makes a texture the new shape and draws it.
//      Then captures. Only a size the Game view has no entry for gets one of this tool's (ResolutionName).
//   3. Writes each capture again as plain RGB. Unity's capture keeps an alpha channel, and App Store Connect refuses any
//      PNG that has one — even one that is opaque everywhere.
//   4. Puts back the Game view's size, the time scale, the stage, the performance overlay and the pause.
//
// The sizes are 2778x1284 (iPhone 6.5") and 2752x2064 (iPad 13"). Not 2868x1320 (iPhone 6.9"): App Store Connect didn't
// take it for 1.0 (Connor, 2026-09-13), and what went up then was a 2778x1284 resize with the alpha stripped by hand —
// the two steps this tool now does itself.
public static class StoreScreenshotCapture
{
    // Beside Assets/, deliberately not inside it, so Unity doesn't import multi-megabyte PNGs as textures and drag them
    // into the build. Ignored by git.
    internal const string OutputDir = "StoreScreenshots";

    // Every size App Store Connect gets for this app, which ships for iPhone and iPad (targetDevice 2). Apple scales the
    // set in each family down for that family's smaller screens, so one set per family is the whole job.
    internal static readonly (string Label, int Width, int Height)[] Sizes =
    {
        ("iPhone 6.5\"", 2778, 1284),
        ("iPad 13\"", 2752, 2064),
    };

    // PNG colour types, from the IHDR chunk: plain RGB, and RGB with an alpha channel.
    internal const int PngRgb = 2;
    internal const int PngRgba = 6;

    // Frames to let run at a new size before capturing, counted from the frame the Game view first reports it. The
    // canvas lays itself out on the first; the home stage makes a new texture and draws it twice before it shows it.
    // Twelve leaves plenty spare, and being frames rather than seconds, a slow editor simply waits longer.
    private const int SettleFrames = 12;

    // The capture gives up — and puts everything back — when the game draws no new frame for this long, in seconds.
    // Slow frames are fine; a slow editor simply takes longer. A step also gives up after six times this in all: a Game
    // view that never takes the size, a capture that is never written.
    private const double StepTimeout = 20.0;

    // The one Game view entry this tool adds, and only for a size the Game view has no entry of its own for. Unity lists
    // 2778x1284 itself (the iPhone 12 Pro Max) and an entry of 2752x2064 added by hand is used as it is, so on a set-up
    // editor a capture leaves the list as it found it. It used to set this entry for every capture and leave it in the
    // list (Connor, 2026-09-13: "i didn't add them").
    private const string ResolutionName = "RoboSim Store";

    private static Session active;

    [MenuItem("Tools/RoboSim/Utilities/Capture Store Screenshots %#s", false, 10)]
    private static void CaptureFromMenu() => Begin(OutputDir, null);

    // Starts a capture. `done` hears the files saved, or null and why not, once everything has been put back.
    internal static void Begin(string outputDir, Action<List<string>, string> done)
    {
        if (!EditorApplication.isPlaying)
        {
            // Outside Play mode the Game view holds a stale frame or none, and a capture would succeed with the wrong
            // picture in it.
            const string why = "Enter Play mode first — the capture takes what the game is drawing.";
            Debug.LogWarning("[Screenshots] " + why);
            done?.Invoke(null, why);
            return;
        }
        if (active != null)
        {
            Debug.LogWarning("[Screenshots] Already capturing. Wait for it to finish.");
            done?.Invoke(null, "already capturing");
            return;
        }
        active = new Session(outputDir, done);
        active.Start();
    }

    private sealed class Session
    {
        private enum Phase { Resizing, Settling, Capturing }

        private readonly string outputDir;
        private readonly Action<List<string>, string> done;
        private readonly List<string> saved = new List<string>();
        private readonly int shot;
        private int sizeIndex;
        private Phase phase;
        private double phaseStarted;
        private int settleFrom;
        private string pendingPath;
        private int lastFrame;
        private double lastFrameAt;

        // What gets put back.
        private float timeScale;
        private bool wasPaused;
        private bool runInBackground;
        private PlayModeWindow.PlayModeViewTypes viewType;
        private uint width;
        private uint height;
        private int sizeSelection = -1;
        private RobotStageView stageView;
        private RobotStageView.StageMode stageMode;

        public Session(string outputDir, Action<List<string>, string> done)
        {
            this.outputDir = outputDir;
            this.done = done;
            Directory.CreateDirectory(outputDir);
            var names = new List<string>();
            foreach (string path in Directory.GetFiles(outputDir)) names.Add(Path.GetFileName(path));
            shot = NextShotNumber(names);
        }

        public void Start()
        {
            timeScale = Time.timeScale;
            wasPaused = EditorApplication.isPaused;
            // The sizes are the Game view's to set. The Device Simulator, if that is what's showing, comes back after.
            viewType = PlayModeWindow.GetViewType();
            if (viewType != PlayModeWindow.PlayModeViewTypes.GameView)
                PlayModeWindow.SetViewType(PlayModeWindow.PlayModeViewTypes.GameView);
            FocusGameView();
            PlayModeWindow.GetRenderingResolution(out width, out height);
            sizeSelection = GameViewSizeIndex();

            Time.timeScale = 0f;
            // Unity stops the game while it isn't the app in front, unless Run In Background is on — and a capture left
            // to run while you look at something else would then wait for frames that never come.
            runInBackground = Application.runInBackground;
            Application.runInBackground = true;
            stageView = Object.FindAnyObjectByType<RobotStageView>();
            if (stageView != null)
            {
                stageMode = stageView.mode;
                if (stageMode == RobotStageView.StageMode.Drift) stageView.SetMode(RobotStageView.StageMode.Still);
            }
            PerfOverlay.HiddenForCapture = true;
            if (wasPaused) EditorApplication.isPaused = false;

            EditorApplication.update += Step;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Resize(0);
        }

        private void Resize(int index)
        {
            sizeIndex = index;
            (string _, int w, int h) = Sizes[index];
            if (!SelectGameViewEntry(w, h)) PlayModeWindow.SetCustomRenderingResolution((uint)w, (uint)h, ResolutionName);
            Enter(Phase.Resizing);
        }

        private void Enter(Phase next)
        {
            phase = next;
            phaseStarted = EditorApplication.timeSinceStartup;
            lastFrame = Time.frameCount;
            lastFrameAt = phaseStarted;
        }

        private void Step()
        {
            try
            {
                Advance();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Finish(e.Message);
            }
        }

        private void Advance()
        {
            (string Label, int Width, int Height) size = Sizes[sizeIndex];
            double now = EditorApplication.timeSinceStartup;
            if (Time.frameCount != lastFrame)
            {
                lastFrame = Time.frameCount;
                lastFrameAt = now;
            }
            if (now - lastFrameAt > StepTimeout || now - phaseStarted > StepTimeout * 6)
            {
                Finish(Stuck(size));
                return;
            }

            switch (phase)
            {
                case Phase.Resizing:
                    PlayModeWindow.GetRenderingResolution(out uint w, out uint h);
                    if (w != size.Width || h != size.Height) return;
                    settleFrom = Time.frameCount;
                    Enter(Phase.Settling);
                    return;

                case Phase.Settling:
                    if (Time.frameCount - settleFrom < SettleFrames) return;
                    if (stageView != null && stageView.isActiveAndEnabled && !stageView.Settled) return;
                    pendingPath = Path.Combine(outputDir, "pending.png");
                    if (File.Exists(pendingPath)) File.Delete(pendingPath);
                    // Written at the end of the frame, on a thread this doesn't control; Capturing waits for it.
                    ScreenCapture.CaptureScreenshot(pendingPath);
                    Enter(Phase.Capturing);
                    return;

                case Phase.Capturing:
                    if (!PngComplete(pendingPath)) return;
                    Save(size);
                    if (sizeIndex + 1 < Sizes.Length) Resize(sizeIndex + 1);
                    else Finish(null);
                    return;
            }
        }

        private string Stuck((string Label, int Width, int Height) size)
        {
            switch (phase)
            {
                case Phase.Resizing:
                    PlayModeWindow.GetRenderingResolution(out uint w, out uint h);
                    return $"The Game view never went to {size.Width}x{size.Height}; it is at {w}x{h}. " +
                           "Is a Game view open (Window > General > Game)?";
                case Phase.Settling:
                    return $"The game drew no new frame for {StepTimeout:0} s, or the home stage never finished drawing at the new size.";
                default:
                    return $"The capture was never written to {pendingPath}.";
            }
        }

        private void Save((string Label, int Width, int Height) size)
        {
            byte[] rgb = WithoutAlpha(File.ReadAllBytes(pendingPath), out int w, out int h);
            if (w != size.Width || h != size.Height)
                throw new InvalidOperationException(
                    $"The {size.Label} capture came out {w}x{h}, not {size.Width}x{size.Height}. Set the Game view's Scale " +
                    "slider to 1x or lower (above 1x Unity renders at the window's size) and capture again.");
            if (!TryReadPngHeader(rgb, out _, out _, out int colourType) || colourType != PngRgb)
                throw new InvalidOperationException(
                    $"The {size.Label} capture is still PNG colour type {colourType} after taking its alpha out.");

            string path = Path.Combine(outputDir, FileName(size, shot));
            File.WriteAllBytes(path, rgb);
            File.Delete(pendingPath);
            saved.Add(path);
        }

        private void Finish(string failure)
        {
            EditorApplication.update -= Step;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            active = null;
            try
            {
                if (pendingPath != null && File.Exists(pendingPath)) File.Delete(pendingPath);
            }
            catch (IOException) { /* a stray pending.png is harmless; the next capture replaces it */ }
            Restore();

            if (failure == null)
                Debug.Log($"[Screenshots] Shot {shot:00}, both sizes: {string.Join(", ", saved)}");
            else
                Debug.LogError("[Screenshots] " + failure +
                               (saved.Count > 0 ? " Saved before it stopped: " + string.Join(", ", saved) : string.Empty));
            done?.Invoke(failure == null ? saved : null, failure);
        }

        private void Restore()
        {
            if (!SelectGameViewSize(sizeSelection) && !SelectGameViewEntry((int)width, (int)height))
            {
                // The same pixels, under this tool's entry: as good as the entry it found for everything but Free Aspect,
                // which follows the window.
                PlayModeWindow.SetCustomRenderingResolution(width, height, ResolutionName);
                Debug.Log($"[Screenshots] Left the Game view at {width}x{height}, the size it was at, under {ResolutionName}.");
            }
            if (viewType != PlayModeWindow.PlayModeViewTypes.GameView) PlayModeWindow.SetViewType(viewType);
            Application.runInBackground = runInBackground;
            if (!EditorApplication.isPlaying) return;
            Time.timeScale = timeScale;
            if (stageView != null) stageView.SetMode(stageMode);
            PerfOverlay.HiddenForCapture = false;
            if (wasPaused) EditorApplication.isPaused = true;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode) Finish("Play mode ended before both sizes were captured.");
        }
    }

    // --- The files ---

    // The capture written again as 8-bit RGB. What the screen shows is the colour. The alpha ScreenCapture keeps belongs
    // to the render target and no display uses it; App Store Connect refuses a PNG with an alpha channel at all.
    internal static byte[] WithoutAlpha(byte[] png, out int width, out int height)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Texture2D opaque = null;
        try
        {
            if (!source.LoadImage(png)) throw new InvalidOperationException("The capture is not a PNG Unity can read.");
            width = source.width;
            height = source.height;
            opaque = new Texture2D(width, height, TextureFormat.RGB24, false);
            opaque.SetPixels32(source.GetPixels32());
            opaque.Apply(false);
            return opaque.EncodeToPNG();
        }
        finally
        {
            Object.DestroyImmediate(source);
            if (opaque != null) Object.DestroyImmediate(opaque);
        }
    }

    // PNG keeps its size and colour type at fixed places in the IHDR chunk, which always comes first: width and height
    // as big-endian ints at 16 and 20, the colour type at 25.
    internal static bool TryReadPngHeader(byte[] png, out int width, out int height, out int colourType)
    {
        width = height = colourType = 0;
        if (png == null || png.Length < 26) return false;
        if (png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') return false;
        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        colourType = png[25];
        return width > 0 && height > 0;
    }

    // Every PNG ends with the IEND chunk, so a file that doesn't end with it isn't finished being written.
    private static readonly byte[] PngEnd = { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

    private static bool PngComplete(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length < 57) return false; // the signature, IHDR and IEND take that much at least
                stream.Seek(-PngEnd.Length, SeekOrigin.End);
                var tail = new byte[PngEnd.Length];
                if (stream.Read(tail, 0, tail.Length) != tail.Length) return false;
                for (int i = 0; i < tail.Length; i++)
                    if (tail[i] != PngEnd[i]) return false;
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    // One number for both sizes of a shot, so the two files of one moment share it, and the files of one size sort in
    // the order they were taken: one past the highest number either size already uses.
    internal static int NextShotNumber(IEnumerable<string> fileNames)
    {
        int highest = 0;
        foreach (string name in fileNames)
        {
            foreach ((string Label, int Width, int Height) size in Sizes)
            {
                Match match = Regex.Match(name, "^" + Regex.Escape(Slug(size)) + @"-(\d+)\.png$");
                if (match.Success)
                    highest = Math.Max(highest, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }
        return highest + 1;
    }

    // "iPhone-6.5-2778x1284": the device, then the size, which is what App Store Connect's upload slots are sorted by.
    internal static string Slug((string Label, int Width, int Height) size) =>
        $"{size.Label.Replace("\"", string.Empty).Replace(" ", "-")}-{size.Width}x{size.Height}";

    internal static string FileName((string Label, int Width, int Height) size, int shot) =>
        $"{Slug(size)}-{shot.ToString("00", CultureInfo.InvariantCulture)}.png";

    // A Game view hidden behind another tab draws nothing, and the capture would wait for frames that never come.
    private static void FocusGameView()
    {
        Type type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        if (type != null) EditorWindow.FocusWindowIfItsOpen(type);
    }

    // --- The Game view's own size entry ---

    // Read and set through reflection, because Unity doesn't publish it: that is what lets the capture use the Game view's
    // own entries and put back the very entry it found, Free Aspect included, which no fixed size can stand in for. If
    // Unity renames it, capturing still works: each size goes through this tool's entry, and the view is left at the size
    // it was at.
    private static int GameViewSizeIndex()
    {
        try
        {
            PropertyInfo property = SizeIndexProperty(out EditorWindow view);
            return property != null ? (int)property.GetValue(view) : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static bool SelectGameViewSize(int index)
    {
        if (index < 0) return false;
        try
        {
            PropertyInfo property = SizeIndexProperty(out EditorWindow view);
            if (property == null) return false;
            // The menu's own callback where there is one: it does everything picking the entry by hand does.
            MethodInfo pick = view.GetType().GetMethod("SizeSelectionCallback",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(object) }, null);
            if (pick != null) pick.Invoke(view, new object[] { index, null });
            else property.SetValue(view, index);
            view.Repaint();
            return (int)property.GetValue(view) == index;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static PropertyInfo SizeIndexProperty(out EditorWindow view)
    {
        view = null;
        Type type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        if (type == null) return null;
        Object[] views = Resources.FindObjectsOfTypeAll(type);
        if (views.Length == 0) return null;
        view = (EditorWindow)views[0];
        for (Type t = type; t != null; t = t.BaseType)
        {
            PropertyInfo property = t.GetProperty("selectedSizeIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.PropertyType == typeof(int) && property.CanRead && property.CanWrite)
                return property;
        }
        return null;
    }

    // The Game view's entry of exactly this size, selected; false if it has none, or Unity's internals have moved.
    private static bool SelectGameViewEntry(int width, int height)
    {
        int index = GameViewEntryOfSize(width, height);
        return index >= 0 && SelectGameViewSize(index);
    }

    // Where in the Game view's list an entry of exactly this size is, or -1. The first fixed-resolution match wins: any
    // entry of the right size renders the same pixels.
    internal static int GameViewEntryOfSize(int width, int height)
    {
        foreach ((int index, bool fixedSize, int w, int h, string _) in GameViewEntries())
            if (fixedSize && w == width && h == height) return index;
        return -1;
    }

    // The Game view's list of sizes, in its order; empty if Unity's internals have moved. It is Unity's GameViewSizes,
    // which isn't published either: one group per platform, Unity's own sizes first and then the ones added by hand.
    internal static List<(int Index, bool Fixed, int Width, int Height, string Name)> GameViewEntries()
    {
        var entries = new List<(int Index, bool Fixed, int Width, int Height, string Name)>();
        try
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type sizesType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameViewSizes");
            if (sizesType == null) return entries;
            object sizes = typeof(ScriptableSingleton<>).MakeGenericType(sizesType)
                .GetProperty("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object group = sizes == null ? null : sizesType.GetProperty("currentGroup", any)?.GetValue(sizes);
            if (group == null) return entries;
            MethodInfo count = group.GetType().GetMethod("GetTotalCount", any, null, Type.EmptyTypes, null);
            MethodInfo get = group.GetType().GetMethod("GetGameViewSize", any, null, new[] { typeof(int) }, null);
            if (count == null || get == null) return entries;
            int total = (int)count.Invoke(group, null);
            for (int i = 0; i < total; i++)
            {
                object size = get.Invoke(group, new object[] { i });
                Type type = size.GetType();
                entries.Add((i,
                    Convert.ToString(type.GetProperty("sizeType", any)?.GetValue(size)) == "FixedResolution",
                    type.GetProperty("width", any)?.GetValue(size) is int w ? w : -1,
                    type.GetProperty("height", any)?.GetValue(size) is int h ? h : -1,
                    Convert.ToString(type.GetProperty("baseText", any)?.GetValue(size))));
            }
        }
        catch (Exception)
        {
            entries.Clear();
        }
        return entries;
    }
}
