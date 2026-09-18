using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Side-by-side pictures of what Reduce Robot Meshes did to a part: original on the left, reduced on the
// right, same camera, same light, same size.
//
// This exists because the ratio is a judgement about how a part LOOKS and the person making the call is
// not always at the machine with the editor open. Connor, 2026-09-17, from doing this in Blender before:
// "at .05 it was mostly good from a far distance but zoom in you can see that like the c-channel holes get
// corrupted". A number in a report cannot answer that. A picture of the c-channel can.
//
// PAIRING comes from git, not from names. The decimated assets are named after their source and the CAD
// exporter names nearly everything "Body1", so matching by name is guesswork. Reduce Robot Meshes rewrites
// the robot prefab's mesh references in place, one for one, in order — so the Nth `m_Mesh:` line before and
// the Nth after are the same part, exactly. Pairs.py in the scratchpad writes that list; this reads it.
//
// The parts worth looking at are the ones the RATIO bit, not the ones that stopped on the error ceiling:
// those stopped because their shape was already as simple as it goes. Sorted by triangles removed.
//
// Batch: -executeMethod DecimationPreview.RunBatch -pairs <json> -out <folder> [-shots 6] [-size 900]
public static class DecimationPreview
{
    private const string Title = "Decimation Preview";

    [Serializable]
    private class Pair
    {
        public string before;   // "guid:fileID" of the source mesh
        public string after;    // asset path of the reduced mesh
    }

    [Serializable]
    private class PairList
    {
        public Pair[] pairs;
    }

    public static void RunBatch()
    {
        string pairs = Argument("-pairs");
        string outFolder = Argument("-out");
        if (string.IsNullOrEmpty(pairs) || string.IsNullOrEmpty(outFolder))
            throw new ArgumentException($"{Title}.RunBatch needs -pairs <json> and -out <folder>.");

        int shots = int.TryParse(Argument("-shots"), out int s) ? s : 6;
        int size = int.TryParse(Argument("-size"), out int p) ? p : 900;

        try
        {
            Debug.Log(Run(pairs, outFolder, shots, size));
        }
        catch (Exception e)
        {
            Debug.LogError($"{Title} FAILED: {e}");
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Argument(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static string Run(string pairsJson, string outFolder, int shots, int size)
    {
        PairList list = JsonUtility.FromJson<PairList>(File.ReadAllText(pairsJson));
        if (list?.pairs == null || list.pairs.Length == 0) throw new InvalidOperationException("no pairs in " + pairsJson);

        // Load both sides, keep only the pairs where the ratio actually bit, biggest reduction first.
        var loaded = new List<(Mesh before, Mesh after, int removed)>();
        foreach (Pair pair in list.pairs)
        {
            Mesh before = LoadByReference(pair.before);
            var after = AssetDatabase.LoadAssetAtPath<Mesh>(pair.after);
            if (before == null || after == null) continue;
            int removed = Triangles(before) - Triangles(after);
            if (removed <= 0) continue;
            loaded.Add((before, after, removed));
        }
        loaded.Sort((a, b) => b.removed.CompareTo(a.removed));

        Directory.CreateDirectory(outFolder);
        var report = new StringBuilder();
        report.AppendLine($"{Title}: {loaded.Count} reduced meshes, writing the {Math.Min(shots, loaded.Count)} biggest.");
        report.AppendLine($"{"part",-30} {"tris in",10} {"tris out",10} {"kept",6}");

        var preview = new PreviewRenderUtility();
        try
        {
            for (int i = 0; i < loaded.Count && i < shots; i++)
            {
                (Mesh before, Mesh after, int _) = loaded[i];
                string name = Sanitize(after.name);
                string path = Path.Combine(outFolder, $"{i:00}-{name}.png");

                // Both halves framed on the ORIGINAL's bounds, so a part that shrank shows it.
                Texture2D left = Shot(preview, before, before.bounds, size);
                Texture2D right = Shot(preview, after, before.bounds, size);
                File.WriteAllBytes(path, SideBySide(left, right).EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(left);
                UnityEngine.Object.DestroyImmediate(right);

                int inTris = Triangles(before), outTris = Triangles(after);
                report.AppendLine($"{Trim(name, 30),-30} {inTris,10:N0} {outTris,10:N0} {100f * outTris / inTris,5:F0} %");
            }
        }
        finally
        {
            preview.Cleanup();
        }

        report.AppendLine();
        report.AppendLine($"Written to {outFolder} — original on the left, reduced on the right.");
        return report.ToString();
    }

    // Three-quarter view from above, close enough that the part fills the frame. The same angle for both
    // halves of a pair, and framed on the original, so the only thing that differs is the geometry.
    private static Texture2D Shot(PreviewRenderUtility preview, Mesh mesh, Bounds frame, int size)
    {
        float radius = Mathf.Max(frame.extents.magnitude, 1e-4f);
        Vector3 direction = new Vector3(0.6f, 0.45f, -1f).normalized;

        preview.BeginStaticPreview(new Rect(0f, 0f, size, size));
        preview.camera.transform.position = frame.center + direction * radius * 2.6f;
        preview.camera.transform.LookAt(frame.center);
        preview.camera.nearClipPlane = radius * 0.05f;
        preview.camera.farClipPlane = radius * 20f;
        preview.camera.fieldOfView = 30f;
        preview.camera.clearFlags = CameraClearFlags.SolidColor;
        preview.camera.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);

        // Two lights: a key across the surface so edges read, and a dim fill so the shadow side is not
        // black. A hole reads as a hole only if its inside wall catches something.
        preview.lights[0].intensity = 1.1f;
        preview.lights[0].transform.rotation = Quaternion.Euler(35f, 135f, 0f);
        preview.lights[1].intensity = 0.45f;
        preview.lights[1].transform.rotation = Quaternion.Euler(-20f, -60f, 0f);
        preview.ambientColor = new Color(0.22f, 0.23f, 0.26f, 1f);

        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetColor("_BaseColor", new Color(0.62f, 0.64f, 0.68f, 1f));
        material.SetFloat("_Smoothness", 0.35f);
        try
        {
            preview.DrawMesh(mesh, Matrix4x4.identity, material, 0);
            preview.camera.Render();
            return preview.EndStaticPreview();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(material);
        }
    }

    // One image, original then reduced, with a one-pixel seam so the join is unambiguous.
    private static Texture2D SideBySide(Texture2D left, Texture2D right)
    {
        int w = left.width, h = left.height;
        var joined = new Texture2D(w * 2 + 1, h, TextureFormat.RGB24, false);
        joined.SetPixels(0, 0, w, h, left.GetPixels());
        joined.SetPixels(w + 1, 0, w, h, right.GetPixels());
        var seam = new Color[h];
        for (int i = 0; i < h; i++) seam[i] = Color.black;
        joined.SetPixels(w, 0, 1, h, seam);
        joined.Apply();
        return joined;
    }

    // "guid:fileID" — the same two numbers the prefab stores, which is the only way to name one mesh
    // inside an FBX that holds hundreds of them under repeated names.
    private static Mesh LoadByReference(string reference)
    {
        int split = reference.IndexOf(':');
        if (split <= 0) return null;
        string guid = reference.Substring(0, split);
        if (!long.TryParse(reference.Substring(split + 1), out long fileId)) return null;

        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path)) return null;
        foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (asset is not Mesh mesh) continue;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string _, out long id) && id == fileId)
                return mesh;
        }
        return null;
    }

    private static int Triangles(Mesh mesh)
    {
        int total = 0;
        for (int sub = 0; sub < mesh.subMeshCount; sub++) total += (int)(mesh.GetIndexCount(sub) / 3);
        return total;
    }

    private static string Sanitize(string name)
    {
        var safe = new StringBuilder(name.Length);
        foreach (char c in name) safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return safe.ToString();
    }

    private static string Trim(string value, int width) =>
        value.Length <= width ? value : value.Substring(0, width - 1) + "…";
}
