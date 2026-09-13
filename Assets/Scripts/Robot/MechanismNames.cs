using System.Text;

// Turns a mechanism's catalog name into something a player can read.
//
// The names in RobotModelCatalog.MechanismInfo are RIG names, typed by whoever set the robot up, for
// the rigging tools: "CascadeLift", "LeftSideToggle", "PlasticClaw Flip". They were never written to
// be shown to anyone, and Configure Controller shows every one of them. (The home stage listed them
// too, under the robot's name, until it moved to chips for the few that matter — see
// RobotModelCatalog.Highlights.)
//
// Splitting only inserts spaces; it never renames. A name that still reads badly once split ("Left
// Side Toggle") is bad DATA, and the fix is to rename it in Tools > RoboSim > Robot > Model Catalog,
// not a cleverer rule here.
public static class MechanismNames
{
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
}
