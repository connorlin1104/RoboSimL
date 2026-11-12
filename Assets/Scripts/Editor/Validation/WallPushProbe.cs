using System.Text;
using UnityEditor;
using UnityEngine;

// PROBE: PUSHING INTO A WALL. Connor, 2026-11-11: "when going against a wall 360 dt goes side to
// side even when going straight perpendicular into it while pressing against."
//
// Drives a robot at full throttle into a static wall on the bare floor and reports what the chassis
// does once it is pressed against it: how far it wanders along the wall, how much it yaws, how often
// it changes direction, and what the tyre was told (the external lateral force, as a share of the
// robot's weight, and the lowest friction it set). Tyre ON and OFF, wall square and a few degrees off.
// Printed, asserted nowhere.
public static class WallPushProbe
{
    private const int SettleSteps = 100;
    private const int ApproachSteps = 300;   // up to 3 s to reach the wall
    private const int PressSteps = 300;      // 3 s pressed against it
    private const float Gap = 3f;            // u between the robot's front and the wall
    private static readonly string[] Robots = { "360RpmDrivetrain", "654V_v3" };
    private static readonly float[] Angles = { 0f, 3f, 8f };

    [MenuItem("Tools/RoboSim/Validate/Probe Wall Push (side to side)", false, 63)]
    public static void Probe() => ValidationUtil.RunInteractive("Wall Push", Run);

    public static void RunBatchProbe() => ValidationUtil.RunBatch("Wall Push", Run);

    private static string Run()
    {
        bool previous = WheelTyreModel.FrictionEnabled;
        try
        {
            var lines = new StringBuilder();
            foreach (string name in Robots)
            {
                GameObject prefab = null;
                foreach (GameObject p in RoboSimPaths.RobotPrefabs())
                    if (p.name == name) prefab = p;
                if (prefab == null) { lines.AppendLine($"'{name}': not found"); continue; }
                lines.AppendLine($"'{name}':");
                foreach (float angle in Angles)
                    foreach (bool tyre in new[] { true, false })
                        lines.AppendLine($"    wall {angle,2:0} deg  tyre {(tyre ? "ON " : "OFF")}  {Press(prefab, angle, tyre)}");
            }
            return $"Wall Push:\n{lines.ToString().TrimEnd()}";
        }
        finally { WheelTyreModel.FrictionEnabled = previous; }
    }

    private static string Press(GameObject prefab, float angleDeg, bool tyreOn, float throttle = 1f)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            WheelTyreModel.FrictionEnabled = tyreOn;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);
            float weight = DrivetrainTuning.MeasureTotalMass(root) * Mathf.Abs(Physics.gravity.y);
            Vector3 fwd = Vector3.ProjectOnPlane(motor.DriveForwardWorld, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);

            // The robot's front, along its drive axis, from every collider it has.
            Vector3 com = Com(bodies);
            float front = 0f;
            foreach (Collider c in root.GetComponentsInChildren<Collider>())
            {
                Bounds b = c.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    front = Mathf.Max(front, Vector3.Dot(corner - com, fwd));
                }
            }

            // A wall 40 u wide and 10 u tall, its face Gap ahead of the robot's front, turned by angleDeg.
            const float thickness = 2f;
            Quaternion rot = Quaternion.AngleAxis(angleDeg, Vector3.up) * Quaternion.LookRotation(fwd, Vector3.up);
            Vector3 wallNormal = rot * Vector3.back;   // the face that looks at the robot
            Vector3 centre = new Vector3(com.x, 5f, com.z) + fwd * (front + Gap) - wallNormal * (thickness / 2f);
            var wall = new GameObject("Wall");
            wall.transform.SetPositionAndRotation(centre, rot);
            wall.AddComponent<BoxCollider>().size = new Vector3(40f, 10f, thickness);
            Physics.SyncTransforms();
            Vector3 along = Vector3.Cross(Vector3.up, -wallNormal).normalized;

            // Drive until pressed: the chassis stops closing on the wall.
            int contactStep = -1;
            Vector3 last = Com(bodies);
            for (int i = 0; i < ApproachSteps && contactStep < 0; i++)
            {
                TipOverValidation.StepDriven(motor, 1f, 0f, 1);
                Vector3 now = Com(bodies);
                float distance = Vector3.Dot(centre - wallNormal * (-thickness / 2f) - now, -wallNormal);
                if (i > 50 && distance < front + 1f && Vector3.Dot(now - last, -wallNormal) / ValidationUtil.StepSeconds < 1f)
                    contactStep = i;
                last = now;
            }
            if (contactStep < 0) return "never reached the wall";
            if (Trace != null) Trace.AppendLine($"      reached the wall at step {contactStep}; stall {motor.Tuning.stallTorque:0.0}, grip {motor.Tuning.gripTorque:0.0}, damping {motor.Tuning.damping:0.0000}, weight {weight:0}");

            // Pressed: skip the first 0.5 s of the impact, then measure.
            TipOverValidation.StepDriven(motor, throttle, 0f, 50);
            if (Trace != null)
            {
                var touching = new StringBuilder("      touching the wall:");
                foreach (Collider c in Physics.OverlapBox(centre, new Vector3(20f, 5f, thickness / 2f + 0.05f), rot))
                    if (c.gameObject != wall) touching.Append(' ').Append(c.name).Append('(').Append(c.attachedArticulationBody != null ? c.attachedArticulationBody.name : "-").Append(')');
                Trace.AppendLine(touching.ToString());
            }
            Vector3 start = Com(bodies);
            float startYaw = Yaw(root, fwd);
            float minAlong = 0f, maxAlong = 0f, minYaw = 0f, maxYaw = 0f;
            float maxExt = 0f, minMu = 2f, lastYaw = 0f, lastRate = 0f, lastAlong = 0f, lastSlide = 0f;
            int yawFlips = 0, slideFlips = 0;
            for (int i = 0; i < PressSteps; i++)
            {
                TipOverValidation.StepDriven(motor, throttle, 0f, 1);
                float a = Vector3.Dot(Com(bodies) - start, along);
                float y = Mathf.DeltaAngle(startYaw, Yaw(root, fwd));
                minAlong = Mathf.Min(minAlong, a); maxAlong = Mathf.Max(maxAlong, a);
                minYaw = Mathf.Min(minYaw, y); maxYaw = Mathf.Max(maxYaw, y);
                maxExt = Mathf.Max(maxExt, WheelTyreModel.PeekExternalLateral(motor));
                foreach (ArticulationBody w in wheels)
                {
                    float mu = WheelTyreModel.PeekLastMu(w);
                    if (mu >= 0f) minMu = Mathf.Min(minMu, mu);
                }
                // A direction change only counts with real speed on both sides of it (0.2 u/s, 2 deg/s).
                float slide = (a - lastAlong) / ValidationUtil.StepSeconds;
                float rate = (y - lastYaw) / ValidationUtil.StepSeconds;
                if (Mathf.Abs(slide) > 0.2f) { if (lastSlide * slide < 0f) slideFlips++; lastSlide = slide; }
                if (Mathf.Abs(rate) > 2f) { if (lastRate * rate < 0f) yawFlips++; lastRate = rate; }
                lastAlong = a; lastYaw = y;
                if (Trace != null && i % 10 == 0)
                {
                    Vector3 c = Com(bodies);
                    float gap = Vector3.Dot(centre + wallNormal * (thickness / 2f) - c, -wallNormal) - front;
                    Vector3 v = root.linearVelocity;
                    var row = new StringBuilder($"      t {i * ValidationUtil.StepSeconds:0.00} along {a,6:0.000} gap {gap,6:0.000} " +
                        $"v_fwd {Vector3.Dot(v, -wallNormal),6:0.00} v_along {Vector3.Dot(v, along),6:0.00} yaw {y,5:0.00} ext {WheelTyreModel.PeekExternalLateral(motor) / weight:0.00} | ");
                    foreach (ArticulationBody w in wheels)
                        row.Append($"{w.name}:{w.jointVelocity[0] * Mathf.Rad2Deg,6:0} tq{(w.driveForce.dofCount > 0 ? w.driveForce[0] : 0f) / motor.Tuning.stallTorque,5:0.00} mu{WheelTyreModel.PeekLastMu(w):0.00} lat{WheelTyreModel.PeekLastLateral(w),5:0.00} N{WheelTyreModel.PeekNormalImpulse(w):0.0}  ");
                    Trace.AppendLine(row.ToString());
                }
            }
            float spin = 0f; foreach (ArticulationBody w in wheels) spin += Mathf.Abs(w.jointVelocity[0] * Mathf.Rad2Deg) / wheels.Length;
            return $"mean wheel spin {spin,5:0} deg/s, along the wall {maxAlong - minAlong,5:0.00} u ({slideFlips,2} reversals), " +
                   $"yaw {maxYaw - minYaw,5:0.0} deg ({yawFlips,2} reversals), " +
                   $"external lateral up to {maxExt / Mathf.Max(weight, 1e-3f):0.00} W, " +
                   $"lowest mu {(minMu <= 1f ? minMu.ToString("0.00") : "-")}";
        }
        finally { Physics.simulationMode = previous; }
    }

    private static StringBuilder Trace;

    public static void RunBatchThrottle() => ValidationUtil.RunBatch("Wall Push Throttle", () =>
    {
        var lines = new StringBuilder();
        foreach (string n in Robots)
        {
            GameObject prefab = null;
            foreach (GameObject p in RoboSimPaths.RobotPrefabs()) if (p.name == n) prefab = p;
            foreach (float t in new[] { 1f, 0.6f, 0.3f })
                lines.AppendLine($"{n} throttle {t:0.0}: {Press(prefab, 0f, true, t)}");
        }
        return "Wall Push Throttle:\n" + lines;
    });

    public static void RunBatchTrace() => ValidationUtil.RunBatch("Wall Push Trace", () =>
    {
        bool prev = WheelTyreModel.FrictionEnabled;
        try
        {
            Trace = new StringBuilder();
            GameObject prefab = null;
            foreach (GameObject p in RoboSimPaths.RobotPrefabs()) if (p.name == "360RpmDrivetrain") prefab = p;
            string summary = "";
            foreach (bool tyre in new[] { true, false })
            {
                Trace.AppendLine($"360 tyre {(tyre ? "ON" : "OFF")}, stall {prefab.GetComponent<RobotMotorController>() != null}");
                summary += Press(prefab, 0f, tyre) + "\n";
            }
            return $"Wall Push Trace 0deg: {summary}\n{Trace}";
        }
        finally { WheelTyreModel.FrictionEnabled = prev; }
    });

    private static float Yaw(ArticulationBody root, Vector3 fwd)
        => Mathf.Atan2(root.transform.forward.x, root.transform.forward.z) * Mathf.Rad2Deg;

    private static Vector3 Com(ArticulationBody[] bodies)
    {
        Vector3 sum = Vector3.zero; float mass = 0f;
        foreach (ArticulationBody b in bodies) { sum += b.worldCenterOfMass * b.mass; mass += b.mass; }
        return mass > 0f ? sum / mass : Vector3.zero;
    }
}
