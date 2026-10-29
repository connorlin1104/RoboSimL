using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Decides, part by part, whether a decimated mesh still LOOKS like the original — by rendering both
// and comparing the pictures.
//
// WHY PICTURES AND NOT THE ERROR NUMBER: MeshDecimator's ceiling is a fraction of each part's own
// diagonal. That is right for a wheel and wrong for a c-channel: on a 25-hole channel 0.4 % of the
// length is about one hole, so the decimator is allowed to pinch holes shut and pull slivers across
// them while reporting a tiny error. 654V v3 at 0.1 shipped exactly that (shredded channels, perfect
// wheels). "Does it look the same" is the actual question, so this asks it directly.
//
// HOW: orthographic views from the six axes and four diagonals, each TILED so a tile is about as wide
// as the part's middle dimension. That keeps a long channel's holes ~30 px across instead of letting
// them shrink to a pixel in one whole-part shot, where a closed hole would vanish from the score.
// A pixel is "different" when any channel moves by more than PixelTolerance, and only counts if its
// whole 3x3 neighbourhood is different too — that erosion throws away the 1 px edge shifts every
// vertex move causes, and keeps a closed hole or a sliver, which are tens of pixels across.
// The score is the WORST tile's different pixels over the pixels either image covers.
public sealed class DecimationVisualCheck : System.IDisposable
{
    private const int TileSize = 160;
    private const int PixelTolerance = 48;
    private const int MaxTilesPerView = 40;
    private const float MinCoverage = 0.01f;

    private static readonly Vector3[] Views =
    {
        Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
        new Vector3(1f, 1f, 1f), new Vector3(-1f, 1f, -1f), new Vector3(1f, -1f, -1f), new Vector3(-1f, -1f, 1f),
    };

    private readonly PreviewRenderUtility preview;
    private readonly Material material;

    // Each reference's tiles, rendered once and compared against every candidate ratio. Holds the two
    // references of the part being reduced; a third mesh means the caller moved on, so it starts over.
    private readonly Dictionary<Mesh, List<Tile>> cache = new Dictionary<Mesh, List<Tile>>();

    private struct Tile
    {
        public Vector3 position;
        public Quaternion rotation;
        public float halfSize;
        public float near, far;
        public Color32[] pixels;
    }

    public DecimationVisualCheck()
    {
        preview = new PreviewRenderUtility();
        preview.camera.orthographic = true;
        preview.camera.clearFlags = CameraClearFlags.SolidColor;
        preview.camera.backgroundColor = Color.black;
        preview.lights[0].intensity = 1.1f;
        preview.lights[0].transform.rotation = Quaternion.Euler(35f, 135f, 0f);
        preview.lights[1].intensity = 0.45f;
        preview.lights[1].transform.rotation = Quaternion.Euler(-20f, -60f, 0f);
        preview.ambientColor = new Color(0.22f, 0.23f, 0.26f, 1f);

        material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetColor("_BaseColor", new Color(0.62f, 0.64f, 0.68f, 1f));
        material.SetFloat("_Smoothness", 0.35f);
        // The first draw out of a fresh PreviewRenderUtility comes back wrong; spend it on nothing.
        Render(new Tile { rotation = Quaternion.identity, halfSize = 1f, near = 0.01f, far = 10f },
            Resources.GetBuiltinResource<Mesh>("Cube.fbx"));
    }

    public void Dispose()
    {
        preview.Cleanup();
        Object.DestroyImmediate(material);
    }

    // 0 = identical pictures. Fraction of covered pixels that differ, in the worst tile.
    public float Score(Mesh original, Mesh candidate)
    {
        if (!cache.TryGetValue(original, out List<Tile> tiles))
        {
            if (cache.Count >= 2) cache.Clear();
            tiles = Plan(original.bounds);
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile t = tiles[i];
                t.pixels = Render(t, original);
                tiles[i] = t;
            }
            cache[original] = tiles;
        }

        float worst = 0f;
        foreach (Tile t in tiles)
        {
            float score = Compare(t.pixels, Render(t, candidate));
            if (score > worst) worst = score;
        }
        return worst;
    }

    private static List<Tile> Plan(Bounds bounds)
    {
        var tiles = new List<Tile>();
        Vector3 size = bounds.size;
        float[] dims = { size.x, size.y, size.z };
        System.Array.Sort(dims);
        float radius = Mathf.Max(bounds.extents.magnitude, 1e-5f);
        // Middle dimension, so a channel is tiled along its length; floored so a long thin rod does
        // not turn into hundreds of tiles of nothing.
        float tileWorld = Mathf.Max(dims[1], dims[2] / 30f, 1e-5f);

        foreach (Vector3 view in Views)
        {
            Vector3 forward = -view.normalized;
            Vector3 helper = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(forward, helper);
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;

            float minR = float.MaxValue, maxR = float.MinValue, minU = float.MaxValue, maxU = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                float r = Vector3.Dot(corner - bounds.center, right), u = Vector3.Dot(corner - bounds.center, up);
                minR = Mathf.Min(minR, r); maxR = Mathf.Max(maxR, r);
                minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
            }

            float side = tileWorld;
            int nx = Mathf.CeilToInt((maxR - minR) / side), ny = Mathf.CeilToInt((maxU - minU) / side);
            while (nx * ny > MaxTilesPerView)
            {
                side *= 1.25f;
                nx = Mathf.CeilToInt((maxR - minR) / side);
                ny = Mathf.CeilToInt((maxU - minU) / side);
            }

            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            {
                float r = minR + (ix + 0.5f) * side, u = minU + (iy + 0.5f) * side;
                tiles.Add(new Tile
                {
                    position = bounds.center + right * r + up * u - forward * radius * 2f,
                    rotation = rotation,
                    halfSize = side * 0.5f,
                    near = radius * 0.5f,
                    far = radius * 3.5f,
                });
            }
        }
        return tiles;
    }

    private Color32[] Render(Tile tile, Mesh mesh)
    {
        preview.BeginStaticPreview(new Rect(0, 0, TileSize, TileSize));
        Camera cam = preview.camera;
        cam.orthographic = true;
        cam.orthographicSize = tile.halfSize;
        cam.transform.SetPositionAndRotation(tile.position, tile.rotation);
        cam.nearClipPlane = tile.near;
        cam.farClipPlane = tile.far;
        for (int s = 0; s < mesh.subMeshCount; s++) preview.DrawMesh(mesh, Matrix4x4.identity, material, s);
        cam.Render();
        Texture2D texture = preview.EndStaticPreview();
        Color32[] pixels = texture.GetPixels32();
        Object.DestroyImmediate(texture);
        return pixels;
    }

    private static float Compare(Color32[] a, Color32[] b)
    {
        int n = TileSize;
        var bad = new bool[a.Length];
        int covered = 0;
        for (int i = 0; i < a.Length; i++)
        {
            bool inA = a[i].r + a[i].g + a[i].b > 6, inB = b[i].r + b[i].g + b[i].b > 6;
            if (inA || inB) covered++;
            bad[i] = Mathf.Abs(a[i].r - b[i].r) > PixelTolerance
                  || Mathf.Abs(a[i].g - b[i].g) > PixelTolerance
                  || Mathf.Abs(a[i].b - b[i].b) > PixelTolerance;
        }
        if (covered < MinCoverage * a.Length) return 0f;

        int different = 0;
        for (int y = 1; y < n - 1; y++)
        for (int x = 1; x < n - 1; x++)
        {
            int i = y * n + x;
            if (!bad[i]) continue;
            if (bad[i - 1] && bad[i + 1] && bad[i - n] && bad[i + n]
                && bad[i - n - 1] && bad[i - n + 1] && bad[i + n - 1] && bad[i + n + 1])
                different++;
        }
        return (float)different / covered;
    }
}
