using System;
using System.Reflection;
using System.Text;
using UnityEngine;

// PROBE: WHAT EACH ROBOT DRIVES LIKE, in numbers. Built 2026-11-16 for Connor's notes on 1.2.0 build 1:
// v3 too slow, a slammed reversal "instant", more coast wanted. On the bare floor, through the controller:
//
//   top     speed after 3 s of full throttle, against the motor's free speed (u/s)
//   t90     seconds from rest to 90% of that
//   1s      distance covered in the first second (u)
//   rev0    seconds from full forward, stick slammed to full back, until the robot stops
//   rev90   ...until it is doing 90% of top speed backwards
//   peak    hardest deceleration in that reversal (g), and the body's peak lean (deg)
//   coast   distance rolled after letting go at full speed (u)
//   pivot   yaw in 1.5 s of full turn from rest (deg)
//   arc     yaw in 1.5 s of full throttle + full turn, entered at full speed (deg)
//   half    the same with half turn (deg)
//
// Each robot as it ships and, where it differs, on the old weight-based motor (driveWatts 0).
// Env: ROBOSIM_PROBE_ROBOT filters by name; ROBOSIM_PROBE_RPM=<name>:<rpm>[,...] overrides a robot's
// maxWheelRpm; ROBOSIM_PROBE_WATTS=<name>:<watts>[,...] overrides its driveWatts. Printed, asserted nowhere.
public static class DriveCharacterProbe
{
    private const int SettleSteps = 100;
    private static float PivotRate = -1f, MovingRate = -1f;

    public static void RunBatchProbe() => ValidationUtil.RunBatch("Drive Character", Run);

    private static string Run()
    {
        string filter = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_ROBOT");
        var lines = new StringBuilder();
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (!string.IsNullOrEmpty(filter) && !prefab.name.Contains(filter)) continue;
            var shipped = prefab.GetComponentInChildren<RobotMotorController>(true);
            if (shipped == null) continue;
            float rpm = Override("ROBOSIM_PROBE_RPM", prefab.name, shipped.maxWheelRpm);
            float watts = Override("ROBOSIM_PROBE_WATTS", prefab.name, shipped.driveWatts);
            lines.AppendLine($"'{prefab.name}' ({rpm:0} RPM):");
            // ROBOSIM_PROBE_RATES=<pivot>/<moving>[,...]: turns only, once per pair of turn rates.
            string rates = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_RATES");
            if (!string.IsNullOrEmpty(rates))
            {
                foreach (string pair in rates.Split(','))
                {
                    string[] pr = pair.Split('/');
                    PivotRate = float.Parse(pr[0]); MovingRate = float.Parse(pr[1]);
                    lines.AppendLine($"    rates {PivotRate:0.00}/{MovingRate:0.00}  pivot {Turn(prefab, rpm, watts, 0f, 1f, 0),4:0}  " +
                                     $"arc {Turn(prefab, rpm, watts, 1f, 1f, 300),4:0}  half {Turn(prefab, rpm, watts, 1f, 0.5f, 300),4:0}");
                }
                PivotRate = MovingRate = -1f;
                continue;
            }
            lines.AppendLine($"    {(watts > 0f ? $"{watts:0.#} W motors" : "weight-based"),-14} {Measure(prefab, rpm, watts)}");
            if (watts > 0f)
                lines.AppendLine($"    {"weight-based",-14} {Measure(prefab, rpm, 0f)}");
        }
        return $"Drive Character:\n{lines.ToString().TrimEnd()}";
    }

    private static float Override(string variable, string robot, float fallback)
    {
        string raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(raw)) return fallback;
        foreach (string pair in raw.Split(','))
        {
            string[] kv = pair.Split(':');
            if (kv.Length == 2 && robot.Contains(kv[0]) && float.TryParse(kv[1], out float value)) return value;
        }
        return fallback;
    }

    private static RobotMotorController Spawn(GameObject prefab, float rpm, float watts, out ArticulationBody root)
    {
        root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
        motor.maxWheelRpm = rpm;
        motor.driveWatts = watts;
        if (PivotRate >= 0f) motor.pivotTurnRate = PivotRate;
        if (MovingRate >= 0f) motor.turnRate = MovingRate;
        motor.Initialise();
        Physics.simulationMode = SimulationMode.Script;
        TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
        return motor;
    }

    private static readonly FieldInfo LeanField =
        typeof(RobotMotorController).GetField("leanDeg", BindingFlags.Instance | BindingFlags.NonPublic);

    private static string Measure(GameObject prefab, float rpm, float watts)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            float dt = ValidationUtil.StepSeconds, g = Mathf.Abs(Physics.gravity.y);
            RobotMotorController motor = Spawn(prefab, rpm, watts, out ArticulationBody root);
            Vector3 fwd = Vector3.ProjectOnPlane(motor.DriveForwardWorld, Vector3.up).normalized;
            float Speed() => Vector3.Dot(root.linearVelocity, fwd);

            // Launch.
            Vector3 start = root.worldCenterOfMass;
            var speeds = new float[300];
            float oneSecond = 0f;
            for (int i = 0; i < speeds.Length; i++)
            {
                TipOverValidation.StepDriven(motor, 1f, 0f, 1);
                speeds[i] = Speed();
                if (i == 99) oneSecond = Vector3.Dot(root.worldCenterOfMass - start, fwd);
            }
            float top = speeds[speeds.Length - 1];
            float t90 = -1f;
            for (int i = 0; i < speeds.Length; i++) if (speeds[i] >= 0.9f * top) { t90 = (i + 1) * dt; break; }

            // Slammed reversal.
            float rev0 = -1f, rev90 = -1f, peakDecel = 0f, peakLean = 0f, last = Speed();
            for (int i = 0; i < 300 && rev90 < 0f; i++)
            {
                TipOverValidation.StepDriven(motor, -1f, 0f, 1);
                float v = Speed();
                peakDecel = Mathf.Max(peakDecel, (last - v) / dt / g);
                last = v;
                if (LeanField != null) peakLean = Mathf.Max(peakLean, Mathf.Abs((float)LeanField.GetValue(motor)));
                if (rev0 < 0f && v <= 0f) rev0 = (i + 1) * dt;
                if (v <= -0.9f * top) rev90 = (i + 1) * dt;
            }

            // Coast: back up to full forward speed, let go.
            TipOverValidation.StepDriven(motor, 1f, 0f, 300);
            Vector3 release = root.worldCenterOfMass;
            for (int i = 0; i < 500 && Mathf.Abs(Speed()) > 0.05f; i++) TipOverValidation.StepDriven(motor, 0f, 0f, 1);
            float coast = Vector3.Dot(root.worldCenterOfMass - release, fwd);

            float pivot = Turn(prefab, rpm, watts, 0f, 1f, 0);
            float arc = Turn(prefab, rpm, watts, 1f, 1f, 300);
            float half = Turn(prefab, rpm, watts, 1f, 0.5f, 300);
            float free = motor.Tuning.topSpeed;
            return $"top {top,5:0.00}/{free,5:0.00}  t90 {t90,4:0.00}s  1s {oneSecond,5:0.0}u  rev0 {rev0,4:0.00}s  rev90 {rev90,4:0.00}s  " +
                   $"peak {peakDecel,4:0.00}g lean {peakLean,3:0.0}  coast {coast,5:0.0}u  pivot {pivot,4:0}  arc {arc,4:0}  half {half,4:0}  " +
                   $"(motors {(float.IsInfinity(motor.Tuning.motorAccel) ? "-" : (motor.Tuning.motorAccel / g).ToString("0.00") + "g")}, ramp {motor.ThrottleRate(motor.throttleRisePerSec):0.0}/s)";
        }
        finally { Physics.simulationMode = previous; }
    }

    // Yaw over 1.5 s of (throttle, turn), after `lead` steps of full throttle straight.
    private static float Turn(GameObject prefab, float rpm, float watts, float throttle, float turn, int lead)
    {
        RobotMotorController motor = Spawn(prefab, rpm, watts, out ArticulationBody root);
        if (lead > 0) TipOverValidation.StepDriven(motor, 1f, 0f, lead);
        float yaw = 0f;
        Vector3 last = Vector3.ProjectOnPlane(motor.DriveForwardWorld, Vector3.up);
        for (int i = 0; i < 150; i++)
        {
            TipOverValidation.StepDriven(motor, throttle, turn, 1);
            Vector3 now = Vector3.ProjectOnPlane(motor.DriveForwardWorld, Vector3.up);
            yaw += Vector3.SignedAngle(last, now, Vector3.up);
            last = now;
        }
        return Mathf.Abs(yaw);
    }
}
