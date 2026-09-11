using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Obvious press feedback for an on-screen control button: while held it shrinks slightly, flashes
// brighter, and — where the position is its own to move — sinks in by a small downward offset,
// then springs back on release. The stock UI.Button ColorTint transition only multiplies the sprite
// color by ~0.78 on press, which is nearly invisible on the dark control buttons; this makes a
// press unmistakable.
//
// Attached to each on-screen control by BuildDriveControls, which also sets the Button's transition
// to None so its ColorTint doesn't fight the color written here. Driven by pointer (touch/mouse)
// events, independent of the OnScreenButton input path — both receive the same pointer events.
//
// Safe against ControlsAppearance: that scales the cluster roots and sets CanvasGroup opacity, none
// of which touch an individual button's own localScale / anchoredPosition / graphic color — the
// three things animated here.
[RequireComponent(typeof(RectTransform))]
public class PressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [Tooltip("Local scale while pressed (1 = no change). 0.88 = 12% smaller, reads as pushed in.")]
    public float pressedScale = 0.88f;
    [Tooltip("Anchored-position offset while pressed, in canvas pixels — the 'indent'. Negative Y sinks the button down.")]
    public Vector2 pressedOffset = new Vector2(0f, -7f);
    [Tooltip("Color the target graphic flashes to while pressed (lerped toward). Bright so the press pops on dark buttons.")]
    public Color pressedColor = new Color(0.85f, 0.9f, 1f, 1f);
    [Tooltip("How strongly to blend toward pressedColor (0 = keep base color, 1 = full pressedColor).")]
    [Range(0f, 1f)]
    public float pressedColorBlend = 0.75f;
    [Tooltip("Spring speed to/from the pressed state (higher = snappier).")]
    public float lerpSpeed = 22f;

    private RectTransform rect;
    private Graphic graphic;          // the button's image; tinted on press (may be null)
    private Selectable selectable;    // this object's Button, if any; a disabled one never presses
    private Vector3 baseScale = Vector3.one;
    private Vector2 basePos;
    // Whether a layout group places this rect, making its position not ours to write — see Awake.
    private bool layoutOwnsPosition;
    private Color baseColor = Color.white;
    private bool pressed;
    // Whether Awake has captured baseColor yet — see Tint, which needs to know.
    internal bool hasAwoken;
    // Whether this button has finished springing back and has nothing left to animate — see Update.
    private bool settled = true;

    // The colour the button rests at and springs back to. An owner script that RETINTS the button
    // at runtime (MatchLoadButton greys itself out while no loader can spawn) must write it here
    // rather than straight onto the Image: Update lerps the graphic back toward this value every
    // frame, so a colour written to the Image alone is wiped within a few frames. Assigning it does
    // not snap — the graphic slides to the new colour at lerpSpeed, so the state change reads as a
    // short fade instead of a pop.
    public Color BaseColor
    {
        get => baseColor;
        // Clearing `settled` is what lets the new colour actually animate: Update stops running
        // entirely once a button has come to rest, so a retint alone would sit there unapplied.
        set { baseColor = value; settled = false; }
    }

    // Retint a button that MAY have press feedback on it.
    //
    // Use this anywhere a colour means state — selected, assigned, unavailable — rather than
    // writing Image.color directly. Update below lerps the graphic back to baseColor every frame,
    // so a colour written straight onto the Image survives about three frames and then vanishes.
    // The failure is quiet and looks like the state never changed.
    //
    // This is not hypothetical: attaching PressFeedback to every home-screen button silently broke
    // the settings tab highlight, the controller-diagram "assigned" colour, the assignment popup's
    // selected row, and the model picker's selection — which is the ONLY thing telling a player
    // which robot they are about to drive.
    //
    // Falls back to the graphic when there is no feedback component, so callers don't have to know
    // which buttons have one.
    public static void Tint(Selectable target, Color color)
    {
        if (target == null) return;
        PressFeedback feedback = target.GetComponent<PressFeedback>();
        if (feedback != null)
        {
            feedback.BaseColor = color; // slides to the new colour at lerpSpeed — the fade is free

            // A button cloned from an inactive template has not run Awake yet, and Awake captures
            // baseColor FROM the graphic — so a tint applied first would be overwritten a moment
            // later by the template's colour. Writing the graphic too makes that capture pick up
            // this colour instead. Every current caller happens to activate before tinting, but
            // that is an invariant spread across four call sites in two files, and the symptom if
            // it is ever broken is a selection highlight that silently doesn't appear.
            if (!feedback.hasAwoken && target.targetGraphic != null)
                target.targetGraphic.color = color;
            return;
        }
        if (target.targetGraphic != null) target.targetGraphic.color = color;
    }

    void Awake()
    {
        rect = (RectTransform)transform;
        baseScale = rect.localScale;
        basePos = rect.anchoredPosition;

        // Does a layout group own where this button sits?
        //
        // A Vertical/HorizontalLayoutGroup writes its children's anchoredPosition during the canvas
        // update, which runs AFTER Awake — so for a layout-driven button the value captured just
        // above is the builder's placeholder, not where the button actually appears. Most
        // home-screen buttons serialize as anchoredPosition (0,0), sizeDelta (0,0) for exactly that
        // reason: the layout group supplies both at runtime.
        //
        // Springing back to that placeholder threw every menu button into the corner of its panel on
        // the first press and left it there, because a layout group only rewrites a child's position
        // when something marks the layout dirty. It cost the tap as well: the button slid out from
        // under the finger, so the pointer-up raycast missed it and Button.onClick never fired —
        // which is why a menu button looked like it had to be pressed twice.
        //
        // Re-reading the position at press time would fix the first press, but it would still be a
        // write the layout group is free to undo at any moment, so these buttons simply don't move.
        // Scale and the colour flash carry the press on their own. The field controls are absolutely
        // positioned and keep the full sink.
        layoutOwnsPosition = transform.parent != null &&
                             transform.parent.GetComponent<LayoutGroup>() != null;

        // Prefer the Button's target graphic (the sprite), else the first Graphic on this object.
        Button button = GetComponent<Button>();
        graphic = (button != null ? button.targetGraphic as Graphic : null) ?? GetComponent<Graphic>();
        if (graphic != null) baseColor = graphic.color;
        selectable = button != null ? button : GetComponent<Selectable>();
        hasAwoken = true;
    }

    // A greyed-out control must not animate a press it will never act on. The EventSystem still
    // delivers pointer events to the OTHER components on a non-interactable Selectable — Button
    // filters them internally, we don't — so without this check the disabled Match Load button
    // sank in and flashed exactly like a live one while doing nothing.
    public void OnPointerDown(PointerEventData eventData)
    {
        pressed = selectable == null || selectable.IsInteractable();
        settled = false;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        pressed = false;
        settled = false; // there is a spring-back to animate before this can rest again
    }

    void OnDisable()
    {
        // Snap back so a button hidden/disabled mid-press doesn't stay stuck indented.
        pressed = false;
        if (rect != null)
        {
            rect.localScale = baseScale;
            if (!layoutOwnsPosition) rect.anchoredPosition = basePos;
        }
        if (graphic != null) graphic.color = baseColor;
        settled = true;
    }

    void Update()
    {
        // Idle early-out. This used to live only on the field controls, where a dozen of them sat
        // still most of the time; the home screen now puts ~20 on screen at once. A button that is
        // not pressed and has already sprung back has nothing to do, but without this it would
        // still run three lerps and write a transform and a colour every frame — and writing a
        // Graphic's colour marks the canvas dirty, which forces uGUI to rebuild that batch.
        if (!pressed && settled) return;

        // Frame-rate-independent approach to the target (unscaled so it works even if paused).
        float t = 1f - Mathf.Exp(-lerpSpeed * Time.unscaledDeltaTime);

        Vector3 targetScale = pressed ? baseScale * pressedScale : baseScale;
        Vector2 targetPos = pressed ? basePos + pressedOffset : basePos;
        rect.localScale = Vector3.Lerp(rect.localScale, targetScale, t);
        if (!layoutOwnsPosition)
            rect.anchoredPosition = Vector2.Lerp(rect.anchoredPosition, targetPos, t);

        Color targetColor = baseColor;
        if (graphic != null)
        {
            targetColor = pressed ? Color.Lerp(baseColor, pressedColor, pressedColorBlend) : baseColor;
            targetColor.a = baseColor.a; // keep the button's own alpha; opacity is a CanvasGroup concern
            graphic.color = Color.Lerp(graphic.color, targetColor, t);
        }

        // An exponential approach never actually arrives, so snap the last sliver and stop. Only
        // while released: a held button has to keep running to follow baseColor if it changes.
        if (pressed) return;
        if ((rect.localScale - targetScale).sqrMagnitude > 1e-8f) return;
        // Skipped when the layout group owns the position: the rect then sits wherever the layout
        // put it and never at targetPos, so testing it would leave the button permanently unsettled
        // — defeating the idle early-out above for every button on screen at once.
        if (!layoutOwnsPosition && (rect.anchoredPosition - targetPos).sqrMagnitude > 1e-4f) return;
        if (graphic != null && ((Vector4)(graphic.color - targetColor)).sqrMagnitude > 1e-6f) return;

        rect.localScale = targetScale;
        if (!layoutOwnsPosition) rect.anchoredPosition = targetPos;
        if (graphic != null) graphic.color = targetColor;
        settled = true;
    }
}
