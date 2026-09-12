using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// Bakes the copy of each robot that the home screen's stage turns: Assets/UI/Showcase/<id>_Showcase.prefab.
//
// A robot prefab can't go on the menu — RobotShowcase says why: a physics rig, gravity, and a script that
// installs a process-wide physics callback when it wakes. A showcase is the same meshes and materials with
// all of that taken off, made light enough to turn 30 times a second on a phone:
//
//   - STRIPPED to renderers by RobotShowcase.StripToRenderers, the one implementation of that.
//   - FASTENERS CULLED: spacers, screws, nuts, washers, inserts and collars — RobotPartClassifier's
//     denylist — tested against each renderer's own PART rather than every ancestor. IsFastenerPart says
//     why that is the difference between culling screws and culling wheels.
//     Measured: 521 -> 361, 800 -> 612, 1,639 -> 737 and 769 -> 576 renderers.
//   - FLATTENED: every renderer moved up to the root and the CAD group nodes deleted. 654V_v2 is 5,645
//     objects seventeen levels deep, and its showcase is about 740. That is prefab size and instantiate
//     time — not per-frame cost, because the stage never moves the robot.
//   - No shadows, no probes, on the ShowcaseRobot layer.
//
// NOT UNDER Assets/Robots. RoboSimPaths.RobotPrefabPaths scans that folder recursively for 42 callers.
// Most skip a prefab with no RobotMotorController, but Delete Robot filters on nothing and would list
// these as orphans to delete.
//
// Build Home Screen calls EnsureAll, which re-bakes only a robot whose prefab — or this bake — has
// changed since the last time. The menu item re-bakes all of them.
// Batch: -executeMethod BuildShowcasePrefabs.RunBatch
public static class BuildShowcasePrefabs
{
    private const string Title = "Build Showcase Prefabs";

    // Bump when the bake itself changes — the cull rule, the flatten, anything that would make a fresh bake
    // of an unchanged robot come out different. It is part of each entry's showcaseSource, so a bump
    // re-bakes every robot on the next Build Home Screen instead of leaving old bakes in place.
    private const string BakeVersion = "showcase-1";

    // What one showcase may cost. Over the budget is reported; over the limit, the robot gets no showcase
    // and goes on the stage as its name over the chassis mark. The stage draws a robot's renderers 30
    // times a second on the screen people sit on while deciding what to do, so a robot that blows this
    // is worth looking at before it ships, not after it's found on a device. Baseline: 361 / 612 / 737 / 576.
    public const int RendererBudget = 800;
    public const int RendererLimit = 900;

    // The CAD exporter's generic leaf names. Every renderer on the shipped robots sits on a "Body1".."BodyN"
    // leaf under the part it belongs to.
    private static readonly Regex GenericLeafName =
        new Regex(@"^(Body|Mesh|Component|Solid)\s*\d*$", RegexOptions.IgnoreCase);

    private class Stats
    {
        public int renderersBefore;
        public int stripped;
        public int fasteners;
        public int hidden;
        public int keptNested;
        public int renderersAfter;
        public int objectsAfter;
        public readonly List<string> warnings = new List<string>();

        public string Describe(string robot) =>
            $"  {robot}: {renderersBefore} -> {renderersAfter} renderers ({fasteners} fastener, {hidden} hidden), " +
            $"{objectsAfter} objects, {stripped} components stripped" +
            (keptNested > 0 ? $", {keptNested} left nested to avoid shearing them" : string.Empty) +
            (renderersAfter > RendererBudget ? $" — OVER the {RendererBudget} budget" : string.Empty) +
            (warnings.Count > 0 ? "\n    " + string.Join("\n    ", warnings) : string.Empty);
    }

    [MenuItem("Tools/RoboSim/Robot/Advanced/Build Showcase Prefabs", false, 13)]
    private static void RunInteractive()
    {
        // No scene is opened or saved — the bake works in a preview scene — so there is nothing to offer to save.
        try
        {
            var failures = new List<string>();
            string report = Bake(LoadCatalog(), true, failures, out string details);
            Debug.Log(report + "\n" + details);
            EditorUtility.DisplayDialog(Title, failures.Count == 0 ? report : report + "\n\n" + string.Join("\n", failures), "OK");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog(Title, "FAILED\n\n" + e.Message, "OK");
        }
    }

    // Batch entry point: bakes every robot whatever state it is in, and throws if any fails, so
    // -executeMethod exits nonzero. Deliberately no menu item — see RunInteractive.
    public static void RunBatch()
    {
        var failures = new List<string>();
        string report = Bake(LoadCatalog(), true, failures, out string details);
        Debug.Log(report + "\n" + details);
        if (failures.Count > 0)
            throw new InvalidOperationException($"{Title}: {failures.Count} robot(s) failed:\n  " + string.Join("\n  ", failures));
    }

    // Called by Build Home Screen: bakes only what is missing or out of date, and never fails the build
    // over one robot — that robot shows as its name over the chassis mark, the error is logged, and
    // Validate Home Stage fails on it.
    public static string EnsureAll(RobotModelCatalog catalog)
    {
        var failures = new List<string>();
        string report = Bake(catalog, false, failures, out string details);
        if (details.Length > 0) Debug.Log($"{Title}:\n{details}");
        return report;
    }

    // Names layer 6 "ShowcaseRobot" in the project's TagManager, the way EnableTgsSolver edits the physics
    // settings (RigDrivetrainArticulation). Idempotent. Refuses to rename a slot that is already in use:
    // the scene, the prefabs and the stage camera all use index 6 directly, and a silent rename would
    // quietly re-point whatever else was on it.
    public static void EnsureStageLayer()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0)
            throw new InvalidOperationException($"{Title}: could not load ProjectSettings/TagManager.asset.");

        var so = new SerializedObject(assets[0]);
        SerializedProperty layers = so.FindProperty("layers");
        if (layers == null || !layers.isArray || layers.arraySize <= RobotShowcase.LayerIndex)
            throw new InvalidOperationException($"{Title}: TagManager.asset has no layer list.");

        SerializedProperty slot = layers.GetArrayElementAtIndex(RobotShowcase.LayerIndex);
        if (slot.stringValue == RobotShowcase.LayerName) return;
        if (!string.IsNullOrEmpty(slot.stringValue))
            throw new InvalidOperationException(
                $"{Title}: layer {RobotShowcase.LayerIndex} is already named '{slot.stringValue}'. The home stage " +
                $"needs that slot for '{RobotShowcase.LayerName}' (RobotShowcase.LayerIndex); move one of them.");

        slot.stringValue = RobotShowcase.LayerName;
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
    }

    // Which version of a robot a showcase was made from: this bake's version and the prefab's asset
    // dependency hash, which moves when the prefab or anything it pulls in (its FBX, its materials) changes.
    internal static string SourceStamp(GameObject source) =>
        BakeVersion + "|" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(source));

    // Whether this renderer belongs to a fastener. Asked of the renderer's own PART — the nearest name up
    // its chain that isn't a generic "BodyN" leaf — and not of every ancestor.
    //
    // The every-ancestor test is what Rebuild Part Colliders uses, and for colliders it is harmless. For a
    // showcase it deletes things you can see. 654V_v2 has an assembly named "48T Screw Joint 2.75 Omni
    // Assembly" — a gear and an omni wheel, joined by a screw — and one named "11W With .5 in Screws", a
    // motor. "Screw" in the ASSEMBLY's name culled 90 renderers of wheel and 32 of motor. Asking each part
    // instead culls the screws inside those assemblies and keeps the wheel and the motor.
    internal static bool IsFastenerPart(Transform renderer, Transform root)
    {
        for (Transform t = renderer; t != null; t = t.parent)
        {
            if (!GenericLeafName.IsMatch(RobotPartClassifier.NormalizeName(t.name)))
                return RobotPartClassifier.IsFastener(t.name);
            if (t == root) break;
        }
        return false;
    }

    // Whether a part is hidden in its robot: switched off, or under an inactive node. Flattening would lift
    // a hidden part out from under the inactive parent hiding it and put it on the menu, so hidden parts
    // are culled with the fasteners. None of the four robots that ship has one; a submitted one might.
    internal static bool IsHidden(MeshRenderer renderer, Transform root)
    {
        if (!renderer.enabled) return true;
        for (Transform t = renderer.transform; t != null && t != root; t = t.parent)
        {
            if (!t.gameObject.activeSelf) return true;
        }
        return false;
    }

    // How many renderers a correct bake of this robot keeps. Validate Home Stage compares each showcase
    // against it, which is what makes "the cull kept the right parts" checkable after flattening has
    // thrown away the names it was decided on.
    internal static int ExpectedRenderers(GameObject source)
    {
        int kept = 0;
        foreach (MeshRenderer renderer in source.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!IsFastenerPart(renderer.transform, source.transform) && !IsHidden(renderer, source.transform)) kept++;
        }
        return kept;
    }

    private static RobotModelCatalog LoadCatalog()
    {
        RobotModelCatalog catalog = RoboSimPaths.LoadRobotCatalog();
        if (catalog == null)
            throw new InvalidOperationException($"{Title}: no RobotModelCatalog at {RoboSimPaths.RobotModelCatalog}.");
        return catalog;
    }

    private static string Bake(RobotModelCatalog catalog, bool force, List<string> failures, out string details)
    {
        if (catalog == null)
            throw new InvalidOperationException($"{Title}: no RobotModelCatalog at {RoboSimPaths.RobotModelCatalog}.");
        EnsureStageLayer();
        EnsureFolder();

        var lines = new StringBuilder();
        var written = new List<GameObject>();
        int current = 0;
        bool changed = false;
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || string.IsNullOrEmpty(entry.id)) continue;

            if (entry.prefab == null)
            {
                // Delivered as a bundle: nothing in the project to bake from, so the stage shows its name over
                // the chassis mark. Clear anything left over from when it had a prefab.
                if (entry.showcasePrefab != null || !string.IsNullOrEmpty(entry.showcaseSource))
                {
                    entry.showcasePrefab = null;
                    entry.showcaseSource = null;
                    changed = true;
                }
                continue;
            }

            string stamp = SourceStamp(entry.prefab);
            string path = ShowcasePath(entry.id);
            if (!force && entry.showcasePrefab != null && entry.showcaseSource == stamp &&
                AssetDatabase.GetAssetPath(entry.showcasePrefab) == path)
            {
                current++;
                continue;
            }

            changed = true;
            try
            {
                Stats stats = BakeOne(entry.prefab, path);
                entry.showcasePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                entry.showcaseSource = stamp;
                written.Add(entry.showcasePrefab);
                lines.AppendLine(stats.Describe(entry.displayName));
            }
            catch (Exception e)
            {
                entry.showcasePrefab = null;
                entry.showcaseSource = null;
                failures.Add($"{entry.displayName}: {e.Message}");
                Debug.LogError($"{Title}: could not bake '{entry.displayName}' — the stage will show its name over " +
                               $"the chassis mark instead. {e.Message}");
            }
        }

        if (changed)
        {
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            VerifyOnDisk(written);
        }

        details = lines.ToString().TrimEnd();
        string report = $"{Title}: baked {written.Count}, {current} already current";
        return failures.Count > 0 ? $"{report}, {failures.Count} FAILED" : report;
    }

    private static Stats BakeOne(GameObject source, string path)
    {
        var stats = new Stats();

        // A preview scene, so the bake never touches — or dirties — whatever scene is open.
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            // Inactive, so nothing on the copy wakes. See RobotShowcase.StripToRenderers.
            var holder = new GameObject("ShowcaseBake");
            SceneManager.MoveGameObjectToScene(holder, preview);
            holder.SetActive(false);

            // A plain clone, NOT PrefabUtility.InstantiatePrefab: a prefab instance refuses to have components
            // or children removed ("Cannot restructure Prefab instance"), and this is nothing but removals.
            GameObject copy = Object.Instantiate(source, holder.transform);
            copy.name = source.name + "_Showcase";
            Transform root = copy.transform;
            stats.renderersBefore = copy.GetComponentsInChildren<MeshRenderer>(true).Length;

            // Strip first: it takes the scripts off, so nothing can object to its MeshFilter going when a
            // part is culled below. The cull reads only names, `enabled` and activeSelf, none of which this
            // touches.
            stats.stripped = RobotShowcase.StripToRenderers(copy);
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
                stats.stripped += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

            CullParts(root, stats);
            stats.keptNested = Flatten(root);
            RobotShowcase.PrepareRenderers(copy);

            // Every robot prefab root sits 12-16 units from the origin, where it was saved from a scene, and
            // the stage is built around the origin. Rotation and scale stay: they are the robot's own.
            root.localPosition = Vector3.zero;

            stats.renderersAfter = copy.GetComponentsInChildren<MeshRenderer>(true).Length;
            stats.objectsAfter = copy.GetComponentsInChildren<Transform>(true).Length;
            CheckDrawable(copy, stats);
            if (stats.renderersAfter > RendererLimit)
                throw new InvalidOperationException(
                    $"{stats.renderersAfter} renderers after the cull, over the limit of {RendererLimit}.");

            // Saved as its own root, out from under the inactive holder.
            root.SetParent(null, false);
            PrefabUtility.SaveAsPrefabAsset(copy, path, out bool saved);
            if (!saved) throw new InvalidOperationException($"could not save {path}.");
            return stats;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    // Removes the renderer and mesh of every part that is a fastener or hidden. Components, not
    // GameObjects: a culled renderer can sit on a node that still has parts under it that stay, and the
    // nodes left empty go when the hierarchy is flattened.
    private static void CullParts(Transform root, Stats stats)
    {
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            bool fastener = IsFastenerPart(renderer.transform, root);
            if (!fastener && !IsHidden(renderer, root)) continue;
            if (fastener) stats.fasteners++;
            else stats.hidden++;

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Object.DestroyImmediate(renderer);
            if (filter != null) Object.DestroyImmediate(filter);
        }
    }

    // Moves every renderer up to the root and deletes the groups left empty. Root-RELATIVE, through
    // SetParent(root, worldPositionStays: true): the root keeps its own rotation — where a Z-up import
    // carries its correction — and the stage composes that once. Baking world matrices would apply it twice.
    //
    // A Transform holds translation, rotation and scale, and nothing else. A part under a non-uniformly
    // scaled parent that is rotated relative to it has a SHEARED placement that no Transform can hold, and
    // moving it would silently drop the shear and deform the part — 654V_v3 has four non-uniform scales in
    // its hierarchy. Those parts stay where they are, with their parents; a partial flatten is fine, the
    // point is fewer nodes. Every placement is then checked, so a part that moved is a failed bake rather
    // than a quietly misplaced part. Returns how many were left nested.
    private static int Flatten(Transform root)
    {
        var placements = new Dictionary<Transform, Matrix4x4>();
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            placements[renderer.transform] = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;

        int keptNested = 0;
        foreach (KeyValuePair<Transform, Matrix4x4> part in placements)
        {
            if (part.Key.parent == root) continue;
            if (!IsTransformShaped(part.Value))
            {
                keptNested++;
                continue;
            }
            part.Key.SetParent(root, true);
        }
        DeleteEmptyGroups(root);

        foreach (KeyValuePair<Transform, Matrix4x4> part in placements)
        {
            Matrix4x4 now = root.worldToLocalMatrix * part.Key.localToWorldMatrix;
            if (!Close(now, part.Value))
                throw new InvalidOperationException($"flattening moved '{part.Key.name}' — its placement changed.");
        }
        return keptNested;
    }

    // Shear is exactly what makes a matrix's three axes stop being perpendicular.
    private static bool IsTransformShaped(Matrix4x4 m)
    {
        Vector3 x = m.GetColumn(0);
        Vector3 y = m.GetColumn(1);
        Vector3 z = m.GetColumn(2);
        float sx = x.magnitude, sy = y.magnitude, sz = z.magnitude;
        if (sx < 1e-8f || sy < 1e-8f || sz < 1e-8f) return false;
        const float Tolerance = 1e-4f;
        return Mathf.Abs(Vector3.Dot(x, y)) / (sx * sy) < Tolerance &&
               Mathf.Abs(Vector3.Dot(y, z)) / (sy * sz) < Tolerance &&
               Mathf.Abs(Vector3.Dot(z, x)) / (sz * sx) < Tolerance;
    }

    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 16; i++)
        {
            if (Mathf.Abs(a[i] - b[i]) > 1e-4f * (1f + Mathf.Abs(b[i]))) return false;
        }
        return true;
    }

    // Deepest-first, so a group is looked at only after everything under it has had its chance to go.
    private static void DeleteEmptyGroups(Transform root)
    {
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = all.Length - 1; i >= 0; i--)
        {
            Transform t = all[i];
            if (t == null || t == root || t.childCount > 0) continue;
            if (t.GetComponent<MeshRenderer>() != null) continue;
            Object.DestroyImmediate(t.gameObject);
        }
    }

    // A part with no mesh, an empty material slot, or a shader that is missing draws nothing or draws
    // magenta — on the first screen of the app. Reported rather than refused, because the same part is
    // already that way on the field and refusing would take the whole robot off the stage over it;
    // Validate Home Stage fails on it.
    //
    // Deliberately NOT checked: material alpha. An opaque URP Lit surface writes alpha 1 whatever its base
    // colour says (the URP shader library's OutputAlpha passes alpha through only for transparent
    // surfaces), and a transparent part — polycarbonate — is SUPPOSED to show the backdrop through it.
    private static void CheckDrawable(GameObject root, Stats stats)
    {
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) stats.warnings.Add($"'{renderer.name}' has no mesh.");
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null) stats.warnings.Add($"'{renderer.name}' has an empty material slot.");
                else if (material.shader == null || material.shader.name == "Hidden/InternalErrorShader")
                    stats.warnings.Add($"'{renderer.name}': material '{material.name}' has no usable shader.");
            }
        }
    }

    // The catalog now names prefabs that were created in this same run — the situation that once saved the
    // home scene's catalog reference as {fileID: 0} and shipped a dead model list (see
    // BuildHomeScene.VerifySavedWiring). Read the file back and prove every reference landed.
    private static void VerifyOnDisk(List<GameObject> showcases)
    {
        if (showcases.Count == 0) return;
        string text = File.ReadAllText(RoboSimPaths.RobotModelCatalog);
        foreach (GameObject showcase in showcases)
        {
            string guid = showcase != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(showcase)) : null;
            if (string.IsNullOrEmpty(guid) || !text.Contains(guid))
                throw new InvalidOperationException(
                    $"{Title}: the catalog on disk does not reference '{(showcase != null ? showcase.name : "a showcase")}' — " +
                    "the stage would show only the robot's name.");
        }
    }

    private static string ShowcasePath(string id)
    {
        var safe = new StringBuilder(id.Length);
        foreach (char c in id) safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return $"{RoboSimPaths.ShowcaseFolder}/{safe}_Showcase.prefab";
    }

    private static void EnsureFolder()
    {
        if (AssetDatabase.IsValidFolder(RoboSimPaths.ShowcaseFolder)) return;
        string parent = Path.GetDirectoryName(RoboSimPaths.ShowcaseFolder).Replace('\\', '/');
        AssetDatabase.CreateFolder(parent, Path.GetFileName(RoboSimPaths.ShowcaseFolder));
    }
}
