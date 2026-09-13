using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// Works out what the home stage's chips can learn from a robot's own rig — its lift, whether it has a
// Floating Intake, whether it has a claw — and counts the motors its CAD carries, which the typed watts are
// checked against. RobotModelCatalog.Highlights says which of its fields are worked out and which are typed.
//
// Read off the components the mechanism builders leave behind, never off names. A mechanism's name is
// whatever someone typed: 654V v2's claw jaws are a mechanism called "PlasticClaw Clamp", because Build Claw
// calls a claw's jaws its clamp, and a rule that matched the word would hand that robot the Clamp label —
// which is for another mechanism altogether, a vertical clamp nothing has rigged yet.
//
//   Lift             a CascadeLift (Build Cascade Lift) or a Dr4bLift (Build DR4B Lift).
//   Claw             a ClawRig with jaws. One that only flips is a flip-out tray, not a claw.
//   Floating Intake  an intake (IntakePull) whose mouth sits at the floor: no more than FloorReach above the
//                    drive wheels' centres, with the robot as drawn. That is what tells a floor intake from a
//                    scoring one — 654V v3 has both, one level with its wheels and one 1.17 units above them.
internal static class RobotHighlightDetection
{
    // How far above the drive wheels' centres an intake's mouth may sit and still be picking up off the floor,
    // in world units (1 = 0.1 m). Measured: the floor intakes sit at +0.01 (654V v3) and -0.09 (654V v1), and
    // the one raised intake at +1.17 (654V v3's scoring intake).
    internal const float FloorReach = 0.5f;

    // The motors in a CAD, named the way every model this project has seen names them: "V5 Motor Simple (11W)
    // v1:3", "V5 Motor Simple (5.5W) v1:1". The brackets matter — 654V v2 wraps one of its motors in an assembly
    // called "11W With .5 in Screws", and matching that as well would count the motor twice.
    private static readonly Regex MotorName = new Regex(@"motor.*\((11|5\.5)\s?W\)", RegexOptions.IgnoreCase);

    internal struct Rig
    {
        public RobotModelCatalog.LiftKind lift;
        public bool floatingIntake;
        public bool claw;
        public int intakes;
        public int floorIntakes;
    }

    internal struct Motors
    {
        public int elevenWatt;
        public int fiveWatt;
        public float Watts => elevenWatt * 11f + fiveWatt * 5.5f;
    }

    internal static Rig Detect(GameObject robot)
    {
        var rig = new Rig();
        if (robot == null) return rig;

        if (robot.GetComponentInChildren<CascadeLift>(true) != null) rig.lift = RobotModelCatalog.LiftKind.Cascade;
        else if (robot.GetComponentInChildren<Dr4bLift>(true) != null) rig.lift = RobotModelCatalog.LiftKind.DR4B;

        foreach (ClawRig claw in robot.GetComponentsInChildren<ClawRig>(true))
        {
            if (claw.clampSections == null) continue;
            if (claw.clampSections.Exists(section => section != null && section.parts != null && section.parts.Count > 0))
            {
                rig.claw = true;
                break;
            }
        }

        IntakePull[] intakes = robot.GetComponentsInChildren<IntakePull>(true);
        rig.intakes = intakes.Length;
        if (intakes.Length > 0 && TryWheelCentreHeight(robot, out float wheels))
        {
            foreach (IntakePull intake in intakes)
            {
                if (intake.transform.position.y - wheels <= FloorReach) rig.floorIntakes++;
            }
        }
        rig.floatingIntake = rig.floorIntakes > 0;
        return rig;
    }

    // The drive wheels' mean centre height, with the robot as drawn. That is upright: the spawner only ever
    // turns a robot about the vertical, on top of the rotation its prefab was drawn with.
    private static bool TryWheelCentreHeight(GameObject robot, out float height)
    {
        height = 0f;
        RobotMotorController drive = robot.GetComponentInChildren<RobotMotorController>(true);
        if (drive == null) return false;
        int wheels = 0;
        foreach (ArticulationBody[] side in new[] { drive.leftWheels, drive.rightWheels })
        {
            if (side == null) continue;
            foreach (ArticulationBody wheel in side)
            {
                if (wheel == null) continue;
                height += wheel.transform.position.y;
                wheels++;
            }
        }
        if (wheels == 0) return false;
        height /= wheels;
        return true;
    }

    internal static Motors CountMotors(GameObject robot)
    {
        var motors = new Motors();
        if (robot != null) CountMotors(robot.transform, ref motors);
        return motors;
    }

    // Outermost match only: the parts inside a motor can carry its name too, and each motor counts once.
    private static void CountMotors(Transform node, ref Motors motors)
    {
        Match match = MotorName.Match(node.name);
        if (match.Success)
        {
            if (match.Groups[1].Value == "11") motors.elevenWatt++;
            else motors.fiveWatt++;
            return;
        }
        foreach (Transform child in node) CountMotors(child, ref motors);
    }

    // What is wrong with a robot's typed watts, or null. Model Catalog shows it beside the numbers; Validate
    // Home Stage fails on it.
    internal static string WattsProblem(RobotModelCatalog.Highlights highlights, Motors cad)
    {
        if (highlights == null) return "It has no chip data at all.";
        float drive = highlights.driveWatts, lift = highlights.liftWatts;
        if (drive < 0f || lift < 0f) return "Watts can't be negative.";
        if (!WholeMotors(drive) || !WholeMotors(lift))
            return "Watts come in 5.5s: 11 for each 11 W motor, 5.5 for each 5.5 W one.";

        // A CAD that names no motor by wattage has nothing to check against.
        if (cad.Watts <= 0f) return null;
        if (drive + lift > cad.Watts + 0.01f)
            return $"The drivetrain and lift come to {Watts(drive + lift)}, more than the {Watts(cad.Watts)} of motors in its CAD.";
        if (drive <= 0f)
            return $"No drivetrain watts are typed, and its CAD has {Watts(cad.Watts)} of motors. Type what the drivetrain uses.";
        string liftName = RobotModelCatalog.Highlights.LiftName(highlights.rigLift);
        if (liftName != null && lift <= 0f)
            return $"It has a {liftName}, but no lift watts are typed.";
        return null;
    }

    private static bool WholeMotors(float watts) => Mathf.Abs(watts / 5.5f - Mathf.Round(watts / 5.5f)) < 0.001f;

    private static string Watts(float watts) => RobotModelCatalog.Highlights.FormatWatts(watts);

    // Writes what each robot's rig says into its catalog entry, wherever that has changed. Build Home Screen
    // and Build Robot Bundles call it; the catalog is saved only when something was written.
    internal static string Refresh(RobotModelCatalog catalog)
    {
        if (catalog == null || catalog.models == null) return "no catalog";
        int read = 0, updated = 0;
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null) continue;
            GameObject robot = BuildRobotBundles.SourcePrefab(entry);
            // Nothing in the project to read: the entry goes on saying what it said.
            if (robot == null) continue;
            read++;

            Rig rig = Detect(robot);
            RobotModelCatalog.Highlights highlights = entry.highlights ??= new RobotModelCatalog.Highlights();
            if (highlights.rigLift == rig.lift && highlights.rigFloatingIntake == rig.floatingIntake &&
                highlights.rigClaw == rig.claw)
                continue;
            highlights.rigLift = rig.lift;
            highlights.rigFloatingIntake = rig.floatingIntake;
            highlights.rigClaw = rig.claw;
            updated++;
        }
        if (updated > 0)
        {
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }
        return $"{read} robot(s) read, {updated} updated";
    }
}
