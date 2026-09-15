using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The home screen's "Configure Controller" sub-screen: a tappable controller diagram where the
// player assigns each button to one or more of the selected robot's mechanism functions — motor
// forward, motor reverse, or pneumatic toggle. The assignment popup is a multi-toggle: tapping a
// function adds/removes it (a ✓ marks the ones on this button) and stays open, so one button can
// drive several mechanisms — e.g. a mirrored DR4B's two sides, one reversed. Assignments persist
// per robot via ControllerMapSettings (PlayerPrefs JSON keyed by the catalog id); ButtonRouter
// reads the same map in the field scene and sums every assignment per motor.
//
// The "Control Style" button reuses the same popup to switch a mechanism between one- and
// two-button control (a motor between hold fwd/rev and a latching toggle; a piston between one
// toggle and separate extend/retract buttons). Style lives in the same saved map, NOT in the robot
// prefab, so it applies to every robot already built with no rebuild — and which functions the
// assignment popup offers follows from it.
//
// The mechanism list comes from the catalog entry's metadata (written by the URDF
// post-processor), so no field-scene loading is needed here. Robots without mechanisms — like
// the built-in drivetrain — get an explanatory empty state; their buttons still open the popup
// (with only Clear/Done) so the flow is discoverable.
//
// Usage: built and fully wired (panel, 12 diagram buttons + captions in ControllerButton
// order, assignment popup) by the Build Home Screen tool. HomeScreenController opens/closes it.
public class ControllerConfigScreen : MonoBehaviour
{
    // The popup row template's second line, found by name. BuildHomeScene makes it and HomeSceneIsValid
    // checks for it, both through this const, so the three can't disagree on what it is called.
    public const string RowDetailName = "RowDetail";

    [Header("Data")]
    [SerializeField] private RobotModelCatalog catalog;

    [Header("Panel")]
    [SerializeField] private GameObject panel;
    [SerializeField] private TMP_Text headerLabel;
    [SerializeField] private GameObject emptyStateLabel;

    [Header("Diagram (ControllerButton order: L1 L2 R1 R2 Up Down Left Right X B A Y)")]
    [SerializeField] private Button[] buttons = new Button[ControllerMapSettings.ButtonCount];
    [SerializeField] private TMP_Text[] assignmentLabels = new TMP_Text[ControllerMapSettings.ButtonCount];

    [Header("Assignment Popup")]
    [Tooltip("The popup together with the dim behind it, shown and hidden as one so the two can never " +
             "be out of step.")]
    [SerializeField] private GameObject assignmentPanel;
    [SerializeField] private TMP_Text assignmentHeader;
    [SerializeField] private Transform assignmentListParent;
    [SerializeField] private Button assignmentRowTemplate;
    [SerializeField] private Button clearButton;
    [SerializeField] private Button cancelButton;
    [Tooltip("Opens the same popup in control-style mode. Optional: a home scene built before " +
             "control styles existed has no such button and simply keeps the per-type defaults.")]
    [SerializeField] private Button controlStyleButton;
    [Tooltip("Puts this robot's shipped layout back, discarding the player's changes. Hidden for a " +
             "robot that ships without one. Optional: older home scenes have no such button.")]
    [SerializeField] private Button resetDefaultsButton;

    [Header("Tints")]
    [SerializeField] private Color assignedTint = new Color(0.24f, 0.49f, 0.92f); // accent blue
    [SerializeField] private Color unassignedTint = new Color(0.23f, 0.25f, 0.30f); // neutral dark
    [Tooltip("Popup rows already on the pending button are filled with this, so what's picked reads " +
             "at a glance.")]
    [SerializeField] private Color selectedRowTint = new Color(0.20f, 0.62f, 0.35f); // green
    [SerializeField] private Color rowTint = new Color(0.23f, 0.25f, 0.30f);         // neutral dark

    // What the shared popup is currently showing.
    private enum PopupMode { Assign, Style }

    private string robotId;
    private string robotDisplayName;
    // The catalog entry the screen is showing, kept so Reset to Default has something to reset TO.
    private RobotModelCatalog.Entry currentEntry;
    private List<RobotModelCatalog.MechanismInfo> mechanisms = new List<RobotModelCatalog.MechanismInfo>();
    private ButtonMap map = new ButtonMap();
    private readonly List<GameObject> spawnedRows = new List<GameObject>();
    private PopupMode popupMode = PopupMode.Assign;
    private int pendingButtonIndex = -1;

    void Awake()
    {
        // The diagram buttons need their index, which persistent onClicks can't carry — wire
        // the listeners here from the serialized array instead.
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i; // per-iteration copy for the closure
            if (buttons[i] != null) buttons[i].onClick.AddListener(() => OnDiagramButtonPressed(index));
        }
        if (clearButton != null) clearButton.onClick.AddListener(OnClearPressed);
        if (controlStyleButton != null) controlStyleButton.onClick.AddListener(OnControlStylePressed);
        if (resetDefaultsButton != null) resetDefaultsButton.onClick.AddListener(OnResetDefaultsPressed);
        if (cancelButton != null)
        {
            cancelButton.onClick.AddListener(CloseAssignmentPopup);
            // Every toggle saves immediately now, so this button just closes the popup — relabel
            // it "Done" (the built prefab says "Cancel", which reads as "discard my changes").
            TMP_Text cancelText = cancelButton.GetComponentInChildren<TMP_Text>(true);
            if (cancelText != null) cancelText.text = "Done";
        }
    }

    // Called by HomeScreenController when the player opens the config screen. Reads the
    // selected robot's mechanisms from the catalog and its saved map from PlayerPrefs.
    public void Open()
    {
        RobotModelCatalog.Entry entry = null;
        string id = catalog != null ? catalog.SelectedModelId : null;
        if (catalog != null && !string.IsNullOrEmpty(id))
        {
            // VisibleModels, not models: a private robot's mechanism list would otherwise leak here
            // even though it isn't listed in the picker.
            foreach (RobotModelCatalog.Entry candidate in catalog.VisibleModels)
            {
                if (candidate.id == id) { entry = candidate; break; }
            }
        }
        // Belt and braces: the home screen already seeds at Start, but this screen is the one place
        // a player looks when the controller seems unbound, so it must not be the one place that
        // shows an empty diagram because seeding was somehow skipped.
        ControllerMapSettings.SeedDefault(entry);

        Bind(id, entry, ControllerMapSettings.Load(id));
        PruneStaleAssignments();
        Show();
    }

    // What Open shows, for a robot and a layout handed in rather than read from this device, and without
    // Open's writes (seeding a default, pruning stale assignments). Public so
    // ControllerConfigLayoutValidation can lay out the real screen for any robot and any layout, in edit
    // mode, and leave PlayerPrefs alone.
    public void ShowForLayoutCheck(RobotModelCatalog.Entry entry, ButtonMap buttonMap)
    {
        Bind(entry != null ? entry.id : null, entry, buttonMap ?? new ButtonMap());
        Show();
    }

    private void Bind(string id, RobotModelCatalog.Entry entry, ButtonMap buttonMap)
    {
        robotId = id;
        currentEntry = entry;
        robotDisplayName = entry != null ? entry.displayName : "No Robot";
        mechanisms = entry != null && entry.mechanisms != null
            ? entry.mechanisms
            : new List<RobotModelCatalog.MechanismInfo>();
        map = buttonMap;
    }

    private void Show()
    {
        if (headerLabel != null) headerLabel.text = $"Controller — {robotDisplayName}";
        if (emptyStateLabel != null) emptyStateLabel.SetActive(mechanisms.Count == 0);
        // Style switching needs mechanisms to act on, so hide the entry point when there are none.
        if (controlStyleButton != null) controlStyleButton.gameObject.SetActive(mechanisms.Count > 0);
        // Same rule for Reset: offering to restore a layout that doesn't exist would just look broken.
        if (resetDefaultsButton != null)
            resetDefaultsButton.gameObject.SetActive(currentEntry != null && currentEntry.HasDefaultButtonMap);

        CloseAssignmentPopup(); // a popup left open from a previous robot must not carry over
        if (panel != null) panel.SetActive(true);
        // After the panel is up: each caption measures its lines against its box (FitCaptionLine), and a
        // text that has never been active has not set itself up to be measured.
        RefreshAllButtons();

        // The two SetActives above happen while the panel is still inactive, so the bottom row's
        // HorizontalLayoutGroup — which is what keeps Back centred whatever subset of the row is
        // showing — hasn't had a chance to react to them. uGUI does rebuild a layout group on
        // enable, so this is insurance rather than the fix, but a lopsided row is exactly the bug
        // the group was added to kill and it costs one line to be certain.
        // Reached through a button rather than a ref of its own: the row is a pure-hierarchy
        // container the builder creates, and every button in it shares the same parent. Works on an
        // inactive button, which is the case that matters.
        Button rowMember = controlStyleButton != null ? controlStyleButton : resetDefaultsButton;
        if (rowMember != null && rowMember.transform.parent is RectTransform row)
            LayoutRebuilder.MarkLayoutForRebuild(row);
    }

    public void Close()
    {
        if (assignmentPanel != null) assignmentPanel.SetActive(false);
        if (panel != null) panel.SetActive(false);
    }

    // --- Assignment popup ---

    // The diagram buttons' click handler. Public, like OnControlStylePressed, so
    // ControllerConfigLayoutValidation can open the popup in edit mode, where Awake never wires the
    // listeners.
    public void OnDiagramButtonPressed(int index)
    {
        if (assignmentPanel == null || assignmentRowTemplate == null || assignmentListParent == null) return;
        popupMode = PopupMode.Assign;
        pendingButtonIndex = index;
        if (assignmentHeader != null) assignmentHeader.text = $"Assign {(ControllerButton)index}";
        if (clearButton != null) clearButton.gameObject.SetActive(true);
        PopulateRows();
        assignmentPanel.SetActive(true);
    }

    // Opens the same popup listing mechanisms instead of button functions. pendingButtonIndex is
    // cleared because there IS no pending button here — OnClearPressed would otherwise wipe
    // whichever button happened to be open last.
    public void OnControlStylePressed()
    {
        if (assignmentPanel == null || assignmentRowTemplate == null || assignmentListParent == null) return;
        popupMode = PopupMode.Style;
        pendingButtonIndex = -1;
        if (assignmentHeader != null) assignmentHeader.text = "Control Style";
        if (clearButton != null) clearButton.gameObject.SetActive(false); // nothing to clear here
        PopulateRows();
        assignmentPanel.SetActive(true);
    }

    private void PopulateRows()
    {
        // DestroyImmediate outside Play: the layout validator opens these popups in edit mode, which
        // refuses Destroy.
        foreach (GameObject row in spawnedRows)
        {
            if (Application.isPlaying) Destroy(row);
            else DestroyImmediate(row);
        }
        spawnedRows.Clear();
        if (popupMode == PopupMode.Style) PopulateStyleRows();
        else PopulateAssignmentRows();
    }

    // One toggle row per mechanism function for the pending button. Which functions a mechanism
    // offers depends on its control style, so a motor switched to one-button shows Toggle rows
    // rather than Forward/Reverse.
    private void PopulateAssignmentRows()
    {
        if (pendingButtonIndex < 0) return;
        ControllerButton button = (ControllerButton)pendingButtonIndex;

        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
            string style = ControllerMapSettings.GetStyle(map, mechanism.id, mechanism.type);
            foreach (string mode in ControllerMapSettings.ModesFor(mechanism.type, style))
            {
                AddRow($"{NameOf(mechanism)} — {FunctionLabel(mode)}", null,
                    mechanism.id + "_" + mode,
                    ControllerMapSettings.HasAssignment(map, button, mechanism.id, mode),
                    () => OnAssignmentRowToggled(mechanism.id, mode));
            }
        }
    }

    // One row per mechanism showing how many buttons drive it; tapping switches to the other style
    // and rewrites its existing bindings to match, so the change takes effect without a trip back
    // to the diagram.
    private void PopulateStyleRows()
    {
        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
            string style = ControllerMapSettings.GetStyle(map, mechanism.id, mechanism.type);
            // A style row is a "tap to switch" action, not a selected state, so it never tints. Two
            // lines, the mechanism and then its style: as one line the pair was too long for the row
            // and wrapped out of it ("Scoring Intake — 2 buttons (hold fwd / rev)").
            AddRow(NameOf(mechanism), StyleLabel(mechanism.type, style),
                mechanism.id + "_style", false,
                () => OnStyleRowToggled(mechanism.id, mechanism.type, style));
        }
    }

    // Clones the row template, filling it green when this function is already on the pending button.
    // detail, when there is one, goes on a second, smaller line under the label.
    //
    // The mark used to be a "✓ " prefix on the label — but the project font (LiberationSans SDF, 250
    // glyphs) has no U+2713 and TMP Settings defines no fallback, so it rendered as the missing-glyph
    // box: the "white box next to it". Tinting the row is both unambiguous and font-proof.
    private void AddRow(string label, string detail, string idSuffix, bool selected,
        UnityEngine.Events.UnityAction onClick)
    {
        Button row = Instantiate(assignmentRowTemplate, assignmentListParent);
        row.name = "Row_" + idSuffix;
        row.gameObject.SetActive(true); // template itself stays inactive

        // The second line is a text of its own, showing only when there is something to put on it; the
        // row's layout group sizes the row to the lines that are showing. A home scene built before rows
        // had one keeps the single line, with the detail after a dash the way it used to read.
        Transform detailLine = row.transform.Find(RowDetailName);
        bool twoLines = !string.IsNullOrEmpty(detail);
        if (detailLine != null)
        {
            detailLine.gameObject.SetActive(twoLines);
            TMP_Text detailText = detailLine.GetComponent<TMP_Text>();
            if (detailText != null) detailText.text = twoLines ? detail : string.Empty;
        }
        else if (twoLines) label = $"{label} — {detail}";

        Transform labelLine = row.transform.Find("Label");
        TMP_Text text = labelLine != null ? labelLine.GetComponent<TMP_Text>() : row.GetComponentInChildren<TMP_Text>(true);
        if (text != null) text.text = label;
        PressFeedback.Tint(row, selected ? selectedRowTint : rowTint); // not row.image — see PressFeedback.Tint
        row.onClick.AddListener(onClick);
        spawnedRows.Add(row.gameObject);
    }

    // Adds the function if the button doesn't have it, removes it if it does — so several
    // functions can be stacked on one button. Saves and re-renders in place (popup stays open).
    private void OnAssignmentRowToggled(string mechanismId, string mode)
    {
        if (pendingButtonIndex < 0) return;
        ControllerButton button = (ControllerButton)pendingButtonIndex;
        if (ControllerMapSettings.HasAssignment(map, button, mechanismId, mode))
            ControllerMapSettings.RemoveAssignment(map, button, mechanismId, mode);
        else
            ControllerMapSettings.AddAssignment(map, button, mechanismId, mode);
        ControllerMapSettings.Save(robotId, map);
        RefreshButton(pendingButtonIndex);
        PopulateRows();
    }

    // Flips the mechanism to the other style. Every diagram caption refreshes because the rewrite
    // can move a function onto a newly-claimed button.
    private void OnStyleRowToggled(string mechanismId, string mechanismType, string currentStyle)
    {
        string next = currentStyle == ControllerMapSettings.StyleOneButton
            ? ControllerMapSettings.StyleTwoButton
            : ControllerMapSettings.StyleOneButton;
        ControllerMapSettings.SetStyle(map, mechanismId, mechanismType, next);
        ControllerMapSettings.Save(robotId, map);
        RefreshAllButtons();
        PopulateRows();
    }

    private void OnClearPressed()
    {
        if (popupMode != PopupMode.Assign || pendingButtonIndex < 0) return;
        ControllerMapSettings.ClearAssignment(map, (ControllerButton)pendingButtonIndex);
        ControllerMapSettings.Save(robotId, map);
        RefreshButton(pendingButtonIndex);
        PopulateRows(); // reflect the cleared state; keep the popup open
    }

    // Throw this device's layout away and take the shipped one back. Deliberately immediate with no
    // confirmation: the thing it discards is a button map, the button is only visible when there IS
    // a default to return to, and re-binding is the screen you are already standing on.
    private void OnResetDefaultsPressed()
    {
        if (!ControllerMapSettings.ResetToDefault(currentEntry)) return;
        map = ControllerMapSettings.Load(robotId);
        PruneStaleAssignments(); // a default authored before a mechanism was removed
        RefreshAllButtons();
        if (assignmentPanel != null && assignmentPanel.activeSelf) PopulateRows();
    }

    private void CloseAssignmentPopup()
    {
        popupMode = PopupMode.Assign;
        pendingButtonIndex = -1;
        if (clearButton != null) clearButton.gameObject.SetActive(true);
        if (assignmentPanel != null) assignmentPanel.SetActive(false);
    }

    // Row copy. ModeLabel/ModeCaption stay lowercase for prose and captions; these are the
    // title-case, player-facing names for the popup.
    private static string FunctionLabel(string mode)
    {
        switch (mode)
        {
            case ControllerMapSettings.ModeReverse: return "Reverse (hold)";
            case ControllerMapSettings.ModeToggle: return "Toggle";
            case ControllerMapSettings.ModeToggleReverse: return "Toggle Reverse";
            case ControllerMapSettings.ModeExtend: return "Extend";
            case ControllerMapSettings.ModeRetract: return "Retract";
            default: return "Forward (hold)";
        }
    }

    private static string StyleLabel(string mechanismType, string style)
    {
        bool one = style == ControllerMapSettings.StyleOneButton;
        if (mechanismType == RobotMechanisms.TypePneumatic)
            return one ? "1 button (toggle)" : "2 buttons (extend / retract)";
        return one ? "1 button (toggle on/off)" : "2 buttons (hold fwd / rev)";
    }

    // --- Refresh ---

    private void RefreshAllButtons()
    {
        for (int i = 0; i < ControllerMapSettings.ButtonCount; i++) RefreshButton(i);
    }

    // Assigned buttons tint accent and list EVERY function they drive beside the button, one per
    // line ("DR4B REV" / "Claw Clamp TOG") — a button can legitimately drive several mechanisms, and
    // showing only the first left the rest invisible. Each caption has a box three lines tall, on a
    // side of its button that nothing else reaches into (BuildHomeScene.CreateConfigButton), so past
    // that they fold into a "+N" tail rather than running into another button.
    private const int MaxCaptionLines = 3;

    private void RefreshButton(int index)
    {
        List<ButtonAssignment> assignments = ControllerMapSettings.FindAll(map, (ControllerButton)index);
        TMP_Text caption = index < assignmentLabels.Length ? assignmentLabels[index] : null;

        var names = new List<string>();
        var tags = new List<string>();
        int shown = 0;
        foreach (ButtonAssignment assignment in assignments)
        {
            RobotModelCatalog.MechanismInfo mechanism = FindMechanism(assignment.mechanismId);
            if (mechanism == null) continue; // stale (mechanism gone); PruneStaleAssignments clears it
            shown++;
            if (names.Count < MaxCaptionLines)
            {
                names.Add(NameOf(mechanism));
                tags.Add(ControllerMapSettings.ModeCaption(assignment.mode));
            }
        }
        if (shown > names.Count && names.Count > 0) tags[tags.Count - 1] += $" +{shown - names.Count}";

        var lines = new List<string>();
        for (int i = 0; i < names.Count; i++) lines.Add(FitCaptionLine(caption, names[i], tags[i]));
        if (caption != null) caption.text = string.Join("\n", lines);
        if (index < buttons.Length)
            PressFeedback.Tint(buttons[index], shown > 0 ? assignedTint : unassignedTint);
    }

    // One caption line, "Name TAG", shortened to fit the caption's box at the smallest size it may shrink
    // to: a name too long loses its end to "...", and the tag (what the button does to it, and any "+N")
    // always survives. Done here, line by line, because TMP's own ellipsis cuts the WHOLE caption at the
    // first line too wide for the box, and every function listed after it vanishes without a trace.
    private static string FitCaptionLine(TMP_Text caption, string name, string tag)
    {
        string line = $"{name} {tag}";
        float room = caption != null ? caption.rectTransform.rect.width : 0f;
        if (room <= 0f) return line;
        // GetPreferredValues measures an auto-sized text at its largest size, and widths scale with size.
        float shrink = caption.enableAutoSizing && caption.fontSizeMax > 0f
            ? caption.fontSizeMin / caption.fontSizeMax
            : 1f;
        if (caption.GetPreferredValues(line).x * shrink <= room) return line;
        for (int keep = name.Length - 1; keep > 0; keep--)
        {
            string cut = $"{name.Substring(0, keep).TrimEnd()}... {tag}";
            if (caption.GetPreferredValues(cut).x * shrink <= room) return cut;
        }
        return tag;
    }

    // A mechanism whose display name never got set would otherwise render as a bare " — Forward
    // (hold)" row with nothing to identify it. The id is ugly but it is never empty. Either way it goes
    // through MechanismNames, so "CascadeLift" reads "Cascade Lift".
    private static string NameOf(RobotModelCatalog.MechanismInfo mechanism)
        => MechanismNames.Pretty(string.IsNullOrWhiteSpace(mechanism.displayName) ? mechanism.id : mechanism.displayName);

    private RobotModelCatalog.MechanismInfo FindMechanism(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism != null && mechanism.id == id) return mechanism;
        }
        return null;
    }

    // Assignments and style choices for mechanisms the robot no longer has (re-import removed a
    // joint) are dropped from the persisted map so they don't linger invisibly. So are assignments
    // on a function the mechanism's CURRENT style doesn't expose.
    //
    // That second sweep is what heals the one-button motor: it used to expose a latching forward and
    // a latching reverse, so switching a motor to "1 button" left two toggle buttons on the diagram
    // — and once ModesFor stopped offering the reverse, no popup row existed to take it off again.
    // Pruning here means opening the screen fixes a map saved under the old table, rather than
    // needing Clear or Reset to Default.
    private void PruneStaleAssignments()
    {
        if (map == null || map.assignments == null) return;
        int removed = map.assignments.RemoveAll(a => a == null || FindMechanism(a.mechanismId) == null);
        if (map.styles != null)
            removed += map.styles.RemoveAll(s => s == null || FindMechanism(s.mechanismId) == null);

        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
            string style = ControllerMapSettings.GetStyle(map, mechanism.id, mechanism.type);
            var offered = new List<string>(ControllerMapSettings.ModesFor(mechanism.type, style));
            removed += map.assignments.RemoveAll(a => a != null
                && a.mechanismId == mechanism.id && !offered.Contains(a.mode));
        }

        if (removed > 0) ControllerMapSettings.Save(robotId, map);
    }
}
