using System;
using UnityEngine;

// Moments worth timing, reported from the code where they happen — a robot going up on the home stage, a scene being
// asked for — and written down by the performance overlay (PerfOverlay) when it is on. With it off nothing listens, so
// a report costs a null check; a caller that has to build a string first asks Listening before it does.
public static class PerfLog
{
    // A scene was asked for: Drive, or a field's Home or Reset button. The overlay times the load from here to the new
    // scene's first frame.
    public const string LoadRequested = "load_requested";

    // A robot went up on the home stage, drawn and showing. The first one after a launch is the launch's "robot" time.
    public const string StageRobotShown = "stage_robot_shown";

    // The home stage was switched between Drift, Still and Off in Settings > Robot > Performance; the detail is the new
    // mode. The log's stage column carries it on every row after.
    public const string StageModeChanged = "stage_mode";

    public static event Action<string, string> Reported;

    public static bool Listening => Reported != null;

    public static void Report(string name, string detail = "") => Reported?.Invoke(name, detail ?? string.Empty);

    // Every Play in the editor starts with nobody listening, whether or not it reloads the domain.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlay() => Reported = null;
}
