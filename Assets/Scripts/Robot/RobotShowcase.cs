using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// The rules for a robot that is LOOKED AT rather than driven — the copy the home screen's stage turns —
// shared by the tool that bakes those copies, the stage that shows them, and the validator.
//
// A robot prefab is a physics rig: 13-28 ArticulationBodies, 123-525 colliders and up to 34 scripts,
// some of which reach out of the robot as they wake. NonSupportingLink is the sharp one: its OnEnable
// registers every collider it owns with a process-wide physics callback backed by a persistent
// NativeArray, so a live robot prefab on the menu would install a physics hook from a screen that has
// no physics on it. The prefabs also use gravity, and the home scene has no floor.
//
// So the stage never shows a robot prefab. It shows a SHOWCASE — the same meshes and materials with
// everything else taken off — baked at author time by Build Showcase Prefabs.
public static class RobotShowcase
{
    // The layer a showcase is drawn on, and the only layer the stage camera draws. Its own layer so the
    // home screen's Main Camera can leave it out: that camera draws Everything by default, and would
    // otherwise draw the whole robot a second time, every frame, behind an opaque backdrop.
    public const string LayerName = "ShowcaseRobot";

    // A fixed slot rather than "the first free one", so the scene, the prefabs and the camera cannot
    // disagree about which index the name landed on in someone else's checkout. 6 is the first user
    // layer; 0-5 are Unity's own.
    public const int LayerIndex = 6;

    // The cylinder a robot turns inside: the centre of its bounds, the furthest any part reaches from
    // the vertical axis through that centre, and its height. A cylinder rather than a sphere because
    // turning never changes it — see RobotStage.FrameDistance.
    public struct Frame
    {
        public Vector3 center;
        public float radius;
        public float height;
        public bool IsValid => radius > 0f && height > 0f;
    }

    // Allowlist, not denylist: what may be left on a showcase. The four robots that ship carry 22
    // distinct scripts between them and a submitted robot can bring more, so a list of what to REMOVE
    // is a list that silently misses the next one. RectTransform counts as a Transform.
    public static bool IsAllowed(Component component) =>
        component is Transform || component is MeshFilter || component is MeshRenderer;

    // Whether anything on this copy is something other than a renderer — which a baked showcase never
    // is, and a robot prefab always is. A missing script reads as null and is skipped: it can't run.
    public static bool NeedsStrip(GameObject root)
    {
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component != null && !IsAllowed(component)) return true;
        }
        return false;
    }

    // Strips a robot down to its renderers, in place. Returns how many components it removed.
    //
    // Call it on a copy that is NOT active in the hierarchy — instantiated under an inactive parent —
    // so nothing on it has woken: no Awake, no OnEnable, and none of its colliders or articulations
    // have been handed to PhysX. Stripping an active robot means undoing registrations that already
    // happened, and NonSupportingLink's is not undone by destroying it in edit mode, where OnDisable
    // never runs.
    //
    // DestroyImmediate, not Destroy: Destroy is deferred to the end of the frame, so a caller that
    // strips and then activates in the same frame would activate a robot with every component still on
    // it. Legal at runtime for components, and what StripRobotRig already does.
    //
    // ORDER MATTERS, because of [RequireComponent]: removing a component that another one requires
    // fails with "Can't remove X because Y depends on it". Scripts are what declare requirements, so
    // they go first, then joints and bodies, then the rest. Each pass is deepest-first — StripRobotRig's
    // order, so removing an ArticulationBody chain doesn't re-root the survivors at every step. Nothing
    // on the shipped robots declares a requirement today; this is the order that keeps that true when
    // something does.
    public static int StripToRenderers(GameObject root)
    {
        if (root == null) return 0;

        int removed = 0;
        removed += DestroyDeepestFirst<MonoBehaviour>(root);
        removed += DestroyDeepestFirst<Joint>(root);
        removed += DestroyDeepestFirst<ArticulationBody>(root);
        removed += DestroyDeepestFirst<Rigidbody>(root);
        removed += DestroyDeepestFirst<Collider>(root);

        // Whatever is left that the allowlist doesn't name: audio, animators, lights, a LODGroup (whose
        // reference point, in the group's own space, would not survive the bake's flattening anyway).
        Component[] rest = root.GetComponentsInChildren<Component>(true);
        for (int i = rest.Length - 1; i >= 0; i--)
        {
            if (rest[i] == null || IsAllowed(rest[i])) continue;
            Object.DestroyImmediate(rest[i]);
            removed++;
        }
        return removed;
    }

    // Every renderer on the stage's layer, and nothing about it that costs work the stage never uses:
    // no shadows cast or received, no probe lookups. Shadows off HERE removes the draws; the stage's
    // lights are shadowless too, and that is what removes the shadow pass itself.
    public static int PrepareRenderers(GameObject root)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = LayerIndex;

        int count = 0;
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            count++;
        }
        return count;
    }

    // Measured from the MESHES' own bounds, carried through each part's transform — not from
    // Renderer.bounds, which is empty on anything that is not active in the hierarchy. The bake measures
    // a copy that is deliberately inactive and the validator measures the prefab asset, which is never
    // active at all; both have to get the same answer the stage does.
    //
    // A part's box corners over-estimate the part slightly. That is the safe direction: the camera
    // stands a touch further back, never close enough to clip. `space` null means world space.
    public static Frame ComputeFrame(GameObject root, Transform space)
    {
        var corners = new List<Vector3>();
        CollectCorners(root, space, corners);
        if (corners.Count == 0) return default;

        var box = new Bounds(corners[0], Vector3.zero);
        foreach (Vector3 corner in corners) box.Encapsulate(corner);

        float radiusSquared = 0f;
        foreach (Vector3 corner in corners)
        {
            float dx = corner.x - box.center.x;
            float dz = corner.z - box.center.z;
            radiusSquared = Mathf.Max(radiusSquared, dx * dx + dz * dz);
        }
        return new Frame { center = box.center, radius = Mathf.Sqrt(radiusSquared), height = box.size.y };
    }

    // The eight corners of every part's mesh box, in `space`. Public for the validator, which projects
    // them through the stage camera at every angle of the turn.
    public static void CollectCorners(GameObject root, Transform space, List<Vector3> into)
    {
        Matrix4x4 toSpace = space != null ? space.worldToLocalMatrix : Matrix4x4.identity;
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            Matrix4x4 toFrame = toSpace * filter.transform.localToWorldMatrix;
            Bounds local = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                into.Add(toFrame.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, sign)));
            }
        }
    }

    private static int DestroyDeepestFirst<T>(GameObject root) where T : Component
    {
        T[] items = root.GetComponentsInChildren<T>(true);
        int removed = 0;
        for (int i = items.Length - 1; i >= 0; i--)
        {
            if (items[i] == null) continue;
            Object.DestroyImmediate(items[i]);
            removed++;
        }
        return removed;
    }
}
