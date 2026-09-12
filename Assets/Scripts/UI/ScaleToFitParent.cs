using UnityEngine;

// Scales a fixed-size child so it fits inside this rect, keeping its proportions.
//
// Why the contents can't simply be resized: the controller diagram (1560x700) and the control
// layout preview (1920x1080) hold everything at absolute offsets — the diagram's twelve button
// pills, and the preview's drag proxies, whose anchoredPosition IS the field control's position in
// 1920x1080 reference pixels. Resizing either rect would move its backdrop out from under its
// contents; scaling moves them together.
//
// Why the scale can't be baked at author time: both panels stretch to the canvas HEIGHT, and that
// is ~899 units on a 6.5" phone against ~1167 on a 13" iPad. One authored number is therefore
// either too big for the phone — the preview shipped at 0.7, which put it 30 units on top of the
// Back button — or leaves a third of the iPad's panel empty.
//
// Lives on the CONTAINER, not on the content: Unity only tells a rect its dimensions changed, and
// the content's dimensions are exactly what never change here.
[RequireComponent(typeof(RectTransform))]
public class ScaleToFitParent : MonoBehaviour
{
    [Tooltip("The fixed-size child to scale. Its rect is never written — only its localScale.")]
    public RectTransform target;
    [Tooltip("Never scale past this. 1 keeps the content at its authored size on a roomy canvas.")]
    public float maxScale = 1f;

    private RectTransform rect;

    void Awake() => rect = (RectTransform)transform;

    void OnEnable() => Apply();

    // Sent to every component on the object whose rect changed — which is why this sits on the
    // stretched container. Covers the canvas resizing, a rotation, and the first layout pass after
    // the panel it belongs to is shown for the first time.
    void OnRectTransformDimensionsChange() => Apply();

    private void Apply()
    {
        if (target == null) return;
        // This message can arrive before Awake on the frame the object is created.
        if (rect == null) rect = (RectTransform)transform;

        Vector2 room = rect.rect.size;
        Vector2 content = target.rect.size;
        // A zero means the layout has not run yet. Another call arrives once it has, so doing
        // nothing here is right — and doing anything is a divide by zero.
        if (room.x <= 0f || room.y <= 0f || content.x <= 0f || content.y <= 0f) return;

        float scale = Mathf.Min(room.x / content.x, room.y / content.y);
        target.localScale = Vector3.one * Mathf.Min(scale, maxScale);
    }
}
