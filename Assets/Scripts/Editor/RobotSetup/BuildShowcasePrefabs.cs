using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
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
//   - MERGED to ONE DRAW PER MATERIAL by MergeByMaterial — 576 parts of 654V v3 become 2 meshes. This is
//     the whole per-frame cost of the stage: measured on a phone, the home screen cost 15.84 ms of GPU a
//     frame with the robot turning and 1.63 ms with it off (Docs/Device-Performance.md, 2026-09-17).
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
    private const string BakeVersion = "showcase-2";

    // What one showcase may cost, counted in PARTS KEPT — before MergeByMaterial folds them into one draw
    // per material. It stopped being a per-frame cost when the merge landed, but it is still the honest
    // measure of how much geometry a robot brings: the merge keeps every triangle, so what a big part count
    // buys you now is vertices rather than draws. Over the budget is reported; over the limit, the robot
    // gets no showcase and goes on the stage as its name over the chassis mark.
    // Baseline: 361 / 612 / 737 / 576.
    public const int RendererBudget = 800;
    public const int RendererLimit = 900;

    // What one showcase may DRAW, in triangles, and the number that actually governs. A phone frame is
    // normally budgeted around 300,000; the stage draws one of these thirty times a second.
    //
    // Measured 2026-09-17 (Geometry Census): every robot that ships is raw CAD, 2.9M to 10.8M triangles a
    // showcase, because ReduceRobotMeshes has never been run on any of them. Merging those into one mesh
    // per material writes the same geometry out where you can see it — 2.5 GB of mesh assets for four
    // robots — so the merge refuses rather than produce that. The limit sits above a robot decimated at
    // the tool's default 0.08 (654V v3 lands near 750k) and far below any of them undecimated.
    public const int TriangleLimit = 1_500_000;

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
        public int partsKept;
        public int renderersAfter;
        public int objectsAfter;
        public int triangles;
        public int vertices;
        public readonly List<string> warnings = new List<string>();

        public string Describe(string robot) =>
            $"  {robot}: {renderersBefore} -> {partsKept} parts ({fasteners} fastener, {hidden} hidden) -> " +
            $"{renderersAfter} draw{(renderersAfter == 1 ? string.Empty : "s")}, " +
            $"{triangles:N0} triangles, {vertices:N0} vertices, {objectsAfter} objects, " +
            $"{stripped} components stripped" +
            (keptNested > 0 ? $", {keptNested} were left nested before the merge" : string.Empty) +
            (partsKept > RendererBudget ? $" — OVER the {RendererBudget} part budget" : string.Empty) +
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

    // How many triangles a correct bake of this robot keeps. Validate Home Stage measures the merged
    // meshes against it: once the parts are merged, counting renderers proves nothing — there is one per
    // material by construction — but every triangle the cull kept still has to be there. Skips exactly
    // what MergeByMaterial skips, or the two numbers would not be comparable.
    internal static int ExpectedTriangles(GameObject source)
    {
        long indices = 0;
        foreach (MeshRenderer renderer in source.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (IsFastenerPart(renderer.transform, source.transform) || IsHidden(renderer, source.transform)) continue;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) continue;
            Material[] materials = renderer.sharedMaterials;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (sub < materials.Length && materials[sub] != null) indices += mesh.GetIndexCount(sub);
            }
        }
        return (int)(indices / 3);
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
                Stats stats = BakeOne(entry.prefab, path, MeshesPath(entry.id));
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

    private static Stats BakeOne(GameObject source, string path, string meshPath)
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

            // Counted, checked and reported while the parts still exist and still have their names — after
            // the merge there is one object per material and nothing left to name.
            stats.partsKept = copy.GetComponentsInChildren<MeshRenderer>(true).Length;
            CheckDrawable(copy, stats);
            if (stats.partsKept > RendererLimit)
                throw new InvalidOperationException(
                    $"{stats.partsKept} parts after the cull, over the limit of {RendererLimit}.");

            MergeByMaterial(copy, meshPath, stats);
            RobotShowcase.PrepareRenderers(copy);

            // Every robot prefab root sits 12-16 units from the origin, where it was saved from a scene, and
            // the stage is built around the origin. Rotation and scale stay: they are the robot's own.
            root.localPosition = Vector3.zero;

            stats.renderersAfter = copy.GetComponentsInChildren<MeshRenderer>(true).Length;
            stats.objectsAfter = copy.GetComponentsInChildren<Transform>(true).Length;

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

    // ONE DRAW PER MATERIAL, instead of one per CAD part.
    //
    // A showcase has no ArticulationBody, no Rigidbody and no collider — RobotShowcase.StripToRenderers
    // takes all of that off — so nothing in it can move relative to anything else in it, ever. Its parts
    // are separate only because that is the shape the CAD came in. 654V v3 arrives as 576 of them built
    // from two materials.
    //
    // Why it is worth doing: measured on an iPhone 13 (Docs/Device-Performance.md, 2026-09-17), the home
    // screen cost 15.84 ms of GPU a frame with the robot turning and 1.63 ms with the stage switched off,
    // on a phone that had 16.7 ms to spend. It is not fill rate — render scale is already 0.8 and MSAA is
    // off — it is the per-draw overhead of hundreds of small draws, and the rasteriser work wasted on CAD
    // triangles small enough to cost a 2x2 quad each.
    //
    // Per SUBMESH, not per renderer: a dozen to two dozen parts on each robot carry two materials, and
    // bucketing on the renderer's whole material array would leave every one of those drawing on its own.
    //
    // Vertices are baked into ROOT-LOCAL space, which also settles what Flatten had to give up on. A part
    // under a non-uniform scale that is rotated relative to it has a sheared placement no Transform can
    // hold, so the flatten leaves those nested rather than deform them (see Flatten). A vertex does not
    // care: the shear is in the matrix it is multiplied through, and CombineMeshes carries normals and
    // tangents through the inverse transpose of that same matrix. None of the four shipping robots has a
    // mirrored part, which is the case this would get wrong — a negative determinant flips winding, and
    // CombineMeshes does not reverse the triangles to match.
    private static void MergeByMaterial(GameObject copy, string meshPath, Stats stats)
    {
        Transform root = copy.transform;
        var order = new List<Material>();
        var groups = new Dictionary<Material, List<CombineInstance>>();
        var expectedIndices = new Dictionary<Material, long>();

        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) continue;                         // CheckDrawable has already reported it

            Matrix4x4 toRoot = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Material[] materials = renderer.sharedMaterials;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                Material material = sub < materials.Length ? materials[sub] : null;
                if (material == null) continue;                 // likewise
                if (!groups.TryGetValue(material, out List<CombineInstance> group))
                {
                    group = new List<CombineInstance>();
                    groups.Add(material, group);
                    expectedIndices.Add(material, 0L);
                    order.Add(material);
                }
                group.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub, transform = toRoot });
                expectedIndices[material] += mesh.GetIndexCount(sub);
            }
        }

        if (order.Count == 0)
            throw new InvalidOperationException("nothing is left to draw after the cull.");

        long totalIndices = 0;
        foreach (long count in expectedIndices.Values) totalIndices += count;
        long totalTriangles = totalIndices / 3;
        if (totalTriangles > TriangleLimit)
            throw new InvalidOperationException(
                $"{totalTriangles:N0} triangles, over the limit of {TriangleLimit:N0} — this robot's meshes " +
                "have not been reduced. Run Tools > RoboSim > Robot > Reduce Robot Meshes on it first " +
                "(colliders are untouched, so it cannot change how the robot drives). Merging raw CAD here " +
                "writes a second copy of every triangle: measured, that is 218-230 MB for one robot.");

        // The parts go before the merged meshes arrive, so what is counted afterwards is the merge alone.
        for (int i = root.childCount - 1; i >= 0; i--) Object.DestroyImmediate(root.GetChild(i).gameObject);
        if (copy.GetComponent<MeshRenderer>() is MeshRenderer onRoot) Object.DestroyImmediate(onRoot);
        if (copy.GetComponent<MeshFilter>() is MeshFilter filterOnRoot) Object.DestroyImmediate(filterOnRoot);

        var meshes = new List<Mesh>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            Material material = order[i];
            var merged = new Mesh { name = $"{copy.name}_{i}_{material.name}" };

            // Combined CAD runs to hundreds of thousands of vertices and 16-bit indices stop at 65,535.
            merged.indexFormat = IndexFormat.UInt32;
            merged.CombineMeshes(groups[material].ToArray(), true, true, false);

            // The robots' FBX are imported with Read/Write OFF. Reading them works here because the editor
            // still holds the imported data — but if that ever stops being true, CombineMeshes returns an
            // empty mesh and logs nothing, and the first screen of the app goes blank. Count the indices in
            // and out rather than trust it.
            long got = 0;
            for (int sub = 0; sub < merged.subMeshCount; sub++) got += merged.GetIndexCount(sub);
            if (got != expectedIndices[material])
                throw new InvalidOperationException(
                    $"merging '{material.name}' produced {got:N0} indices out of {expectedIndices[material]:N0} in — " +
                    "the source meshes did not read back. Turn Read/Write on for this robot's FBX.");

            merged.RecalculateBounds();
            merged.Optimize();
            meshes.Add(merged);

            var part = new GameObject(merged.name, typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(root, false);
            part.GetComponent<MeshFilter>().sharedMesh = merged;
            part.GetComponent<MeshRenderer>().sharedMaterial = material;

            stats.triangles += (int)(got / 3);
            stats.vertices += merged.vertexCount;
        }

        // A mesh made in memory has to be an asset before the prefab referencing it is saved, or the prefab
        // saves a null mesh and the stage draws nothing. One file holds all of a robot's merged meshes.
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(meshes[0], meshPath);
        for (int i = 1; i < meshes.Count; i++) AssetDatabase.AddObjectToAsset(meshes[i], meshPath);
        AssetDatabase.SaveAssets();
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

    // The merged meshes, one file per robot beside its showcase. A sub-asset of the prefab would be
    // tidier, but SaveAsPrefabAsset rewrites the file and would take them with it on every re-bake.
    private static string MeshesPath(string id)
    {
        var safe = new StringBuilder(id.Length);
        foreach (char c in id) safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return $"{RoboSimPaths.ShowcaseFolder}/{safe}_ShowcaseMeshes.asset";
    }

    private static void EnsureFolder()
    {
        if (AssetDatabase.IsValidFolder(RoboSimPaths.ShowcaseFolder)) return;
        string parent = Path.GetDirectoryName(RoboSimPaths.ShowcaseFolder).Replace('\\', '/');
        AssetDatabase.CreateFolder(parent, Path.GetFileName(RoboSimPaths.ShowcaseFolder));
    }
}
