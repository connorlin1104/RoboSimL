using UnityEngine;

// Fades and eases a panel in when it appears, instead of it simply blinking into existence.
//
// Every screen change on the home screen is a raw SetActive — main panel off, settings panel on,
// in one frame. That is most of what makes the UI feel like a debug menu rather than an app: not
// the colours, the fact that nothing ever moves. This is the cheapest possible fix that isn't a
// tween library: one CanvasGroup, one lerp, no dependencies.
//
// Reused for two different feels via the two `from` fields:
//   - a PANEL comes up from 96% scale, which reads as arriving toward you;
//   - a settings TAB PAGE rises a few units with no scale, because scaling a page inside a scroll
//     view looks like the scroll jumped.
//
// Uses the same frame-rate-independent approach as PressFeedback (1 - exp(-k·dt)) and the same
// unscaled time, so both feel like the same app and neither breaks if the game is ever paused.
[RequireComponent(typeof(CanvasGroup))]
[RequireComponent(typeof(RectTransform))]
public class PanelTransition : MonoBehaviour
{
    [Tooltip("Scale to start from. 1 = no scale animation.")]
    public float fromScale = 0.96f;
    [Tooltip("Anchored-position offset to start from, in canvas units. Negative Y rises into place.")]
    public Vector2 fromOffset = Vector2.zero;
    [Tooltip("Approach speed (higher = snappier). 18 settles in roughly a sixth of a second.")]
    public float speed = 18f;

    private CanvasGroup group;
    private RectTransform rect;
    private Vector3 baseScale = Vector3.one;
    private Vector2 basePos;
    private bool baseCaptured;
    private bool animating;

    // Whether this instance animates the transform at all. A settings TAB PAGE is a ScrollRect's
    // content, and its anchoredPosition IS the scroll position — writing to it, even to hold it at
    // its resting value, drags the scroll back for as long as the animation runs. So a page is
    // configured with no offset and no scale, and then these keep the animation off its transform
    // entirely rather than trusting a lerp toward an unchanged value to be harmless.
    private bool movesPosition;
    private bool movesScale;

    void Awake() => Capture();

    // The resting state has to be read BEFORE the first animation moves anything, and Awake is the
    // only moment that is guaranteed to be. Guarded because OnEnable runs before Awake when an
    // object starts active, and re-capturing mid-animation would bake the animated pose in as the
    // target — which shows up as a panel that shrinks a little more every time it is opened.
    private void Capture()
    {
        if (baseCaptured) return;
        group = GetComponent<CanvasGroup>();
        rect = (RectTransform)transform;
        baseScale = rect.localScale;
        basePos = rect.anchoredPosition;
        movesPosition = fromOffset != Vector2.zero;
        movesScale = !Mathf.Approximately(fromScale, 1f);
        baseCaptured = true;
    }

    void OnEnable()
    {
        Capture();
        group.alpha = 0f;
        if (movesScale) rect.localScale = baseScale * fromScale;
        if (movesPosition) rect.anchoredPosition = basePos + fromOffset;
        animating = true;
    }

    void OnDisable()
    {
        // Snap back, so a panel closed mid-animation is not left half-faded the next time it is
        // opened — and so anything reading its transform sees the authored values.
        if (!baseCaptured) return;
        animating = false;
        group.alpha = 1f;
        if (movesScale) rect.localScale = baseScale;
        if (movesPosition) rect.anchoredPosition = basePos;
    }

    void Update()
    {
        if (!animating) return;

        float t = 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime);
        group.alpha = Mathf.Lerp(group.alpha, 1f, t);
        if (movesScale) rect.localScale = Vector3.Lerp(rect.localScale, baseScale, t);
        if (movesPosition) rect.anchoredPosition = Vector2.Lerp(rect.anchoredPosition, basePos, t);

        // An exponential approach never arrives; snap the last sliver and stop updating, so an
        // open panel costs nothing per frame.
        if (group.alpha < 0.999f) return;
        if (movesScale && (rect.localScale - baseScale).sqrMagnitude > 1e-8f) return;
        if (movesPosition && (rect.anchoredPosition - basePos).sqrMagnitude > 1e-4f) return;

        group.alpha = 1f;
        if (movesScale) rect.localScale = baseScale;
        if (movesPosition) rect.anchoredPosition = basePos;
        animating = false;
    }
}
