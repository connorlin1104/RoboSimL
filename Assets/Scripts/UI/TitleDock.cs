using UnityEngine;

// Slides the app title between two horizontal spans: centred on the whole canvas while the home
// menu is up, and centred over the stage — the left region — once the settings panel docks into
// the right of the screen.
//
// Animates the ANCHOR rather than a pixel offset, so both resting places stay correct at any
// canvas width and the builder never has to know the device. anchoredPosition and sizeDelta are
// left alone, which is what keeps the same gutter on either side of whichever span the title
// currently occupies: with pivot 0.5, anchoredPosition.x 0 and a negative sizeDelta.x, the rect
// centres in its anchor span and insets by the same amount at both ends.
[RequireComponent(typeof(RectTransform))]
public class TitleDock : MonoBehaviour
{
    [Tooltip("anchorMax.x with the home menu showing. 1 is the full canvas width.")]
    public float homeAnchorMaxX = 1f;
    [Tooltip("anchorMax.x with settings open — the right edge of the stage region.")]
    public float dockedAnchorMaxX = 0.55f;
    [Tooltip("Spring speed. The same 1-exp(-k*dt) form as PressFeedback, so it is frame-rate independent.")]
    public float speed = 11f;

    private RectTransform rect;
    private bool docked;
    // Whether there is anything left to animate — see Update.
    private bool settled;

    void Awake() => SetImmediate(docked);

    // Called by HomeScreenController as the settings panel opens and closes.
    public void SetDocked(bool value)
    {
        if (docked == value) return;
        docked = value;
        settled = false;
    }

    // Skips the slide. Used for the state the screen opens in, where an animation would read as
    // the title having briefly been in the wrong place.
    public void SetImmediate(bool value)
    {
        docked = value;
        if (rect == null) rect = (RectTransform)transform;
        Vector2 anchor = rect.anchorMax;
        anchor.x = docked ? dockedAnchorMaxX : homeAnchorMaxX;
        rect.anchorMax = anchor;
        settled = true;
    }

    void Update()
    {
        if (settled) return;

        float target = docked ? dockedAnchorMaxX : homeAnchorMaxX;
        Vector2 anchor = rect.anchorMax;
        // Unscaled, as everywhere else in this UI: a menu that stops animating when something
        // pauses the game is the kind of thing nobody notices until it ships.
        anchor.x = Mathf.Lerp(anchor.x, target, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));

        // An exponential approach never actually arrives, so snap the last sliver and stop.
        if (Mathf.Abs(anchor.x - target) < 0.0005f)
        {
            anchor.x = target;
            settled = true;
        }
        rect.anchorMax = anchor;
    }
}
