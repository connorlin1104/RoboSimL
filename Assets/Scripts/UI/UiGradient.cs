using UnityEngine;
using UnityEngine.UI;

// A vertical two-stop gradient on any uGUI Graphic, with no texture.
//
// uGUI has no gradient fill: an Image is one flat colour. The usual answer is to author a gradient
// PNG per colour pair, which means a new asset every time a colour changes. A mesh modifier gets
// the same result by writing the two colours into the quad's existing vertices — no texture, no
// extra draw call, and the colours stay editable numbers in BuildHomeScene's palette.
//
// This is what carries depth on the panels now that the panel sprite has no baked drop shadow, and
// it is what makes a primary action look like a primary action: Drive is the app's icon gradient,
// at full strength, and nothing else in the UI is.
//
// NOT for TextMeshPro. TMP_Text derives from MaskableGraphic but generates its geometry itself and
// never calls Graphic.UpdateGeometry, so a mesh modifier attached to one is silently ignored — it
// doesn't warn, it just does nothing. TMP has its own equivalent (enableVertexGradient +
// colorGradient), which BuildHomeScene.CreateText uses for the title.
[AddComponentMenu("UI/Effects/Vertical Gradient")]
public class UiGradient : BaseMeshEffect
{
    [Tooltip("Colour at the TOP of the graphic's own geometry.")]
    public Color topColor = Color.white;
    [Tooltip("Colour at the BOTTOM.")]
    public Color bottomColor = Color.white;

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0) return;

        // Measured from the GEOMETRY, not from the RectTransform. A sliced sprite fills its rect
        // exactly, but a Filled or Simple one need not, and normalising against the rect would then
        // put the gradient's ends somewhere off the visible shape.
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        var vertex = new UIVertex();
        for (int i = 0; i < vh.currentVertCount; i++)
        {
            vh.PopulateUIVertex(ref vertex, i);
            if (vertex.position.y < minY) minY = vertex.position.y;
            if (vertex.position.y > maxY) maxY = vertex.position.y;
        }

        // A zero-height graphic would divide by zero; it also has nothing to shade.
        float height = maxY - minY;
        if (height <= Mathf.Epsilon) return;

        for (int i = 0; i < vh.currentVertCount; i++)
        {
            vh.PopulateUIVertex(ref vertex, i);
            Color gradient = Color.Lerp(bottomColor, topColor, (vertex.position.y - minY) / height);
            // MULTIPLIED into whatever colour is already there rather than replacing it, so the
            // Graphic's own color still works as a tint on top — which is what lets PressFeedback
            // flash a gradient button without this effect wiping the flash out.
            vertex.color = (Color)vertex.color * gradient;
            vh.SetUIVertex(vertex, i);
        }
    }

    // Colours written from a script (or nudged in the Inspector) don't reach the mesh on their own:
    // uGUI only re-runs mesh modifiers when the Graphic is marked dirty.
    public void SetColors(Color top, Color bottom)
    {
        topColor = top;
        bottomColor = bottom;
        if (graphic != null) graphic.SetVerticesDirty();
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        if (graphic != null) graphic.SetVerticesDirty();
    }
#endif
}
