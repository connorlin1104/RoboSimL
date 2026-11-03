using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Reduces the field's render meshes the same way Reduce Robot Meshes reduces a robot: each mesh is
// decimated, rendered against the original up close, and only kept if it looks the same (gentler
// ratios first, then full detail). Every field mesh comes straight out of OverrideFieldVersion3.fbx,
// and SampleScene drew 6.2M triangles of it a frame (LiteScene 2.7M) before this ran.
//
// What it does NOT change, on purpose:
// - Physics. A MeshCollider holds its own reference to the FBX mesh, so repointing the MeshFilter next
//   to it leaves the collider exactly as it was. 134 field colliders shared their render mesh; they
//   still use the full-detail original
// - Bounds. PieceGeometry and ClawGrab read a piece's mesh bounds to stand it up and to space carried
//   pieces, so every reduced mesh is given its source's bounds back. Gameplay reads identical numbers
// - Parts that fail the look check at every ratio. They keep pointing at the FBX, so no copy of them
//   is written; a full-detail copy would only add to Git LFS for nothing
//
// Both shipped scenes are repointed from one reduction: LiteScene is built from SampleScene and holds
// the same FBX meshes, so rebuilding it (and re-settling its pieces) is not needed.
//
// Reduced meshes are written to Assets/FieldMeshes as <mesh name>_<id in the FBX>.asset, so a run that
// stops part way can be started again and picks up the meshes already written instead of redoing them.
public static class ReduceFieldMeshes
{
    private const string Title = "Reduce Field Meshes";
    private const string MeshFolder = "Assets/FieldMeshes";

    // The field is held to a stricter look than a robot, because a player drives right up to it. Measured
    // 2026-11-02 with the robots' settings (1 %, tolerance 48, each part tiled at its own size): a goal
    // passed at 0.35 and, rendered from 4 units away, its rim highlight pinched and its engraved rings
    // changed. The engravings are a pixel or two wide in a whole-part tile and the check's erosion threw
    // them away. So the field asks:
    // - at the closest the chase camera gets: 4 units (minDistance 6 from the robot's middle, less for
    //   a part beside it), 60 degree FOV, 936 px tall (an iPhone 13 at render scale 0.8) is about 200 px
    //   per world unit, converted to each mesh's own units by the largest scale it is drawn at
    // - with a smaller pixel tolerance, which catches a highlight moving as well as a hole closing
    // - at 0.5 % instead of 1 %, with one more gentle step (0.7) before keeping every triangle
    private const float ClosestPixelsPerWorldUnit = 200f;
    private const int FieldPixelTolerance = 24;
    private const float FieldLookLimit = 0.005f;
    private static readonly float[] FieldGentlerRatios = { 0.2f, 0.35f, 0.5f, 0.7f };

    // Meshes smaller than this are left at full detail. Even with the stricter look above, a goal's
    // engraved rings (separate meshes of a few hundred triangles) came out a plain circle where they were
    // a hexagon: the check forgives any difference under 3 px wide, which is a thin engraved line
    // entirely. Every mesh that broke was under 1,000 triangles, and all the meshes under 2,000 together
    // only saved 43k of SampleScene's 6.2M, so they are not worth the risk.
    private const int MinTrianglesToReduce = 2000;

    [MenuItem("Tools/RoboSim/Field & Pieces/Reduce Field Meshes", false, 56)]
    private static void RunFromMenu()
    {
        if (!EditorUtility.DisplayDialog(Title,
                "Reduces the field's render meshes in SampleScene and LiteScene, keeping only reductions that " +
                "look the same. This takes a long time and saves both scenes. Continue?", "Reduce", "Cancel"))
            return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string previous = EditorSceneManager.GetActiveScene().path;
        try { Debug.Log(Run()); }
        finally
        {
            if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
        }
    }

    // Batch: -executeMethod ReduceFieldMeshes.RunBatch
    public static void RunBatch()
    {
        Debug.Log(Run());
        EditorApplication.Exit(0);
    }

    internal static string Run()
    {
        EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);
        var sources = new List<Mesh>();
        var seen = new HashSet<Mesh>();
        var drawnScale = new Dictionary<Mesh, float>();
        foreach (MeshFilter filter in SceneFilters())
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || !IsImported(mesh)) continue;
            Vector3 s = filter.transform.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            drawnScale[mesh] = Mathf.Max(drawnScale.TryGetValue(mesh, out float had) ? had : 0f, scale);
            if (seen.Add(mesh)) sources.Add(mesh);
        }

        var report = new StringBuilder();
        report.AppendLine(Title);
        if (sources.Count == 0) return report.AppendLine("Every field mesh is already reduced; nothing to do.").ToString();

        if (!AssetDatabase.IsValidFolder(MeshFolder)) AssetDatabase.CreateFolder("Assets", "FieldMeshes");
        var replacements = new Dictionary<Mesh, Mesh>();
        long trisBefore = 0, trisAfter = 0;
        int reused = 0, gentler = 0, fullDetail = 0, unreadable = 0, small = 0;
        // The import's own normals, carried through: rebuilt by angle, even an unreduced corner piece
        // shaded differently from the import and failed the look check at every ratio.
        var options = new MeshDecimator.Options { keepSourceNormals = true };
        List<string> unlocked = ReduceRobotMeshes.MakeReadable(sources);
        try
        {
            using var check = new DecimationVisualCheck(FieldPixelTolerance);
            for (int i = 0; i < sources.Count; i++)
            {
                Mesh source = sources[i];
                if (EditorUtility.DisplayCancelableProgressBar(Title, $"{source.name}  ({i + 1}/{sources.Count})",
                        (float)i / sources.Count))
                {
                    report.AppendLine($"CANCELLED after {i} of {sources.Count}; what was written is kept and linked " +
                                      "and the counts below cover only those. Run it again to finish the rest.");
                    break;
                }
                int sourceTris = TriangleCount(source);
                trisBefore += sourceTris;

                string path = AssetPath(source);
                if (sourceTris < MinTrianglesToReduce)
                {
                    // An earlier run may have reduced it; that copy is stale, and the renderer goes back to the FBX.
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                    trisAfter += sourceTris;
                    small++;
                    continue;
                }
                Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null)
                {
                    replacements[source] = existing;
                    trisAfter += TriangleCount(existing);
                    reused++;
                    continue;
                }

                float closest = DecimationVisualCheck.AtLeast(source.bounds, ClosestPixelsPerWorldUnit * drawnScale[source]);
                MeshDecimator.Result result = ReduceRobotMeshes.SimplifyLookingTheSame(source, options, check, out string verdict,
                    closest, FieldLookLimit, FieldGentlerRatios);
                if (!string.IsNullOrEmpty(result.note) || verdict == ReduceRobotMeshes.FullDetailVerdict)
                {
                    if (result.mesh != null && result.mesh != source) Object.DestroyImmediate(result.mesh);
                    if (string.IsNullOrEmpty(result.note)) fullDetail++; else unreadable++;
                    trisAfter += sourceTris;
                    continue;
                }
                if (verdict != null) gentler++;

                result.mesh.bounds = source.bounds;
                AssetDatabase.CreateAsset(result.mesh, path);
                replacements[source] = result.mesh;
                trisAfter += result.triangles;
                if (i % 50 == 49) AssetDatabase.SaveAssets();
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            ReduceRobotMeshes.RestoreReadable(unlocked);
        }

        report.AppendLine($"{sources.Count} field meshes: {replacements.Count} reduced ({reused} already written by an " +
                          $"earlier run, {gentler} at a gentler ratio), {fullDetail} kept full detail because reducing " +
                          $"them showed, {small} under {MinTrianglesToReduce:N0} triangles left alone, " +
                          $"{unreadable} the decimator could not read.");
        report.AppendLine($"triangles {trisBefore:N0} -> {trisAfter:N0} " +
                          $"({(trisBefore == 0 ? 0f : 1f - (float)trisAfter / trisBefore):P1} removed), SampleScene's field.");

        foreach (string scene in new[] { RoboSimPaths.MainScene, RoboSimPaths.LiteScene })
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            int repointed = 0, missed = 0, drawnBefore = 0, drawnAfter = 0, colliders = 0;
            foreach (MeshFilter filter in SceneFilters())
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                drawnBefore += TriangleCount(mesh);
                if (replacements.TryGetValue(mesh, out Mesh reduced))
                {
                    Undo.RecordObject(filter, Title);
                    filter.sharedMesh = reduced;
                    EditorUtility.SetDirty(filter);
                    repointed++;
                }
                else if (IsImported(mesh) && !seen.Contains(mesh)) missed++;
                drawnAfter += TriangleCount(filter.sharedMesh);
            }
            foreach (MeshCollider collider in Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (collider.sharedMesh != null && replacements.ContainsKey(collider.sharedMesh)) colliders++;
            EditorSceneManager.SaveOpenScenes();
            report.AppendLine($"{scene}: {repointed} renderers repointed, drawn triangles {drawnBefore:N0} -> {drawnAfter:N0}; " +
                              $"{colliders} MeshCollider(s) keep the full-detail original" +
                              (missed > 0 ? $"; {missed} imported mesh(es) not in SampleScene were left alone" : "") + ".");
        }
        return report.ToString();
    }

    private static MeshFilter[] SceneFilters() =>
        Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None);

    // A mesh straight out of a model file. Anything already written as a .asset (ours, or a generated
    // mesh like the floor tile slab) is left alone: decimating a decimated mesh compounds the error.
    private static bool IsImported(Mesh mesh) => AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mesh)) is ModelImporter;

    // CAD exports reuse names ("Body1"), so the id inside the model file is what makes this unique, and
    // it is stable across runs, which is what lets a stopped run resume.
    private static string AssetPath(Mesh source)
    {
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string _, out long id);
        var used = new HashSet<string>();
        return $"{MeshFolder}/{ReduceRobotMeshes.UniqueName(source.name, used)}_{(ulong)id:x}.asset";
    }

    private static int TriangleCount(Mesh mesh)
    {
        int count = 0;
        for (int i = 0; i < mesh.subMeshCount; i++) count += (int)mesh.GetIndexCount(i) / 3;
        return count;
    }
}
