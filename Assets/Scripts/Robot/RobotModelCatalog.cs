using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Catalog of the robot models the player can choose from on the home screen.
//
// Each entry pairs a stable string id (safe to persist across renames of the display text)
// with the human-readable name shown in the UI. The current selection is stored in
// PlayerPrefs — not on the asset — so picking a model never dirties the project and the
// choice survives app restarts per device.
//
// Usage: create via Assets > Create > VEX > Robot Model Catalog (the Build Home Scene tool
// creates Assets/Settings/RobotModelCatalog.asset automatically), then assign it to the
// HomeScreenController in the home scene.
[CreateAssetMenu(menuName = "RoboSim/Robot Model Catalog", fileName = "RobotModelCatalog")]
public class RobotModelCatalog : ScriptableObject
{
    // Mirror of a RobotMechanisms.Mechanism (id/displayName/type only, no component refs) so
    // the home-screen controller-config UI can list a robot's mechanisms without loading the
    // field scene. Written by the URDF post-processor alongside the scene-side registry.
    [Serializable]
    public class MechanismInfo
    {
        public string id;
        public string displayName;
        public string type; // RobotMechanisms.TypeMotor or RobotMechanisms.TypePneumatic
    }

    // The kind of lift the home stage names on a robot's chip. Worked out from the rig — see Highlights.
    //
    // Serialized as a number, here and in the published index a phone reads, so members may be APPENDED
    // but never renumbered or reordered.
    public enum LiftKind
    {
        None = 0,
        Cascade = 1,
        DR4B = 2,
    }

    // Whether one of the home stage's labels shows: as the robot's rig says, or forced either way for a
    // robot the rule reads wrong. Serialized as a number, like LiftKind.
    public enum LabelSetting
    {
        FromRobot = 0,
        Always = 1,
        Never = 2,
    }

    // What the chips under a robot's name on the home stage say: the watts its drivetrain and its lift use,
    // and a label for each mechanism worth naming — the lift, a Floating Intake, a claw, a clamp. Nothing
    // else a robot has (toggles, an arm, a doinker) gets a chip; Configure Controller lists all of them.
    //
    // Three kinds of field, and which one writes what is the point:
    //   - the WATTS are typed, in Tools > RoboSim > Robot > Model Catalog. The CAD names every motor by its
    //     wattage, so a robot's total is knowable, but the motors are bolted to the chassis and geared out
    //     to where they work, so which mechanism uses which is not. Model Catalog and Validate Home Stage
    //     check the typed numbers against the CAD's total.
    //   - the `rig` fields are WORKED OUT from the robot, by Build Home Screen and Build Robot Bundles
    //     (RobotHighlightDetection). Never typed: a typed copy of what the rig already says goes stale the
    //     first time the robot is rebuilt.
    //   - the LABEL settings override the rig, for a robot the rule gets wrong.
    //
    // Plain data with no references, so a downloaded robot's copy travels in the published index as is.
    [Serializable]
    public class Highlights
    {
        [Tooltip("Watts of V5 motor on the drivetrain — 11 for each 11 W motor, 5.5 for each 5.5 W one. " +
                 "0 leaves the drivetrain chip off.")]
        public float driveWatts;
        [Tooltip("Watts of V5 motor on the lift, shown on the lift's chip. 0 shows the lift's name alone.")]
        public float liftWatts;

        [Tooltip("Worked out from the robot by Build Home Screen: the lift Build Cascade Lift or Build DR4B " +
                 "Lift made. Not typed.")]
        public LiftKind rigLift;
        [Tooltip("Worked out from the robot by Build Home Screen: an intake whose mouth sits at the floor. " +
                 "Not typed.")]
        public bool rigFloatingIntake;
        [Tooltip("Worked out from the robot by Build Home Screen: a Build Claw claw with jaws. Not typed.")]
        public bool rigClaw;

        public LabelSetting floatingIntakeLabel;
        public LabelSetting clawLabel;
        // No rig field to go with this one: nothing rigs a clamp yet. When the clamp mechanism exists, its
        // builder will mark the robot the way Build Claw does, and a rigClamp joins the three above.
        public LabelSetting clampLabel;

        public bool ShowsFloatingIntake => Shows(floatingIntakeLabel, rigFloatingIntake);
        public bool ShowsClaw => Shows(clawLabel, rigClaw);
        public bool ShowsClamp => Shows(clampLabel, false);

        private static bool Shows(LabelSetting setting, bool fromRobot) =>
            setting == LabelSetting.Always || (setting == LabelSetting.FromRobot && fromRobot);

        // One chip: its label, and the watts beside it — 0 for a chip that has none.
        public struct Chip
        {
            public string label;
            public float watts;

            // What the chip says, the watts first: "44 W Drive", or just "Claw". The stage colours the watts, so
            // it passes the tags to wrap them in; the Model Catalog's preview passes none.
            public string Text(string openTag = "", string closeTag = "") =>
                watts > 0f ? $"{openTag}{FormatWatts(watts)}{closeTag} {label}" : label;
        }

        // The chips in the order the stage shows them: the drivetrain, the lift, then the labels. Five at the
        // most, which is how many slots Build Home Screen makes.
        public List<Chip> Chips()
        {
            var chips = new List<Chip>();
            if (driveWatts > 0f) chips.Add(new Chip { label = "Drive", watts = driveWatts });

            // A lift the rig can't name — one no lift builder made — still gets its typed watts shown, under
            // the plain word.
            string lift = LiftName(rigLift) ?? (liftWatts > 0f ? "Lift" : null);
            if (lift != null) chips.Add(new Chip { label = lift, watts = Mathf.Max(0f, liftWatts) });

            if (ShowsFloatingIntake) chips.Add(new Chip { label = "Floating Intake" });
            if (ShowsClaw) chips.Add(new Chip { label = "Claw" });
            if (ShowsClamp) chips.Add(new Chip { label = "Clamp" });
            return chips;
        }

        public static string LiftName(LiftKind kind) => kind switch
        {
            LiftKind.Cascade => "Cascade",
            LiftKind.DR4B => "DR4B",
            _ => null,
        };

        // "44 W", "5.5 W". Invariant, so a phone set to a comma-decimal language doesn't write "5,5 W" into
        // an English screen.
        public static string FormatWatts(float watts) =>
            watts.ToString("0.#", CultureInfo.InvariantCulture) + " W";
    }

    // Who can see a model. Public is 0 on purpose: entries serialized before this field existed
    // deserialize to 0, so every robot already in the catalog stays visible with no migration.
    public enum Visibility
    {
        Public = 0,
        Private = 1,
    }

    // The second way an entry can name a robot: an AssetBundle to load it out of, instead of a
    // direct reference to a prefab that had to exist when the app was built.
    //
    // This is what makes a robot deliverable at all. With only a direct reference, every accepted
    // upload is baked into the binary forever for every player — which is what makes a private robot
    // only *hidden* rather than absent, and what puts a hard ceiling on how many robots the game can
    // ever have (measured: ~226 MB of mesh data each, before decimation).
    //
    // WHY sourceGuid IS A STRING AND NOT A GameObject. A serialized reference to an asset is what
    // PULLS THAT ASSET INTO THE BUILD — that is the entire mechanism behind the problem above. So
    // the build tool remembers which prefab to bundle by its GUID, in text, and resolves it through
    // AssetDatabase when it runs. A convenient `public GameObject sourcePrefab;` here would quietly
    // undo the whole point of this class while looking tidier.
    //
    // THE VERSION FIELDS ARE NOT BOOKKEEPING. A bundle serializes the SCRIPTS on the robot prefab,
    // not just its meshes — so changing a serialized field on RobotMotorController silently
    // invalidates every bundle built before the change. `scriptVersion` records which layout this
    // bundle was built against, the app refuses anything that isn't its own, and the refusal is a
    // message rather than a robot that spawns with its tuning quietly zeroed. See RobotBundleFormat.
    [Serializable]
    public class BundleRef
    {
        [Tooltip("Bundle file name, without extension. Empty means this robot isn't delivered as a " +
                 "bundle.")]
        public string id;

        [Tooltip("Which build of that bundle. Part of the path, so an old app keeps asking for the " +
                 "build it was shipped against and never picks up an incompatible newer one.")]
        public string version;

        [Tooltip("The RobotBundleFormat.Version this bundle was built against. A mismatch means the " +
                 "robot scripts changed shape since, and the bundle cannot be trusted to load.")]
        public int scriptVersion;

        [Tooltip("Set when the bundle is served from Storage rather than shipped in StreamingAssets.")]
        public bool remote;

        [Tooltip("Asset GUID of the prefab this bundle is built from. Editor-only bookkeeping.")]
        public string sourceGuid;

        public bool IsSet => !string.IsNullOrWhiteSpace(id);
    }

    [Serializable]
    public class Entry
    {
        public string id;           // stable identifier persisted in PlayerPrefs
        public string displayName;  // what the home screen shows
        // The robot prefab RobotSpawner instantiates into the field scene when this model is
        // selected. Built by the Build Robot Prefabs & Spawner tool; null entries are skipped
        // by the spawner (it falls back to the first entry that has one).
        //
        // A direct reference means the robot is COMPILED INTO THE APP: its geometry ships to every
        // device that installs the build, whether or not that player can see it in the picker. That
        // is the right trade for the handful of robots shipped with the game, and the wrong one for
        // every robot a player sends in — see `bundle` below, which is the other way to fill this in.
        public GameObject prefab;

        // The home screen's copy of this robot: the same meshes and materials with every script,
        // collider and joint stripped off, the fasteners culled and the hierarchy flattened — baked by
        // Tools > RoboSim > Robot > Advanced > Build Showcase Prefabs, which Build Home Screen also runs.
        // RobotShowcase says why the stage can't simply show `prefab`.
        //
        // A direct reference, so it is compiled into the app just as `prefab` is — the trade BundleRef
        // below exists to avoid. It is the right one here: it is only ever set for a robot whose `prefab`
        // is already compiled in, and it points at geometry that robot already carries. A robot delivered
        // as a bundle leaves it null, and the stage shows its name over the app's chassis mark instead.
        [Tooltip("The stripped copy the home screen's stage turns. Written by Build Showcase Prefabs — " +
                 "don't assign it by hand.")]
        public GameObject showcasePrefab;

        // Which version of `prefab` (and of the bake itself) the showcase was made from. Build Home Screen
        // re-bakes whenever this stops matching, so editing a robot can't leave the menu showing the old one.
        [HideInInspector] public string showcaseSource;

        [Tooltip("Where to fetch this robot from when it isn't compiled into the app. Leave empty " +
                 "for a built-in robot; the direct prefab reference above wins if both are set.")]
        public BundleRef bundle = new BundleRef();

        public List<MechanismInfo> mechanisms = new List<MechanismInfo>();

        [Tooltip("What the chips under this robot's name on the home stage say. Type the watts in Tools > " +
                 "RoboSim > Robot > Model Catalog; the rest is worked out from the robot by Build Home Screen.")]
        public Highlights highlights = new Highlights();

        [Tooltip("Public models are listed for everyone. Private models are hidden until someone " +
                 "enters this entry's owner code in Settings.")]
        public Visibility visibility = Visibility.Public;
        [Tooltip("The code(s) that reveal this model when its owner types one in Settings > Team Code. " +
                 "Only meaningful on a Private entry. Separate several with commas — a robot can carry " +
                 "its own one-off code AND its team's code, and holding either one reveals it. " +
                 "Case- and space-insensitive.")]
        public string ownerCode;
        [Tooltip("Optional label for whose robot this is (e.g. a team number). Shown in the picker.")]
        public string ownerLabel;

        [Tooltip("The button layout a fresh install starts with for this robot. Bind the controller " +
                 "how you want it in Configure Controller, then publish it from Tools > RoboSim > " +
                 "Robot > Model Catalog > Make Current Bindings the Default. Empty means the robot " +
                 "ships unbound, which is what every robot did before this existed — the auto-assign " +
                 "that runs when a mechanism is built writes into the EDITOR's PlayerPrefs, and those " +
                 "never reach a build.")]
        public ButtonMap defaultButtonMap = new ButtonMap();

        public bool HasDefaultButtonMap =>
            defaultButtonMap != null && defaultButtonMap.assignments != null
            && defaultButtonMap.assignments.Count > 0;

        // Every code that reveals this entry. Giving several entries one code in common is how a team
        // shares its robots: "654V-TEAM" on five entries means one code opens all five, with no
        // accounts and nothing to verify.
        public List<string> OwnerCodes => RobotOwnerSettings.SplitCodes(ownerCode);

        // A private entry with no code can never be revealed, so it would silently disappear from the
        // picker. That's a misconfiguration rather than a policy, and VisibleModels warns about it.
        public bool IsVisibleOnThisDevice =>
            visibility == Visibility.Public || RobotOwnerSettings.HasAnyCode(ownerCode);
    }

    public List<Entry> models = new List<Entry>();

    // Robots learned about at runtime from the published index (RobotCatalogSync) — ones this build
    // was never shipped with at all.
    //
    // Deliberately NOT part of `models`, and deliberately [NonSerialized]. Adding them to the
    // serialized list would work at runtime and then quietly write itself into the catalog ASSET the
    // next time the editor saved, so a Play-mode session that happened to be online would commit
    // whatever was published that day into the project. This list is rebuilt from the network on
    // every launch, which is also what makes an unpublished robot disappear again.
    [System.NonSerialized] private List<Entry> synced;

    public IEnumerable<Entry> AllModels
    {
        get
        {
            if (models != null)
            {
                foreach (Entry entry in models) yield return entry;
            }
            if (synced == null) yield break;
            foreach (Entry entry in synced) yield return entry;
        }
    }

    // Adds a robot discovered at runtime. Ignores one whose id is already known, so a robot that is
    // both shipped and published stays the shipped copy — the local one is guaranteed loadable and
    // needs no network.
    public bool AddSynced(Entry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.id)) return false;
        foreach (Entry existing in AllModels)
        {
            if (existing != null && existing.id == entry.id) return false;
        }

        synced ??= new List<Entry>();
        synced.Add(entry);
        return true;
    }

    public void ClearSynced() => synced?.Clear();

    // PlayerPrefs key for the selected model id (public so loaders can read it directly).
    public const string SelectedModelPrefKey = "SelectedRobotModelId";

    // The models this device is allowed to see: everything public, plus any private model whose
    // owner code has been entered in Settings.
    //
    // EVERY reader must go through this rather than `models` directly. A private robot is only
    // actually hidden if the picker, the spawner, the controller-config screen and the selection
    // fallbacks all agree — otherwise a stale PlayerPrefs selection or a "first entry" fallback
    // quietly puts it back on the field.
    public IEnumerable<Entry> VisibleModels
    {
        get
        {
            foreach (Entry entry in AllModels)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id)) continue;
                if (entry.IsVisibleOnThisDevice) { yield return entry; continue; }

                if (entry.visibility == Visibility.Private && entry.OwnerCodes.Count == 0)
                {
                    Debug.LogWarning($"RobotModelCatalog: '{entry.displayName}' is Private but has no " +
                                     "owner code, so nothing can ever reveal it. Give it a code, or " +
                                     "set it back to Public.", this);
                }
            }
        }
    }

    // The currently selected model id. Reads fall back to the first VISIBLE catalog entry when the
    // pref is unset, names an id no longer in the catalog (e.g. after an entry is removed), or names
    // a private model this device hasn't unlocked — so callers always get a usable id, and never one
    // the player isn't allowed to see.
    public string SelectedModelId
    {
        get
        {
            string saved = PlayerPrefs.GetString(SelectedModelPrefKey, string.Empty);
            string firstVisible = null;
            foreach (Entry entry in VisibleModels)
            {
                if (!string.IsNullOrEmpty(saved) && entry.id == saved) return saved;
                if (firstVisible == null) firstVisible = entry.id;
            }
            return firstVisible;
        }
        set
        {
            PlayerPrefs.SetString(SelectedModelPrefKey, value);
            PlayerPrefs.Save(); // flush immediately so a crash/force-quit doesn't lose the choice
            NotifySelectionMayHaveChanged();
        }
    }

    // Raised when the robot the player would drive CHANGES — not merely when the selection is written.
    //
    // The selection can move without the setter above ever running. The getter FALLS BACK to the first
    // visible robot whenever the saved id names nothing this device can see, so forgetting a code
    // quietly moves the selection off a private robot, and a sync that adds a robot can change which one
    // is first. A listener on the setter alone would miss both, and the home stage would go on showing a
    // robot that is no longer the one Drive loads.
    //
    // So whatever changes VisibleModels calls NotifySelectionMayHaveChanged, which re-reads the EFFECTIVE
    // selection and raises this only when it differs from what was last announced. The de-duplication is
    // what makes calling it too often free: the rule is "call it wherever the visible list changes", not
    // a list of call sites someone has to keep complete.
    //
    // A listener should also read the selection when it subscribes. This can fire while it is disabled,
    // and — because the catalog is an asset — what was last announced outlives a Play session in the editor.
    public event Action<Entry> SelectionChanged;
    [NonSerialized] private string announcedSelectionId;

    public void NotifySelectionMayHaveChanged()
    {
        string selected = SelectedModelId;
        if (selected == announcedSelectionId) return;
        announcedSelectionId = selected;
        SelectionChanged?.Invoke(SelectedModel);
    }

    // The Entry for the current selection (mirrors SelectedModelId's fallback), or null if there is
    // nothing visible to select. RobotSpawner reads this to know which prefab to place on the field.
    public Entry SelectedModel
    {
        get
        {
            string id = SelectedModelId;
            if (id == null) return null;
            foreach (Entry entry in VisibleModels)
            {
                if (entry.id == id) return entry;
            }
            return null;
        }
    }

    // First visible entry that actually has a prefab — the spawner's last resort when the selection
    // has no prefab built yet. Visible-only, so it can't surface a private robot.
    //
    // Deliberately NOT bundle-aware: this is the fallback used when the thing the player asked for
    // could not be put on the field, and a fallback that has to go to the network to find out
    // whether it works is not a fallback. FirstVisibleSpawnable is the one to ask when you want
    // "anything at all"; this is the one to ask when you want "anything, right now, guaranteed".
    public Entry FirstVisibleWithPrefab()
    {
        foreach (Entry entry in VisibleModels)
        {
            if (entry.prefab != null) return entry;
        }
        return null;
    }

    // First visible entry that names a robot at all, by either route. What the picker should offer
    // and what the spawner should try before giving up.
    public Entry FirstVisibleSpawnable()
    {
        foreach (Entry entry in VisibleModels)
        {
            if (entry.prefab != null || (entry.bundle != null && entry.bundle.IsSet)) return entry;
        }
        return null;
    }
}
