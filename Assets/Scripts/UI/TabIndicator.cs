using UnityEngine;

// A bar that slides to sit under whichever settings tab is active.
//
// The tabs already recolour on selection, and that recolour now fades rather than snapping (it
// goes through PressFeedback.Tint). The bar adds the thing colour alone can't: a sense that the
// three tabs are one control rather than three buttons that happen to be next to each other, and
// a direction of travel when you move between them.
//
// Positioned ABSOLUTELY against the tab row rather than parented to a tab, because the tabs live
// in a HorizontalLayoutGroup — a child of one would be laid out by the group, and moving it would
// fight the layout every frame. It carries an ignored LayoutElement for the same reason.
[RequireComponent(typeof(RectTransform))]
public class TabIndicator : MonoBehaviour
{
    // Written by BuildHomeScene through SerializedObject, like every other reference in that
    // builder — rather than through a public setter behind #if UNITY_EDITOR, which would make a
    // runtime script's API depend on which assembly it was compiled into.
    [Tooltip("The tab buttons' rects, in tab order. Written by BuildHomeScene.")]
    [SerializeField] private RectTransform[] tabs;
    [Tooltip("Approach speed (higher = snappier). Matches PanelTransition so the screen moves as one.")]
    [SerializeField] private float speed = 18f;
    [Tooltip("How much narrower than its tab the bar sits, in canvas units, so it reads as an " +
             "underline rather than as a second button.")]
    [SerializeField] private float inset = 22f;

    private RectTransform rect;
    private int active = -1;
    private bool animating;

    void Awake() => rect = (RectTransform)transform;

    // Called by HomeScreenController whenever the tab changes. Snapping on the FIRST call matters:
    // the settings panel is built with tab 0 active, and without this the bar would slide in from
    // wherever the builder happened to leave it every time the screen is opened.
    public void SetActiveTab(int index, bool snap = false)
    {
        if (tabs == null || index < 0 || index >= tabs.Length) return;
        bool first = active < 0;
        active = index;
        if (snap || first) ApplyImmediate();
        else animating = true;
    }

    void OnEnable()
    {
        // The panel this lives in is switched with SetActive, so a reopen has to re-seat the bar —
        // layout may have changed size underneath it while it was hidden.
        if (active >= 0) ApplyImmediate();
    }

    private void ApplyImmediate()
    {
        if (!TryGetTarget(out Vector2 position, out float width)) return;
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        animating = false;
    }

    void Update()
    {
        if (!animating) return;
        if (!TryGetTarget(out Vector2 position, out float width)) { animating = false; return; }

        float t = 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime);
        rect.anchoredPosition = Vector2.Lerp(rect.anchoredPosition, position, t);
        rect.sizeDelta = new Vector2(Mathf.Lerp(rect.sizeDelta.x, width, t), rect.sizeDelta.y);

        if ((rect.anchoredPosition - position).sqrMagnitude > 1e-4f) return;
        if (Mathf.Abs(rect.sizeDelta.x - width) > 0.05f) return;
        ApplyImmediate();
    }

    // Where the bar wants to be, in ITS OWN parent's space.
    //
    // The tab's position is read through the shared parent rather than from its anchoredPosition,
    // because the tabs are positioned by a layout group and their anchors are not the bar's. Going
    // via world space is what makes this correct whatever the group decides.
    private bool TryGetTarget(out Vector2 position, out float width)
    {
        position = Vector2.zero;
        width = 0f;
        if (tabs == null || active < 0 || active >= tabs.Length) return false;
        RectTransform tab = tabs[active];
        if (tab == null || rect == null || rect.parent == null) return false;

        var parent = (RectTransform)rect.parent;
        Vector3 tabCentre = tab.TransformPoint(tab.rect.center);
        Vector2 local = parent.InverseTransformPoint(tabCentre);

        // Keep the bar's own Y — it is authored at the bottom of the row — and take only the
        // horizontal placement from the tab.
        position = new Vector2(local.x, rect.anchoredPosition.y);
        width = Mathf.Max(tab.rect.width - inset, 8f);
        return true;
    }
}
