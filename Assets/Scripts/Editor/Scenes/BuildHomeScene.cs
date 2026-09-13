using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;

// One-click builder for the app's home screen.
//
// Creates Assets/Scenes/HomeScene.unity from scratch each run: a dark URP camera, an overlay
// Canvas (same scaler settings as the field scene), an EventSystem on the Input System UI
// module, and a TMP-based UI (title, Drive/Settings main panel, and a settings panel where
// the player picks a robot model from the RobotModelCatalog and sizes the on-screen joysticks).
// It also:
//   - imports the TMP Essential Resources on first run (asset-only package: no scripts, so
//     no domain reload — safe to keep building in the same run),
//   - creates Assets/Settings/RobotModelCatalog.asset if missing,
//   - registers HomeScene + SampleScene in Build Settings (home first, so it boots the app),
//   - adds a "Home" button to the field scene's Canvas that loads back into HomeScene,
//   - wires ControlsAppearance on the field Canvas (via Build Drive Controls) so the control
//     size/opacity settings take effect there.
//
// Usage: Tools > RoboSim > Scenes > Build Home Screen (safe to re-run; every step skips if already done,
// except the HomeScene itself which is rebuilt). Batch: -executeMethod BuildHomeScene.RunBatch.
public class BuildHomeScene
{
    private const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

    // The Input System package's built-in DefaultInputActions asset (same one the field
    // scene's EventSystem uses), resolved via GUID so a package move doesn't break us.
    private const string DefaultInputActionsGuid = "ca9f5fa95ffab41fb9a615ab714db018";

    // An empty marker object carrying this name is created in the scene and checked by
    // HomeSceneIsValid. BUMP IT ON EVERY CHANGE TO THE HOME SCREEN'S APPEARANCE.
    //
    // Why it exists: HomeSceneIsValid is entirely STRUCTURAL — it asks which objects are present
    // and which serialized refs are set. A restyle adds and removes nothing, so a new palette, a
    // different sprite, a font swap, or a component attached to every button all leave every
    // existing check passing. The rebuild is skipped and the redesign silently never ships. That
    // has already happened once on this project: see Docs/App-Store-Submission.md on the app
    // rename, where a text-only change never triggered a rebuild and the old title kept shipping.
    //
    // A version stamp turns "did I remember to add a check for this?" — a judgement call that has
    // to be made correctly every time — into a one-line bump. It is also the ONLY thing that can
    // catch a change with no object footprint at all, which an added component is.
    internal const string HomeSceneStamp = "HomeSceneStamp_v8";

    // The theme, derived from the app icon (Assets/Icons/AppIcon.png) rather than invented.
    //
    // The icon is a white chassis with navy wheels and a blue chevron on a #1D4ED8 -> #0EA5E9
    // gradient. The UI used to share nothing with it: its darks were NEUTRAL GREY (#1A1D23,
    // #262A33, #1F232B) while the brand's dark is NAVY, and its one accent (#3D7DEB) was near to
    // but not either brand blue. Shifting the greys into the navy family is most of what makes the
    // app look like its own icon, and it costs six constants.
    //
    // Internal (with the UI helpers below) so Build Drive Controls builds the field-scene buttons
    // in the same style. HomeScreenController's and ControllerConfigScreen's runtime selection
    // tints are WRITTEN from these values by the builder (see WriteRuntimeTints) rather than kept
    // in sync by hand — a comment saying "these must match" is not a mechanism.

    // Background: the icon's gradient driven far down in value. Full-strength brand blue behind a
    // whole screen of text is unreadable, and it would fight the 3D robot the stage puts in front
    // of it; the same hue family at a fraction of the value reads as the icon without either cost.
    internal static readonly Color BackgroundTopColor = new Color32(0x0D, 0x1A, 0x3A, 0xFF);
    internal static readonly Color BackgroundBottomColor = new Color32(0x12, 0x23, 0x4A, 0xFF);

    // Surfaces. PanelColor is FULLY OPAQUE, and that is a bug fix, not a preference: it used to
    // carry alpha 0xF5, and because the title is created before the panels, "RoboSimL" ghosted
    // through every one of them at 4%. That is visible in three of the four shipped App Store
    // screenshots and reads as broken rendering rather than as style.
    internal static readonly Color PanelColor = new Color32(0x1B, 0x30, 0x60, 0xFF);
    // The top of a panel's own gentle vertical gradient. Subtle on purpose — this is what reads as
    // "raised" now that the panel sprite carries no drop shadow, and a strong ramp looks cheap.
    internal static readonly Color PanelTopColor = new Color32(0x21, 0x3A, 0x70, 0xFF);
    internal static readonly Color ListColor = new Color32(0x0A, 0x14, 0x2C, 0xFF);
    internal static readonly Color NeutralColor = new Color32(0x2A, 0x40, 0x70, 0xFF);
    internal static readonly Color BorderColor = new Color32(0x35, 0x54, 0x92, 0xFF);

    // The two ends of the primary-action gradient — the icon's own blues, at full strength. Used
    // only for the things that DO something (Drive, Back, Unlock, Submit), never for state.
    internal static readonly Color PrimaryTopColor = new Color32(0x1D, 0x4E, 0xD8, 0xFF);
    internal static readonly Color PrimaryBottomColor = new Color32(0x0E, 0xA5, 0xE9, 0xFF);

    // Selection/live state, deliberately a DIFFERENT colour from a primary action. Today they are
    // the same blue, so the selected robot row and the Drive button are indistinguishable and the
    // player cannot tell "this is chosen" from "tap this".
    internal static readonly Color SelectedColor = new Color32(0x0E, 0xA5, 0xE9, 0xFF);

    internal static readonly Color TextColor = new Color32(0xF2, 0xF6, 0xFF, 0xFF);

    // Kept as the flat stand-in for the primary gradient, for the places that need one colour
    // rather than two: Build Drive Controls' field buttons (which sit over the 3D field, not over
    // this navy, and would gain nothing from a gradient) and the section rules.
    internal static readonly Color AccentColor = PrimaryTopColor;

    // --- The split ---

    // The home screen is two columns: a stage on the left and the menu docked right.
    //
    // A FRACTION of the canvas width, not a fixed number of units. A fixed 900 was tried first and
    // is what a reader would expect here, because CreateSliderRow hard-codes its label column at
    // 340 units and a menu that narrows with the screen could crush the sliders. But 900 is 42% of
    // a 6.5" phone's canvas and 54% of a 13" iPad's, and that difference is plainly visible: the
    // same screen reads as menu-on-the-right on one device and split-down-the-middle on the other.
    //
    // 0.45 holds the proportion identical everywhere and still leaves the narrowest target — the
    // iPad's 1663-unit canvas — a 724-wide menu: 666 of interior, which is the 340 label, the 16
    // gap and 310 of slider. The phone gets 929. The stage takes the aspect difference instead,
    // 1117 against 867, which is exactly where a difference in screen shape belongs.
    //
    // What makes this cheap: the settings panel's entire interior — tab row, viewport, scrollbar,
    // Back button, layout groups — is anchored against the PANEL's rect, never the canvas. Moving
    // or resizing the panel moves all of it, so not one row, tab or scroll needs touching.
    private const float MenuFraction = 0.45f;

    // The button row pinned to the bottom of the two full-bleed sub-screens, and the clearance the
    // dark content area above it keeps. 18 was the inset before and read as none: at that height the
    // buttons touch the panel's rounded bottom edge and very nearly touch the backdrop above them.
    private const float BottomRowInset = 28f;
    private const float BottomRowHeight = 64f;
    private const float StageGutter = 24f;   // between stage and menu, and at the screen edges
    private const float StageMargin = 40f;   // top and bottom

    // The robot's window keeps clear of the title above it and the caption below it by the SAME amount,
    // so the robot sits at the stage's centre — level with the glow, the chassis mark and the
    // Drive/Settings card beside it, which is the alignment Stage 2 set up. The title band is the
    // title's own height (see the Title block).
    private const float StageTitleBand = 140f;
    private const float StageCaptionBand = 140f;

    // The chips under the robot's name (RobotModelCatalog.Highlights). Five slots, which is every chip a robot
    // can have: a drivetrain, a lift and three labels. Sized so all five, carrying the widest watts, fit the
    // narrowest stage — the 13" iPad's 867 units — which Validate Home Stage measures with the real font. The
    // corner is the button sprite's over StageChipRoundness: more than a 40-unit chip can hold, so the Image
    // scales it back to fit and the ends come out nearly round. Filled with the menu card's own navy, opaque:
    // a translucent white was tried first, and blended in linear light it came out a flat grey that the cyan
    // watts read on at barely 2:1.
    internal const int StageChipSlots = 5;
    private const float StageChipHeight = 40f;
    private const float StageChipSpacing = 10f;
    private const int StageChipPadding = 16;
    private const float StageChipFontSize = 22f;
    private const float StageChipRoundness = 0.7f;
    private static readonly Color StageChipColor = PanelTopColor;

    // A long lens. A wide one exaggerates perspective, and on a CAD model of square tube and flat plate
    // that reads as a boxy fish-eye; 26 degrees flattens it toward the product-shot look.
    private const float StageFieldOfView = 26f;

    // The stage's lights (see BuildRobotStage): a warm key, a cool fill, and the icon's cyan on the rim.
    private static readonly Color KeyLightColor = new Color32(0xFF, 0xF4, 0xE6, 0xFF);
    private static readonly Color FillLightColor = new Color32(0xA9, 0xC8, 0xFF, 0xFF);

    // Muted text — section headers, column titles, hints. Was written out as
    // "new Color(TextColor.r, TextColor.g, TextColor.b, 0.62f)" at six separate call sites.
    internal static readonly Color TextMutedColor =
        new Color(TextColor.r, TextColor.g, TextColor.b, 0.62f);

    [MenuItem("Tools/RoboSim/Scenes/Build Home Screen", false, 1)]
    private static void BuildInteractive()
    {
        Build(true, false);
    }

    // Force a full rebuild even when a valid HomeScene already exists — use after changing the
    // home-screen UI code. The default menu item above skips the rebuild when it isn't needed.
    [MenuItem("Tools/RoboSim/Scenes/Rebuild Home Screen (Force)", false, 2)]
    private static void RebuildInteractive()
    {
        Build(true, true);
    }

    // Batch entry point for -executeMethod: no dialogs, throws on failure (nonzero exit). Always
    // forces a clean rebuild so CI is deterministic from any checkout.
    public static void RunBatch()
    {
        Build(false, true);
    }

    private static void Build(bool interactive, bool force)
    {
        // 1) TMP essential resources (fonts/shaders/settings) must exist before we create
        //    any TextMeshProUGUI, or the labels have no default font.
        bool tmpImported;
        if (!EnsureTmpEssentials(interactive, out tmpImported)) return;

        // 1b) The UI's generated sprites and font assets. Before the scene is built, because every
        //     panel, button and label in it loads one, and both generators throw rather than
        //     quietly falling back to the builtin skin. Each skips whatever already exists, so
        //     this costs nothing on a re-run.
        HomeThemeSprites.EnsureAll();
        HomeThemeFonts.EnsureAll();

        // 1c) The layer the stage robot is drawn on. Before anything that uses it — the showcase bake
        //     and the stage camera's culling mask — so the name exists in the project's layer list.
        BuildShowcasePrefabs.EnsureStageLayer();

        // 2) The model catalog the home screen lists, and the (initially blank) submissions
        //    destination the Submit a Robot screen posts to.
        bool catalogCreated;
        RobotModelCatalog catalog = EnsureCatalog(out catalogCreated);
        EnsureUploadConfig(out bool uploadConfigCreated);

        // 2b) The stripped robots the home stage turns. After the catalog, whose entries they are written
        //     into; before the scene, which reads them. Re-bakes only a robot that has changed since its
        //     last bake, so this costs nothing on a re-run.
        string showcaseStatus = BuildShowcasePrefabs.EnsureAll(catalog);

        // 2c) What the stage's chips can learn from each robot's rig — its lift, a Floating Intake, a claw —
        //     written into the catalog the scene reads at runtime. Saved only when a robot has changed.
        string chipsStatus = RobotHighlightDetection.Refresh(catalog);

        string previousScenePath = SceneManager.GetActiveScene().path;
        if (interactive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("Build Home Scene: cancelled at the save prompt; nothing changed.");
            return;
        }

        // 3) Rebuild the home scene ONLY when needed: a full teardown+rebuild is skipped when a
        //    valid HomeScene already exists (fast, no scene churn). Force (or a missing/broken
        //    scene) does the from-scratch rebuild. The field-scene edits below run either way and
        //    are idempotent.
        string rebuildStatus;
        if (!force && HomeSceneIsValid())
        {
            rebuildStatus = "skipped (already built; use Rebuild Home Screen (Force) to regenerate)";
        }
        else
        {
            Scene homeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildHomeSceneContents(catalog);
            EditorSceneManager.SaveScene(homeScene, RoboSimPaths.HomeScene);

            // Cross-asset references can fail to persist when the referenced asset was created in
            // the same batch as the scene save (the shipped scene once carried catalog: {fileID: 0}
            // and the model list was silently dead on device). Reload from disk and prove the
            // catalog reference survived.
            if (!VerifySavedWiring(interactive)) return;
            rebuildStatus = force ? "rebuilt (forced)" : "rebuilt (was missing or invalid)";
        }

        // 4) Build settings: home screen boots the app at index 0, the field scene follows.
        //    Scenes this tool doesn't know about are preserved (re-running must not clobber
        //    scenes added later).
        var buildScenes = new List<EditorBuildSettingsScene>
        {
            new EditorBuildSettingsScene(RoboSimPaths.HomeScene, true),
            new EditorBuildSettingsScene(RoboSimPaths.MainScene, true),
        };
        foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
        {
            if (existing.path != RoboSimPaths.HomeScene && existing.path != RoboSimPaths.MainScene)
                buildScenes.Add(existing);
        }
        EditorBuildSettings.scenes = buildScenes.ToArray();

        // 5) Field scene edits: a way back to the home screen, and the ControlsAppearance that
        //    applies the home screen's control size/opacity settings to the on-screen controls.
        Scene sampleScene = EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);
        string homeButtonStatus = EnsureFieldHomeButton(sampleScene, interactive, out bool homeButtonAdded);
        string appearanceStatus = BuildDriveControls.EnsureControlsAppearance(sampleScene, out bool appearanceChanged);
        if (homeButtonAdded || appearanceChanged) EditorSceneManager.SaveScene(sampleScene);

        // Interactive runs put the user back where they were; batch leaves SampleScene open.
        if (interactive && !string.IsNullOrEmpty(previousScenePath) && previousScenePath != RoboSimPaths.MainScene)
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

        Debug.Log($"Build Home Scene: TMP essentials {(tmpImported ? "imported" : "already present")}, " +
                  $"catalog {(catalogCreated ? "created at " + RoboSimPaths.RobotModelCatalog : "found")}, " +
                  $"upload config {(uploadConfigCreated ? "created at " + RoboSimPaths.RobotUploadConfig + " (fill in the Firebase bucket + key to switch submissions on)" : "found")}, " +
                  $"showcases {showcaseStatus}, chips {chipsStatus}, HomeScene {rebuildStatus}, build settings = [HomeScene, SampleScene], " +
                  $"field Home button {homeButtonStatus}, controls appearance {appearanceStatus}.");
    }

    // Is there already a valid HomeScene so a full rebuild can be skipped? True when the scene
    // exists and its HomeScreenController's catalog + controller-config + controls-layout
    // references all survived (an older scene missing the layout screen counts as invalid, so the
    // first run after adding it rebuilds once, then subsequent runs skip). Opens the scene to
    // inspect it — the caller has already offered to save the current one.
    //
    // Whenever a NEW serialized ref is added to a built panel, check it here too: the committed
    // HomeScene.unity serializes it as {fileID: 0}, and without a check for it this method reports
    // "valid", the rebuild is skipped, and the new control silently never appears.
    //
    // The rule runs BOTH ways, and the second direction is easy to forget: a control that has been
    // REMOVED needs an inverted check that the committed scene no longer contains it. Otherwise
    // every remaining check still passes against the old scene, the rebuild is skipped, and the
    // button someone asked to delete keeps shipping — now wired to a handler that no longer exists.
    internal static bool HomeSceneIsValid()
    {
        if (!File.Exists(RoboSimPaths.HomeScene)) return false;
        Scene scene = EditorSceneManager.OpenScene(RoboSimPaths.HomeScene, OpenSceneMode.Single);
        HomeScreenController controller = null;
        ControllerConfigScreen configScreen = null;
        foreach (GameObject rootGo in scene.GetRootGameObjects())
        {
            if (controller == null) controller = rootGo.GetComponentInChildren<HomeScreenController>(true);
            if (configScreen == null) configScreen = rootGo.GetComponentInChildren<ControllerConfigScreen>(true);
        }
        if (controller == null || configScreen == null) return false;

        // The appearance stamp, checked FIRST so a restyle short-circuits every other check. Every
        // test below this line is structural, and a change to how the screen LOOKS rather than what
        // it contains passes all of them — see HomeSceneStamp.
        if (FindDescendantRect(scene, HomeSceneStamp) == null) return false;

        // The theme's own structural pieces. The stamp above would already catch these, but they
        // are checked by name too so a scene that is stale for a SPECIFIC reason says which.
        if (FindDescendantRect(scene, "HomeBackdrop") == null) return false;
        if (FindDescendantRect(scene, "SettingsTabsIndicator") == null) return false;
        // The split: the stage on the left, the menu docked right. Both panels are REPARENTED into
        // MenuColumn, so a scene built before the split still has them centred on the canvas while
        // every serialized ref below it still reads as wired. HomeStage arrived later still, as the
        // wrapper holding the stage and the title together so the sub-screens can hide both.
        if (FindDescendantRect(scene, "HomeStage") == null) return false;
        if (FindDescendantRect(scene, "StageRegion") == null) return false;
        if (FindDescendantRect(scene, "MenuColumn") == null) return false;
        // The two sub-screens size their dark content area to the panel now instead of carrying an
        // authored scale measured against one canvas height. No serialized ref, so check the object.
        if (FindDescendantRect(scene, "ConfigDiagramArea") == null) return false;
        if (FindDescendantRect(scene, "LayoutPreviewArea") == null) return false;
        // Stage 3's robot. The window and its caption are UI, so the Rect search sees them; the rig is a
        // camera, three lights and two holders — plain Transforms, which FindDescendantRect is blind to.
        if (FindDescendantRect(scene, "StageRobotView") == null) return false;
        if (FindDescendantRect(scene, "StageCaption") == null) return false;
        if (FindDescendantRect(scene, "StageRobotChips") == null) return false;
        if (FindDescendantTransform(scene, "RobotStage") == null) return false;
        if (FindDescendantTransform(scene, "StagePivot") == null) return false;
        if (FindDescendantTransform(scene, "StageCamera") == null) return false;
        if (FindDescendantTransform(scene, "StageRobotHolder") == null) return false;

        // Structural checks for things that have no serialized reference of their own. The tab row
        // and the scrollbar are pure hierarchy, so without these a pre-tabs HomeScene would report
        // "valid", the rebuild would be skipped, and the redesign would silently never appear.
        if (FindDescendantRect(scene, "SettingsTabs") == null) return false;
        if (FindDescendantRect(scene, "SettingsScrollbar") == null) return false;
        // The config screen's Back / Control Style / Reset row now lives in a layout group so it
        // stays centred whatever subset of it is showing. No serialized ref, so check the object.
        if (FindDescendantRect(scene, "ConfigBottomRow") == null) return false;
        // The model picker is two columns now; the split has no serialized ref of its own.
        if (FindDescendantRect(scene, "ModelListSplit") == null) return false;
        // Each column scrolls inside its own viewport rather than growing the whole Robot page. The
        // viewports have serialized refs (below) but the list is only reparented under them here, so
        // a scene built before the columns scrolled would keep its unbounded lists.
        if (FindDescendantRect(scene, "PublicModelColumnViewport") == null) return false;
        if (FindDescendantRect(scene, "PrivateModelColumnViewport") == null) return false;
        // The inbox notice carries a developer message now, and is a centred dialog rather than the
        // banner it used to be — a scene built before that has neither of these objects.
        if (FindDescendantRect(scene, "InboxMessageViewport") == null) return false;
        if (FindDescendantRect(scene, "InboxPanel") == null) return false;
        // The submit screen tells a player what to export. That advice is a plain label with no
        // serialized ref, so without this check a scene built before it would still validate and the
        // one line that changes what people send would never ship. Note the check only asks whether
        // the object EXISTS — rewording FormatAdvice still needs Rebuild Home Screen (Force).
        if (FindDescendantRect(scene, "SubmitFormatHint") == null) return false;

        // Inverted checks: catalog authoring moved out of the game and into
        // Tools > RoboSim > Robot > Model Catalog. While these objects still exist in the committed
        // scene it is stale, however well-wired the rest of it looks.
        if (FindDescendantRect(scene, "EditModelsButton") != null) return false;
        if (FindDescendantRect(scene, "SaveDefaultBindingsButton") != null) return false;
        if (FindDescendantRect(scene, "SmoothAccelerationToggle") != null) return false;
        if (FindDescendantRect(scene, "CoastOnReleaseToggle") != null) return false;
        // ...and the same for the settings redesign: the single model list, the wheel-type box, the
        // Driving tab and the whole Your ID section are all gone. Each of these would otherwise
        // leave a scene that passes every remaining check while still shipping the removed control.
        if (FindDescendantRect(scene, "ModelList") != null) return false;
        if (FindDescendantRect(scene, "TractionWheelsToggle") != null) return false;
        if (FindDescendantRect(scene, "SettingsPage_Driving") != null) return false;
        if (FindDescendantRect(scene, "RecoveryIdLabel") != null) return false;
        if (FindDescendantRect(scene, "RecoveryIdInput") != null) return false;
        if (FindDescendantRect(scene, "TeamCodeInput") != null) return false;
        // The drive-feel hint paragraph under the sensitivity sliders is gone (2026-09-01):
        // the sliders' own percent labels say what they do.
        if (FindDescendantRect(scene, "DriveFeelHint") != null) return false;
        // The stage's line naming every one of a robot's mechanisms gave way to chips for the few that
        // matter (2026-09-12).
        if (FindDescendantRect(scene, "StageRobotMechanisms") != null) return false;

        // The stage view holds its own refs — to the catalog it listens to, the rig it drives and the
        // labels it fills — so a scene built with the view but without them still has to rebuild.
        RobotStageView stageView = null;
        foreach (GameObject rootGo in scene.GetRootGameObjects())
        {
            if (stageView == null) stageView = rootGo.GetComponentInChildren<RobotStageView>(true);
        }
        if (stageView == null) return false;
        SerializedObject viewSo = new SerializedObject(stageView);
        if (!IsRefSet(viewSo, "catalog") || !IsRefSet(viewSo, "stage") || !IsRefSet(viewSo, "stageCamera") ||
            !IsRefSet(viewSo, "fallbackMark") || !IsRefSet(viewSo, "nameLabel") || !IsRefSet(viewSo, "chipRow") ||
            !IsArrayFilled(viewSo, "chipLabels"))
            return false;

        SerializedObject so = new SerializedObject(controller);
        SerializedObject configSo = new SerializedObject(configScreen);
        return IsRefSet(so, "catalog") && IsRefSet(so, "controllerConfig") &&
               IsRefSet(so, "controlsLayout") && IsRefSet(so, "loadingOverlay") &&
               IsRefSet(so, "homeStage") && IsRefSet(so, "titleDock") &&
               IsRefSet(so, "publicModelListParent") && IsRefSet(so, "privateModelListParent") &&
               IsRefSet(so, "privateEmptyLabel") && IsRefSet(so, "modelButtonTemplate") &&
               IsRefSet(so, "publicListViewport") && IsRefSet(so, "privateListViewport") &&
               IsRefSet(so, "automaticMatchloadToggle") && IsRefSet(so, "liteFieldToggle") &&
               IsRefSet(so, "reverseDriveToggle") &&
               IsRefSet(so, "robotCodeInput") && IsRefSet(so, "robotCodeStatusLabel") &&
               IsRefSet(so, "yourCodesLabel") &&
               IsRefSet(so, "submitRobot") && IsRefSet(so, "uploadConfig") &&
               IsRefSet(so, "inboxNotice") && IsRefSet(so, "inboxLabel") &&
               IsRefSet(so, "inboxMessageLabel") && IsRefSet(so, "inboxMessageViewport") &&
               IsRefSet(so, "inboxActionLabel") &&
               IsRefSet(so, "settingsScroll") && IsRefSet(so, "settingsTabIndicator") &&
               IsArrayFilled(so, "settingsTabButtons") && IsArrayFilled(so, "settingsTabPages") &&
               IsRefSet(so, "driveSensitivitySlider") && IsRefSet(so, "turnSensitivitySlider") &&
               IsRefSet(configSo, "controlStyleButton") &&
               IsRefSet(configSo, "resetDefaultsButton");
    }

    // A serialized array counts as wired only when it's non-empty AND every slot is filled — a
    // half-populated tab list would leave a tab that does nothing.
    private static bool IsArrayFilled(SerializedObject so, string propertyName)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property == null || !property.isArray || property.arraySize == 0) return false;
        for (int i = 0; i < property.arraySize; i++)
        {
            if (property.GetArrayElementAtIndex(i).objectReferenceValue == null) return false;
        }
        return true;
    }

    private static bool IsRefSet(SerializedObject so, string propertyName)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        return property != null && property.objectReferenceValue != null;
    }

    // Reloads the just-saved HomeScene from disk and proves the critical cross-asset references
    // survived the save. Returns false (after logging + a dialog in interactive mode) when a
    // reference was lost; throws in batch mode so -executeMethod exits nonzero.
    private static bool VerifySavedWiring(bool interactive)
    {
        Scene reloaded = EditorSceneManager.OpenScene(RoboSimPaths.HomeScene, OpenSceneMode.Single);
        HomeScreenController saved = null;
        foreach (GameObject rootGo in reloaded.GetRootGameObjects())
        {
            saved = rootGo.GetComponentInChildren<HomeScreenController>(true);
            if (saved != null) break;
        }
        SerializedProperty savedCatalog = saved != null
            ? new SerializedObject(saved).FindProperty("catalog") : null;
        if (savedCatalog == null || savedCatalog.objectReferenceValue == null)
        {
            const string msg = "Build Home Scene: the saved HomeScene lost its RobotModelCatalog " +
                               "reference — the model list would never build at runtime.";
            Debug.LogError(msg);
            if (!interactive) throw new InvalidOperationException(msg);
            EditorUtility.DisplayDialog("Build Home Scene", msg, "OK");
            return false;
        }

        ControllerConfigScreen savedConfig = null;
        foreach (GameObject rootGo in reloaded.GetRootGameObjects())
        {
            savedConfig = rootGo.GetComponentInChildren<ControllerConfigScreen>(true);
            if (savedConfig != null) break;
        }
        SerializedProperty savedConfigCatalog = savedConfig != null
            ? new SerializedObject(savedConfig).FindProperty("catalog") : null;
        if (savedConfigCatalog == null || savedConfigCatalog.objectReferenceValue == null)
        {
            const string msg = "Build Home Scene: the saved HomeScene lost the ControllerConfigScreen's " +
                               "RobotModelCatalog reference — the mapping screen would show no mechanisms.";
            Debug.LogError(msg);
            if (!interactive) throw new InvalidOperationException(msg);
            EditorUtility.DisplayDialog("Build Home Scene", msg, "OK");
            return false;
        }

        // The stage view listens to the same catalog for the selected robot. Same failure, same check:
        // without it the stage would sit on the chassis mark forever.
        RobotStageView savedView = null;
        foreach (GameObject rootGo in reloaded.GetRootGameObjects())
        {
            savedView = rootGo.GetComponentInChildren<RobotStageView>(true);
            if (savedView != null) break;
        }
        SerializedProperty savedViewCatalog = savedView != null
            ? new SerializedObject(savedView).FindProperty("catalog") : null;
        if (savedViewCatalog == null || savedViewCatalog.objectReferenceValue == null)
        {
            const string msg = "Build Home Scene: the saved HomeScene lost the robot stage's RobotModelCatalog " +
                               "reference — the stage would never show a robot.";
            Debug.LogError(msg);
            if (!interactive) throw new InvalidOperationException(msg);
            EditorUtility.DisplayDialog("Build Home Scene", msg, "OK");
            return false;
        }
        return true;
    }

    // --- Step 1: TMP essentials ---

    private static bool EnsureTmpEssentials(bool interactive, out bool imported)
    {
        imported = false;
        if (File.Exists(TmpSettingsPath)) return true;

        // Asset-only package (fonts/shaders/materials — no .cs), so importing it triggers no
        // domain reload. NOTE: AssetDatabase.ImportPackage queues asynchronously even in batch
        // mode, so a headless first run can still come up empty — in that case bootstrap by
        // extracting the unitypackage (a tar.gz of guid/asset+meta entries) into Assets first.
        TMP_PackageResourceImporter.ImportResources(true, false, false);
        AssetDatabase.Refresh();

        if (File.Exists(TmpSettingsPath))
        {
            imported = true;
            return true;
        }

        // In the interactive editor the package import can complete asynchronously, after
        // this method returns — tell the user to simply run the tool again.
        if (interactive)
        {
            EditorUtility.DisplayDialog("Build Home Scene",
                "TMP Essential Resources are still importing. Wait for the import to finish, " +
                "then run Tools > RoboSim > Scenes > Build Home Screen again.", "OK");
            return false;
        }
        Debug.LogError("Build Home Scene: TMP Essential Resources missing after import.");
        throw new InvalidOperationException("TMP Essential Resources missing after import.");
    }

    // --- Step 2: model catalog asset ---

    private static RobotModelCatalog EnsureCatalog(out bool created)
    {
        created = false;
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        if (catalog != null) return catalog;

        if (!AssetDatabase.IsValidFolder("Assets/Settings"))
            AssetDatabase.CreateFolder("Assets", "Settings");

        catalog = ScriptableObject.CreateInstance<RobotModelCatalog>();
        catalog.models.Add(new RobotModelCatalog.Entry
        {
            id = "360rpm-drivetrain",
            displayName = "360 RPM Drivetrain",
        });
        AssetDatabase.CreateAsset(catalog, RoboSimPaths.RobotModelCatalog);
        AssetDatabase.SaveAssets();
        created = true;
        return catalog;
    }

    // --- Step 3: home scene contents ---

    private static void BuildHomeSceneContents(RobotModelCatalog catalog)
    {
        // Camera: pure UI backdrop, solid dark clear. GetUniversalAdditionalCameraData()
        // adds the URP camera data component if it doesn't exist yet.
        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.transform.position = new Vector3(0f, 1f, -10f);
        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        // Cleared to the TOP of the background gradient. The gradient itself is a full-screen
        // Image on the canvas (see HomeBackdrop); this only matters for the frame before the
        // canvas draws, and for any letterboxed edge.
        camera.backgroundColor = BackgroundTopColor;
        cameraGo.AddComponent<AudioListener>();
        camera.GetUniversalAdditionalCameraData();

        // The home screen's only 3D content is the stage robot, and this camera must not draw it. It
        // draws Everything by default, so it would render every one of the robot's several hundred parts
        // a second time, each frame, into a backbuffer the opaque backdrop then covers. No structural
        // check can see a culling mask; the stamp and Validate Home Stage are what guard this line.
        camera.cullingMask = ~(1 << RobotShowcase.LayerIndex);

        // The stage's 3D half — lighting, then the rig — straight after this camera, because the two
        // cameras' depths only mean anything relative to each other (see BuildRobotStage).
        ApplyStageLighting();
        RobotStage stageRig = BuildRobotStage(camera, out Camera stageCamera);

        // Canvas: mirror the field scene's setup (overlay, scale-with-screen 1920x1080).
        GameObject canvasGo = CreateUIObject("Canvas", null);
        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // EventSystem on the Input System UI module (the project uses the new Input System).
        GameObject eventSystemGo = new GameObject("EventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        InputSystemUIInputModule uiModule = eventSystemGo.AddComponent<InputSystemUIInputModule>();
        AssignDefaultUiActions(uiModule);

        // The background gradient — the app icon's own blues, driven far down in value. Created
        // FIRST so it is the bottom-most canvas child and everything else draws over it.
        //
        // A full-screen Image rather than the camera's clear colour, because a camera can only
        // clear to one flat colour. It is also why the robot stage in front of it has to composite
        // through a RenderTexture: this canvas is ScreenSpaceOverlay, so it draws on top of every
        // camera in the scene, and a directly-rendered robot would be hidden behind it.
        GameObject backdrop = CreateUIObject("HomeBackdrop", canvasGo.transform);
        RectTransform backdropRect = (RectTransform)backdrop.transform;
        backdropRect.anchorMin = Vector2.zero;
        backdropRect.anchorMax = Vector2.one;
        backdropRect.offsetMin = Vector2.zero;
        backdropRect.offsetMax = Vector2.zero;
        Image backdropImage = backdrop.AddComponent<Image>();
        backdropImage.color = Color.white; // the gradient supplies the colour; this is its tint
        backdropImage.raycastTarget = false;
        UiGradient backdropGradient = backdrop.AddComponent<UiGradient>();
        backdropGradient.topColor = BackgroundTopColor;
        backdropGradient.bottomColor = BackgroundBottomColor;

        // Appearance version stamp — see HomeSceneStamp. An empty RectTransform with no Graphic,
        // so it renders nothing and its sibling order is irrelevant. Created here rather than last
        // because the loading overlay has to stay the top-most canvas child.
        CreateUIObject(HomeSceneStamp, canvasGo.transform);

        // Everything that is NOT the menu: the stage region and the title. One wrapper rather than
        // two canvas children because the three full-bleed screens hide all of it together, and a
        // controller holding two refs to keep in step is a worse version of holding one.
        //
        // Spans the whole canvas, so the title can be centred on the SCREEN with a plain anchor
        // while the stage inside it keeps to the left. Carries no Graphic, so it costs no draw call
        // and swallows no taps.
        GameObject homeStage = CreateUIObject("HomeStage", canvasGo.transform);
        RectTransform homeStageRect = (RectTransform)homeStage.transform;
        homeStageRect.anchorMin = Vector2.zero;
        homeStageRect.anchorMax = Vector2.one;
        homeStageRect.offsetMin = Vector2.zero;
        homeStageRect.offsetMax = Vector2.zero;

        // The stage: everything left of the menu. Empty of its own graphics — it is a region, and
        // what fills it is parented in. Stage 3 puts the robot's RenderTexture here.
        GameObject stageRegion = CreateUIObject("StageRegion", homeStage.transform);
        RectTransform stageRect = (RectTransform)stageRegion.transform;
        stageRect.anchorMin = Vector2.zero;
        stageRect.anchorMax = new Vector2(1f - MenuFraction, 1f);
        stageRect.offsetMin = new Vector2(StageGutter, StageMargin);
        stageRect.offsetMax = new Vector2(-StageGutter, -StageMargin);

        // The menu column: the right MenuFraction of the canvas, which the panels dock into.
        // offsetMin/offsetMax are insets from the anchor span, so the gutter and the top/bottom
        // margin are stated once here instead of by every panel that docks in — which is what
        // retires the per-panel StretchPanelHeight call for the settings panel below.
        GameObject menuColumn = CreateUIObject("MenuColumn", canvasGo.transform);
        RectTransform menuRect = (RectTransform)menuColumn.transform;
        menuRect.anchorMin = new Vector2(1f - MenuFraction, 0f);
        menuRect.anchorMax = new Vector2(1f, 1f);
        menuRect.pivot = new Vector2(0.5f, 0.5f);
        menuRect.offsetMin = new Vector2(0f, StageMargin);
        menuRect.offsetMax = new Vector2(-StageGutter, -StageMargin);

        // A glow and a chassis mark, standing in for the robot until the stage can render one.
        //
        // Not an empty half-screen: an empty stage would be a different composition from the one
        // being built, and judging the split against it would answer the wrong question. These are
        // created BEFORE the title so the title draws over them.
        GameObject stageGlow = CreateUIObject("StageGlow", stageRegion.transform);
        RectTransform glowRect = (RectTransform)stageGlow.transform;
        glowRect.anchorMin = glowRect.anchorMax = new Vector2(0.5f, 0.5f);
        glowRect.pivot = new Vector2(0.5f, 0.5f);
        // Dead centre, which is also where MainPanel's centre lands: the column is inset by the
        // same margin top and bottom, so both rects are centred on the canvas and the mark lines up
        // with the Drive/Settings card beside it rather than sitting 70 units below it.
        glowRect.anchoredPosition = Vector2.zero;
        // 720, not larger: the iPad's stage is only 691 units wide (the phone's is 1146), and a glow
        // wider than the stage spills off the screen on one side and under the menu panel on the
        // other. At this size it still reads as a broad wash behind the 420 mark on both.
        glowRect.sizeDelta = new Vector2(720f, 720f);
        Image glowImage = stageGlow.AddComponent<Image>();
        glowImage.sprite = HomeThemeSprites.Shadow;
        glowImage.color = new Color(PrimaryBottomColor.r, PrimaryBottomColor.g, PrimaryBottomColor.b, 0.18f);
        glowImage.raycastTarget = false;

        GameObject chassisMark = CreateUIObject("StageChassisMark", stageRegion.transform);
        RectTransform markRect = (RectTransform)chassisMark.transform;
        markRect.anchorMin = markRect.anchorMax = new Vector2(0.5f, 0.5f);
        markRect.pivot = new Vector2(0.5f, 0.5f);
        markRect.anchoredPosition = Vector2.zero; // see StageGlow: level with MainPanel
        markRect.sizeDelta = new Vector2(420f, 420f);
        Image markImage = chassisMark.AddComponent<Image>();
        markImage.sprite = HomeThemeSprites.Chassis;
        markImage.color = Color.white; // the gradient supplies the colour, as on a primary button
        markImage.preserveAspect = true;
        markImage.raycastTarget = false;
        UiGradient markGradient = chassisMark.AddComponent<UiGradient>();
        markGradient.topColor = TextColor;
        markGradient.bottomColor = PrimaryBottomColor;

        // The window the robot is seen through: a RawImage showing the stage camera's render texture,
        // and the surface a finger spins it on. The chassis mark above stays as the FALLBACK — for a
        // robot delivered as a bundle, or one with no baked showcase — and RobotStageView hides it while
        // a robot is showing. Created after the mark so it draws over it, and before the title, which is
        // a later sibling of this whole region and so draws over everything here.
        GameObject stageViewGo = CreateUIObject("StageRobotView", stageRegion.transform);
        RectTransform stageViewRect = (RectTransform)stageViewGo.transform;
        stageViewRect.anchorMin = Vector2.zero;
        stageViewRect.anchorMax = Vector2.one;
        stageViewRect.offsetMin = new Vector2(0f, StageCaptionBand);
        stageViewRect.offsetMax = new Vector2(0f, -StageTitleBand);
        RawImage stageImage = stageViewGo.AddComponent<RawImage>();
        stageImage.color = Color.white;
        stageImage.raycastTarget = true; // the whole window is the drag surface, not just the robot's pixels
        // Off until the stage has drawn into it: a RawImage with no texture draws a white rectangle.
        stageImage.enabled = false;
        RobotStageView stageView = stageViewGo.AddComponent<RobotStageView>();

        // The caption band under the window: the robot's name, and a row of chips beneath it. A layout group,
        // so hiding the row for a robot that has no chips re-centres the name.
        GameObject stageCaption = CreateUIObject("StageCaption", stageRegion.transform);
        RectTransform captionRect = (RectTransform)stageCaption.transform;
        captionRect.anchorMin = Vector2.zero;
        captionRect.anchorMax = new Vector2(1f, 0f);
        captionRect.pivot = new Vector2(0.5f, 0f);
        captionRect.anchoredPosition = Vector2.zero;
        captionRect.sizeDelta = new Vector2(0f, StageCaptionBand);
        VerticalLayoutGroup captionLayout = AddVerticalLayout(stageCaption, 0, 6f);
        captionLayout.childAlignment = TextAnchor.MiddleCenter;

        TextMeshProUGUI stageName = CreateText("StageRobotName", stageCaption.transform, string.Empty, 44f);
        stageName.fontStyle = FontStyles.Bold;
        stageName.textWrappingMode = TextWrappingModes.NoWrap;
        stageName.enableAutoSizing = true;
        stageName.fontSizeMin = 30f;
        stageName.fontSizeMax = 44f;
        stageName.raycastTarget = false;
        SetLayoutHeight(stageName.gameObject, 56f);

        // The chips: the drivetrain's and the lift's watts, then a label for each mechanism worth naming. A fixed
        // row of slots, each saved switched off; RobotStageView fills in and turns on as many as the robot has.
        // Forced to the row's height, so every chip is the same height whatever its text.
        GameObject stageChips = CreateUIObject("StageRobotChips", stageCaption.transform);
        HorizontalLayoutGroup chipsLayout = stageChips.AddComponent<HorizontalLayoutGroup>();
        chipsLayout.spacing = StageChipSpacing;
        chipsLayout.childAlignment = TextAnchor.MiddleCenter;
        chipsLayout.childControlWidth = true;
        chipsLayout.childControlHeight = true;
        chipsLayout.childForceExpandWidth = false;
        chipsLayout.childForceExpandHeight = true;
        SetLayoutHeight(stageChips, StageChipHeight);
        var stageChipLabels = new TextMeshProUGUI[StageChipSlots];
        for (int slot = 0; slot < StageChipSlots; slot++)
            stageChipLabels[slot] = CreateStageChip(stageChips.transform, slot + 1);

        // Title. "RoboSimL" is 8 glyphs and fits at full size on every canvas we target, so the
        // autosize range below no longer does any work — it is kept because it costs nothing and is
        // what stops a longer name from overflowing if this string is ever changed again. The app
        // was called "Override Simulation" until the App Store rename; ProjectSettings.productName
        // and this string are now the same word, and the bundle id (…overridesim) deliberately is
        // not — it was already registered and a bundle id cannot be changed after the first upload.
        // The title is a sibling of the stage, not a child of it, because it does not stay in one
        // place: centred on the whole screen while the home menu is up, and sliding left to sit
        // over the stage as the settings panel docks in. See TitleDock at the end of this block.
        TextMeshProUGUI title = CreateText("Title", homeStage.transform, "RoboSimL", 88f);
        title.fontStyle = FontStyles.Bold;
        title.textWrappingMode = TextWrappingModes.NoWrap;
        title.enableAutoSizing = true;
        title.fontSizeMin = 52f;
        title.fontSizeMax = 96f;

        // The app's name in the app's own gradient — white at the top falling to the icon's cyan,
        // so the one piece of type nobody reads twice still says which app this is.
        //
        // TMP's OWN gradient, not the UiGradient component used everywhere else: TMP_Text builds
        // its geometry itself and never runs uGUI's mesh modifiers, so a UiGradient here would
        // attach cleanly, log nothing, and do nothing at all.
        title.enableVertexGradient = true;
        title.colorGradient = new VertexGradient(TextColor, TextColor,
            PrimaryBottomColor, PrimaryBottomColor);

        // Stretched across an anchor SPAN rather than given a width of its own, so the autosize
        // range above always has the real width to work against and TitleDock can move the title by
        // moving the span. A fixed 1400 would have overflowed the iPad's 867-wide stage outright.
        //
        // pivot 0.5 + anchoredPosition.x 0 + a negative sizeDelta.x is what makes the inset EQUAL at
        // both ends of whatever span it currently occupies: the rect centres in the span and loses
        // half the negative width at each side. So one number gives the title the same 24-unit
        // gutter centred on the screen and centred on the stage.
        RectTransform titleRect = title.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.anchoredPosition = new Vector2(0f, -StageMargin);
        titleRect.sizeDelta = new Vector2(-StageGutter * 2f, 140f);
        // The title band crosses the settings panel's tab row. MenuColumn is a later sibling so it
        // already wins the raycast, but a label is not something anyone should be able to press.
        title.raycastTarget = false;

        // Home: centred on the screen, because with the menu closed there is nothing on the right
        // for it to be centred against and a title pinned to one half reads as misaligned.
        // Settings: docked over the stage, where it would otherwise hang half across the panel.
        TitleDock titleDock = title.gameObject.AddComponent<TitleDock>();
        titleDock.homeAnchorMaxX = 1f;
        titleDock.dockedAnchorMaxX = 1f - MenuFraction;

        // Main panel: Drive / Settings.
        // Docked into the column, and deliberately NOT stretched to fill it: a 900-wide card with
        // two buttons in it would read as a stretched dialog. It keeps its own size, centred.
        GameObject mainPanel = CreatePanel("MainPanel", menuColumn.transform, new Vector2(520f, 340f));
        AddVerticalLayout(mainPanel, 40, 28f);
        Button driveButton = CreateButton("DriveButton", mainPanel.transform, "Drive", 52f, AccentColor);
        SetLayoutHeight(driveButton.gameObject, 110f);
        Button settingsButton = CreateButton("SettingsButton", mainPanel.transform, "Settings", 52f, AccentColor);
        SetLayoutHeight(settingsButton.gameObject, 110f);

        // Settings panel. Three tab pages instead of one flat column: the old layout was a single
        // VerticalLayoutGroup ~2,300 units tall in a ~956 viewport — 2.4 screens of scrolling, with
        // no scrollbar and no grouping, so the robot picker, the joystick sliders, the team codes
        // and four sub-screen entry points all ran together and most of them were simply invisible.
        //
        // Each page is its own scroll content; the controller activates one and points the shared
        // ScrollRect at it. Separate contents rather than nested layout groups because a
        // ContentSizeFitter inside a ContentSizeFitter is a reliable source of layout thrash.
        // This one DOES fill the column — it becomes the column. That also retires its
        // StretchPanelHeight call: the column already carries the top and bottom margin, and a
        // panel that took its own as well would inset twice.
        // The size passed here is immediately replaced by FillParent — the panel IS the column.
        GameObject settingsPanel = CreatePanel("SettingsPanel", menuColumn.transform, new Vector2(900f, 980f));
        FillParent(settingsPanel);

        const float TabRowHeight = 72f;
        const float BackRowHeight = 108f;
        // Driving is gone as a tab of its own: once the wheel-type box was removed (it asked the
        // player a hardware question that changed the physics) all it held was the two sensitivity
        // sliders and the drive-direction toggle, which belong with the controls and with the robot
        // respectively. A tab with two rows in it is a worse home for them than either.
        Button[] settingsTabs = CreateTabRow(settingsPanel, "SettingsTabs", TabRowHeight,
            out TabIndicator settingsTabIndicator, "Robot", "Controls", "Account");

        // The viewport is shared; only its content changes with the tab. Insets leave the tab row
        // above and the Back button below outside the scrolling area, so Back is always reachable
        // however long a page gets.
        GameObject content = CreateScrollingContent(settingsPanel, "Settings",
            TabRowHeight + 20f, BackRowHeight, true, out ScrollRect settingsScroll, out _);

        // Back sits OUTSIDE the scroll, pinned to the panel's bottom edge.
        Button backButton = CreateButton("BackButton", settingsPanel.transform, "Back", 40f, AccentColor);
        RectTransform backRect = backButton.GetComponent<RectTransform>();
        backRect.anchorMin = backRect.anchorMax = new Vector2(0.5f, 0f);
        backRect.pivot = new Vector2(0.5f, 0f);
        backRect.anchoredPosition = new Vector2(0f, 20f);
        backRect.sizeDelta = new Vector2(300f, 80f);

        // --- Robot page ---
        // The scroll view's own content object IS the first page; the other three are built as its
        // siblings and swapped in by the controller.
        GameObject robotPage = content;
        robotPage.name = "SettingsPage_Robot";

        TextMeshProUGUI header = CreateText("HeaderLabel", robotPage.transform, "Select Robot Model", 40f);
        header.fontStyle = FontStyles.Bold;
        header.alignment = TextAlignmentOptions.MidlineLeft;
        SetLayoutHeight(header.gameObject, 56f);

        // The picker is two columns, not one list: public robots on the left, the private ones this
        // device has unlocked on the right. One undifferentiated list gave the player no way to tell
        // a robot everybody gets from one they were personally given a code for — and the private
        // column doubles as the visible proof that a code they typed in Account did something.
        //
        // Full width, split in half with a gap: same footprint as the old list, so the Match section
        // below it stays where players are used to reaching for it.
        GameObject modelSplit = CreateUIObject("ModelListSplit", robotPage.transform);
        HorizontalLayoutGroup splitLayout = modelSplit.AddComponent<HorizontalLayoutGroup>();
        splitLayout.padding = new RectOffset(0, 0, 0, 0);
        splitLayout.spacing = 16f; // the padding between the halves
        splitLayout.childAlignment = TextAnchor.UpperCenter;
        splitLayout.childControlWidth = true;
        splitLayout.childControlHeight = true;
        splitLayout.childForceExpandWidth = true;  // each column takes exactly half
        splitLayout.childForceExpandHeight = true; // and both are as tall as the taller one
        LayoutElement splitElement = modelSplit.AddComponent<LayoutElement>();
        splitElement.flexibleHeight = 0f; // inside the scroll view the split takes its natural height

        // No ContentSizeFitter on the columns: a layout group already reports its preferred height
        // up the chain, and a fitter inside the page's fitter is the layout thrash CreateScrollPage
        // warns about. The fitters INSIDE the columns are on scroll contents, which no layout group
        // controls, so they're the ordinary pattern rather than a nesting.
        Transform publicList = CreateModelColumn(modelSplit.transform, "PublicModelColumn",
            "PublicModelList", "Public", out LayoutElement publicViewport);
        Transform privateList = CreateModelColumn(modelSplit.transform, "PrivateModelColumn",
            "PrivateModelList", "Private", out LayoutElement privateViewport);

        Button template = CreateButton("ModelButtonTemplate", publicList, "Model", 34f, NeutralColor);
        SetLayoutHeight(template.gameObject, 84f);
        // Half-width rows carry names like "Krypton  (654V)". Autosize down rather than clip, with a
        // small inset so nothing sits on the button's edge — the full-width list never needed this.
        TextMeshProUGUI templateLabel = template.GetComponentInChildren<TextMeshProUGUI>(true);
        templateLabel.enableAutoSizing = true;
        templateLabel.fontSizeMin = 22f;
        templateLabel.fontSizeMax = 34f;
        templateLabel.rectTransform.offsetMin = new Vector2(10f, 6f);
        templateLabel.rectTransform.offsetMax = new Vector2(-10f, -6f);
        template.gameObject.SetActive(false); // template stays inactive; controller clones it

        // Shown in place of the private column's rows when this device holds no codes — an empty
        // half-width box next to a full one reads as a bug rather than as "you have none yet".
        TextMeshProUGUI privateEmpty = CreateText("PrivateEmptyLabel", privateList,
            "Robots someone shares with you appear here once you enter their code under Account.", 22f);
        privateEmpty.alignment = TextAlignmentOptions.Top;
        SetLayoutHeight(privateEmpty.gameObject, 120f);

        // NOTE: an "Edit Models" button used to sit under the list, turning a row tap into a delete,
        // plus a "Make My Buttons the Default" button beside it. Both are gone from the game — see
        // the note in HomeScreenController. They live in Tools > RoboSim > Robot > Model Catalog,
        // which is Editor-only by construction rather than by an #if the builder could forget.
        CreateSectionHeader(robotPage.transform, "SectionMatch", "Match");

        // Automatic matchloading: checkbox (persisted via MatchLoadSettings). When off, the field
        // scene shows a Match Load button and the loaders wait for it instead of spawning on arrival.
        Toggle autoMatchloadToggle = CreateToggle("AutomaticMatchloadToggle", robotPage.transform,
            "Automatic Matchloading", MatchLoadSettings.DefaultAutomatic);
        SetLayoutHeight(autoMatchloadToggle.gameObject, 64f);

        // Which end of the robot the sticks treat as front (persisted via ReverseDriveSettings).
        // It sits with the robot rather than with the controls because it is a fact about THIS
        // robot — which end carries the intake — not a preference about the sticks.
        Toggle reverseDriveToggle = CreateToggle("ReverseDriveToggle", robotPage.transform,
            "Drive Backwards (swap which end is the front)", ReverseDriveSettings.DefaultReversed);
        SetLayoutHeight(reverseDriveToggle.gameObject, 64f);

        // Lite field: checkbox (persisted via FieldSceneSettings). Drive loads the stripped-down
        // LiteScene — one of each field feature — instead of the full field. Build it with
        // Tools > RoboSim > Scenes > Build Lite Field Scene; until then the setting falls back.
        Toggle liteFieldToggle = CreateToggle("LiteFieldToggle", robotPage.transform,
            "Lite Field (faster)", FieldSceneSettings.DefaultUseLiteField);
        SetLayoutHeight(liteFieldToggle.gameObject, 64f);

        // --- Controls page ---
        GameObject controlsPage = CreateTabPage(content, "SettingsPage_Controls");

        CreateSectionHeader(controlsPage.transform, "SectionOnScreen", "On-screen controls");

        // Joystick size control: label (also the live percentage readout) + slider. The
        // controller reads/writes JoystickSettings; the field scene's ControlsAppearance applies it.
        CreateSliderRow(controlsPage.transform, "JoystickSizeRow",
            "JoystickSizeLabel", "Joystick Size", "JoystickSizeSlider",
            JoystickSettings.MinScale, JoystickSettings.MaxScale, JoystickSettings.DefaultScale,
            out TextMeshProUGUI joystickLabel, out Slider joystickSlider);

        // Controls opacity: applies to joysticks AND the on-screen controller buttons
        // (ControlsAppearance reads it in the field scene).
        CreateSliderRow(controlsPage.transform, "ControlsOpacityRow",
            "ControlsOpacityLabel", "Controls Opacity", "ControlsOpacitySlider",
            ControlsOpacitySettings.MinOpacity, ControlsOpacitySettings.MaxOpacity,
            ControlsOpacitySettings.DefaultOpacity,
            out TextMeshProUGUI opacityLabel, out Slider opacitySlider);

        CreateSectionHeader(controlsPage.transform, "SectionButtons", "Buttons and layout");

        // Entry point to the button -> mechanism mapping screen.
        Button configureButton = CreateButton("ConfigureControllerButton", controlsPage.transform,
            "Configure Controller", 36f, AccentColor);
        SetLayoutHeight(configureButton.gameObject, 80f);

        // Entry point to the drag-to-reposition control layout screen.
        Button editLayoutButton = CreateButton("EditLayoutButton", controlsPage.transform,
            "Edit Control Layout", 36f, AccentColor);
        SetLayoutHeight(editLayoutButton.gameObject, 80f);

        CreateSectionHeader(controlsPage.transform, "SectionDriveFeel", "Drive feel");

        // Drive/turn sensitivity, persisted via DriveFeelSettings and snapshotted by
        // RobotMotorController.Awake — so they take effect on the next Drive.
        //
        // There were two checkboxes here as well, Smooth Acceleration and Coast When You Let Go.
        // They are gone on purpose: a drivetrain that ramps its throttle and rolls when released
        // isn't a preference, it is what a drivetrain does, and offering the alternative meant
        // shipping a mode that was wrong. Both behaviours are now unconditional for every robot.
        CreateSliderRow(controlsPage.transform, "DriveSensitivityRow",
            "DriveSensitivityLabel", "Drive Sensitivity", "DriveSensitivitySlider",
            DriveFeelSettings.MinDriveSensitivity, DriveFeelSettings.MaxDriveSensitivity,
            DriveFeelSettings.DefaultDriveSensitivity,
            out TextMeshProUGUI driveSensitivityLabel, out Slider driveSensitivitySlider);

        CreateSliderRow(controlsPage.transform, "TurnSensitivityRow",
            "TurnSensitivityLabel", "Turn Sensitivity", "TurnSensitivitySlider",
            DriveFeelSettings.MinTurnSensitivity, DriveFeelSettings.MaxTurnSensitivity,
            DriveFeelSettings.DefaultTurnSensitivity,
            out TextMeshProUGUI turnSensitivityLabel, out Slider turnSensitivitySlider);

        // --- Account page ---
        GameObject accountPage = CreateTabPage(content, "SettingsPage_Account");

        CreateSectionHeader(accountPage.transform, "SectionRobotCode", "Enter robot code");

        // A robot code reveals private robots whose owner code matches. Private models ship inside
        // the app but stay out of the picker until someone types the code here (RobotOwnerSettings
        // keeps the entered codes in PlayerPrefs). "Robot code" rather than "team code": one code
        // can cover a whole team's robots, but what the player is holding is a code for a robot.
        TextMeshProUGUI robotCodeHint = CreateText("RobotCodeHint", accountPage.transform,
            "Enter a code to unlock a robot someone shared with you.", 24f);
        robotCodeHint.alignment = TextAlignmentOptions.TopLeft;
        SetLayoutHeight(robotCodeHint.gameObject, 44f);

        TMP_InputField robotCodeInput = CreateInputField("RobotCodeInput", accountPage.transform,
            "Enter robot code", 30f);
        SetLayoutHeight(robotCodeInput.gameObject, 66f);

        Button unlockButton = CreateButton("UnlockCodeButton", accountPage.transform, "Unlock", 32f, AccentColor);
        SetLayoutHeight(unlockButton.gameObject, 64f);

        TextMeshProUGUI robotCodeStatus = CreateText("RobotCodeStatus", accountPage.transform, string.Empty, 26f);
        robotCodeStatus.alignment = TextAlignmentOptions.Center;
        SetLayoutHeight(robotCodeStatus.gameObject, 40f);

        CreateSectionHeader(accountPage.transform, "SectionYourCodes", "Your codes");

        // The codes this device holds, spelled out. A code is a bearer token — the player is meant to
        // pass it on to a teammate — and until now the only place it existed after being typed was
        // inside PlayerPrefs, so anyone who lost the message it came in could never share it again.
        // This is also what replaces the recovery ID: a new phone re-enters these, nothing else.
        TextMeshProUGUI yourCodesLabel = CreateText("YourCodesLabel", accountPage.transform,
            "No codes entered on this device.", 26f);
        yourCodesLabel.alignment = TextAlignmentOptions.TopLeft;
        yourCodesLabel.textWrappingMode = TextWrappingModes.Normal;
        // Deliberately NOT SetLayoutHeight: this is the one row whose height depends on how many
        // codes the player holds, and a fixed preferred height would clip the fourth one. Leaving
        // preferredHeight unset lets TMP's own ILayoutElement report the wrapped text's height, so
        // the row grows with the list; minHeight only keeps the empty state from collapsing.
        LayoutElement codesElement = yourCodesLabel.gameObject.AddComponent<LayoutElement>();
        codesElement.minHeight = 44f;
        codesElement.flexibleHeight = 0f;

        Button forgetCodesButton = CreateButton("ForgetCodesButton", accountPage.transform,
            "Forget Codes", 30f, NeutralColor);
        SetLayoutHeight(forgetCodesButton.gameObject, 56f);

        // NOTE: a "Your ID" section used to sit here — the uploader id minted on the first
        // submission, shown to be written down, with Copy and a paste-it-back Restore. It is gone.
        // It was an account system nobody asked for, protecting a link (device -> submission) that
        // the player never needs to follow: the robot comes back as a CODE, and the codes are listed
        // right above. A new phone re-enters those. The id itself is still minted and still used to
        // check the inbox, it just isn't a thing the player is asked to look after.
        CreateSectionHeader(accountPage.transform, "SectionSubmit", "Submit your own robot");

        // Entry point to the upload-your-own-robot screen.
        Button submitRobotButton = CreateButton("SubmitRobotButton", accountPage.transform,
            "Submit a Robot", 36f, AccentColor);
        SetLayoutHeight(submitRobotButton.gameObject, 80f);

        // Only the first page starts visible; the controller swaps the rest in.
        controlsPage.SetActive(false);
        accountPage.SetActive(false);

        settingsPanel.SetActive(false); // controller shows it via OnSettingsPressed

        // Controller config panel (inactive; opened from Settings > Configure Controller).
        ControllerConfigParts configParts = BuildControllerConfigPanel(canvasGo.transform);

        // Control layout panel (inactive; opened from Settings > Edit Control Layout).
        ControlsLayoutParts layoutParts = BuildControlsLayoutPanel(canvasGo.transform);

        // Submit-a-robot panel (inactive; opened from Settings > Submit a Robot).
        SubmitRobotParts submitParts = BuildSubmitRobotPanel(canvasGo.transform);

        // Inbox notice (inactive; shown at launch when a robot the player submitted has come back).
        InboxNoticeParts inboxParts = BuildInboxNotice(canvasGo.transform);

        // Loading overlay: built LAST so it's the top-most canvas child, covering everything while
        // the field scene loads. Inactive until Drive is pressed.
        GameObject loadingOverlay = BuildLoadingOverlay(canvasGo.transform);

        // Controller root: wire the private serialized refs (same SerializedObject pattern
        // as the Fix Robot Drive Collider tool) and persistent onClicks so everything
        // serializes into the scene.
        GameObject homeRoot = new GameObject("HomeScreen");
        HomeScreenController controller = homeRoot.AddComponent<HomeScreenController>();
        SerializedObject so = new SerializedObject(controller);
        // Re-load rather than trusting the instance loaded before NewScene: the scene swap can
        // destroy the native object behind an already-loaded asset reference, and a destroyed
        // object silently serializes as {fileID: 0} — the shipped-dead-model-list bug.
        RobotModelCatalog freshCatalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        so.FindProperty("catalog").objectReferenceValue = freshCatalog != null ? freshCatalog : catalog;
        so.FindProperty("mainPanel").objectReferenceValue = mainPanel;
        so.FindProperty("settingsPanel").objectReferenceValue = settingsPanel;
        // The stage AND the title. Both menu panels dock beside them, so only the three full-bleed
        // screens turn this off — see HomeScreenController.ShowStage.
        so.FindProperty("homeStage").objectReferenceValue = homeStage;
        so.FindProperty("titleDock").objectReferenceValue = titleDock;
        so.FindProperty("loadingOverlay").objectReferenceValue = loadingOverlay;
        so.FindProperty("publicModelListParent").objectReferenceValue = publicList;
        so.FindProperty("privateModelListParent").objectReferenceValue = privateList;
        so.FindProperty("privateEmptyLabel").objectReferenceValue = privateEmpty.gameObject;
        so.FindProperty("publicListViewport").objectReferenceValue = publicViewport;
        so.FindProperty("privateListViewport").objectReferenceValue = privateViewport;
        so.FindProperty("modelButtonTemplate").objectReferenceValue = template;
        so.FindProperty("settingsScroll").objectReferenceValue = settingsScroll;
        SerializedProperty tabButtonsProp = so.FindProperty("settingsTabButtons");
        SerializedProperty tabPagesProp = so.FindProperty("settingsTabPages");
        GameObject[] tabPages = { robotPage, controlsPage, accountPage };
        tabButtonsProp.arraySize = settingsTabs.Length;
        tabPagesProp.arraySize = tabPages.Length;
        for (int i = 0; i < settingsTabs.Length; i++)
            tabButtonsProp.GetArrayElementAtIndex(i).objectReferenceValue = settingsTabs[i];
        for (int i = 0; i < tabPages.Length; i++)
            tabPagesProp.GetArrayElementAtIndex(i).objectReferenceValue = tabPages[i];
        so.FindProperty("driveSensitivitySlider").objectReferenceValue = driveSensitivitySlider;
        so.FindProperty("driveSensitivityLabel").objectReferenceValue = driveSensitivityLabel;
        so.FindProperty("turnSensitivitySlider").objectReferenceValue = turnSensitivitySlider;
        so.FindProperty("turnSensitivityLabel").objectReferenceValue = turnSensitivityLabel;
        so.FindProperty("joystickSizeSlider").objectReferenceValue = joystickSlider;
        so.FindProperty("joystickSizeLabel").objectReferenceValue = joystickLabel;
        so.FindProperty("controlsOpacitySlider").objectReferenceValue = opacitySlider;
        so.FindProperty("controlsOpacityLabel").objectReferenceValue = opacityLabel;
        so.FindProperty("automaticMatchloadToggle").objectReferenceValue = autoMatchloadToggle;
        so.FindProperty("reverseDriveToggle").objectReferenceValue = reverseDriveToggle;
        so.FindProperty("liteFieldToggle").objectReferenceValue = liteFieldToggle;
        so.FindProperty("robotCodeInput").objectReferenceValue = robotCodeInput;
        so.FindProperty("robotCodeStatusLabel").objectReferenceValue = robotCodeStatus;
        so.FindProperty("yourCodesLabel").objectReferenceValue = yourCodesLabel;
        so.FindProperty("uploadConfig").objectReferenceValue = RoboSimPaths.LoadUploadConfig();
        so.FindProperty("inboxNotice").objectReferenceValue = inboxParts.overlay;
        so.FindProperty("inboxLabel").objectReferenceValue = inboxParts.label;
        so.FindProperty("inboxMessageLabel").objectReferenceValue = inboxParts.message;
        so.FindProperty("inboxMessageViewport").objectReferenceValue = inboxParts.messageViewport;
        so.FindProperty("inboxActionLabel").objectReferenceValue = inboxParts.unlockLabel;

        // Runtime selection tints, written FROM the palette rather than kept in step with it by
        // hand. These are serialized fields with C# defaults, so before this the palette and the
        // tints were two independent copies of the same colours joined only by a comment — and a
        // restyle that missed the second copy leaves the selected robot row painted in the old
        // theme's blue while every other button has moved on.
        //
        // Note selected != primary: SelectedColor marks state ("this robot is chosen"), the
        // primary gradient marks an action ("tap this"). They used to be the same blue.
        so.FindProperty("settingsTabIndicator").objectReferenceValue = settingsTabIndicator;
        so.FindProperty("selectedTint").colorValue = SelectedColor;
        so.FindProperty("normalTint").colorValue = NeutralColor;

        // Controller config screen: same root object, wired to the diagram it opens.
        ControllerConfigScreen configScreen = homeRoot.AddComponent<ControllerConfigScreen>();
        SerializedObject configSo = new SerializedObject(configScreen);
        configSo.FindProperty("catalog").objectReferenceValue = freshCatalog != null ? freshCatalog : catalog;
        configSo.FindProperty("panel").objectReferenceValue = configParts.panel;
        configSo.FindProperty("headerLabel").objectReferenceValue = configParts.header;
        configSo.FindProperty("emptyStateLabel").objectReferenceValue = configParts.emptyState;
        SerializedProperty buttonsProp = configSo.FindProperty("buttons");
        SerializedProperty labelsProp = configSo.FindProperty("assignmentLabels");
        buttonsProp.arraySize = configParts.buttons.Length;
        labelsProp.arraySize = configParts.captions.Length;
        for (int i = 0; i < configParts.buttons.Length; i++)
        {
            buttonsProp.GetArrayElementAtIndex(i).objectReferenceValue = configParts.buttons[i];
            labelsProp.GetArrayElementAtIndex(i).objectReferenceValue = configParts.captions[i];
        }
        configSo.FindProperty("assignmentPanel").objectReferenceValue = configParts.assignmentPanel;
        configSo.FindProperty("assignmentHeader").objectReferenceValue = configParts.assignmentHeader;
        configSo.FindProperty("assignmentListParent").objectReferenceValue = configParts.assignmentList;
        configSo.FindProperty("assignmentRowTemplate").objectReferenceValue = configParts.rowTemplate;
        configSo.FindProperty("clearButton").objectReferenceValue = configParts.clearButton;
        configSo.FindProperty("cancelButton").objectReferenceValue = configParts.cancelButton;
        configSo.FindProperty("controlStyleButton").objectReferenceValue = configParts.controlStyleButton;
        configSo.FindProperty("resetDefaultsButton").objectReferenceValue = configParts.resetDefaultsButton;

        // The config screen's four tints, from the same palette, for the same reason as above.
        // "A button that has something mapped" and "a popup row already on this button" both mean
        // CHOSEN, so both take SelectedColor; the popup row used to be a one-off green that
        // appeared nowhere else in the app.
        configSo.FindProperty("assignedTint").colorValue = SelectedColor;
        configSo.FindProperty("unassignedTint").colorValue = NeutralColor;
        configSo.FindProperty("selectedRowTint").colorValue = SelectedColor;
        configSo.FindProperty("rowTint").colorValue = NeutralColor;
        configSo.ApplyModifiedPropertiesWithoutUndo();

        so.FindProperty("controllerConfig").objectReferenceValue = configScreen;

        // Controls layout screen: same root object, wired to the preview it drives.
        ControlsLayoutScreen layoutScreen = homeRoot.AddComponent<ControlsLayoutScreen>();
        SerializedObject layoutSo = new SerializedObject(layoutScreen);
        layoutSo.FindProperty("panel").objectReferenceValue = layoutParts.panel;
        SerializedProperty proxiesProp = layoutSo.FindProperty("proxies");
        proxiesProp.arraySize = layoutParts.proxies.Length;
        for (int i = 0; i < layoutParts.proxies.Length; i++)
            proxiesProp.GetArrayElementAtIndex(i).objectReferenceValue = layoutParts.proxies[i];
        layoutSo.ApplyModifiedPropertiesWithoutUndo();

        so.FindProperty("controlsLayout").objectReferenceValue = layoutScreen;

        // Submit-a-robot screen: same root object, wired to its panel and the upload destination.
        SubmitRobotScreen submitScreen = homeRoot.AddComponent<SubmitRobotScreen>();
        SerializedObject submitSo = new SerializedObject(submitScreen);
        submitSo.FindProperty("panel").objectReferenceValue = submitParts.panel;
        submitSo.FindProperty("config").objectReferenceValue = RoboSimPaths.LoadUploadConfig();
        submitSo.FindProperty("teamInput").objectReferenceValue = submitParts.team;
        submitSo.FindProperty("robotInput").objectReferenceValue = submitParts.robot;
        submitSo.FindProperty("contactInput").objectReferenceValue = submitParts.contact;
        submitSo.FindProperty("notesInput").objectReferenceValue = submitParts.notes;
        submitSo.FindProperty("sharingButton").objectReferenceValue = submitParts.sharing;
        submitSo.FindProperty("chooseFileButton").objectReferenceValue = submitParts.chooseFile;
        submitSo.FindProperty("fileLabel").objectReferenceValue = submitParts.fileLabel;
        submitSo.FindProperty("sendButton").objectReferenceValue = submitParts.send;
        submitSo.FindProperty("progressBar").objectReferenceValue = submitParts.progress;
        submitSo.FindProperty("statusLabel").objectReferenceValue = submitParts.status;
        submitSo.ApplyModifiedPropertiesWithoutUndo();

        so.FindProperty("submitRobot").objectReferenceValue = submitScreen;
        so.ApplyModifiedPropertiesWithoutUndo();

        // The robot stage's window: the catalog it listens to for the selected robot (re-loaded, for the
        // same reason as the controller's above), the rig it drives, and what it fills in.
        SerializedObject viewSo = new SerializedObject(stageView);
        viewSo.FindProperty("catalog").objectReferenceValue = freshCatalog != null ? freshCatalog : catalog;
        viewSo.FindProperty("stage").objectReferenceValue = stageRig;
        viewSo.FindProperty("stageCamera").objectReferenceValue = stageCamera;
        viewSo.FindProperty("fallbackMark").objectReferenceValue = chassisMark;
        viewSo.FindProperty("nameLabel").objectReferenceValue = stageName;
        viewSo.FindProperty("chipRow").objectReferenceValue = stageChips;
        SerializedProperty chipSlots = viewSo.FindProperty("chipLabels");
        chipSlots.arraySize = stageChipLabels.Length;
        for (int slot = 0; slot < stageChipLabels.Length; slot++)
            chipSlots.GetArrayElementAtIndex(slot).objectReferenceValue = stageChipLabels[slot];
        viewSo.FindProperty("wattsColor").colorValue = SelectedColor;
        viewSo.ApplyModifiedPropertiesWithoutUndo();

        UnityEventTools.AddPersistentListener(driveButton.onClick, controller.OnDrivePressed);
        UnityEventTools.AddPersistentListener(settingsButton.onClick, controller.OnSettingsPressed);
        UnityEventTools.AddPersistentListener(backButton.onClick, controller.OnBackPressed);
        UnityEventTools.AddPersistentListener(configureButton.onClick, controller.OnConfigureControllerPressed);
        UnityEventTools.AddPersistentListener(settingsTabs[0].onClick, controller.OnRobotTabPressed);
        UnityEventTools.AddPersistentListener(settingsTabs[1].onClick, controller.OnControlsTabPressed);
        UnityEventTools.AddPersistentListener(settingsTabs[2].onClick, controller.OnAccountTabPressed);
        UnityEventTools.AddPersistentListener(unlockButton.onClick, controller.OnUnlockCodePressed);
        UnityEventTools.AddPersistentListener(forgetCodesButton.onClick, controller.OnForgetCodesPressed);
        UnityEventTools.AddPersistentListener(configParts.backButton.onClick, controller.OnConfigBackPressed);
        UnityEventTools.AddPersistentListener(editLayoutButton.onClick, controller.OnEditLayoutPressed);
        UnityEventTools.AddPersistentListener(layoutParts.backButton.onClick, controller.OnLayoutBackPressed);
        UnityEventTools.AddPersistentListener(layoutParts.resetButton.onClick, layoutScreen.OnResetPressed);
        UnityEventTools.AddPersistentListener(submitRobotButton.onClick, controller.OnSubmitRobotPressed);
        UnityEventTools.AddPersistentListener(submitParts.backButton.onClick, controller.OnSubmitBackPressed);
        UnityEventTools.AddPersistentListener(submitParts.chooseFile.onClick, submitScreen.OnChooseFilePressed);
        UnityEventTools.AddPersistentListener(submitParts.sharing.onClick, submitScreen.OnSharingPressed);
        UnityEventTools.AddPersistentListener(submitParts.send.onClick, submitScreen.OnSendPressed);
        UnityEventTools.AddPersistentListener(inboxParts.unlockButton.onClick, controller.OnInboxUnlockPressed);
    }

    // --- The robot stage ---

    // The home scene's lighting, which until the stage existed nothing had ever set. A new scene lights
    // itself from Unity's default procedural sky — a daylight blue that, as ambient, would light the
    // robot's shadowed side sky-blue against the navy UI. Set here, between NewScene (which resets it)
    // and the save.
    //
    // Ambient from the palette instead: the icon's navy family, lighter above, darkest below, so a
    // robot's underside reads as part of the screen rather than a hole in it. No sky and no reflection
    // source: both cameras clear to a solid colour, so a skybox would be drawn by neither and only cost
    // a reflection capture at load.
    private static void ApplyStageLighting()
    {
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = NeutralColor;
        RenderSettings.ambientEquatorColor = PanelColor;
        RenderSettings.ambientGroundColor = ListColor;
        RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = null;
        RenderSettings.fog = false;
    }

    // The robot stage's 3D half: a camera and three lights on one turning pivot, the holder the robot
    // stands in, and an inactive staging object robots are built under. See RobotStage for why the
    // camera orbits and the robot doesn't.
    private static RobotStage BuildRobotStage(Camera mainCamera, out Camera stageCamera)
    {
        GameObject root = new GameObject("RobotStage");
        RobotStage stage = root.AddComponent<RobotStage>();

        GameObject pivot = new GameObject("StagePivot");
        pivot.transform.SetParent(root.transform, false);
        GameObject holder = new GameObject("StageRobotHolder");
        holder.transform.SetParent(root.transform, false);
        GameObject staging = new GameObject("StageStaging");
        staging.transform.SetParent(root.transform, false);
        staging.SetActive(false);

        GameObject cameraGo = new GameObject("StageCamera");
        cameraGo.transform.SetParent(pivot.transform, false);
        stageCamera = cameraGo.AddComponent<Camera>();
        stageCamera.clearFlags = CameraClearFlags.SolidColor;
        // Transparent, so the robot composites over the backdrop — but NAVY at zero alpha, not black.
        // The texture is shown slightly scaled, and bilinear filtering blends each edge pixel of the
        // robot with its transparent neighbours: black ones would outline every edge in a dark fringe
        // against the navy, navy ones blend into it.
        Color clear = Color.Lerp(BackgroundTopColor, BackgroundBottomColor, 0.5f);
        clear.a = 0f;
        stageCamera.backgroundColor = clear;
        stageCamera.cullingMask = 1 << RobotShowcase.LayerIndex;
        stageCamera.fieldOfView = StageFieldOfView;
        stageCamera.allowHDR = false;
        stageCamera.allowMSAA = false;
        stageCamera.useOcclusionCulling = false;
        // BELOW the main camera in render order, by a WHOLE number. URP draws this overlay canvas itself,
        // on whichever camera is both screen-bound and the LAST base camera; one with a target texture is
        // not screen-bound, so if it sorted last NEITHER camera would draw the UI and the whole home
        // screen would blink out on every frame the stage draws. Cameras sort on (int)depth, and a tie
        // leaves the order to an unstable sort — hence a whole number clear of the main camera's.
        stageCamera.depth = mainCamera.depth - 10f;
        // RobotStageView switches it on for the frames it draws.
        stageCamera.enabled = false;

        UniversalAdditionalCameraData stageData = stageCamera.GetUniversalAdditionalCameraData();
        stageData.renderType = CameraRenderType.Base;
        stageData.renderShadows = false;
        stageData.renderPostProcessing = false;
        stageData.requiresDepthOption = CameraOverrideOption.Off;
        stageData.requiresColorOption = CameraOverrideOption.Off;
        stageData.antialiasing = AntialiasingMode.None;
        stageData.stopNaN = false;
        stageData.dithering = false;
        stageData.volumeLayerMask = 0;

        // Three lights on the pivot, so they turn with the camera and the robot is lit the same way from
        // every angle of the turn. In pivot space the camera looks along +Z, so "front" is -Z. All three
        // shadowless: a shadowless renderer skips the shadow DRAWS, but only shadowless lights skip the
        // shadow PASS, and the phone tier has main-light shadows on.
        //
        //   key  — warm, from above, front-right
        //   fill — cool and weaker, from the front-left, lifting what the key leaves in shadow
        //   rim  — the icon's cyan, from behind and above, catching the top and back edges
        //
        // The strengths were MEASURED, not picked: five rigs rendered headlessly on three robots. The stage
        // camera has no tonemapping (nor does the field), so light much past 1.2 on this near-white aluminium
        // just clips — a 1.5 key turned 30-41% of every robot pure white, and the V5 motors from dark grey to
        // white. The fill is what tints: at 0.5 a quarter of each robot read blue, at 0.22 it is 2-4%. The rim
        // sits 10 degrees up, so it grazes the top and back edges instead of washing the tops cyan.
        Light key = CreateStageLight("KeyLight", pivot.transform, KeyLightColor, 1.2f, new Vector3(42f, -38f, 0f));
        CreateStageLight("FillLight", pivot.transform, FillLightColor, 0.22f, new Vector3(12f, 48f, 0f));
        CreateStageLight("RimLight", pivot.transform, PrimaryBottomColor, 0.9f, new Vector3(10f, 205f, 0f));
        RenderSettings.sun = key;

        SerializedObject so = new SerializedObject(stage);
        so.FindProperty("pivot").objectReferenceValue = pivot.transform;
        so.FindProperty("stageCamera").objectReferenceValue = stageCamera;
        so.FindProperty("holder").objectReferenceValue = holder.transform;
        so.FindProperty("staging").objectReferenceValue = staging.transform;
        so.ApplyModifiedPropertiesWithoutUndo();
        return stage;
    }

    // One of the stage's chips: a rounded fill, with its label padded inside it. Saved switched off.
    private static TextMeshProUGUI CreateStageChip(Transform row, int number)
    {
        GameObject chip = CreateUIObject($"StageChip{number}", row);
        Image fill = chip.AddComponent<Image>();
        fill.sprite = HomeThemeSprites.Button;
        fill.type = Image.Type.Sliced;
        fill.pixelsPerUnitMultiplier = StageChipRoundness;
        fill.color = StageChipColor;
        fill.raycastTarget = false;
        HorizontalLayoutGroup inside = chip.AddComponent<HorizontalLayoutGroup>();
        inside.padding = new RectOffset(StageChipPadding, StageChipPadding, 0, 0);
        inside.childAlignment = TextAnchor.MiddleCenter;
        inside.childControlWidth = true;
        inside.childControlHeight = true;
        inside.childForceExpandWidth = false;
        inside.childForceExpandHeight = true;

        TextMeshProUGUI label = CreateText("Label", chip.transform, string.Empty, StageChipFontSize);
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        chip.SetActive(false);
        return label;
    }

    private static Light CreateStageLight(string name, Transform pivot, Color color, float intensity, Vector3 euler)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(pivot, false);
        go.transform.localRotation = Quaternion.Euler(euler);
        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = color;
        light.intensity = intensity;
        light.shadows = LightShadows.None;
        light.renderMode = LightRenderMode.ForcePixel;
        light.lightmapBakeType = LightmapBakeType.Realtime;
        // Documents intent. URP masks lights with rendering layers rather than this, but the stage is the
        // only 3D thing in the scene, so what these light is the robot either way.
        light.cullingMask = 1 << RobotShowcase.LayerIndex;
        return light;
    }

    // --- Robot inbox notice ---

    private class InboxNoticeParts
    {
        public GameObject overlay;
        public TextMeshProUGUI label;
        public TextMeshProUGUI message;
        public LayoutElement messageViewport;
        public Button unlockButton;
        public TextMeshProUGUI unlockLabel;
    }

    // Whatever came back about a submitted robot: that it has arrived, with a button that enters the
    // owner code — or that it couldn't be set up, and what to change so it can be.
    //
    // A DIALOG, not the banner this used to be. The banner was sized for one bold line and sat in the
    // gap above the main panel; a real message ("the arm came in as one solid piece, so nothing can
    // pivot — re-export with the arm as its own component") is four or five lines, and a panel that
    // grows to fit one either walks up into the title or down over the Drive button. Neither is a
    // layout that can be tuned into working, because the developer writing the message decides its
    // length. So: centred, dimmed behind, message capped and scrolling, one button out.
    //
    // Built before the loading overlay so the overlay stays the top-most canvas child.
    private static InboxNoticeParts BuildInboxNotice(Transform canvas)
    {
        var parts = new InboxNoticeParts();

        GameObject overlay = CreateUIObject("InboxNotice", canvas);
        RectTransform overlayRect = (RectTransform)overlay.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        // Dimmed rather than opaque: the home screen stays recognisable behind it, so this reads as
        // something on top of the app instead of a screen the app has moved to. raycastTarget stays
        // true, which is what stops a tap landing on Drive through the dim.
        Image scrim = overlay.AddComponent<Image>();
        scrim.color = new Color(BackgroundTopColor.r, BackgroundTopColor.g, BackgroundTopColor.b, 0.86f);
        scrim.raycastTarget = true;
        parts.overlay = overlay;

        GameObject panel = CreatePanel("InboxPanel", overlay.transform, new Vector2(800f, 200f));
        AddVerticalLayout(panel, 24, 18f);
        ContentSizeFitter panelFitter = panel.AddComponent<ContentSizeFitter>();
        panelFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        parts.label = CreateText("InboxLabel", panel.transform, "Your robot is ready.", 34f);
        parts.label.fontStyle = FontStyles.Bold;
        SetLayoutHeight(parts.label.gameObject, 56f);

        // The message scrolls inside a capped viewport. Its height is set at runtime from the text's
        // own preferred height (HomeScreenController.ShowInboxNotice), so a one-line aside takes one
        // line and a set of re-export instructions takes the cap and scrolls.
        GameObject messageViewport = CreateUIObject("InboxMessageViewport", panel.transform);
        Image messageCatcher = messageViewport.AddComponent<Image>();
        messageCatcher.color = Color.clear;
        messageCatcher.raycastTarget = true; // else the gaps between lines can't be dragged
        messageViewport.AddComponent<RectMask2D>();
        parts.messageViewport = messageViewport.AddComponent<LayoutElement>();
        parts.messageViewport.flexibleHeight = 0f;
        parts.messageViewport.preferredHeight = 120f; // authored default; the controller overwrites it

        parts.message = CreateText("InboxMessage", messageViewport.transform, string.Empty, 26f);
        parts.message.alignment = TextAlignmentOptions.TopLeft;
        parts.message.color = new Color(TextColor.r, TextColor.g, TextColor.b, 0.88f);
        RectTransform messageRect = parts.message.rectTransform;
        messageRect.anchorMin = new Vector2(0f, 1f);
        messageRect.anchorMax = new Vector2(1f, 1f);
        messageRect.pivot = new Vector2(0.5f, 1f);
        messageRect.anchoredPosition = Vector2.zero;
        messageRect.sizeDelta = Vector2.zero;
        ContentSizeFitter messageFitter = parts.message.gameObject.AddComponent<ContentSizeFitter>();
        messageFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // A plain ScrollRect here, not a NestedScrollRect: this dialog has no scroll view above it to
        // hand a drag back to.
        ScrollRect messageScroll = messageViewport.AddComponent<ScrollRect>();
        messageScroll.horizontal = false;
        messageScroll.vertical = true;
        messageScroll.movementType = ScrollRect.MovementType.Clamped;
        messageScroll.scrollSensitivity = 28f;
        messageScroll.viewport = (RectTransform)messageViewport.transform;
        messageScroll.content = messageRect;

        messageViewport.SetActive(false); // shown only when there is something to say

        parts.unlockButton = CreateButton("InboxUnlockButton", panel.transform,
            "Add it to my list", 32f, AccentColor);
        SetLayoutHeight(parts.unlockButton.gameObject, 64f);
        // The same button dismisses a note that has no code to add, so its caption is set at runtime.
        parts.unlockLabel = parts.unlockButton.GetComponentInChildren<TextMeshProUGUI>(true);

        overlay.SetActive(false); // shown only when the inbox actually has something waiting
        return parts;
    }

    // --- Controller config panel ---

    private class ControllerConfigParts
    {
        public GameObject panel;
        public TextMeshProUGUI header;
        public GameObject emptyState;
        public Button[] buttons = new Button[ControllerMapSettings.ButtonCount];
        public TextMeshProUGUI[] captions = new TextMeshProUGUI[ControllerMapSettings.ButtonCount];
        public GameObject assignmentPanel;
        public TextMeshProUGUI assignmentHeader;
        public Transform assignmentList;
        public Button rowTemplate;
        public Button clearButton;
        public Button cancelButton;
        public Button controlStyleButton;
        public Button resetDefaultsButton;
        public Button backButton;
    }

    // Near-fullscreen panel showing a stylized controller: 12 tappable buttons laid out like
    // the drive scene's on-screen controller (shoulders top corners, arrow + XBAY diamonds in
    // the middle, decorative stick circles below), each with an assignment caption beneath it.
    // Tapping a button opens the assignment popup; ControllerConfigScreen drives the logic.
    private static ControllerConfigParts BuildControllerConfigPanel(Transform canvas)
    {
        var parts = new ControllerConfigParts();

        // The size passed here is replaced by FillParent; the panel takes the canvas less a margin.
        GameObject panel = CreatePanel("ControllerConfigPanel", canvas, new Vector2(1700f, 980f));
        FillParent(panel, StageMargin);
        parts.panel = panel;

        parts.header = CreateText("ConfigHeader", panel.transform, "Controller", 48f);
        parts.header.fontStyle = FontStyles.Bold;
        RectTransform headerRect = parts.header.rectTransform;
        headerRect.anchorMin = headerRect.anchorMax = new Vector2(0.5f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.anchoredPosition = new Vector2(0f, -20f);
        headerRect.sizeDelta = new Vector2(1200f, 60f);

        TextMeshProUGUI emptyState = CreateText("EmptyStateLabel", panel.transform,
            "This robot has no mechanisms to control. Import a URDF robot with arm or piston " +
            "joints, then map its buttons here.", 28f);
        RectTransform emptyRect = emptyState.rectTransform;
        emptyRect.anchorMin = emptyRect.anchorMax = new Vector2(0.5f, 1f);
        emptyRect.pivot = new Vector2(0.5f, 1f);
        emptyRect.anchoredPosition = new Vector2(0f, -84f);
        emptyRect.sizeDelta = new Vector2(1500f, 76f);
        parts.emptyState = emptyState.gameObject;

        // The room the diagram may use: from under the empty-state label down to a clear gap above
        // the button row. Stretched, so it shrinks with the panel — a 6.5" phone's panel is ~899
        // units tall against ~1167 on a 13" iPad, and at the phone's height a fixed 700-tall diagram
        // left the Back row 12 units off the dark backdrop, reading as no padding at all.
        GameObject diagramArea = CreateUIObject("ConfigDiagramArea", panel.transform);
        RectTransform diagramAreaRect = (RectTransform)diagramArea.transform;
        diagramAreaRect.anchorMin = Vector2.zero;
        diagramAreaRect.anchorMax = Vector2.one;
        // The panel now fits the canvas, so this inset is just the gap between the dark content
        // area and the panel's own edge — the same StageGutter used between stage and menu.
        diagramAreaRect.offsetMin = new Vector2(StageGutter, BottomRowInset + BottomRowHeight + 24f);
        diagramAreaRect.offsetMax = new Vector2(-StageGutter, -176f); // clear of the header and empty state

        GameObject diagram = CreateUIObject("ControllerDiagram", diagramArea.transform);
        RectTransform diagramRect = (RectTransform)diagram.transform;
        diagramRect.anchorMin = diagramRect.anchorMax = new Vector2(0.5f, 0.5f);
        diagramRect.pivot = new Vector2(0.5f, 0.5f);
        diagramRect.anchoredPosition = Vector2.zero;
        diagramRect.sizeDelta = new Vector2(1560f, 700f);
        // Scaled rather than resized: the twelve pills sit at absolute offsets from the diagram's
        // centre, so resizing would slide the backdrop out from under them.
        ScaleToFitParent diagramFit = diagramArea.AddComponent<ScaleToFitParent>();
        diagramFit.target = diagramRect;
        Image diagramImage = diagram.AddComponent<Image>();
        diagramImage.sprite = HomeThemeSprites.Panel;
        diagramImage.type = Image.Type.Sliced;
        diagramImage.color = ListColor;

        AddDecorativeStick(diagram.transform, "LeftStickMarker", new Vector2(-450f, -180f));
        AddDecorativeStick(diagram.transform, "RightStickMarker", new Vector2(450f, -180f));

        // ControllerButton order: L1 L2 R1 R2 | Up Down Left Right | X B A Y.
        Vector2 pillSize = new Vector2(170f, 70f);
        parts.buttons[0] = CreateConfigButton(diagram.transform, "CfgL1", "L1",
            new Vector2(-600f, 250f), pillSize, false, out parts.captions[0]);
        parts.buttons[1] = CreateConfigButton(diagram.transform, "CfgL2", "L2",
            new Vector2(-600f, 130f), pillSize, false, out parts.captions[1]);
        parts.buttons[2] = CreateConfigButton(diagram.transform, "CfgR1", "R1",
            new Vector2(600f, 250f), pillSize, false, out parts.captions[2]);
        parts.buttons[3] = CreateConfigButton(diagram.transform, "CfgR2", "R2",
            new Vector2(600f, 130f), pillSize, false, out parts.captions[3]);

        // Diamond centers sit +-260 from the panel center: far enough apart that the two
        // inner buttons' 220px-wide assignment captions (CfgRight at x-140, CfgY at x+140)
        // never overlap each other.
        Vector2 roundSize = new Vector2(76f, 76f);
        parts.buttons[4] = CreateConfigButton(diagram.transform, "CfgUp", "Up",
            new Vector2(-260f, 180f), roundSize, true, out parts.captions[4]);
        parts.buttons[5] = CreateConfigButton(diagram.transform, "CfgDown", "Down",
            new Vector2(-260f, -60f), roundSize, true, out parts.captions[5]);
        parts.buttons[6] = CreateConfigButton(diagram.transform, "CfgLeft", "Left",
            new Vector2(-380f, 60f), roundSize, true, out parts.captions[6]);
        parts.buttons[7] = CreateConfigButton(diagram.transform, "CfgRight", "Right",
            new Vector2(-140f, 60f), roundSize, true, out parts.captions[7]);

        parts.buttons[8] = CreateConfigButton(diagram.transform, "CfgX", "X",
            new Vector2(260f, 180f), roundSize, true, out parts.captions[8]);
        parts.buttons[9] = CreateConfigButton(diagram.transform, "CfgB", "B",
            new Vector2(380f, 60f), roundSize, true, out parts.captions[9]);
        parts.buttons[10] = CreateConfigButton(diagram.transform, "CfgA", "A",
            new Vector2(260f, -60f), roundSize, true, out parts.captions[10]);
        parts.buttons[11] = CreateConfigButton(diagram.transform, "CfgY", "Y",
            new Vector2(140f, 60f), roundSize, true, out parts.captions[11]);

        // Bottom row: Back | Control Style | Reset to Default, inside a layout group.
        //
        // The group is the point. Two of the three are hidden per robot — Control Style needs at
        // least one mechanism, Reset to Default needs the robot to actually ship a default layout —
        // so the row was authored at fixed x = -330 / 0 / +330 and then lost buttons at runtime,
        // leaving whatever survived stranded at its absolute slot. On a bare drivetrain that meant
        // a lone Back button sitting 330 units left of centre, which is what "the Back button is
        // un-centered" is. A HorizontalLayoutGroup lays out only ACTIVE children, so the row
        // re-centres itself for any subset, including combinations nobody enumerated.
        //
        // Created before AssignmentPanel below so the popup stays the last sibling and keeps
        // drawing on top of these.
        GameObject bottomRow = CreateUIObject("ConfigBottomRow", panel.transform);
        RectTransform rowRect = (RectTransform)bottomRow.transform;
        rowRect.anchorMin = new Vector2(0f, 0f);
        rowRect.anchorMax = new Vector2(1f, 0f);
        rowRect.pivot = new Vector2(0.5f, 0f);
        rowRect.offsetMin = new Vector2(12f, BottomRowInset);
        rowRect.offsetMax = new Vector2(-12f, BottomRowInset + BottomRowHeight);
        HorizontalLayoutGroup rowLayout = bottomRow.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 40f;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        // Both false, and both load-bearing: childControlWidth would discard the per-button widths
        // below, and childForceExpandWidth would make the children fill the row so MiddleCenter
        // had nothing left to centre.
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = false;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;

        // The layout group drives each child's anchors and x position, so only the size is set
        // here — authoring anchors as well would bake dead values that read as authoritative.
        parts.backButton = CreateButton("ConfigBackButton", bottomRow.transform, "Back", 36f, AccentColor);
        ((RectTransform)parts.backButton.transform).sizeDelta = new Vector2(240f, 64f);

        // Switches a mechanism between one- and two-button control (ControllerConfigScreen reuses
        // the assignment popup for it).
        parts.controlStyleButton = CreateButton("ControlStyleButton", bottomRow.transform,
            "Control Style", 36f, NeutralColor);
        ((RectTransform)parts.controlStyleButton.transform).sizeDelta = new Vector2(300f, 64f);

        // Puts this robot's shipped layout back. ControllerConfigScreen hides it for a robot that
        // ships without one, so it never offers to restore something that doesn't exist.
        parts.resetDefaultsButton = CreateButton("ResetDefaultsButton", bottomRow.transform,
            "Reset to Default", 32f, NeutralColor);
        ((RectTransform)parts.resetDefaultsButton.transform).sizeDelta = new Vector2(300f, 64f);

        // Assignment popup: header + scrollable option list + Clear/Cancel. Scrolls because a
        // many-motor robot yields two rows per motor.
        GameObject assignmentPanel = CreatePanel("AssignmentPanel", panel.transform, new Vector2(720f, 780f));
        AddVerticalLayout(assignmentPanel, 32, 16f);
        parts.assignmentPanel = assignmentPanel;

        parts.assignmentHeader = CreateText("AssignmentHeader", assignmentPanel.transform, "Assign", 40f);
        parts.assignmentHeader.fontStyle = FontStyles.Bold;
        SetLayoutHeight(parts.assignmentHeader.gameObject, 56f);

        GameObject scroll = CreateUIObject("AssignmentScroll", assignmentPanel.transform);
        Image scrollImage = scroll.AddComponent<Image>(); // list backdrop + drag-catcher
        scrollImage.sprite = HomeThemeSprites.Panel;
        scrollImage.type = Image.Type.Sliced;
        scrollImage.color = ListColor;
        scroll.AddComponent<RectMask2D>();
        ScrollRect scrollRect = scroll.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.scrollSensitivity = 28f; // the default 1 crawls; match the settings list
        LayoutElement scrollElement = scroll.AddComponent<LayoutElement>();
        scrollElement.flexibleHeight = 1f; // the list absorbs the leftover popup height

        GameObject list = CreateUIObject("AssignmentList", scroll.transform);
        RectTransform listRect = (RectTransform)list.transform;
        listRect.anchorMin = new Vector2(0f, 1f);
        listRect.anchorMax = new Vector2(1f, 1f);
        listRect.pivot = new Vector2(0.5f, 1f);
        listRect.anchoredPosition = Vector2.zero;
        listRect.sizeDelta = Vector2.zero;
        VerticalLayoutGroup listLayout = AddVerticalLayout(list, 16, 12f);
        listLayout.childAlignment = TextAnchor.UpperCenter;
        ContentSizeFitter fitter = list.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scrollRect.content = listRect;
        scrollRect.viewport = (RectTransform)scroll.transform;
        parts.assignmentList = list.transform;

        parts.rowTemplate = CreateButton("AssignmentRowTemplate", list.transform,
            "Mechanism — Forward", 32f, NeutralColor);
        SetLayoutHeight(parts.rowTemplate.gameObject, 64f);
        parts.rowTemplate.gameObject.SetActive(false); // template stays inactive; screen clones it

        parts.clearButton = CreateButton("ClearButton", assignmentPanel.transform,
            "Clear Assignment", 36f, NeutralColor);
        SetLayoutHeight(parts.clearButton.gameObject, 72f);
        parts.cancelButton = CreateButton("CancelButton", assignmentPanel.transform, "Cancel", 36f, AccentColor);
        SetLayoutHeight(parts.cancelButton.gameObject, 72f);

        assignmentPanel.SetActive(false);
        panel.SetActive(false); // ControllerConfigScreen.Open shows it
        return parts;
    }

    // Diagram button + the assignment caption below it (a sibling, so button tints don't dim it).
    private static Button CreateConfigButton(Transform parent, string name, string label,
        Vector2 position, Vector2 size, bool round, out TextMeshProUGUI caption)
    {
        GameObject go = CreateUIObject(name, parent);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        Image image = go.AddComponent<Image>();
        if (round)
        {
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        }
        else
        {
            image.sprite = HomeThemeSprites.Button;
            image.type = Image.Type.Sliced;
        }
        image.color = NeutralColor;
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;

        TextMeshProUGUI text = CreateText("Label", go.transform, label, round ? 22f : 32f);
        text.fontStyle = FontStyles.Bold;
        text.raycastTarget = false;
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        // A button can drive several mechanisms at once, and the caption lists them one per line — so
        // it's anchored by its TOP edge and given room for three lines. Growing downward rather than
        // wider is deliberate: the 220 width was tuned so the two inner diamond captions (CfgRight at
        // x=-140 and CfgY at x=+140) don't collide, and widening would put them back on top of each
        // other. Anything past three lines folds into a "+N" tail (see ControllerConfigScreen).
        const float captionLine = 26f;
        const float captionLines = 3f;
        caption = CreateText(name + "_Assign", parent, string.Empty, 20f);
        caption.color = AccentColor;
        caption.raycastTarget = false;
        caption.verticalAlignment = VerticalAlignmentOptions.Top;
        RectTransform captionRect = caption.rectTransform;
        captionRect.anchorMin = captionRect.anchorMax = new Vector2(0.5f, 0.5f);
        captionRect.pivot = new Vector2(0.5f, 1f); // top-anchored: extra lines hang downward
        captionRect.anchoredPosition = new Vector2(position.x, position.y - size.y * 0.5f - 9f);
        captionRect.sizeDelta = new Vector2(220f, captionLine * captionLines);
        return button;
    }

    private static void AddDecorativeStick(Transform parent, string name, Vector2 position)
    {
        GameObject stick = CreateUIObject(name, parent);
        RectTransform rect = (RectTransform)stick.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(140f, 140f);
        Image image = stick.AddComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        image.color = NeutralColor;
        image.raycastTarget = false;

        TextMeshProUGUI caption = CreateText(name + "_Caption", parent, "Drive (fixed)", 22f);
        caption.raycastTarget = false;
        RectTransform captionRect = caption.rectTransform;
        captionRect.anchorMin = captionRect.anchorMax = new Vector2(0.5f, 0.5f);
        captionRect.pivot = new Vector2(0.5f, 0.5f);
        captionRect.anchoredPosition = new Vector2(position.x, position.y - 92f);
        captionRect.sizeDelta = new Vector2(240f, 28f);
    }

    // --- Submit a Robot panel ---

    private class SubmitRobotParts
    {
        public GameObject panel;
        public TMP_InputField team;
        public TMP_InputField robot;
        public TMP_InputField contact;
        public TMP_InputField notes;
        public Button sharing;
        public Button chooseFile;
        public TextMeshProUGUI fileLabel;
        public Button send;
        public Slider progress;
        public TextMeshProUGUI status;
        public Button backButton;
    }

    // Where a player sends their own robot in. The screen is deliberately plain-spoken: the file goes
    // to the developer and comes back set up in an update, because every step of robot setup
    // (collider generation, the drivetrain rig, mechanism joints, prefab saving) is Editor-only and
    // cannot run in the shipped app.
    private static SubmitRobotParts BuildSubmitRobotPanel(Transform canvas)
    {
        var parts = new SubmitRobotParts();

        GameObject panel = CreatePanel("SubmitRobotPanel", canvas, new Vector2(760f, 980f));
        StretchPanelHeight(panel, 760f); // see ControllerConfigPanel
        parts.panel = panel;
        // Scrollbar here too: this panel's eleven rows fit at 1080 but not on a shorter canvas, and
        // the same "nothing tells you there's more below" complaint applies.
        GameObject content = CreateScrollingContent(panel, "Submit", 12f, 12f, true, out _, out _);

        TextMeshProUGUI header = CreateText("SubmitHeader", content.transform, "Submit a Robot", 48f);
        header.fontStyle = FontStyles.Bold;
        SetLayoutHeight(header.gameObject, 66f);

        // Two facts, and only two: it takes days, and nobody has to keep checking. This used to
        // spell out that the finished robot ships inside the next version of the app — true when it
        // was written, and no longer: a published robot now arrives over the air (RobotCatalogSync
        // at launch, and the inbox notice for one submitted from this device), so the update caveat
        // would now be the misleading half.
        TextMeshProUGUI hint = CreateText("SubmitHint", content.transform,
            "Send your robot to get set up. Will take a few days to process, you will be " +
            "notified when it gets complete.", 26f);
        SetLayoutHeight(hint.gameObject, 96f);

        parts.team = CreateInputField("TeamInput", content.transform, "Team (e.g. 654V)", 32f);
        SetLayoutHeight(parts.team.gameObject, 68f);
        // Optional because Select() fills it in from the file name — the field exists for the player
        // who wants a nicer name than "654V_v3_final.fbx", not as a thing to demand.
        parts.robot = CreateInputField("RobotNameInput", content.transform,
            "Robot name (optional)", 32f);
        SetLayoutHeight(parts.robot.gameObject, 68f);
        // Email only. Discord was offered and shouldn't have been: replying there means a shared
        // server or a DM request, neither of which exists as part of this flow.
        parts.contact = CreateInputField("ContactInput", content.transform, "Your email", 32f);
        SetLayoutHeight(parts.contact.gameObject, 68f);
        parts.notes = CreateInputField("NotesInput", content.transform,
            "Any specific instructions? (optional)", 32f);
        SetLayoutHeight(parts.notes.gameObject, 68f);

        // Who may use the robot afterwards — the uploader's call, since it decides whether the finished
        // catalog entry ships Public or Private. Cycles through RobotUploadService.SharingOptions.
        parts.sharing = CreateButton("SharingButton", content.transform,
            "Who can use it:  " + RobotUploadService.SharingOptions[0], 28f, NeutralColor);
        SetLayoutHeight(parts.sharing.gameObject, 72f);

        // Which format to send, said immediately above the button that picks one — the only moment
        // the advice can still change what someone exports.
        TextMeshProUGUI formatHint = CreateText("SubmitFormatHint", content.transform,
            RobotFilePicker.FormatAdvice, 24f);
        formatHint.fontStyle = FontStyles.Italic;
        // A fixed height does not grow with the text, so check the character count against this
        // before editing FormatAdvice. Measured at 24pt across this panel: ~35 characters to the
        // line, so up to 70 fits two lines at 60, up to 112 fits three at 90, and 120 needs four.
        // The current advice is 106 — deliberately kept under 112 so adding the Files path cost no
        // height at all.
        SetLayoutHeight(formatHint.gameObject, 90f);

        parts.chooseFile = CreateButton("ChooseFileButton", content.transform, "Choose File", 36f, NeutralColor);
        SetLayoutHeight(parts.chooseFile.gameObject, 72f);

        parts.fileLabel = CreateText("SubmitFileLabel", content.transform, "No file chosen", 28f);
        SetLayoutHeight(parts.fileLabel.gameObject, 44f);

        parts.progress = CreateSlider("SubmitProgress", content.transform, 0f, 1f, 0f);
        SetLayoutHeight(parts.progress.gameObject, 30f);
        parts.progress.interactable = false; // a read-out, not a control
        parts.progress.gameObject.SetActive(false);

        parts.send = CreateButton("SendRobotButton", content.transform, "Send", 40f, AccentColor);
        SetLayoutHeight(parts.send.gameObject, 84f);
        parts.send.interactable = false; // nothing to send until a file is chosen

        // No SetLayoutHeight, deliberately. This label is EMPTY until a send is attempted, and a
        // fixed 90 reserved a blank the height of a button between Send and Back for the whole life
        // of the screen — which is why Back read as being stranded far below the thing it follows.
        // With no LayoutElement the vertical layout group asks TMP for its preferred height, which
        // is nothing while there is no text and grows to fit the message when there is one.
        parts.status = CreateText("SubmitStatus", content.transform, string.Empty, 28f);

        parts.backButton = CreateButton("SubmitBackButton", content.transform, "Back", 36f, AccentColor);
        SetLayoutHeight(parts.backButton.gameObject, 76f);

        panel.SetActive(false); // opened from Settings > Submit a Robot
        return parts;
    }

    // The submissions destination asset. Created empty on purpose — it needs a Firebase bucket and
    // web API key that only the project owner can supply, and the submit screen says so until it has
    // them rather than failing halfway through an upload.
    private static RobotUploadConfig EnsureUploadConfig(out bool created)
    {
        RobotUploadConfig config = RoboSimPaths.LoadUploadConfig();
        created = false;
        if (config != null) return config;

        if (!AssetDatabase.IsValidFolder("Assets/Settings")) AssetDatabase.CreateFolder("Assets", "Settings");
        config = ScriptableObject.CreateInstance<RobotUploadConfig>();
        AssetDatabase.CreateAsset(config, RoboSimPaths.RobotUploadConfig);
        AssetDatabase.SaveAssets();
        created = true;
        return config;
    }

    // --- Control layout panel ---

    private class ControlsLayoutParts
    {
        public GameObject panel;
        public DraggableControlProxy[] proxies;
        public Button resetButton;
        public Button backButton;
    }

    // Near-fullscreen panel for repositioning the on-screen controls: a scaled 1920x1080 preview
    // of the field with one draggable proxy tile per control group (joysticks, shoulder pairs,
    // arrow diamond, XYAB diamond). The preview's LOCAL space is the 1920x1080 reference (a
    // localScale shrinks it to fit), so a proxy's anchoredPosition is in reference pixels and the
    // drag delta transfers 1:1 to the real control. DraggableControlProxy saves the deltas;
    // ControlsAppearance applies them in the field scene.
    private static ControlsLayoutParts BuildControlsLayoutPanel(Transform canvas)
    {
        var parts = new ControlsLayoutParts();

        // The size passed here is replaced by FillParent; see ControllerConfigPanel.
        GameObject panel = CreatePanel("ControlsLayoutPanel", canvas, new Vector2(1700f, 980f));
        FillParent(panel, StageMargin);
        parts.panel = panel;

        TextMeshProUGUI header = CreateText("LayoutHeader", panel.transform, "Edit Control Layout", 48f);
        header.fontStyle = FontStyles.Bold;
        RectTransform headerRect = header.rectTransform;
        headerRect.anchorMin = headerRect.anchorMax = new Vector2(0.5f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.anchoredPosition = new Vector2(0f, -20f);
        headerRect.sizeDelta = new Vector2(1200f, 60f);

        TextMeshProUGUI hint = CreateText("LayoutHint", panel.transform,
            "Drag each control to reposition it. Arrows and X/Y/A/B each move as one group.", 26f);
        RectTransform hintRect = hint.rectTransform;
        hintRect.anchorMin = hintRect.anchorMax = new Vector2(0.5f, 1f);
        hintRect.pivot = new Vector2(0.5f, 1f);
        hintRect.anchoredPosition = new Vector2(0f, -82f);
        hintRect.sizeDelta = new Vector2(1500f, 40f);

        // The room the preview may use: under the hint, clear of the buttons. See ConfigDiagramArea
        // — the authored 0.7 scale this replaces was measured against a 1080-tall canvas, and on a
        // ~899-tall one it put the preview 30 units ON TOP of the Back button.
        GameObject previewArea = CreateUIObject("LayoutPreviewArea", panel.transform);
        RectTransform previewAreaRect = (RectTransform)previewArea.transform;
        previewAreaRect.anchorMin = Vector2.zero;
        previewAreaRect.anchorMax = Vector2.one;
        // The panel now fits the canvas, so this inset is just the gap between the dark content
        // area and the panel's own edge — the same StageGutter used between stage and menu.
        previewAreaRect.offsetMin = new Vector2(StageGutter, BottomRowInset + BottomRowHeight + 24f);
        previewAreaRect.offsetMax = new Vector2(-StageGutter, -138f); // clear of the header and hint

        // The preview: local space = 1920x1080 reference, scaled to fit the area above.
        GameObject preview = CreateUIObject("LayoutPreview", previewArea.transform);
        RectTransform previewRect = (RectTransform)preview.transform;
        previewRect.anchorMin = previewRect.anchorMax = new Vector2(0.5f, 0.5f);
        previewRect.pivot = new Vector2(0.5f, 0.5f);
        previewRect.anchoredPosition = Vector2.zero;
        previewRect.sizeDelta = new Vector2(1920f, 1080f);
        // Scale, never size: a proxy's anchoredPosition IS the control's position in 1920x1080
        // reference pixels, and DraggableControlProxy clamps against dragArea.rect — the UNSCALED
        // rect — so the reference space has to stay exactly 1920x1080 whatever it is drawn at.
        ScaleToFitParent previewFit = previewArea.AddComponent<ScaleToFitParent>();
        previewFit.target = previewRect;
        Image previewImage = preview.AddComponent<Image>();
        previewImage.sprite = HomeThemeSprites.Panel;
        previewImage.type = Image.Type.Sliced;
        previewImage.color = ListColor;
        previewImage.raycastTarget = false; // drags belong to the proxies, not the backdrop

        parts.proxies = new DraggableControlProxy[ControlsLayout.Controls.Length];
        for (int i = 0; i < ControlsLayout.Controls.Length; i++)
            parts.proxies[i] = BuildLayoutProxy(previewRect, ControlsLayout.Controls[i]);

        parts.resetButton = CreateButton("LayoutResetButton", panel.transform, "Reset", 34f, NeutralColor);
        RectTransform resetRect = (RectTransform)parts.resetButton.transform;
        resetRect.anchorMin = resetRect.anchorMax = new Vector2(0.5f, 0f);
        resetRect.pivot = new Vector2(1f, 0f);
        resetRect.anchoredPosition = new Vector2(-12f, BottomRowInset);
        resetRect.sizeDelta = new Vector2(240f, BottomRowHeight);

        parts.backButton = CreateButton("LayoutBackButton", panel.transform, "Back", 34f, AccentColor);
        RectTransform backRect = (RectTransform)parts.backButton.transform;
        backRect.anchorMin = backRect.anchorMax = new Vector2(0.5f, 0f);
        backRect.pivot = new Vector2(0f, 0f);
        backRect.anchoredPosition = new Vector2(12f, BottomRowInset);
        backRect.sizeDelta = new Vector2(240f, BottomRowHeight);

        panel.SetActive(false); // ControlsLayoutScreen.Open shows it
        return parts;
    }

    // One draggable proxy tile (label + image) inside the preview, standing in for a field control.
    private static DraggableControlProxy BuildLayoutProxy(RectTransform previewRect, ControlsLayout.ControlInfo info)
    {
        GameObject go = CreateUIObject(info.name + "Proxy", previewRect);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = info.previewCenter;
        rect.sizeDelta = info.previewSize;

        Image image = go.AddComponent<Image>();
        image.sprite = HomeThemeSprites.Button;
        image.type = Image.Type.Sliced;
        image.color = AccentColor;
        // raycastTarget stays true: the image is the drag handle.

        TextMeshProUGUI label = CreateText("Label", go.transform, info.label, 30f);
        label.fontStyle = FontStyles.Bold;
        label.raycastTarget = false;
        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        DraggableControlProxy proxy = go.AddComponent<DraggableControlProxy>();
        proxy.controlName = info.name;
        proxy.dragArea = previewRect;
        proxy.basePosition = info.previewCenter;
        return proxy;
    }

    // --- Loading overlay ---

    // Full-screen overlay shown while the field scene loads: a dim, click-blocking backdrop with a
    // spinning arc and a "Loading…" label. The backdrop's raycastTarget swallows taps so Drive
    // can't be spammed. Starts inactive; HomeScreenController activates it on Drive.
    private static GameObject BuildLoadingOverlay(Transform canvas)
    {
        GameObject overlay = CreateUIObject("LoadingOverlay", canvas);
        RectTransform rect = (RectTransform)overlay.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        // Near-opaque dim backdrop; raycastTarget stays true so it blocks input to everything below.
        Image scrim = overlay.AddComponent<Image>();
        scrim.color = new Color(BackgroundTopColor.r, BackgroundTopColor.g, BackgroundTopColor.b, 0.92f);

        // Spinner: a quarter-arc cut from the generated ring, rotated by LoadingSpinner.
        GameObject spinner = CreateUIObject("Spinner", overlay.transform);
        RectTransform spinnerRect = (RectTransform)spinner.transform;
        spinnerRect.anchorMin = spinnerRect.anchorMax = new Vector2(0.5f, 0.5f);
        spinnerRect.pivot = new Vector2(0.5f, 0.5f);
        spinnerRect.anchoredPosition = new Vector2(0f, 50f);
        spinnerRect.sizeDelta = new Vector2(120f, 120f);
        Image spinnerImage = spinner.AddComponent<Image>();
        spinnerImage.sprite = HomeThemeSprites.Spinner;
        spinnerImage.color = Color.white; // white so the gradient below isn't tinted on the way out
        spinnerImage.type = Image.Type.Filled;
        spinnerImage.fillMethod = Image.FillMethod.Radial360;
        spinnerImage.fillAmount = 0.25f; // a spinning quarter-arc reads as "busy"
        spinnerImage.raycastTarget = false;

        // The app's gradient, across the arc. UiGradient works in the graphic's own local space, so
        // the colours turn WITH the spinner instead of staying pinned to the screen — the bright end
        // chases the dark one around the circle, which is the whole point of putting it here.
        UiGradient spinnerGradient = spinner.AddComponent<UiGradient>();
        spinnerGradient.topColor = PrimaryTopColor;
        spinnerGradient.bottomColor = PrimaryBottomColor;
        spinner.AddComponent<LoadingSpinner>();

        TextMeshProUGUI label = CreateText("LoadingLabel", overlay.transform, "Loading…", 44f);
        label.fontStyle = FontStyles.Bold;
        label.raycastTarget = false;
        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0.5f);
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.anchoredPosition = new Vector2(0f, -70f);
        labelRect.sizeDelta = new Vector2(600f, 80f);

        overlay.SetActive(false); // HomeScreenController shows it on Drive
        return overlay;
    }

    // Point the UI module at the package's DefaultInputActions asset (what the field scene's
    // EventSystem uses) and at its imported action sub-assets, so the scene serializes
    // asset-backed references. Written via SerializedObject — the C# property setters hook
    // action callbacks as a side effect, which we don't want in edit mode. If the asset can't
    // be found we leave everything null; the module assigns runtime defaults in OnEnable.
    private static void AssignDefaultUiActions(InputSystemUIInputModule uiModule)
    {
        string path = AssetDatabase.GUIDToAssetPath(DefaultInputActionsGuid);
        InputActionAsset actions = string.IsNullOrEmpty(path)
            ? null
            : AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
        if (actions == null)
        {
            Debug.LogWarning("Build Home Scene: DefaultInputActions asset not found; the UI input " +
                             "module will self-initialize at runtime.");
            return;
        }

        SerializedObject so = new SerializedObject(uiModule);
        so.FindProperty("m_ActionsAsset").objectReferenceValue = actions;
        foreach (UnityEngine.Object sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
        {
            InputActionReference reference = sub as InputActionReference;
            if (reference == null || reference.action == null) continue;
            string field = null;
            switch (reference.action.name)
            {
                case "Point": field = "m_PointAction"; break;
                case "Click": field = "m_LeftClickAction"; break;
                case "RightClick": field = "m_RightClickAction"; break;
                case "MiddleClick": field = "m_MiddleClickAction"; break;
                case "ScrollWheel": field = "m_ScrollWheelAction"; break;
                case "Navigate": field = "m_MoveAction"; break;
                case "Submit": field = "m_SubmitAction"; break;
                case "Cancel": field = "m_CancelAction"; break;
                case "TrackedDevicePosition": field = "m_TrackedDevicePositionAction"; break;
                case "TrackedDeviceOrientation": field = "m_TrackedDeviceOrientationAction"; break;
            }
            if (field != null) so.FindProperty(field).objectReferenceValue = reference;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // --- Step 5: field scene Home button ---

    private static string EnsureFieldHomeButton(Scene sampleScene, bool interactive, out bool added)
    {
        added = false;
        GameObject canvasGo = null;
        foreach (GameObject root in sampleScene.GetRootGameObjects())
        {
            if (root.name == "Canvas" && root.GetComponent<Canvas>() != null)
            {
                canvasGo = root;
                break;
            }
        }
        if (canvasGo == null)
        {
            if (interactive)
            {
                EditorUtility.DisplayDialog("Build Home Scene",
                    "SampleScene has no root 'Canvas' object; the Home button was not added.", "OK");
                return "skipped (no root Canvas found)";
            }
            Debug.LogError("Build Home Scene: SampleScene has no root 'Canvas' object.");
            throw new InvalidOperationException("SampleScene has no root 'Canvas' object.");
        }

        // Idempotency: never add a second button (and never touch the joystick objects). The
        // position IS re-asserted, and the comparison is made against what PlaceInTopRow actually
        // produces rather than against constants copied from it — see PositionHomeButton.
        Transform existingHome = canvasGo.transform.Find("HomeButton");
        if (existingHome != null)
        {
            RectTransform existingRect = (RectTransform)existingHome;
            Vector2 wasPivot = existingRect.pivot;
            Vector2 wasAnchoredPosition = existingRect.anchoredPosition;
            Vector2 wasSize = existingRect.sizeDelta;

            // Re-apply the CURRENT skin. This button is the one control the builders create once and
            // only ever find again, so a restyle reached every other button in the app and left this
            // one behind — a square grey Home sitting in a row of rounded gradient buttons.
            Image wasImage = existingHome.GetComponent<Image>();
            TextMeshProUGUI homeLabel = existingHome.GetComponentInChildren<TextMeshProUGUI>(true);
            bool themeChanged = wasImage == null || wasImage.sprite != HomeThemeSprites.Button ||
                                (homeLabel != null && homeLabel.font != HomeThemeFonts.Regular);

            Undo.RecordObject(existingRect, "Move Home Button");
            ApplyButtonTheme(existingHome.gameObject, AccentColor);
            if (homeLabel != null)
            {
                homeLabel.font = HomeThemeFonts.Regular;
                homeLabel.color = TextColor;
            }
            PositionHomeButton(existingRect);

            if (!themeChanged && existingRect.pivot == wasPivot &&
                existingRect.anchoredPosition == wasAnchoredPosition && existingRect.sizeDelta == wasSize)
                return "already present";

            EditorSceneManager.MarkSceneDirty(sampleScene);
            added = true; // reuse the flag so the caller saves the scene
            return themeChanged ? "re-themed to the current skin" : "re-positioned to the top center";
        }

        Button homeButton = CreateButton("HomeButton", canvasGo.transform, "Home", 32f, AccentColor);
        PositionHomeButton((RectTransform)homeButton.transform);

        SceneNavButton nav = homeButton.gameObject.AddComponent<SceneNavButton>();
        nav.sceneName = "HomeScene";
        UnityEventTools.AddPersistentListener(homeButton.onClick, nav.Load);

        Undo.RegisterCreatedObjectUndo(homeButton.gameObject, "Add Home Button");
        EditorSceneManager.MarkSceneDirty(sampleScene);
        added = true;
        return "added";
    }

    // Home is the MIDDLE slot of the field HUD's top row, with Reset to its left and the camera
    // toggle to its right.
    //
    // Delegated rather than duplicated, because duplicating it is exactly how it broke. This tool
    // used to carry its own copy of the geometry from back when Home and Reset were a pair pivoted
    // against the centre line; Build Drive Controls then grew a three-slot row and moved on, and
    // because step 5 here writes SampleScene unconditionally — even on runs where the HomeScene
    // rebuild is skipped — whichever tool ran last won. Running Build Home Screen after Build Drive
    // Controls silently shoved Home 92 px left, half of it underneath Reset, while logging that it
    // had "re-positioned to the top center". One authority now, so they cannot drift again.
    private static void PositionHomeButton(RectTransform rect) =>
        BuildDriveControls.PlaceInTopRow(rect, 0f);

    internal static RectTransform FindDescendantRect(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (RectTransform rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.name == name) return rect;
            }
        }
        return null;
    }

    // The same search over plain Transforms, for the scene's NON-UI objects.
    //
    // FindDescendantRect above walks RectTransforms, so it is blind to anything that isn't a UI
    // element — a camera, a light, the inactive holder the showcase robot is built under. Gating a
    // rebuild on one of those with the Rect version silently never matches, which reads exactly
    // like the check passing.
    internal static Transform FindDescendantTransform(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name) return child;
            }
        }
        return null;
    }

    // --- UI building helpers ---

    internal static GameObject CreateUIObject(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    // Centered panel: a rounded, opaque surface with a soft shadow around it.
    //
    // The shadow is baked into the sprite rather than being a second object. A child would draw ON
    // TOP of this object's own Image (uGUI draws a parent's graphic before its children), and a
    // sibling behind it would have to be kept in step with the panel's rect by hand — which the two
    // panels that stretch to the canvas height would immediately break. Baked in, it follows the
    // panel for free and costs no extra draw call. See HomeThemeSprites for how the tint survives.
    private static GameObject CreatePanel(string name, Transform parent, Vector2 size)
    {
        GameObject go = CreateUIObject(name, parent);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = size;
        Image image = go.AddComponent<Image>();
        image.sprite = HomeThemeSprites.Panel;
        image.type = Image.Type.Sliced;
        // White, with the gradient supplying the colour — UiGradient multiplies into this, so
        // tinting here as well would square the navy. Same arrangement as a primary button.
        image.color = Color.white;
        UiGradient gradient = go.AddComponent<UiGradient>();
        gradient.topColor = PanelTopColor;
        gradient.bottomColor = PanelColor;

        // Every panel arrives instead of blinking on. The controller shows and hides these with a
        // raw SetActive, which fires OnEnable — so the transition needs no cooperation from
        // HomeScreenController at all, and nothing in it has to be kept in step with this.
        go.AddComponent<CanvasGroup>();
        go.AddComponent<PanelTransition>();
        return go;
    }

    // Children get their width from the layout; heights come from each child's LayoutElement.
    private static VerticalLayoutGroup AddVerticalLayout(GameObject go, int padding, float spacing)
    {
        VerticalLayoutGroup layout = go.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(padding, padding, padding, padding);
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    // CreatePanel assumes a 1080-tall canvas. It isn't: the CanvasScaler matches width and height
    // equally, so a 20:9 phone (2400x1080) gives a canvas only ~966 reference units tall and the
    // 980-tall settings and controller panels ran off the bottom edge. Stretching vertically instead
    // makes the height follow the canvas — sizeDelta.y is a DELTA against a stretched anchor span,
    // so -80 means "canvas height minus a 40 margin at each end".
    // A panel that IS its container, stretched to all four of its edges and inset by a margin.
    //
    // margin 0 is for a panel whose margin is already owned by what it sits in — MenuColumn carries
    // it for the whole column, and a panel taking its own as well would inset twice.
    //
    // Use this rather than StretchPanelHeight for anything meant to be near-fullscreen. A width in
    // units cannot be right on two devices: the sub-screens were authored at 1700, which is 80% of
    // a 6.5" phone's 2118-unit canvas but 37 units WIDER than a 13" iPad's 1663, so on the iPad
    // their rounded left and right edges fell off the screen entirely.
    private static void FillParent(GameObject panel, float margin = 0f)
    {
        RectTransform rect = (RectTransform)panel.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(-margin * 2f, -margin * 2f);
    }

    private static void StretchPanelHeight(GameObject panel, float width, float verticalMargin = 40f)
    {
        RectTransform rect = (RectTransform)panel.transform;
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(width, -verticalMargin * 2f);
    }

    // Width reserved down the right edge of a panel for a permanent scrollbar, plus the gap between
    // it and the content.
    private const float ScrollbarWidth = 26f;
    private const float ScrollbarGap = 8f;

    // A panel whose rows scroll. Both the settings and submit panels have more rows than fit at a
    // fixed height, and without this the layout overflows CENTERED — the lower rows (the toggles, the
    // buttons) simply end up off-screen with no scrollbar to reveal them. The viewport clips; the
    // content grows to fit. Returns the object rows should be added to.
    // topInset/bottomInset reserve room for anything pinned outside the scroll (a tab row, a Back
    // button that must never scroll away); addScrollbar adds a visible one.
    private static GameObject CreateScrollingContent(GameObject panel, string namePrefix,
        float topInset, float bottomInset, bool addScrollbar,
        out ScrollRect scroll, out Scrollbar scrollbar)
    {
        GameObject viewport = CreateUIObject(namePrefix + "Viewport", panel.transform);
        RectTransform viewportRect = (RectTransform)viewport.transform;
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(12f, bottomInset);
        viewportRect.offsetMax = new Vector2(
            addScrollbar ? -(12f + ScrollbarWidth + ScrollbarGap) : -12f, -topInset);
        // Scroll-catcher. UGUI routes a wheel event by raycasting the pointer and bubbling UP from
        // whatever graphic it hit, so with no raycast target here the only scrollable spots are the
        // rows that happen to have their own graphic — the layout padding, the gaps between rows and
        // any space past the end of the list are all dead, which reads as "I have to put my cursor on
        // a button to scroll". The panel's own image can't stand in: it's this object's PARENT, and
        // bubbling never travels back down. Transparent, so the panel fill still shows through.
        Image viewportCatcher = viewport.AddComponent<Image>();
        viewportCatcher.color = Color.clear;
        viewportCatcher.raycastTarget = true;
        viewport.AddComponent<RectMask2D>();
        scroll = viewport.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28f;

        scrollbar = addScrollbar
            ? CreateVerticalScrollbar(panel, namePrefix + "Scrollbar", topInset, bottomInset)
            : null;
        if (scrollbar != null)
        {
            scroll.verticalScrollbar = scrollbar;
            // Permanent, NOT AutoHideAndExpandViewport. ScrollRect only rewrites its view rect's
            // anchors in the expanding mode — and here the view rect IS the ScrollRect's own
            // RectTransform (scroll.viewport is set to itself below), so that mode would have the
            // layout system drive the rect the layout controller lives on. A permanently visible
            // bar is also the point: the complaint was that nothing showed there was more below.
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        GameObject content = CreateScrollPage(viewport.transform, namePrefix + "Content");
        scroll.viewport = viewportRect;
        scroll.content = (RectTransform)content.transform;
        return content;
    }

    // A column of rows sized to its contents, laid out for a ScrollRect to scroll.
    //
    // The settings panel builds SEVERAL of these as siblings — one per tab — and the controller
    // activates one and repoints scroll.content at it. Siblings rather than nested children on
    // purpose: a ContentSizeFitter inside a parent VerticalLayoutGroup that also controls child
    // height is a genuine conflict (both write the same height), and the usual symptom is rows
    // that jitter or collapse to zero on the frame a tab is switched.
    private static GameObject CreateScrollPage(Transform viewport, string name)
    {
        GameObject page = CreateUIObject(name, viewport);
        RectTransform rect = (RectTransform)page.transform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = Vector2.zero; // width stretches to the viewport; height fits content
        AddVerticalLayout(page, 24, 20f);
        ContentSizeFitter fitter = page.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Cross-fade between tabs. Fade ONLY: this object is the ScrollRect's content, so its
        // anchoredPosition is the scroll position and animating it would drag the page back up
        // under the player's finger. fromScale 1 + fromOffset zero is what turns that off.
        page.AddComponent<CanvasGroup>();
        PanelTransition transition = page.AddComponent<PanelTransition>();
        transition.fromScale = 1f;
        transition.fromOffset = Vector2.zero;
        return page;
    }

    // One tab's page, built alongside the scroll view's first page.
    private static GameObject CreateTabPage(GameObject firstPage, string name)
        => CreateScrollPage(firstPage.transform.parent, name);

    // The project's first scrollbar. Lives as a SIBLING of the viewport, never a child: the
    // viewport carries the RectMask2D that clips the rows, and a child would be clipped by it.
    private static Scrollbar CreateVerticalScrollbar(GameObject panel, string name,
        float topInset, float bottomInset)
    {
        var resources = new DefaultControls.Resources
        {
            standard = HomeThemeSprites.Button,
            background = HomeThemeSprites.Panel,
        };
        GameObject go = DefaultControls.CreateScrollbar(resources);
        go.name = name;
        go.transform.SetParent(panel.transform, false);
        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = LayerMask.NameToLayer("UI");

        Scrollbar bar = go.GetComponent<Scrollbar>();
        bar.direction = Scrollbar.Direction.BottomToTop;

        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(ScrollbarWidth, -(topInset + bottomInset));
        rect.anchoredPosition = new Vector2(-12f, (bottomInset - topInset) * 0.5f);

        // DefaultControls builds a HORIZONTAL 160x20 bar, so its Sliding Area is inset 10 per side
        // on the axis we're about to make the long one. Left alone on a 26-wide vertical bar that
        // leaves a 6-unit track and a handle too thin to see, let alone grab.
        RectTransform slidingArea = (RectTransform)go.transform.Find("Sliding Area");
        if (slidingArea != null) slidingArea.sizeDelta = new Vector2(0f, -6f);

        Image track = go.GetComponent<Image>();
        if (track != null) track.color = ListColor;
        TintChildImage(go, "Sliding Area/Handle", BorderColor);
        return bar;
    }

    // A row of tab buttons pinned across the top of a panel. Returns them in the order given.
    private static Button[] CreateTabRow(GameObject panel, string name, float height,
        out TabIndicator indicator, params string[] labels)
    {
        GameObject row = CreateUIObject(name, panel.transform);
        RectTransform rect = (RectTransform)row.transform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(12f, 0f);
        rect.offsetMax = new Vector2(-12f, -12f);
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);

        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;

        var buttons = new Button[labels.Length];
        var tabRects = new RectTransform[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            buttons[i] = CreateButton("SettingsTab_" + labels[i].Replace(" ", string.Empty),
                row.transform, labels[i], 32f, NeutralColor);
            tabRects[i] = (RectTransform)buttons[i].transform;
        }

        // The sliding underline. Built AFTER the tabs so it draws over them, and given an ignored
        // LayoutElement so the HorizontalLayoutGroup above treats it as not being there — without
        // that the group would count it as a fourth tab and squeeze the real three.
        GameObject bar = CreateUIObject(name + "Indicator", row.transform);
        RectTransform barRect = (RectTransform)bar.transform;
        barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0f);
        barRect.pivot = new Vector2(0.5f, 0f);
        barRect.anchoredPosition = new Vector2(0f, 4f);
        barRect.sizeDelta = new Vector2(120f, 4f);
        LayoutElement barLayout = bar.AddComponent<LayoutElement>();
        barLayout.ignoreLayout = true;
        Image barImage = bar.AddComponent<Image>();
        barImage.sprite = HomeThemeSprites.Button;
        barImage.type = Image.Type.Sliced;
        barImage.color = SelectedColor;
        barImage.raycastTarget = false; // the tab under it takes the tap

        indicator = bar.AddComponent<TabIndicator>();
        SerializedObject indicatorSo = new SerializedObject(indicator);
        SerializedProperty tabsProp = indicatorSo.FindProperty("tabs");
        tabsProp.arraySize = tabRects.Length;
        for (int i = 0; i < tabRects.Length; i++)
            tabsProp.GetArrayElementAtIndex(i).objectReferenceValue = tabRects[i];
        indicatorSo.ApplyModifiedPropertiesWithoutUndo();
        return buttons;
    }

    // Label on the left, control on the right, in one row. Two stacked full-width rows per slider
    // was costing 112 units of vertical space each for no gain — and vertical space is exactly what
    // the settings screen was short of. The child objects keep their original names because
    // HomeScreenController finds them by serialized reference.
    private static GameObject CreateSliderRow(Transform parent, string rowName,
        string labelName, string labelText, string sliderName, float min, float max, float value,
        out TextMeshProUGUI label, out Slider slider)
    {
        GameObject row = CreateUIObject(rowName, parent);
        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = 16f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;

        label = CreateText(labelName, row.transform, labelText, 32f);
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        LayoutElement labelElement = label.gameObject.AddComponent<LayoutElement>();
        labelElement.preferredWidth = 340f;
        labelElement.flexibleWidth = 0f;

        slider = CreateSlider(sliderName, row.transform, min, max, value);
        LayoutElement sliderElement = slider.gameObject.AddComponent<LayoutElement>();
        sliderElement.flexibleWidth = 1f;

        SetLayoutHeight(row, 60f);
        return row;
    }

    // One half of the split model picker: a titled, dark-filled column the controller clones model
    // buttons into. Returns the LIST, not the column — the title is a sibling above it, so a title
    // can never be mistaken for a row or destroyed by a list rebuild.
    //
    // The list SCROLLS inside the column rather than growing without bound. With the picker at its
    // natural height, a player who has unlocked fifteen robots pushes Match, Field and every other
    // Robot-page row down by a screen and a half — the picker stops being one section of a page and
    // becomes the page. `viewportSize` is the window the controller sizes: a fixed three rows for
    // both halves, scrolling past that (see HomeScreenController.ResizeModelColumns).
    private static Transform CreateModelColumn(Transform parent, string columnName, string listName,
        string title, out LayoutElement viewportSize)
    {
        GameObject column = CreateUIObject(columnName, parent);
        Image fill = column.AddComponent<Image>();
        fill.sprite = HomeThemeSprites.Panel;
        fill.type = Image.Type.Sliced;
        fill.color = ListColor;
        VerticalLayoutGroup columnLayout = AddVerticalLayout(column, 12, 10f);
        columnLayout.childAlignment = TextAnchor.UpperCenter;

        TextMeshProUGUI header = CreateText(columnName + "Title", column.transform,
            title.ToUpperInvariant(), 24f);
        header.fontStyle = FontStyles.Bold;
        header.color = TextMutedColor;
        SetLayoutHeight(header.gameObject, 32f);

        GameObject viewport = CreateUIObject(columnName + "Viewport", column.transform);
        // Same scroll-catcher as CreateScrollingContent, for the same reason: a wheel or a drag is
        // routed by raycasting the pointer and bubbling UP, so without a transparent target here the
        // gaps between rows and the space past the last row are dead. The column's own fill can't
        // stand in — it is this object's PARENT, and bubbling never travels back down.
        Image catcher = viewport.AddComponent<Image>();
        catcher.color = Color.clear;
        catcher.raycastTarget = true;
        viewport.AddComponent<RectMask2D>();
        viewportSize = viewport.AddComponent<LayoutElement>();
        viewportSize.flexibleHeight = 0f;
        // Authored with a real height, not left at the -1 that means "ignored". The controller
        // overwrites this on every list build — but a LayoutElement reporting -1 makes the column
        // collapse to nothing, so if that call is ever missed the picker doesn't shrink, it VANISHES.
        viewportSize.preferredHeight = 200f;

        GameObject list = CreateUIObject(listName, column.transform);
        list.transform.SetParent(viewport.transform, false);
        RectTransform listRect = (RectTransform)list.transform;
        listRect.anchorMin = new Vector2(0f, 1f);
        listRect.anchorMax = new Vector2(1f, 1f);
        listRect.pivot = new Vector2(0.5f, 1f);
        listRect.anchoredPosition = Vector2.zero;
        listRect.sizeDelta = Vector2.zero; // width from the viewport, height from the fitter
        VerticalLayoutGroup listLayout = AddVerticalLayout(list, 0, 12f);
        listLayout.childAlignment = TextAnchor.UpperCenter;
        ContentSizeFitter listFitter = list.AddComponent<ContentSizeFitter>();
        listFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // NestedScrollRect, not ScrollRect: this sits inside the settings page's own vertical scroll,
        // and a plain one would swallow every drag over the picker — including on the three-robot
        // device where there is nothing here to scroll at all.
        NestedScrollRect scroll = viewport.AddComponent<NestedScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28f;
        scroll.viewport = (RectTransform)viewport.transform;
        scroll.content = listRect;

        return list.transform;
    }

    // A quiet section heading with a rule under it, so related settings read as a group instead of
    // one undifferentiated column.
    private static void CreateSectionHeader(Transform parent, string name, string text)
    {
        TextMeshProUGUI label = CreateText(name, parent, text.ToUpperInvariant(), 28f);
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.color = TextMutedColor;
        SetLayoutHeight(label.gameObject, 40f);

        GameObject rule = CreateUIObject(name + "Rule", parent);
        Image ruleImage = rule.AddComponent<Image>();
        ruleImage.color = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.55f);
        SetLayoutHeight(rule, 2f);
    }

    private static void SetLayoutHeight(GameObject go, float height)
    {
        LayoutElement element = go.AddComponent<LayoutElement>();
        element.minHeight = height;
        element.preferredHeight = height;
        element.flexibleHeight = 0f;
    }

    internal static TextMeshProUGUI CreateText(string name, Transform parent, string text, float fontSize)
    {
        GameObject go = CreateUIObject(name, parent);
        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        // Assigned explicitly rather than by changing TMP_Settings.defaultFontAsset: this covers
        // every label BOTH scene builders make (Build Drive Controls calls through here too),
        // without reaching into a global setting that also governs objects these tools don't own.
        //
        // Only the regular face is set. Labels that want bold keep saying fontStyle = Bold, and
        // HomeThemeFonts.LinkBoldWeight has made that resolve to the real SemiBold face.
        tmp.font = HomeThemeFonts.Regular;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = TextColor;
        tmp.alignment = TextAlignmentOptions.Center;
        return tmp;
    }

    internal static Button CreateButton(string name, Transform parent, string label, float fontSize, Color color)
    {
        GameObject go = CreateUIObject(name, parent);
        Button button = go.AddComponent<Button>();
        ApplyButtonTheme(go, color);
        button.targetGraphic = go.GetComponent<Image>();

        TextMeshProUGUI text = CreateText("Label", go.transform, label, fontSize);
        text.raycastTarget = false; // clicks belong to the button, not the label
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        return button;
    }

    // Everything about how a button LOOKS: sprite, fill, and press behaviour.
    //
    // Split out of CreateButton because one button in the app is created once and only ever found
    // afterwards — the field scene's Home button, which EnsureFieldHomeButton returns early for when
    // it already exists. With the theme inlined in CreateButton there was no way to re-apply it, so
    // Home stayed the single square grey control in a field of rounded ones, which is exactly how it
    // was spotted.
    //
    // Find-or-add throughout, so re-running over a button that already has these components
    // re-themes it instead of stacking a second copy.
    internal static void ApplyButtonTheme(GameObject go, Color color)
    {
        Image image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
        image.sprite = HomeThemeSprites.Button;
        image.type = Image.Type.Sliced;

        // A button asking for the accent colour IS a primary action — that has been this file's
        // convention since it was written, and every call site already follows it. So the gradient
        // keys off the colour rather than off a new parameter threaded through a dozen callers.
        //
        // The Image's own colour stays WHITE for a gradient button and the gradient supplies the
        // colour, because UiGradient multiplies into it. Tinting both would square the blue.
        bool primary = color == AccentColor;
        UiGradient gradient = go.GetComponent<UiGradient>();
        if (primary)
        {
            image.color = Color.white;
            if (gradient == null) gradient = go.AddComponent<UiGradient>();
            gradient.topColor = PrimaryTopColor;
            gradient.bottomColor = PrimaryBottomColor;
        }
        else
        {
            image.color = color;
            // A button that USED to be primary would otherwise keep multiplying the old gradient
            // over its new flat colour.
            if (gradient != null) UnityEngine.Object.DestroyImmediate(gradient);
        }
        AddPressFeedback(go, primary);
    }

    // Gives a button the same press feel the field-scene controls have had all along: it sinks in,
    // shrinks slightly, and shifts colour while held. The home screen had NONE of this — every one
    // of its buttons relied on the stock ColorTint transition, a ~0.78 multiply that is close to
    // invisible on the accent blue, so tapping Drive gave no acknowledgement at all.
    //
    // The Button's own transition goes to None, because PressFeedback drives the colour and the two
    // would otherwise fight over the same Image. That has a consequence worth knowing: a
    // non-interactable button no longer greys itself out for free. Anything that sets
    // `interactable` must paint its own disabled state, and must do it through
    // PressFeedback.BaseColor — see SubmitRobotScreen, and MatchLoadButton for the pattern.
    private static void AddPressFeedback(GameObject go, bool primary)
    {
        Button button = go.GetComponent<Button>();
        if (button != null) button.transition = Selectable.Transition.None;
        PressFeedback feedback = go.GetComponent<PressFeedback>() ?? go.AddComponent<PressFeedback>();

        // PressFeedback MULTIPLIES its pressed colour into the graphic, so which way a press reads
        // depends on what the graphic's base colour is:
        //   - a neutral button is a dark flat fill, so a near-white pressed colour brightens it;
        //   - a primary button is WHITE with the gradient supplying the colour (multiply can't go
        //     above white), so the same value would do nothing. It gets a darkening instead, which
        //     is the conventional direction for a press anyway.
        feedback.pressedColor = primary ? new Color(0.74f, 0.78f, 0.86f, 1f)
                                        : new Color(0.85f, 0.90f, 1.00f, 1f);
    }

    // Horizontal slider built from Unity's DefaultControls (same structure as GameObject > UI >
    // Slider) so the Background/Fill/Handle wiring is correct, then themed to match the panel.
    private static Slider CreateSlider(string name, Transform parent, float min, float max, float value)
    {
        var resources = new DefaultControls.Resources
        {
            standard = HomeThemeSprites.Button,
            background = HomeThemeSprites.Panel,
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
        };

        GameObject go = DefaultControls.CreateSlider(resources);
        go.name = name;
        go.transform.SetParent(parent, false);
        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = LayerMask.NameToLayer("UI");

        // Theme the parts to match the dark panel: dark track, accent fill, light knob.
        TintChildImage(go, "Background", ListColor);
        TintChildImage(go, "Fill Area/Fill", AccentColor);
        TintChildImage(go, "Handle Slide Area/Handle", TextColor);

        Slider slider = go.GetComponent<Slider>();
        slider.wholeNumbers = false;
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;
        return slider;
    }

    // Checkbox row built from Unity's DefaultControls toggle (correct Background/Checkmark/Toggle
    // wiring), then restyled to match the panel: a 44px box on the left, accent checkmark, and the
    // legacy Text label replaced with a left-aligned TMP label like the rest of the settings rows.
    private static Toggle CreateToggle(string name, Transform parent, string label, bool isOn)
    {
        var resources = new DefaultControls.Resources
        {
            standard = HomeThemeSprites.Button,
            background = HomeThemeSprites.Panel,
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
        };

        GameObject go = DefaultControls.CreateToggle(resources);
        go.name = name;
        go.transform.SetParent(parent, false);
        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = LayerMask.NameToLayer("UI");

        // The checkbox: middle-left of the row, a LIGHT box so it's clearly visible against the
        // dark panel (a panel-dark box on the panel was effectively invisible), accent checkmark.
        RectTransform background = (RectTransform)go.transform.Find("Background");
        background.anchorMin = background.anchorMax = new Vector2(0f, 0.5f);
        background.pivot = new Vector2(0f, 0.5f);
        background.anchoredPosition = new Vector2(8f, 0f);
        background.sizeDelta = new Vector2(48f, 48f);
        background.GetComponent<Image>().color = new Color32(0xD8, 0xE3, 0xF7, 0xFF);
        RectTransform checkmark = (RectTransform)background.Find("Checkmark");
        checkmark.anchorMin = Vector2.zero;
        checkmark.anchorMax = Vector2.one;
        checkmark.anchoredPosition = Vector2.zero;
        checkmark.sizeDelta = Vector2.zero;
        checkmark.GetComponent<Image>().color = AccentColor;

        // Replace the legacy Text label with the panel's TMP style, left-aligned beside the box.
        Transform legacyLabel = go.transform.Find("Label");
        if (legacyLabel != null) UnityEngine.Object.DestroyImmediate(legacyLabel.gameObject);
        TextMeshProUGUI text = CreateText("Label", go.transform, label, 40f);
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.raycastTarget = false; // taps belong to the toggle, not the label
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(68f, 0f);
        textRect.offsetMax = Vector2.zero;

        Toggle toggle = go.GetComponent<Toggle>();
        toggle.isOn = isOn;
        return toggle;
    }

    // Text box built from TMP's own default control (correct Text Area / Text / Placeholder wiring,
    // which is fiddly to hand-roll), then themed to match the panel. Same approach as the slider and
    // toggle helpers above.
    private static TMP_InputField CreateInputField(string name, Transform parent, string placeholder,
        float fontSize)
    {
        var resources = new TMP_DefaultControls.Resources
        {
            standard = HomeThemeSprites.Button,
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
        };

        GameObject go = TMP_DefaultControls.CreateInputField(resources);
        go.name = name;
        go.transform.SetParent(parent, false);
        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = LayerMask.NameToLayer("UI");

        Image background = go.GetComponent<Image>();
        if (background != null) background.color = ListColor;

        TMP_InputField field = go.GetComponent<TMP_InputField>();
        if (field.textComponent != null)
        {
            field.textComponent.color = TextColor;
            field.textComponent.fontSize = fontSize;
            field.textComponent.alignment = TextAlignmentOptions.MidlineLeft;
        }
        if (field.placeholder is TextMeshProUGUI placeholderText)
        {
            placeholderText.text = placeholder;
            placeholderText.color = new Color(TextColor.r, TextColor.g, TextColor.b, 0.45f);
            placeholderText.fontSize = fontSize;
            placeholderText.alignment = TextAlignmentOptions.MidlineLeft;
        }
        field.pointSize = fontSize;
        // Owner codes are short tags like "654V-8213"; uppercase keeps them readable and matches how
        // RobotOwnerSettings normalizes them before comparing.
        field.characterValidation = TMP_InputField.CharacterValidation.None;
        field.lineType = TMP_InputField.LineType.SingleLine;
        return field;
    }

    private static void TintChildImage(GameObject root, string path, Color color)
    {
        Transform child = root.transform.Find(path);
        Image image = child != null ? child.GetComponent<Image>() : null;
        if (image != null) image.color = color;
    }
}
