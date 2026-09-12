using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// The window the home screen's robot is seen through — a RawImage showing the stage camera's render
// texture — and everything about WHEN that camera draws.
//
// Why a render texture at all: the home canvas is ScreenSpaceOverlay, which draws over every camera,
// so a robot rendered straight to the screen would sit hidden behind the menu's own backdrop.
//
// Why it lives on the RawImage and not on the rig: HomeScreenController.ShowStage hides the whole left
// half whenever a full-screen sub-screen opens. That deactivates this object, which runs OnDisable,
// which stops the camera — so the stage costs nothing behind those screens, and nothing had to be added
// to the controller to make that true. The rig is a scene root and would otherwise keep drawing into a
// texture nobody could see.
//
// The robot turns on its own at a slow drift; a finger spins it faster, and when the finger lifts it
// settles back to the drift (RobotStage). The camera draws 30 times a second while it drifts and 60
// while a finger is on it, and never while it is hidden.
[RequireComponent(typeof(RawImage))]
public class RobotStageView : MonoBehaviour,
    IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler, IDragHandler
{
    // The escape hatch, cheapest last: Drift is the stage as designed; Still draws the robot once and
    // never again (a still picture, and the drag stops); Off shows the chassis mark and draws nothing.
    public enum StageMode { Drift, Still, Off }

    [Header("Wiring (Build Home Screen)")]
    [SerializeField] private RobotModelCatalog catalog;
    [SerializeField] private RobotStage stage;
    [SerializeField] private Camera stageCamera;
    [Tooltip("Shown instead of the robot when there is none to draw — the app's chassis mark.")]
    [SerializeField] private GameObject fallbackMark;
    [SerializeField] private TMP_Text nameLabel;
    [SerializeField] private TMP_Text mechanismsLabel;

    [Header("Cadence")]
    public StageMode mode = StageMode.Drift;
    [Tooltip("Draws per second while it drifts. At 30, a 12 degree-per-second turn moves 0.4 degrees a draw, which reads as smooth.")]
    public float driftRenderRate = 30f;
    [Tooltip("Draws per second while a finger is on it, so it keeps up with the finger.")]
    public float heldRenderRate = 60f;

    [Header("Quality")]
    [Tooltip("The texture's longest side in pixels. The texture is sized to the view's pixels over the pipeline's render " +
             "scale, then capped here: it costs 8 bytes a pixel with depth, plus the intermediate URP allocates " +
             "when render scale is below 1. Raise it if thin parts shimmer on a big screen.")]
    public int maxTextureSize = 1536;

    [Header("Drag")]
    [Tooltip("How far a swipe across the whole view turns it, in degrees. Per view WIDTH rather than per pixel, " +
             "so the same swipe turns it the same distance on a phone and on an iPad.")]
    public float degreesPerViewWidth = 260f;

    private const int NoPointer = int.MinValue;
    // Waiting this long before BUILDING a robot, so scrubbing down the picker doesn't build every one it
    // passes. A robot already built goes up at once.
    private const float SwapDelay = 0.15f;
    // See LateUpdate.
    private const float RenderSlack = 0.004f;
    // How quickly the tracked drag speed follows the finger, and how quickly it bleeds away while the
    // finger rests. Both per second, both 1-exp(-k*dt).
    private const float SpeedTracking = 20f;
    private const float RestingFingerDecay = 12f;
    private const int ResizeTolerance = 2;

    private RawImage view;
    private RenderTexture texture;
    private bool hasRobot;
    private bool renderPending = true;
    // A new texture is drawn on two consecutive frames and shown from the second. The first frame drawn in
    // a fresh session can come out with the wrong material colours — seen headlessly: a 654V v1 render with
    // its red gears grey and its battery lime green, correct on the very next draw and identical to a draw
    // with shader compilation forced synchronous. It costs one frame at launch, and again only when iOS
    // takes the texture away.
    private const int DrawsBeforeShowing = 2;
    private int drawsIntoTexture;
    private float nextRenderTime;
    private float renderedYaw = float.NaN;
    private int pointer = NoPointer;
    private float dragSpeed;
    private bool draggedThisFrame;
    private bool hasPendingEntry;
    private RobotModelCatalog.Entry pendingEntry;
    private float pendingAt;

    void OnEnable()
    {
        view = GetComponent<RawImage>();
        renderPending = true;
        if (catalog == null) return;
        catalog.SelectionChanged += OnSelectionChanged;
        // Read NOW, not only on the next change: the event can fire while this is hidden, and the
        // catalog's memory of what it last announced outlives a Play session in the editor. Applied on
        // the first LateUpdate, by which time every Awake in the scene has run.
        Queue(catalog.SelectedModel, immediately: true);
    }

    void OnDisable()
    {
        if (catalog != null) catalog.SelectionChanged -= OnSelectionChanged;
        if (stageCamera != null) stageCamera.enabled = false;
        // A finger that was down when the stage was hidden never sends its pointer-up here.
        if (pointer != NoPointer && stage != null) stage.Release(0f);
        pointer = NoPointer;
    }

    void OnDestroy() => ReleaseTexture();

    private void OnSelectionChanged(RobotModelCatalog.Entry entry) =>
        Queue(entry, immediately: !hasRobot || (stage != null && stage.IsBuilt(entry)));

    private void Queue(RobotModelCatalog.Entry entry, bool immediately)
    {
        pendingEntry = entry;
        hasPendingEntry = true;
        pendingAt = Time.unscaledTime + (immediately ? 0f : SwapDelay);
    }

    private void Show(RobotModelCatalog.Entry entry)
    {
        hasRobot = mode != StageMode.Off && stage != null && stage.Show(entry);
        if (fallbackMark != null) fallbackMark.SetActive(!hasRobot);
        renderPending = true;
        ShowCaption(entry);
    }

    private void ShowCaption(RobotModelCatalog.Entry entry)
    {
        if (nameLabel != null) nameLabel.text = entry != null ? entry.displayName : string.Empty;
        if (mechanismsLabel == null) return;
        string line = entry != null ? MechanismNames.Line(entry.mechanisms) : string.Empty;
        mechanismsLabel.text = line;
        // Hidden rather than left blank, so the caption's layout centres the name in the band instead of
        // leaving it perched over an empty line.
        mechanismsLabel.gameObject.SetActive(line.Length > 0);
    }

    void LateUpdate()
    {
        if (hasPendingEntry && Time.unscaledTime >= pendingAt)
        {
            hasPendingEntry = false;
            Show(pendingEntry);
        }
        if (stage == null || stageCamera == null) return;

        float dt = Time.unscaledDeltaTime;
        if (pointer != NoPointer)
        {
            // OnDrag only arrives on frames the finger MOVES. A finger that stops and then lifts must not
            // fling at the speed it had before it stopped, so every still frame bleeds the speed away.
            if (!draggedThisFrame) dragSpeed *= Mathf.Exp(-RestingFingerDecay * dt);
            draggedThisFrame = false;
        }
        if (mode == StageMode.Drift) stage.Tick(dt);

        bool render = false;
        if (hasRobot && EnsureTexture())
        {
            float now = Time.unscaledTime;
            // The slack lets a draw that falls due on this frame land on it. At 60 fps a 30 Hz interval
            // ends within float noise of every second frame, and missing by a hair would push it to every
            // THIRD frame — 20 Hz, which reads as steppy.
            bool due = now + RenderSlack >= nextRenderTime;
            bool moved = !Mathf.Approximately(stage.Yaw, renderedYaw);
            render = renderPending || (mode == StageMode.Drift && due && moved);
            if (render)
            {
                float rate = pointer != NoPointer ? heldRenderRate : driftRenderRate;
                nextRenderTime = now + 1f / Mathf.Max(1f, rate);
                stage.ApplyYaw();
                renderedYaw = stage.Yaw;
                drawsIntoTexture++;
                renderPending = drawsIntoTexture < DrawsBeforeShowing; // see DrawsBeforeShowing
            }
        }

        // Only the frames that draw turn the camera on. A camera left off costs nothing — URP never sees
        // it — and the texture keeps its last picture, which is what the RawImage goes on showing.
        stageCamera.enabled = render;
        bool visible = hasRobot && texture != null && drawsIntoTexture >= DrawsBeforeShowing;
        if (view.enabled != visible) view.enabled = visible;
    }

    // True when there is a texture the camera can draw into this frame.
    private bool EnsureTexture()
    {
        Vector2 pixels = ((RectTransform)transform).rect.size * CanvasScale();
        // Before the first layout pass the view has no size, and URP skips a camera whose target has
        // none — with a warning — so don't hand it one.
        if (pixels.x < 16f || pixels.y < 16f) return false;

        // The view's pixels over the pipeline's render scale: URP draws into a texture target at render
        // scale too (0.8 on the phone tier), so a texture sized to the screen would come out soft on a
        // device and sharp in the editor. Then capped — see maxTextureSize.
        float longest = Mathf.Max(pixels.x, pixels.y);
        float fit = Mathf.Min(longest / RenderScale(), maxTextureSize) / longest;
        int width = Mathf.Max(16, Mathf.RoundToInt(pixels.x * fit));
        int height = Mathf.Max(16, Mathf.RoundToInt(pixels.y * fit));

        if (texture != null && Mathf.Abs(texture.width - width) <= ResizeTolerance &&
            Mathf.Abs(texture.height - height) <= ResizeTolerance)
        {
            if (texture.IsCreated()) return true;
            // iOS releases render textures when the app goes to the background. Recreate it and draw
            // again, or the stage comes back as a black rectangle.
            texture.Create();
            drawsIntoTexture = 0;
            renderPending = true;
            return true;
        }

        ReleaseTexture();
        texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "RobotStage",
            // MSAA would be wasted: URP only honours a target's samples when the pipeline asset has MSAA
            // on, and the mobile asset has it off.
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        texture.Create();
        stageCamera.targetTexture = texture;
        stageCamera.ResetAspect();
        view.texture = texture;
        stage.SetAspect((float)width / height);
        drawsIntoTexture = 0;
        renderPending = true;
        return true;
    }

    private void ReleaseTexture()
    {
        if (texture == null) return;
        if (stageCamera != null && stageCamera.targetTexture == texture) stageCamera.targetTexture = null;
        if (view != null && view.texture == texture) view.texture = null;
        texture.Release();
        Destroy(texture);
        texture = null;
    }

    private float CanvasScale()
    {
        Canvas canvas = view != null ? view.canvas : null;
        return canvas != null ? canvas.rootCanvas.scaleFactor : 1f;
    }

    // Read from the pipeline asset rather than assumed: 0.8 on the phone tier, 1 in the editor.
    private static float RenderScale()
    {
        UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
        return Mathf.Clamp(asset != null ? asset.renderScale : 1f, 0.25f, 2f);
    }

    // --- The finger ---

    // The drag starts the moment the finger moves, rather than after the EventSystem's dead zone: a
    // turntable that ignores the first few pixels feels stuck.
    public void OnInitializePotentialDrag(PointerEventData eventData) => eventData.useDragThreshold = false;

    public void OnPointerDown(PointerEventData eventData)
    {
        // One finger turns it. A second is ignored rather than fighting the first for the angle.
        if (pointer != NoPointer || !hasRobot || mode != StageMode.Drift || stage == null) return;
        pointer = eventData.pointerId;
        dragSpeed = 0f;
        draggedThisFrame = false;
        stage.Grab();
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (eventData.pointerId != pointer || stage == null) return;

        float widthPixels = ((RectTransform)transform).rect.width * CanvasScale();
        float degrees = eventData.delta.x / Mathf.Max(1f, widthPixels) * degreesPerViewWidth;
        stage.Turn(degrees);

        // Smoothed over a few frames: one frame's delta is noisy under a finger, and whatever this reads
        // at the moment the finger lifts becomes the flick.
        float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
        dragSpeed = Mathf.Lerp(dragSpeed, degrees / dt, 1f - Mathf.Exp(-SpeedTracking * dt));
        draggedThisFrame = true;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (eventData.pointerId != pointer || stage == null) return;
        pointer = NoPointer;
        stage.Release(dragSpeed);
    }
}
