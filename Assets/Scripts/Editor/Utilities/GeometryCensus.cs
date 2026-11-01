using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

// How much geometry each screen of the app actually draws.
//
// Written 2026-09-17, after the first on-device performance run measured the home screen at 15.84 ms of GPU
// a frame with the turntable on against 1.63 ms with it off, and LiteScene at 22 ms on a cool phone against
// a 16.7 ms budget (Docs/Device-Performance.md). The first guess was draw calls — hundreds of CAD parts,
// three materials between them. Merging them by material turned out to leave the cost where it was and
// produce 2.5 GB of mesh assets, which is how the real number came to light: these robots are millions of
// triangles each. Nothing in the project reported that, so nothing caught it.
//
// NOT a duplicate of RobotMeshReport, which answers the other half: that one counts each mesh ONCE and
// breaks a robot down per mesh and per vertex channel, because the question it was written for is which
// meshes to decimate. This one counts what each SCREEN draws per frame, including the field, which is the
// question the phone's GPU numbers ask. Reach for RobotMeshReport to pick targets, this to size the bill.
//
// Counts the two things that get confused with each other:
//   - DRAWN, per frame: every renderer's mesh counted every time it is drawn. This is the GPU's bill.
//   - UNIQUE: each mesh counted once however many renderers share it. This is the memory bill, and
//     Profiler.GetRuntimeMemorySizeLong is asked rather than estimated from the vertex count, because the
//     answer depends on which attributes and which index format the mesh actually carries.
//
// A phone frame is normally budgeted in the low hundreds of thousands of triangles. Read the totals against
// that, not against each other.
//
// Batch: -executeMethod GeometryCensus.RunBatch
public static class GeometryCensus
{
    private const string Title = "Geometry Census";

    // What a frame on a phone can reasonably push. Not a validator threshold — nothing fails on it — it is
    // printed beside each subject so the numbers mean something without having to be looked up.
    private const long PhoneFrameTriangles = 300_000;

    private class Subject
    {
        public string name;
        public int renderers;
        public long drawnTriangles;
        public long drawnVertices;
        public long uniqueTriangles;
        public long meshBytes;
        public int uniqueMeshes;
        public int materials;

        public string Describe()
        {
            string over = drawnTriangles > PhoneFrameTriangles
                ? $"  [{(double)drawnTriangles / PhoneFrameTriangles:F0}x a phone frame]"
                : string.Empty;
            return $"  {name,-34} {renderers,6} draws  {drawnTriangles,12:N0} tri  {drawnVertices,12:N0} vert  " +
                   $"{materials,4} mat  {uniqueMeshes,6} meshes  {meshBytes / (1024.0 * 1024.0),8:F1} MB{over}";
        }
    }

    [MenuItem("Tools/RoboSim/Validate/Geometry Census", false, 56)]
    private static void RunInteractive()
    {
        // Scenes are opened to be counted, so anything unsaved has to be dealt with first.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string open = SceneManager.GetActiveScene().path;
        try
        {
            string report = Run();
            Debug.Log(report);
            EditorUtility.DisplayDialog(Title, "Written to the Console.", "OK");
        }
        finally
        {
            if (!string.IsNullOrEmpty(open)) EditorSceneManager.OpenScene(open, OpenSceneMode.Single);
        }
    }

    // Deliberately no menu item on this one: a [MenuItem] on a RunBatch method quits the editor, unsaved.
    public static void RunBatch()
    {
        try
        {
            Debug.Log(Run());
        }
        catch (Exception e)
        {
            Debug.LogError(Title + " FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Run()
    {
        var report = new StringBuilder();
        report.AppendLine($"{Title}: drawn = every renderer counted every frame; unique = each mesh once.");
        report.AppendLine($"A phone frame is normally budgeted around {PhoneFrameTriangles:N0} triangles.");

        report.AppendLine();
        report.AppendLine("SCENES (as they sit on disk — the robot is spawned into the field at runtime, on top of this)");
        foreach (EditorBuildSettingsScene entry in EditorBuildSettings.scenes)
        {
            if (!entry.enabled) continue;
            Scene scene = EditorSceneManager.OpenScene(entry.path, OpenSceneMode.Single);
            var subject = new Subject { name = scene.name };
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            foreach (GameObject root in scene.GetRootGameObjects())
                Count(root, subject, meshes, materials);
            Finish(subject, meshes, materials);
            report.AppendLine(subject.Describe());
        }

        report.AppendLine();
        report.AppendLine("ROBOTS (drivable prefabs — what gets spawned into a field)");
        report.Append(Catalog(entry => entry.prefab));

        report.AppendLine();
        report.AppendLine("SHOWCASES (the home screen's turntable — one of these is drawn 30 times a second)");
        report.Append(Catalog(entry => entry.showcasePrefab));

        return report.ToString().TrimEnd();
    }

    private static string Catalog(Func<RobotModelCatalog.Entry, GameObject> pick)
    {
        RobotModelCatalog catalog = RoboSimPaths.LoadRobotCatalog();
        if (catalog == null) return $"  no RobotModelCatalog at {RoboSimPaths.RobotModelCatalog}.\n";

        var lines = new StringBuilder();
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null) continue;
            GameObject prefab = pick(entry);
            if (prefab == null) continue;

            var subject = new Subject { name = entry.displayName };
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            Count(prefab, subject, meshes, materials);
            Finish(subject, meshes, materials);
            lines.AppendLine(subject.Describe());
        }
        return lines.ToString();
    }

    // Inactive renderers included: a part switched off in the prefab is still shipped, and the field turns
    // things on at runtime.
    private static void Count(GameObject root, Subject subject, HashSet<Mesh> meshes, HashSet<Material> materials)
    {
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) continue;

            subject.renderers++;
            subject.drawnVertices += mesh.vertexCount;
            for (int sub = 0; sub < mesh.subMeshCount; sub++) subject.drawnTriangles += mesh.GetIndexCount(sub) / 3;
            meshes.Add(mesh);
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null) materials.Add(material);
            }
        }
    }

    private static void Finish(Subject subject, HashSet<Mesh> meshes, HashSet<Material> materials)
    {
        subject.uniqueMeshes = meshes.Count;
        subject.materials = materials.Count;
        foreach (Mesh mesh in meshes)
        {
            subject.meshBytes += Profiler.GetRuntimeMemorySizeLong(mesh);
            for (int sub = 0; sub < mesh.subMeshCount; sub++) subject.uniqueTriangles += mesh.GetIndexCount(sub) / 3;
        }
    }
}
