using System.Collections.Generic;
using System.Text;

// Turns a mechanism's catalog name into something a player can read.
//
// The names in RobotModelCatalog.MechanismInfo are RIG names, typed by whoever set the robot up, for
// the rigging tools: "CascadeLift", "LeftSideToggle", "PlasticClaw Flip". They were never written to
// be shown to anyone, and two screens now show them — Configure Controller, which always has, and the
// robot stage on the home screen — so the cleanup lives here once instead of as two copies that drift.
//
// Splitting only inserts spaces; it never renames. A name that still reads badly once split ("Left
// Side Toggle") is bad DATA, and the fix is to rename it in Tools > RoboSim > Robot > Model Catalog,
// not a cleverer rule here.
public static class MechanismNames
{
    // How long the stage's mechanism line may run before the rest is summarised as "+N". Measured
    // against the narrowest stage — the 13" iPad's, 867 units — at the line's 24pt size; the label
    // autosizes down a little past this rather than clipping.
    public const int LineBudget = 58;

    // A comma, not the middle dot the mock-up showed: the UI font is a STATIC atlas baked with ASCII
    // plus the em dash and the ellipsis (see HomeThemeFonts), so a "·" would be drawn from the
    // fallback font, or not at all.
    private const string Separator = ", ";

    // "CascadeLift" -> "Cascade Lift". A space goes before an uppercase letter that follows a
    // lowercase one, and before an uppercase letter that starts a word after a run of capitals
    // ("DRLift" -> "DR Lift"). Underscores read as spaces.
    //
    // NEVER after a digit: "DR4B Lift" has to survive intact, and a rule that splits there turns it
    // into "DR4 B Lift". A name that is already spaced comes through unchanged.
    public static string Pretty(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string name = raw.Trim().Replace('_', ' ');
        var pretty = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && pretty[pretty.Length - 1] != ' ')
            {
                char before = name[i - 1];
                bool afterLower = char.IsLower(before);
                bool wordAfterCapitals = char.IsUpper(before) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (afterLower || wordAfterCapitals) pretty.Append(' ');
            }
            pretty.Append(c);
        }
        return pretty.ToString();
    }

    // The line under the robot's name on the home stage: its mechanisms, split into words and joined
    // until the budget runs out, then "+N" for the rest. Empty for a robot with none — the stage hides
    // the line then, rather than showing a blank one.
    //
    // Cut between whole names, never inside one, and never "+0". The first name is always kept even
    // when it alone is over budget: a line reading only "+4" says less than one that runs a little long.
    public static string Line(IList<RobotModelCatalog.MechanismInfo> mechanisms, int budget = LineBudget)
    {
        if (mechanisms == null || mechanisms.Count == 0) return string.Empty;

        var names = new List<string>(mechanisms.Count);
        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null) continue;
            string name = Pretty(string.IsNullOrWhiteSpace(mechanism.displayName) ? mechanism.id : mechanism.displayName);
            if (name.Length > 0) names.Add(name);
        }
        if (names.Count == 0) return string.Empty;

        var line = new StringBuilder();
        int shown = 0;
        foreach (string name in names)
        {
            if (shown > 0 && line.Length + Separator.Length + name.Length > budget) break;
            if (shown > 0) line.Append(Separator);
            line.Append(name);
            shown++;
        }
        if (shown < names.Count) line.Append("  +").Append(names.Count - shown);
        return line.ToString();
    }
}
