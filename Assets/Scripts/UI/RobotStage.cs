using System.Collections.Generic;
using UnityEngine;

// The home screen's turntable: which robot stands on the stage, how far round it has turned, and where
// the camera has to be to see all of it.
//
// THE ROBOT NEVER MOVES. The camera and the three lights ride one pivot, and the pivot turns. Turning
// the world around a still robot looks exactly like turning the robot under fixed lights — the same
// turntable, the same highlights sweeping across it — but it is ONE transform write per frame. Turning
// the robot itself would dirty every transform in it each frame: several hundred, even after the
// showcase bake.
//
// It draws nothing by itself. RobotStageView, on the RawImage the robot is seen through, owns the render
// texture, decides which frames the camera draws, feeds in the finger and ticks this — so none of it
// runs unless that view is on screen.
public class RobotStage : MonoBehaviour
{
    [Header("Rig (wired by Build Home Screen)")]
    [SerializeField] private Transform pivot;
    [SerializeField] private Camera stageCamera;
    [Tooltip("Where the robot stands. Never rotated.")]
    [SerializeField] private Transform holder;
    [Tooltip("Inactive. A robot is instantiated here first, so nothing on it can wake before it has been checked.")]
    [SerializeField] private Transform staging;

    [Header("Turntable")]
    [Tooltip("The slow turn it always settles back to, in degrees per second. 12 is once round every 30 s.")]
    public float driftSpeed = 12f;
    [Tooltip("How quickly a flick decays back to the drift, per second. At 3 the hardest flick settles within about 2 s.")]
    public float settleRate = 3f;
    [Tooltip("The fastest a flick can spin it, in degrees per second.")]
    public float maxFlingSpeed = 540f;
    [Tooltip("Where the turn starts, so the first view is three-quarter on rather than side-on.")]
    public float startYaw = -28f;

    [Header("Framing")]
    [Tooltip("Degrees the camera looks down at the robot.")]
    public float pitch = 18f;
    [Tooltip("Room around the robot. The distance is solved exactly, so this is breathing space, not a correction.")]
    public float framingMargin = 1.05f;

    // Robots kept built: the one on show and the one before it, so flicking between two rows of the
    // picker swaps instantly. Each more would hold another several-hundred-object hierarchy for nothing.
    private const int CacheSize = 2;

    private class Shown
    {
        public string id;
        public GameObject instance;
        public RobotShowcase.Frame frame;
    }

    private readonly List<Shown> cache = new List<Shown>(); // most recently shown first
    private Shown current;
    private float yaw;
    private float velocity;
    private bool held;
    private float aspect = 1.5f;
    private bool initialised;

    public bool HasRobot => current != null;
    public float Yaw => yaw;

    void Awake() => Initialise();

    // Also run from every public entry point: the order objects wake in is not defined, and the view
    // that drives this can call in before this has woken.
    private void Initialise()
    {
        if (initialised) return;
        initialised = true;
        yaw = startYaw;
        velocity = driftSpeed;
        if (staging != null && staging.gameObject.activeSelf) staging.gameObject.SetActive(false);
    }

    public bool IsBuilt(RobotModelCatalog.Entry entry) =>
        entry != null && cache.Exists(s => s.id == entry.id && s.instance != null);

    // Puts `entry` on the stage. False when there is nothing to show — no entry, or one with no baked
    // showcase (a robot delivered as a bundle, or one Build Home Screen hasn't baked) — and the view
    // falls back to the app's chassis mark.
    public bool Show(RobotModelCatalog.Entry entry)
    {
        Initialise();
        if (entry != null && current != null && current.id == entry.id && current.instance != null) return true;

        GameObject prefab = entry != null ? entry.showcasePrefab : null;
        if (prefab == null || holder == null || staging == null)
        {
            SetCurrent(null);
            return false;
        }

        cache.RemoveAll(s => s.instance == null);
        Shown shown = cache.Find(s => s.id == entry.id);
        if (shown == null) shown = Build(entry.id, prefab);
        else cache.Remove(shown);
        cache.Insert(0, shown);
        SetCurrent(shown);

        while (cache.Count > CacheSize)
        {
            Shown oldest = cache[cache.Count - 1];
            cache.RemoveAt(cache.Count - 1);
            if (oldest.instance != null) Destroy(oldest.instance);
        }
        return true;
    }

    private void SetCurrent(Shown shown)
    {
        if (current != null && current != shown && current.instance != null) current.instance.SetActive(false);
        current = shown;
        if (current == null) return;
        current.instance.SetActive(true);
        ApplyFraming();
    }

    private Shown Build(string id, GameObject prefab)
    {
        // Under the INACTIVE staging object, so nothing on it wakes while it is checked.
        GameObject instance = Instantiate(prefab, staging);
        instance.name = prefab.name;

        // A baked showcase has nothing to strip. This is the net under one assigned by hand or baked by
        // an older tool: a robot prefab reaching the stage would otherwise wake with gravity, colliders
        // and — on the robots that carry NonSupportingLink — a global physics callback, on a screen with
        // no floor and no physics.
        if (RobotShowcase.NeedsStrip(instance))
        {
            Debug.LogWarning($"RobotStage: '{prefab.name}' is not a baked showcase, so it is being stripped " +
                             "here instead. Run Tools > RoboSim > Robot > Advanced > Build Showcase Prefabs.", prefab);
            RobotShowcase.StripToRenderers(instance);
            RobotShowcase.PrepareRenderers(instance);
        }

        Transform t = instance.transform;
        t.SetParent(holder, false);
        // Rotation and scale from the prefab, position NOT. Instantiate(prefab, parent) keeps the prefab
        // root's local transform, and every robot prefab root sits 12-16 units from the origin, where it
        // was saved out of a scene — outside this camera's view. The bake zeroes it as well; this is the
        // net for a showcase that didn't come through the bake.
        //
        // The rotation IS kept: it is the robot's own orientation, not a placement — a robot imported
        // Z-up carries its correction there. RobotSpawner composes it the same way.
        t.localPosition = Vector3.zero;
        t.localRotation = prefab.transform.localRotation;
        t.localScale = prefab.transform.localScale;

        return new Shown { id = id, instance = instance, frame = RobotShowcase.ComputeFrame(instance, holder) };
    }

    // The view's texture changed shape.
    public void SetAspect(float value)
    {
        Initialise();
        aspect = Mathf.Max(0.05f, value);
        ApplyFraming();
    }

    private void ApplyFraming()
    {
        if (current == null || pivot == null || stageCamera == null || !current.frame.IsValid) return;

        RobotShowcase.Frame frame = current.frame;
        // The pivot goes at the robot's centre, so turning it is a turntable about the robot rather than
        // an orbit about wherever the prefab's origin happened to be.
        pivot.localPosition = holder.localPosition + holder.localRotation * frame.center;

        float distance = FrameDistance(frame.radius, frame.height, stageCamera.fieldOfView, aspect, pitch) * framingMargin;
        Quaternion look = Quaternion.Euler(pitch, 0f, 0f);
        Transform cameraTransform = stageCamera.transform;
        cameraTransform.localRotation = look;
        cameraTransform.localPosition = look * new Vector3(0f, 0f, -distance);

        // Clip planes that hug the robot. Depth precision is spread between near and far, and Unity's
        // default 0.3 to 1000 would spread it across hundreds of times the depth there is to draw.
        float reach = frame.radius + frame.height;
        stageCamera.nearClipPlane = Mathf.Max(0.01f, distance - reach);
        stageCamera.farClipPlane = distance + reach;
    }

    // Moves the turn on by dt. While a finger holds the robot, the turn is the finger's alone.
    public void Tick(float dt)
    {
        Initialise();
        if (!held) Step(ref yaw, ref velocity, driftSpeed, settleRate, dt);
        yaw = Mathf.Repeat(yaw, 360f);
    }

    // Writes the turn to the pivot. Separate from Tick so the view writes the transform only on the
    // frames the camera actually draws.
    public void ApplyYaw()
    {
        if (pivot != null) pivot.localRotation = Quaternion.Euler(0f, yaw, 0f);
    }

    public void Grab()
    {
        Initialise();
        held = true;
        velocity = 0f;
    }

    public void Turn(float degrees)
    {
        Initialise();
        yaw = Mathf.Repeat(yaw + degrees, 360f);
    }

    public void Release(float flingSpeed)
    {
        Initialise();
        held = false;
        velocity = Mathf.Clamp(flingSpeed, -maxFlingSpeed, maxFlingSpeed);
    }

    // One step of "decay toward the drift" — dv/dt = -rate * (v - drift) — integrated EXACTLY over dt,
    // not by nudging v and then adding v * dt. Exactness is the point: it makes the turn frame-rate
    // independent in POSITION as well as speed, so a flick comes to rest at the same angle at 30 fps and
    // at 60. The usual Euler step lands several degrees apart on a hard flick. Validate Home Stage holds
    // it to that.
    public static void Step(ref float yaw, ref float velocity, float drift, float rate, float dt)
    {
        if (dt <= 0f) return;
        if (rate <= 0f)
        {
            yaw += velocity * dt;
            return;
        }
        float decay = Mathf.Exp(-rate * dt);
        yaw += drift * dt + (velocity - drift) * (1f - decay) / rate;
        velocity = drift + (velocity - drift) * decay;
    }

    // How far from the robot's axis the camera must stand to see all of it at every angle of the turn.
    //
    // The robot is bounded by a CYLINDER — radius about the vertical axis, and height — not a sphere. A
    // sphere is the textbook bound and the wrong one here: a robot is far wider than it is tall, and a
    // sphere big enough for its width stands the camera back as if it were that tall too, leaving it
    // small in the middle of the stage. A cylinder is exact for a turntable, because turning never
    // changes it: if it fits at one angle, it fits at all of them.
    //
    // Solved rather than bounded: bisection on the distance, testing the rims of the cylinder's top and
    // bottom against both halves of the view. A closed-form bound exists — it is the starting bracket
    // below — but it pairs the far rim's height with the near rim's depth, which never happen together,
    // and stood the robot about a third smaller than it needed to be.
    public static float FrameDistance(float radius, float height, float verticalFov, float aspect, float pitchDegrees)
    {
        float tanV = Mathf.Tan(Mathf.Clamp(verticalFov, 1f, 170f) * 0.5f * Mathf.Deg2Rad);
        float tanH = tanV * Mathf.Max(aspect, 0.05f);
        float sinP = Mathf.Sin(pitchDegrees * Mathf.Deg2Rad);
        float cosP = Mathf.Cos(pitchDegrees * Mathf.Deg2Rad);

        float near = 0f;
        float far = Mathf.Max((0.5f * height * cosP + radius * sinP) / tanV, radius / tanH)
                    + radius + 0.5f * height * sinP;
        for (int i = 0; i < 32; i++)
        {
            float mid = 0.5f * (near + far);
            if (CylinderFits(mid, radius, height, tanV, tanH, sinP, cosP)) far = mid;
            else near = mid;
        }
        return far;
    }

    // Camera `distance` back from the axis along its view, looking down by the pitch: does every point
    // on the two rims land inside the view? 48 points a rim put samples exactly on the four extremes
    // (0, 90, 180 and 270 degrees), which is where the rims reach furthest across and furthest up.
    private static bool CylinderFits(float distance, float radius, float height, float tanV, float tanH, float sinP, float cosP)
    {
        const int Samples = 48;
        for (int s = 0; s < Samples; s++)
        {
            float angle = s * (2f * Mathf.PI / Samples);
            float x = radius * Mathf.Cos(angle);
            float z = radius * Mathf.Sin(angle);
            for (int rim = 0; rim < 2; rim++)
            {
                float y = rim == 0 ? 0.5f * height : -0.5f * height;
                float depth = distance - y * sinP + z * cosP;
                if (depth <= 0.001f) return false;
                float up = y * cosP + z * sinP;
                if (Mathf.Abs(x) > tanH * depth || Mathf.Abs(up) > tanV * depth) return false;
            }
        }
        return true;
    }
}
