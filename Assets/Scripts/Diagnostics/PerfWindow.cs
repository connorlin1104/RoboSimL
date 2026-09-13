using System.Globalization;
using System.Text;

// One second of frames, boiled down to what a row of the performance log carries. Plain arithmetic with no Unity in
// it, so Validate Performance Overlay can feed it frames it made up and check the answers. Unknown is NaN, never 0,
// so a reading that was never taken can't pass for a fast one.
public sealed class PerfWindow
{
    public int Frames { get; private set; }
    public double Seconds { get; private set; }

    private double worstFrameMs;
    private int cpuFrames;
    private double cpuMainTotal;
    private int renderFrames;
    private double renderTotal;
    private int gpuFrames;
    private double gpuTotal;
    private double worstGpuMs;
    private ulong lastTimedFrame;

    // A frame as the player felt it: the real time since the frame before.
    public void AddFrame(double seconds)
    {
        if (!(seconds > 0)) return; // refuses NaN as well
        Frames++;
        Seconds += seconds;
        double ms = seconds * 1000.0;
        if (ms > worstFrameMs) worstFrameMs = ms;
    }

    // A frame as FrameTimingManager measured it, which it can only do a few frames after the fact. Asked every frame, it
    // hands back the latest frame it has finished — the same one again when none finished in between — so the frame's
    // start time is what keeps one frame from counting twice. A time of 0 is a timer the platform doesn't have.
    public void AddTiming(ulong frameStart, double cpuMainMs, double cpuRenderMs, double gpuMs)
    {
        if (frameStart == lastTimedFrame) return;
        lastTimedFrame = frameStart;
        if (cpuMainMs > 0) { cpuFrames++; cpuMainTotal += cpuMainMs; }
        if (cpuRenderMs > 0) { renderFrames++; renderTotal += cpuRenderMs; }
        if (gpuMs > 0)
        {
            gpuFrames++;
            gpuTotal += gpuMs;
            if (gpuMs > worstGpuMs) worstGpuMs = gpuMs;
        }
    }

    public double Fps => Seconds > 0 ? Frames / Seconds : double.NaN;
    public double AverageFrameMs => Frames > 0 ? Seconds * 1000.0 / Frames : double.NaN;
    public double WorstFrameMs => Frames > 0 ? worstFrameMs : double.NaN;
    public double CpuMainMs => cpuFrames > 0 ? cpuMainTotal / cpuFrames : double.NaN;
    public double CpuRenderMs => renderFrames > 0 ? renderTotal / renderFrames : double.NaN;
    public double GpuMs => gpuFrames > 0 ? gpuTotal / gpuFrames : double.NaN;
    public double WorstGpuMs => gpuFrames > 0 ? worstGpuMs : double.NaN;

    // Ready for the next second. Which frame FrameTimingManager last handed over is kept: it is still the one not to
    // count again.
    public void Clear()
    {
        Frames = 0;
        Seconds = 0;
        worstFrameMs = 0;
        cpuFrames = 0;
        cpuMainTotal = 0;
        renderFrames = 0;
        renderTotal = 0;
        gpuFrames = 0;
        gpuTotal = 0;
        worstGpuMs = 0;
    }
}

// The performance log's columns, and how a row of them is written.
//
// Numbers always take a full stop, whatever language the phone is in: on a phone set to German or French C# writes 7.2
// as "7,2", which a comma-separated file reads as two cells, and every column after it would shift. Unknown is an
// empty cell. A cell holding a comma is quoted — the device's model name alone ("iPhone14,2") would otherwise split
// its row.
public static class PerfCsv
{
    public static readonly string[] Columns =
    {
        "t_s", "kind", "scene",
        "fps", "frame_ms", "worst_frame_ms", "cpu_main_ms", "cpu_render_ms", "gpu_ms", "worst_gpu_ms",
        "heat", "low_power", "footprint_mb", "headroom_mb", "battery_pct", "power",
        "stage", "event", "detail",
    };

    // One second's row. Times are seconds since the app's process started.
    public struct Tick
    {
        public double t;
        public string scene;
        public double fps, frameMs, worstFrameMs, cpuMainMs, cpuRenderMs, gpuMs, worstGpuMs;
        public string heat;
        public int lowPower;
        public double footprintMb, headroomMb, batteryPct;
        public string power;
        public string stage;
    }

    public static string Header() => Join(Columns);

    public static string TickRow(Tick r) => Join(new[]
    {
        Number(r.t, "0.000"), "tick", r.scene,
        Number(r.fps, "0.0"), Number(r.frameMs, "0.00"), Number(r.worstFrameMs, "0.0"),
        Number(r.cpuMainMs, "0.00"), Number(r.cpuRenderMs, "0.00"), Number(r.gpuMs, "0.00"), Number(r.worstGpuMs, "0.00"),
        r.heat, r.lowPower < 0 ? string.Empty : r.lowPower.ToString(CultureInfo.InvariantCulture),
        Number(r.footprintMb, "0.0"), Number(r.headroomMb, "0.0"), Number(r.batteryPct, "0"), r.power,
        r.stage, string.Empty, string.Empty,
    });

    // Something that happened between two ticks: its time, where, and what, with the tick's cells left empty.
    public static string EventRow(double t, string scene, string name, string detail)
    {
        var fields = new string[Columns.Length];
        fields[0] = Number(t, "0.000");
        fields[1] = "event";
        fields[2] = scene;
        fields[Columns.Length - 2] = name;
        fields[Columns.Length - 1] = detail;
        return Join(fields);
    }

    // Unknown — NaN, or the -1 the device calls answer with — is an empty cell. Nothing this log measures is negative.
    public static string Number(double value, string format) =>
        double.IsNaN(value) || double.IsInfinity(value) || value < 0
            ? string.Empty
            : value.ToString(format, CultureInfo.InvariantCulture);

    public static string Field(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(Special) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static readonly char[] Special = { ',', '"', '\n', '\r' };

    private static string Join(string[] fields)
    {
        var line = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) line.Append(',');
            line.Append(Field(fields[i]));
        }
        return line.ToString();
    }
}
