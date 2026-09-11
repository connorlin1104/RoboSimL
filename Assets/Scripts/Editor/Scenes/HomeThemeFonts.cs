using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

// Bakes the UI's TextMeshPro font assets from the Inter TTFs in Assets/UI/Fonts.
//
// The app shipped in LiberationSans — TextMesh Pro's bundled default, the font you get by not
// choosing one. It is the single loudest "generic" tell in the UI, and unlike most of what makes a
// screen look default it cannot be fixed with colour or spacing.
//
// Inter is SIL OFL (the licence ships beside the TTFs as Inter-OFL-LICENSE.txt, which is what the
// OFL requires), and is drawn for screen UI at small sizes — which is most of what this app's text
// is, since the settings screen carries far more type than the home screen does.
//
// Generated rather than authored, for the same reason the sprites are: the atlas size and sampling
// are numbers in this file instead of settings someone re-enters in a dialog. Existing assets are
// left alone, so a font asset built by hand in the Font Asset Creator is respected rather than
// overwritten.
//
// Usage: Tools > RoboSim > Scenes > Build Home Screen calls EnsureAll. To force a rebake after
// changing the numbers below: Tools > RoboSim > Scenes > Regenerate UI Fonts.
public static class HomeThemeFonts
{
    // A 512² atlas rather than the 1024² the Font Asset Creator defaults to. The whole character
    // set below is ~100 glyphs, which fits with room to spare, and the atlas is serialized INTO the
    // .asset as text — this repo is public and its LFS bandwidth is already a live concern, so a
    // 4x larger texture would be 4x the committed bytes for glyphs that do not exist.
    private const int AtlasSize = 512;

    // Sampling size and padding trade atlas space against how far the SDF can be scaled before the
    // edges soften. 40/4 is comfortable for type rendered between 22 and 88pt, which is this UI's
    // whole range.
    private const int SamplingPointSize = 40;
    private const int AtlasPadding = 4;

    [MenuItem("Tools/RoboSim/Scenes/Regenerate UI Fonts", false, 4)]
    private static void RegenerateInteractive()
    {
        int written = Build(true);
        Debug.Log($"Regenerate UI Fonts: baked {written} font asset(s) into {RoboSimPaths.UiFontsFolder}.");
    }

    public static void EnsureAll() => Build(false);

    private static int Build(bool force)
    {
        int written = 0;
        written += Ensure(RoboSimPaths.UiFontRegularSource, RoboSimPaths.UiFontRegular, force);
        written += Ensure(RoboSimPaths.UiFontBoldSource, RoboSimPaths.UiFontBold, force);
        if (written > 0)
        {
            LinkBoldWeight();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        return written;
    }

    // Point the regular face's BOLD weight at the SemiBold asset.
    //
    // This is what lets the rest of the codebase stay exactly as it is. Roughly twenty places set
    // `fontStyle = FontStyles.Bold` on a label; without a weight table TMP answers that by
    // synthesising bold — smearing the glyph sideways — which on an SDF font looks soft and muddy
    // and is the other half of why default TMP text reads as default. With the table populated the
    // same flag resolves to a real drawn SemiBold, and not one call site has to change.
    //
    // Index 7 is weight 700. TMP's table is ten entries, one per hundred from 100 to 900.
    private static void LinkBoldWeight()
    {
        TMP_FontAsset regular = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(RoboSimPaths.UiFontRegular);
        TMP_FontAsset bold = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(RoboSimPaths.UiFontBold);
        if (regular == null || bold == null) return;

        // Through SerializedObject because TMP_FontAsset.fontWeightTable is read-only in the public
        // API — and because that is how everything else in this project writes serialized state it
        // does not own, so the failure mode (a renamed backing field) is a loud one rather than a
        // silently ignored assignment.
        var so = new SerializedObject(regular);
        SerializedProperty table = so.FindProperty("m_FontWeightTable");
        if (table == null || !table.isArray)
        {
            Debug.LogWarning("UI Fonts: TMP_FontAsset has no m_FontWeightTable to write, so bold " +
                             "text will be synthesised rather than drawn. Set the Bold slot of " +
                             $"{regular.name}'s Font Weights to {bold.name} by hand.", regular);
            return;
        }

        if (table.arraySize < 10) table.arraySize = 10;
        SerializedProperty boldPair = table.GetArrayElementAtIndex(7);
        boldPair.FindPropertyRelative("regularTypeface").objectReferenceValue = bold;
        // No italic face is shipped, so italic-bold resolves to the same drawn SemiBold rather than
        // falling back to a synthesised slant of the regular face.
        boldPair.FindPropertyRelative("italicTypeface").objectReferenceValue = bold;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(regular);
    }

    // Every character the app can actually render, spelled out.
    //
    // A default bake covers ASCII and stops. Two of the characters in shipped strings are NOT
    // ASCII — the em dash in "Joystick Size — 100%" (HomeScreenController) and the ellipsis in
    // "Loading…" (BuildHomeScene) — and a glyph that is missing from a static atlas renders as a
    // blank box on the device while looking perfectly fine in the editor, where the fallback is
    // free to step in. The curly apostrophe is here as cheap insurance for the same reason.
    private static string CharacterSet()
    {
        var set = new StringBuilder();
        for (char c = ' '; c <= '~'; c++) set.Append(c); // printable ASCII
        set.Append('—');                            // — em dash
        set.Append('…');                            // … ellipsis
        set.Append('’');                            // ’ right single quote
        return set.ToString();
    }

    private static int Ensure(string ttfPath, string assetPath, bool force)
    {
        if (!force && AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath) != null) return 0;

        Font source = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
        if (source == null)
            throw new System.InvalidOperationException(
                $"UI Fonts: {ttfPath} is missing. The Inter TTFs and their OFL licence belong in " +
                $"{RoboSimPaths.UiFontsFolder}.");

        // Built DYNAMIC and then frozen. A dynamic asset is the only one that can be asked to add
        // characters; a static one renders whatever is already in its atlas and nothing else. So
        // the sequence is: create dynamic, add the character set, then switch to static so the
        // shipped build never tries to rasterize a glyph on device.
        TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
            source, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA,
            AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: false);
        if (asset == null)
            throw new System.InvalidOperationException($"UI Fonts: could not create a font asset from {ttfPath}.");

        asset.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
        if (!asset.TryAddCharacters(CharacterSet(), out string missing) || !string.IsNullOrEmpty(missing))
        {
            // multiAtlasSupport is off deliberately: a second atlas page would fit silently and
            // double the committed size. Failing here means AtlasSize is genuinely too small.
            throw new System.InvalidOperationException(
                $"UI Fonts: {asset.name} could not fit these characters in a {AtlasSize}x{AtlasSize} " +
                $"atlas: \"{missing}\". Raise AtlasSize or lower SamplingPointSize in HomeThemeFonts.");
        }
        asset.atlasPopulationMode = AtlasPopulationMode.Static;

        // LiberationSans stays reachable underneath. Not for the characters above — those are baked
        // in — but for anything a player types: the robot code box and the submit form's team and
        // robot name fields accept arbitrary text, and a name with an accent in it should come out
        // as that letter rather than as a box.
        TMP_FontAsset fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(RoboSimPaths.LiberationSansFont);
        if (fallback != null)
            asset.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };

        AssetDatabase.CreateAsset(asset, assetPath);

        // The atlas texture and material are created in memory alongside the font asset and are NOT
        // assets in their own right. Without adding them as sub-assets they are discarded on the
        // next domain reload and the font asset comes back referencing nothing — which looks like
        // every label in the app losing its font at once, for no visible reason.
        if (asset.material != null)
        {
            asset.material.name = asset.name + " Material";
            AssetDatabase.AddObjectToAsset(asset.material, asset);
        }
        if (asset.atlasTextures != null)
        {
            for (int i = 0; i < asset.atlasTextures.Length; i++)
            {
                if (asset.atlasTextures[i] == null) continue;
                asset.atlasTextures[i].name = asset.name + " Atlas";
                AssetDatabase.AddObjectToAsset(asset.atlasTextures[i], asset);
            }
        }
        EditorUtility.SetDirty(asset);
        return 1;
    }

    // --- Loading ---

    public static TMP_FontAsset Regular => Load(RoboSimPaths.UiFontRegular);
    public static TMP_FontAsset Bold => Load(RoboSimPaths.UiFontBold);

    // Generates on demand, like HomeThemeSprites — so whichever builder runs first on a fresh
    // checkout works, rather than the one that happens to run second.
    private static TMP_FontAsset Load(string path)
    {
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (font != null) return font;

        EnsureAll();
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        return font ?? throw new System.InvalidOperationException(
            $"UI Fonts: {path} could not be generated. If scripted baking is failing, build the two " +
            "assets once with Window > TextMeshPro > Font Asset Creator and save them at these " +
            "paths — this tool leaves an existing asset alone.");
    }
}
