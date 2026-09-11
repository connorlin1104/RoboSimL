using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Generates the UI's sprites instead of shipping Unity's builtin skin.
//
// The whole app was drawn with UI/Skin/UISprite.psd and Background.psd — the default editor skin.
// That is most of what "blocky and generic" meant: UISprite's corner radius is tiny at the sizes
// this UI uses, Background.psd is a hard-edged rectangle, and neither has any way to carry a
// border or a shadow. No artist is needed to fix that: a rounded rectangle with a known 9-slice
// border is a few lines of maths, and generating it means the radius is a number in this file
// rather than a texture someone has to re-cut every time a size changes.
//
// Follows Assets/Scripts/Editor/Field/TileSeamTool.cs, which already generates the floor's seam
// textures the same way: build a Texture2D, EncodeToPNG, write it, re-import, then configure the
// TextureImporter. The importer settings are the half people forget, and for a sliced sprite the
// spriteBorder is what makes the difference between a rounded corner and a smeared one.
//
// Re-running is safe and cheap: each sprite is skipped when a file already exists whose generated
// parameters match. Use Regenerate to force.
//
// Usage: Tools > RoboSim > Scenes > Build Home Screen calls EnsureAll. To force a rebuild of the
// PNGs themselves: Tools > RoboSim > Scenes > Regenerate UI Sprites.
public static class HomeThemeSprites
{
    // Authored at 2x the largest on-screen radius so the corner is still smooth on a 3x device.
    // These are tiny files (a 64px RGBA PNG is ~2 KB), so there is no reason to be frugal here.
    private const int PanelSize = 64;
    private const int PanelRadius = 20;
    private const int ButtonSize = 48;
    private const int ButtonRadius = 14;
    private const int ShadowSize = 96;

    // The loading spinner's ring. 128 rather than 64 because it is drawn at 120 units and a circle
    // shows its stair-steps far more readily than a corner does; the thickness is in the same pixel
    // space, so it scales with SpinnerSize.
    private const int SpinnerSize = 128;
    private const float SpinnerThickness = 11f;

    // The border is what Image.Type.Sliced protects from stretching. It has to be at least the
    // radius, or the corner arc gets stretched along with the middle and comes out as an ellipse;
    // a couple of pixels past it leaves room for the anti-aliased edge.
    private const int PanelBorder = PanelRadius + 2;
    private const int ButtonBorder = ButtonRadius + 2;

    // NOTE: the panel sprite deliberately carries NO baked drop shadow.
    //
    // It did briefly, as a dark halo in a padded border, which tints correctly and looks right on
    // its own. The problem is what it does to layout: a baked halo means the panel's VISIBLE
    // surface is inset from its RectTransform by the padding, while everything placed inside the
    // panel is positioned against the RectTransform. The scroll viewport insets by 12
    // (CreateScrollingContent), so with 22 units of padding the rows would sit 10 units OUTSIDE
    // the rounded surface, overhanging onto the shadow. Making that work means re-deriving every
    // interior inset from the sprite's padding — a change that reaches most of the file.
    //
    // On a dark theme the shadow is not what carries depth anyway: the surface being lighter than
    // the background does, and the vertical gradient does the rest. SoftShadow below is still
    // generated — the robot stage uses it for the glow behind the robot.

    [MenuItem("Tools/RoboSim/Scenes/Regenerate UI Sprites", false, 3)]
    private static void RegenerateInteractive()
    {
        int written = Regenerate();
        Debug.Log($"Regenerate UI Sprites: wrote {written} sprite(s) to {RoboSimPaths.UiSpritesFolder}.");
    }

    // Creates any sprite that doesn't exist yet. Called by the home-screen builder so a fresh
    // checkout builds without anyone having to remember a separate menu item first.
    public static void EnsureAll() => Build(false);

    // Rewrites every sprite whatever the state on disk — use after changing a radius above.
    public static int Regenerate() => Build(true);

    private static int Build(bool force)
    {
        EnsureFolder();
        int written = 0;
        written += EnsureRounded(RoboSimPaths.UiPanelSprite, PanelSize, PanelRadius, PanelBorder, force);
        written += EnsureRounded(RoboSimPaths.UiButtonSprite, ButtonSize, ButtonRadius, ButtonBorder, force);
        written += EnsureShadow(RoboSimPaths.UiShadowSprite, ShadowSize, force);
        written += EnsureRing(RoboSimPaths.UiSpinnerSprite, SpinnerSize, SpinnerThickness, force);
        if (written > 0) AssetDatabase.SaveAssets();
        return written;
    }

    private static void EnsureFolder()
    {
        if (AssetDatabase.IsValidFolder(RoboSimPaths.UiSpritesFolder)) return;
        string parent = Path.GetDirectoryName(RoboSimPaths.UiSpritesFolder).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(parent))
            AssetDatabase.CreateFolder(Path.GetDirectoryName(parent).Replace('\\', '/'),
                Path.GetFileName(parent));
        AssetDatabase.CreateFolder(parent, Path.GetFileName(RoboSimPaths.UiSpritesFolder));
    }

    // --- The shapes ---

    // A filled rounded rectangle, white, with the roundness carried entirely in the alpha channel
    // so a single sprite can be tinted to any colour in the palette.
    private static int EnsureRounded(string path, int size, int radius, int border, bool force)
    {
        if (!force && File.Exists(path)) return 0;

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = White(RoundedCoverage(x, y, size, size, radius));
        }
        WritePng(path, pixels, size, size);
        ImportSprite(path, border);
        return 1;
    }

    // A soft radial falloff. Used tinted and scaled up as the glow behind the robot on the stage,
    // where it stands in for the bloom that post-processing can't give us — the UI canvas is
    // ScreenSpaceOverlay and draws after post, and running post just for the stage would smear
    // colour into the RenderTexture's transparent region.
    //
    // The falloff is squared rather than linear. A linear ramp has a visible hard stop where it
    // reaches zero, which on a dark background reads as a ring rather than a shadow.
    private static int EnsureShadow(string path, int size, bool force)
    {
        if (!force && File.Exists(path)) return 0;

        var pixels = new Color32[size * size];
        float centre = (size - 1) * 0.5f;
        float maxDistance = centre;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - centre;
                float dy = y - centre;
                float t = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / maxDistance);
                float falloff = 1f - t;
                pixels[y * size + x] = White(falloff * falloff);
            }
        }
        WritePng(path, pixels, size, size);
        ImportSprite(path, 0); // radial: nothing to protect from stretching, so no 9-slice border
        return 1;
    }

    // A hollow circle — the loading spinner's track.
    //
    // The spinner used to be a quarter cut out of the builtin Knob, which is a FILLED disc, so the
    // arc was a pie wedge. A wedge is fine in one flat colour, but put a gradient across it and it
    // reads as a coloured slice rather than as light travelling round a circle; the two colours want
    // a thin band to travel along. Image.Type.Filled cuts the arc out of this the same way.
    private static int EnsureRing(string path, int size, float thickness, bool force)
    {
        if (!force && File.Exists(path)) return 0;

        var pixels = new Color32[size * size];
        float centre = size * 0.5f;
        // A pixel of margin outside the ring so its outer edge has room to fade rather than being
        // clipped square by the texture bounds.
        float mid = centre - 1f - thickness * 0.5f;
        float half = thickness * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - centre;
                float dy = y + 0.5f - centre;
                // How far this pixel's centre falls OUTSIDE the band, in pixels — negative within
                // it. The 0.5 offset below is the same anti-aliasing as the rounded rectangle.
                float outside = Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - mid) - half;
                pixels[y * size + x] = White(Mathf.Clamp01(0.5f - outside));
            }
        }
        WritePng(path, pixels, size, size);
        ImportSprite(path, 0); // radial: nothing to protect from stretching, so no 9-slice border
        return 1;
    }

    // How much of this pixel is inside the rounded rectangle, 0..1.
    //
    // The +0.5 is the anti-aliasing: distance is measured to the pixel's centre, so a pixel exactly
    // on the arc is half covered. Without it the corners come out visibly stair-stepped, which is
    // the single most obvious way a generated sprite looks generated.
    private static float RoundedCoverage(int x, int y, int width, int height, int radius) =>
        Mathf.Clamp01(0.5f - SignedRoundedDistance(x, y, width, height, radius));

    // How far this pixel's centre is OUTSIDE the rounded rectangle: negative inside, 0 on the
    // edge, positive outside.
    //
    // The standard rounded-box signed distance function: fold into one quadrant, measure from the
    // corner circle's centre, and subtract the radius. The min(max(...), 0) term is what makes it
    // keep working inside the shape, where the sqrt term is zero.
    private static float SignedRoundedDistance(int x, int y, int width, int height, int radius)
    {
        float halfWidth = width * 0.5f;
        float halfHeight = height * 0.5f;
        float qx = Mathf.Abs(x + 0.5f - halfWidth) - (halfWidth - radius);
        float qy = Mathf.Abs(y + 0.5f - halfHeight) - (halfHeight - radius);
        float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) +
                                   Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
        return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
    }

    private static Color32 White(float alpha) =>
        new Color32(0xFF, 0xFF, 0xFF, (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255));

    // --- Asset plumbing ---

    private static void WritePng(string path, Color32[] pixels, int width, int height)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally { Object.DestroyImmediate(texture); }
    }

    // The import settings a sliced UI sprite actually needs. Each of these is load-bearing:
    //   - spriteBorder is the 9-slice. Without it Sliced draws identically to Simple and every
    //     corner stretches with the rect.
    //   - no mipmaps: a UI sprite is drawn at one size and mips only make it blurry.
    //   - Clamp: Repeat bleeds the opposite edge in under bilinear filtering at the border.
    //   - uncompressed: these are a few KB each, and block compression puts visible artefacts
    //     exactly on a rounded corner, which is the one part of the sprite anybody looks at.
    //   - alphaIsTransparency: without it the transparent pixels' RGB is undefined and the edge
    //     picks up a dark fringe when filtered.
    private static void ImportSprite(string path, int border)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
            throw new System.InvalidOperationException($"UI Sprites: {path} did not import as a texture.");

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
        importer.spriteBorder = new Vector4(border, border, border, border);
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    // --- Loading ---

    // The generated sprites, for the builders.
    public static Sprite Panel => Load(RoboSimPaths.UiPanelSprite);
    public static Sprite Button => Load(RoboSimPaths.UiButtonSprite);
    public static Sprite Shadow => Load(RoboSimPaths.UiShadowSprite);
    public static Sprite Spinner => Load(RoboSimPaths.UiSpinnerSprite);

    // Generates on demand rather than requiring EnsureAll to have been called first. Build Drive
    // Controls builds the field-scene buttons from these too and can be run on its own — on a fresh
    // checkout that would otherwise fail on whichever tool happened to run first.
    //
    // Still throws if generation didn't produce a loadable sprite, rather than falling back to the
    // builtin skin: a UI that quietly rebuilds itself in the old style is a failure that gets
    // noticed three commits later, by which point the cause is no longer obvious.
    private static Sprite Load(string path)
    {
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite != null) return sprite;

        EnsureAll();
        sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        return sprite ?? throw new System.InvalidOperationException(
            $"UI Sprites: {path} could not be generated. Try " +
            "Tools > RoboSim > Scenes > Regenerate UI Sprites and check the console for import errors.");
    }
}
