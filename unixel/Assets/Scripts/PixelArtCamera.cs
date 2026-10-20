// =====================================================================================================
//  PixelArtCamera.cs  -  pixel-perfect 3D pixel art for Unity's Universal Render Pipeline (URP)
// =====================================================================================================
//
//  WHAT THIS SCRIPT DOES (the 10-second version)
//  ---------------------------------------------
//  Instead of drawing the 3D scene at your monitor's resolution, we draw it into a TINY image
//  (e.g. 640x360), then stretch that tiny image across the screen with no blurring. Every pixel of
//  the tiny image becomes a crisp square block on screen -> "pixel art". On top of that we add a
//  trick that keeps the pixels perfectly still while the camera moves, yet still lets motion look
//  smooth. This is the technique popularised by t3ssel8r and re-implemented in Godot by David Holland
//  (https://www.davidhol.land/articles/3d-pixel-art-rendering/).
//
//
//  GLOSSARY  (read this first if rendering terms are new to you)
//  --------------------------------------------------------------
//  Pixel
//      One dot on your physical screen.
//
//  Texel ("texture pixel")
//      One pixel of OUR LOW-RES image. In this project one texel = a block of NxN screen pixels
//      (N is the integer upscale factor, e.g. 3 when a 640x360 image is shown on a 1920x1080 screen).
//      We use "texel" whenever we mean "a pixel of the low-res image" to avoid confusion.
//
//  Orthographic projection
//      A camera mode with NO perspective: parallel lines stay parallel and an object has the same
//      size no matter how far away it is. Think of an architectural blueprint or an isometric
//      game. It matters here because with an orthographic camera one texel always covers exactly
//      the same amount of WORLD space (a fixed "texel size" in world units). With a perspective
//      camera that size would change with distance, so a stable pixel grid would be impossible.
//
//  RenderTexture
//      An image that lives on the GPU which a camera can draw INTO instead of drawing to the
//      screen. We make a small one, let our camera render the scene into it, then display it.
//
//  Point filtering (a.k.a. "nearest neighbour")
//      How the GPU picks a colour when an image is shown larger than it really is.
//        - Bilinear (default): blends neighbouring texels -> smooth but BLURRY.
//        - Point: copies the single nearest texel -> hard, square, crisp pixels.   <-- we want this
//
//  Integer scaling
//      Upscaling by a whole number (2x, 3x, 4x ...). Every texel becomes exactly the same size.
//      With a fractional scale (e.g. 2.5x) some texels would be 2 screen pixels wide and others 3,
//      which looks uneven. If the window doesn't divide evenly we add black bars ("letterbox").
//
//  Pixel creep / pixel swimming / shimmering
//      When a 3D camera moves by tiny amounts, which texel a given surface point lands in keeps
//      changing, so pixels appear to crawl across surfaces. A hand-drawn sprite never does this.
//      Removing it is the main job of this script.
//
//  Snapping
//      Forcing the camera position onto a grid whose cell size equals exactly one texel (in world
//      units). When the camera only ever sits on grid points, a given world point always lands
//      on the same texel -> no creep. Downside: movement becomes steppy (jumps one texel at a time).
//
//  Sub-texel offset ("snap error")
//      How far (0 to +-0.5 texel) the snapped camera is from where the camera REALLY wanted to be.
//      We throw this information away when snapping, so instead we measure it and cancel it out
//      later, when displaying the image (see UV below). This restores smooth motion.
//
//  Logical position vs. visual (snapped) position
//      A moving object really lives at a smooth "logical" position (physics, input, AI use this).
//      What we DRAW is a copy of that position snapped to the texel grid, so the object's pixels
//      never crawl. Keeping the two separate is the same idea as pivot vs. camera.
//
//  UV coordinates
//      Coordinates used to look up a position inside an image. (0,0) = bottom-left corner,
//      (1,1) = top-right corner, regardless of the image's size in texels. "Shifting the UVs"
//      means "reading from a slightly different place in the image", which visually slides the image.
//
//  uvRect (RawImage.uvRect)
//      A UI setting that says "show only this rectangle of the texture": (x, y, width, height) in
//      UV space. Changing x/y slides the visible window across the texture = sliding the image.
//
//  Camera space (right / up / forward)
//      The camera's own three axes: transform.right (screen-left/right), transform.up (screen
//      up/down), transform.forward (into the screen). We snap along right and up only.
//
//  Dot product (Vector3.Dot)
//      Used here to ask "how far along this axis is this point?". Dot(position, cam.right) is the
//      position's horizontal coordinate as seen by the camera. It is how we convert a world position
//      into camera-space x and y so we can snap them.
//
//  Clear flags
//      What a camera does to the target before drawing: wipe it to a solid colour, to a skybox, or
//      do nothing.
//
//  Culling mask
//      A bit-mask of Unity layers a camera is allowed to see. A mask of 0 = sees nothing.
//
//  Back buffer
//      The image that is actually sent to your monitor. If no camera ever clears it, it contains
//      leftover/undefined data (flickering garbage in builds).
//
//  MSAA / anti-aliasing (AA)
//      Techniques that smooth jagged edges by blending. Great for realistic games, but they
//      deliberately blur edges, which destroys crisp pixel art -> we disable them everywhere.
//
//  Depth buffer / depth texture
//      A hidden image storing "how far from the camera" every pixel is. Used by the GPU to decide
//      which object is in front. URP can also expose it as a texture for effects like outlines.
//
//  Near / far clip plane
//      Anything closer than "near" or farther than "far" from the camera is not drawn. For an
//      orthographic camera they simply define the depth slab that is visible.
//
//  Render pass
//      One step in building a frame, e.g. "draw all opaque objects", "draw shadows", "draw
//      outlines". URP runs a list of passes in order for each camera.
//
//  Renderer Feature / ScriptableRenderPass  (URP extension points - NOT used in this file)
//      The official way to inject your OWN pass into URP's list (e.g. an outline pass). See the
//      "IDEAS FOR EXTENDING" section at the bottom for where they fit in.
//
//  Full-screen pass / post-processing / blit
//      A pass that runs a shader on every pixel of the finished image (e.g. outlines, colour
//      grading). "Blit" = copy one texture into another, optionally through a shader.
//
//  Canvas (Screen Space - Overlay)
//      Unity's UI system. An Overlay canvas is drawn on top of everything at the very end, directly
//      in screen pixels. We use it simply as a convenient way to draw our low-res image full-screen.
//
//
//  WHAT HAPPENS EVERY FRAME  (timeline)
//  ------------------------------------
//    1. Your game code moves the PIVOT smoothly (any speed, any amount, no snapping needed).
//    2. [This script, LateUpdate] compute where the camera should sit, snap it to the texel grid,
//       and remember the snap error.
//    3. [Unity/URP] The camera renders the scene into the small RenderTexture.
//    4. [This script] Slide the displayed image by the snap error so it looks like the camera
//       was NOT snapped.
//    5. [Unity UI] The Overlay canvas draws that texture full-screen, point filtered, integer scaled.
//
//
//  WORKED EXAMPLE OF THE SNAP MATH  (pixelsPerUnit = 32 -> one texel = 1/32 = 0.03125 world units)
//  ----------------------------------------------------------------------------------------------
//    The camera wants to be at camera-space x = 1.0200
//    Nearest grid point:   round(1.0200 / 0.03125) * 0.03125 = round(32.64) * 0.03125 = 33 * 0.03125 = 1.03125
//    Snap error:           (1.0200 - 1.03125) / 0.03125 = -0.36 texels
//    The camera actually sits 0.36 texel to the RIGHT of where it wanted to be, so the world looks
//    0.36 texel too far LEFT. We fix that by sliding the displayed image 0.36 texel to the RIGHT.
//    Next frame the camera might want x = 1.0210 -> same grid point, error -0.33 -> image slides
//    smoothly. At x = 1.0470 the nearest grid point becomes 34 * 0.03125 and the error flips sign
//    to about +0.46: the camera "jumps" one texel and the image shifts back by almost a texel,
//    so on screen the two effects cancel and nothing visibly jumps - EXCEPT that the texels stay
//    glued to the world, which is exactly what we want.
//
// =====================================================================================================

using UnityEngine;
using UnityEngine.UI;                 // RawImage / Image used to show the low-res texture
using UnityEngine.Rendering.Universal; // URP-specific camera settings (UniversalAdditionalCameraData)

/// <summary>
/// Pixel-perfect 3D pixel-art camera for URP.
///
///  1. Renders an ORTHOGRAPHIC camera into a small, point-filtered RenderTexture.
///  2. Snaps the camera to a texel-sized grid aligned with the camera's own right/up axes
///     (removes "pixel creep" / swimming when the camera moves).
///  3. Measures the snap error (always within +-0.5 texel) and shifts the upscaled image back
///     by that amount, so camera motion still looks smooth instead of stepping texel by texel.
///  4. Shows the texture full-screen with point filtering at an integer scale (letterboxed).
///
/// SETUP: put this on your Camera. Move the PIVOT (not the camera) to move the view.
/// The camera's ROTATION is yours to set (e.g. pitch 30, yaw 45) - it is never modified.
/// </summary>
[RequireComponent(typeof(Camera))]   // this script is useless without a Camera on the same object
[DisallowMultipleComponent]          // two of these on one object would fight over the camera
[DefaultExecutionOrder(1000)]        // Run LATE among all scripts so we see the pivot's final position this frame
public class PixelArtCamera : MonoBehaviour
{
    // ------------------------------------------------------------------------------------------
    //  INSPECTOR SETTINGS
    // ------------------------------------------------------------------------------------------

    [Header("Low-res target (visible area, in texels)")]
    /// <summary>Width of the visible low-res image in texels. Wider = more world visible AND finer detail.</summary>
    [Min(16)] public int targetWidth = 640;
    /// <summary>Height of the visible low-res image in texels. 640x360 is 16:9 and scales 3x to 1080p, 6x to 4K.</summary>
    [Min(16)] public int targetHeight = 360;

    [Header("Scale")]
    [Tooltip("Texels per world unit. Keep constant (or change only in whole steps), otherwise the pixel grid shimmers.")]
    /// <summary>
    /// How many texels fit in one Unity world unit. With 32, a 1x1x1 cube is 32 texels wide.
    /// This is the "zoom" of the game. It also defines the snap-grid size (1 / pixelsPerUnit).
    /// Changing it smoothly (e.g. animated zoom) makes the pixel grid constantly resize and shimmer,
    /// so only change it in discrete steps.
    /// </summary>
    [Min(1f)] public float pixelsPerUnit = 32f;

    [Header("Follow")]
    [Tooltip("Smooth, UNSNAPPED point the camera looks at. Move this, not the camera. Auto-created if empty.")]
    /// <summary>
    /// The point in the world the camera is "looking at". YOU move this freely and smoothly.
    /// We cannot let you move the camera directly because every frame we overwrite the camera's
    /// position with a SNAPPED version of it. If we didn't keep the original unsnapped target
    /// somewhere, the snap error would be lost and motion would become steppy.
    /// </summary>
    public Transform pivot;

    [Tooltip("Orthographic: only affects near/far clipping. Keep it larger than half the depth of your scene.")]
    /// <summary>
    /// How far BEHIND the pivot (along the view direction) the camera is parked. With an orthographic
    /// camera this does NOT change how big things look - only which depth slab is inside the
    /// near/far clip planes. Too small and tall objects/terrain get cut off.
    /// </summary>
    public float distance = 50f;

    [Header("Presentation")]
    [Tooltip("Integer scale = every texel is exactly NxN screen pixels (letterboxed). Off = fill as much as possible (uneven pixels).")]
    public bool integerScale = true;

    [Tooltip("Compensate the snap error when upscaling. Turn off to see raw snapped (steppy) movement.")]
    /// <summary>Great for learning: toggle in Play mode to compare "steppy but stable" vs "smooth and stable".</summary>
    public bool subTexelSmoothing = true;

    /// <summary>Colour of the bars around the image when the window doesn't match the aspect ratio / integer scale.</summary>
    public Color letterboxColor = Color.black;

    // ------------------------------------------------------------------------------------------
    //  INTERNAL STATE
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Extra texels rendered around the visible area on EVERY side.
    /// Why: to cancel the snap error we slide the visible window across the texture by up to +-0.5 texel.
    /// If the texture were exactly the visible size, sliding would read OUTSIDE the image and show
    /// a smeared edge. One spare texel of border guarantees the window always stays inside the data.
    /// </summary>
    const int Pad = 1;

    Camera cam;               // the camera on this GameObject; renders the actual 3D scene
    Camera displayCam;        // an empty helper camera that only clears the real screen (see BuildDisplayCamera)
    RenderTexture rt;         // the low-res image the 3D camera draws into
    RawImage view;            // UI element that shows 'rt' on screen
    RectTransform viewRect;   // its position/size on screen, in screen pixels
    Image letterbox;          // solid-colour full-screen UI image behind the view (the "bars")
    GameObject canvasGO;      // root of the UI we create at runtime (so we can destroy it in OnDestroy)
    int lastScreenW, lastScreenH; // remembered so we only re-layout when the window is resized

    /// <summary>
    /// Handle to a GLOBAL shader variable named "_PixelArtParams". Shader.PropertyToID turns the string
    /// into a fast integer id once (string lookups every frame would be slow).
    /// </summary>
    static readonly int PixelArtParamsId = Shader.PropertyToID("_PixelArtParams");

    // ------------------------------------------------------------------------------------------
    //  PUBLIC READ-ONLY INFO  (hooks for your own scripts / features)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Current snap error in texels (x = along camera right, y = along camera up), range [-0.5, 0.5].
    /// Useful if you want OTHER things to cancel the snap too (see "IDEAS FOR EXTENDING").
    /// </summary>
    public Vector2 SubTexelOffset { get; private set; }

    /// <summary>Size of one texel in WORLD units (e.g. 1/32 = 0.03125). Also the snap-grid cell size.</summary>
    public float TexelWorldSize => 1f / pixelsPerUnit;

    /// <summary>The low-res render target, in case another script wants to read or copy it.</summary>
    public RenderTexture LowResTarget => rt;

    // ------------------------------------------------------------------------------------------
    //  SETUP  (runs once when the scene starts)
    // ------------------------------------------------------------------------------------------

    void Awake()
    {
        cam = GetComponent<Camera>();

        // Orthographic = no perspective (see glossary). Required for the texel grid to have a constant world size.
        cam.orthographic = true;

        // Disable every form of anti-aliasing on this camera: it would blur the hard pixel edges.
        // (The URP asset's own MSAA setting should ALSO be Disabled - see your pipeline asset.)
        cam.allowMSAA = false;
        cam.GetUniversalAdditionalCameraData().antialiasing = AntialiasingMode.None;

        // Near/far clip planes define the visible depth slab. The camera sits 'distance' behind the pivot,
        // so far = 2 * distance covers everything from the camera to equally far behind the pivot.
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = distance * 2f;

        // Safety net: if you forgot to assign a pivot, create one directly in front of the camera.
        if (pivot == null)
        {
            var go = new GameObject("PixelCameraPivot");
            go.transform.position = transform.position + transform.forward * distance;
            pivot = go.transform;
        }

        // ORDER MATTERS: BuildRenderTexture needs 'view' (created in BuildDisplay) to assign the texture to it.
        BuildDisplayCamera();
        BuildDisplay();
        BuildRenderTexture();
        UpdateLayout();
    }

    /// <summary>
    /// Our main camera renders into a texture, so NOTHING is rendering to the actual screen.
    /// Unity's Game view then warns "Display 1 No cameras rendering", and in a build the back buffer is never
    /// cleared. This empty camera clears the screen to the letterbox color and fixes both.
    /// (The UI canvas is not a camera, so it does not count.)
    /// </summary>
    void BuildDisplayCamera()
    {
        var go = new GameObject("PixelArtDisplayCamera");
        go.transform.SetParent(transform, false); // child of this object -> destroyed together with it

        displayCam = go.AddComponent<Camera>();
        displayCam.cullingMask = 0;                          // sees no layers = draws nothing
        displayCam.clearFlags = CameraClearFlags.SolidColor; // ...but still wipes the screen to a colour
        displayCam.backgroundColor = letterboxColor;
        displayCam.depth = -100;                             // draw order among cameras; irrelevant but keeps it out of the way
        displayCam.allowMSAA = false;
        displayCam.allowHDR = false;

        // Turn off everything URP would otherwise spend time on for this do-nothing camera.
        var data = displayCam.GetUniversalAdditionalCameraData();
        data.renderShadows = false;
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
    }

    /// <summary>Clean up GPU memory and the runtime-created UI when this component is destroyed.</summary>
    void OnDestroy()
    {
        if (cam != null) cam.targetTexture = null; // detach before releasing the texture
        if (rt != null) { rt.Release(); Destroy(rt); } // RenderTextures are GPU resources: Release frees the GPU memory
        if (canvasGO != null) Destroy(canvasGO);
    }

    // ------------------------------------------------------------------------------------------
    //  PER-FRAME LOGIC  (the heart of the technique)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// LateUpdate runs after every script's Update(). We use it because the pivot must already have
    /// moved this frame; otherwise the camera would lag one frame behind its target.
    /// </summary>
    void LateUpdate()
    {
        // Window resized (or fullscreen toggled)? Recompute the integer scale and centring.
        if (Screen.width != lastScreenW || Screen.height != lastScreenH) UpdateLayout();

        // Let the letterbox colour be changed at runtime.
        if (letterbox != null) letterbox.color = letterboxColor;
        if (displayCam != null) displayCam.backgroundColor = letterboxColor;

        float texel = TexelWorldSize; // size of one texel in world units

        // Orthographic size = HALF the vertical world-space height the camera sees.
        // The render texture is (targetHeight + 2*Pad) texels tall and each texel is 'texel' world units.
        // => one texel on screen always equals exactly one texel-sized chunk of world.
        cam.orthographicSize = rt.height * texel * 0.5f;

        // Step 1: where the camera WOULD be if there were no snapping:
        // exactly 'distance' units behind the pivot, looking at it.
        // (We only ever READ the camera's rotation - it is yours to set.)
        Vector3 ideal = pivot.position - transform.forward * distance;

        // Step 2: snap that position onto the texel grid and get back the snap error.
        // The maths is explained in detail in SnapToTexelGrid below.
        transform.position = SnapToTexelGrid(ideal, out Vector2 errTexels);

        // Step 3: remember the error, in TEXELS (always within -0.5 .. +0.5).
        // The snapped camera sees the world shifted by this amount relative to the ideal camera.
        // Sampling the texture shifted by +error moves the picture by -error, cancelling it out.
        SubTexelOffset = errTexels;

        // Step 4: slide the displayed picture by that error (or not, if smoothing is disabled for comparison).
        ApplyUV(subTexelSmoothing ? SubTexelOffset : Vector2.zero);
    }

    /// <summary>
    /// Snaps a WORLD position onto the texel grid. This is THE snapping function: the camera uses it
    /// on itself, and moving objects use it (via PixelSnapObject) so that their pixels stay stable.
    ///
    /// How it works:
    ///   1. Convert the position into camera-space x (along right) and y (along up) with dot products.
    ///   2. Round each to the nearest multiple of one texel (TexelWorldSize). THIS is the snap.
    ///      The grid is anchored to the world origin, so the same world point always lands on the same
    ///      texel phase no matter where you are. We do not snap along 'forward': in an orthographic
    ///      view, moving toward/away from the scene doesn't shift anything sideways, so it cannot cause
    ///      pixel creep.
    ///   3. Move the position by the tiny corrections (sx - x, sy - y) along the matching axes.
    ///
    /// Only the camera's ROTATION and texel size are used, never its position, so it can be called
    /// at any time in the frame.
    /// </summary>
    /// <param name="worldPos">The smooth, unsnapped world position.</param>
    /// <param name="errorTexels">Output: (unsnapped - snapped) along camera right/up, in texels (-0.5 .. +0.5).</param>
    /// <returns>The snapped world position.</returns>
    public Vector3 SnapToTexelGrid(Vector3 worldPos, out Vector2 errorTexels)
    {
        float texel = TexelWorldSize;
        Vector3 right = transform.right; // screen-right in world space
        Vector3 up = transform.up;       // screen-up in world space

        float x = Vector3.Dot(worldPos, right);
        float y = Vector3.Dot(worldPos, up);
        float sx = Mathf.Round(x / texel) * texel;
        float sy = Mathf.Round(y / texel) * texel;

        errorTexels = new Vector2(x - sx, y - sy) / texel;
        return worldPos + right * (sx - x) + up * (sy - y);
    }

    /// <summary>Convenience overload when you don't need the error.</summary>
    public Vector3 SnapToTexelGrid(Vector3 worldPos) => SnapToTexelGrid(worldPos, out _);

    /// <summary>
    /// Chooses which rectangle of the render texture is shown, in UV space (0..1).
    ///   - Start at the border (Pad texels in) so we show the central targetWidth x targetHeight area...
    ///   - ...then slide by 'errTexels' to cancel the camera snap.
    /// Dividing by rt.width / rt.height converts texel counts into the 0..1 UV range.
    /// </summary>
    void ApplyUV(Vector2 errTexels)
    {
        view.uvRect = new Rect(
            (Pad + errTexels.x) / rt.width,        // left edge of the visible window
            (Pad + errTexels.y) / rt.height,       // bottom edge of the visible window
            (float)targetWidth / rt.width,         // window width  (excludes the padding)
            (float)targetHeight / rt.height);      // window height (excludes the padding)
    }

    // ------------------------------------------------------------------------------------------
    //  RESOURCE CREATION
    // ------------------------------------------------------------------------------------------

    /// <summary>Creates the small image the 3D camera draws into and wires it up.</summary>
    void BuildRenderTexture()
    {
        // If one already exists (e.g. you call this again after changing the resolution), free it first.
        if (rt != null) { cam.targetTexture = null; rt.Release(); Destroy(rt); }

        // Size = visible area + 1 texel of padding on every side (hence 2 * Pad).
        // The "24" is the DEPTH BUFFER precision in bits: the render texture needs a depth buffer so
        // that nearer objects correctly hide farther ones.
        rt = new RenderTexture(targetWidth + 2 * Pad, targetHeight + 2 * Pad, 24)
        {
            name = "PixelArt_LowRes",
            filterMode = FilterMode.Point,       // the whole point: no blending when enlarged
            wrapMode = TextureWrapMode.Clamp,    // never repeat/wrap the image at its edges
            antiAliasing = 1,                    // 1 = no MSAA
            useMipMap = false,                   // mip-maps are pre-shrunk copies, which would blur; not wanted
            autoGenerateMips = false
        };
        rt.Create(); // actually allocates it on the GPU

        // From now on the camera draws into 'rt' instead of the screen,
        // and the UI element shows 'rt'.
        cam.targetTexture = rt;
        view.texture = rt;

        // Publish the texture size to ALL shaders as a global variable. Post-processing shaders
        // (e.g. an outline effect) need it to step exactly one texel in a given direction.
        // Layout: x = width, y = height, z = 1/width, w = 1/height.
        Shader.SetGlobalVector(PixelArtParamsId,
            new Vector4(rt.width, rt.height, 1f / rt.width, 1f / rt.height));
    }

    /// <summary>
    /// Builds, at runtime, the little UI hierarchy that displays the low-res image:
    ///
    ///   PixelArtCanvas (Canvas, Screen Space - Overlay, drawn behind other HUD canvases)
    ///    +- Letterbox     (Image,    stretched over the whole screen, solid colour)
    ///    +- PixelArtView  (RawImage, shows the render texture, sized by UpdateLayout)
    ///
    /// We use the UI system as a simple, version-independent way to draw one textured rectangle.
    /// (A more advanced alternative is a custom Renderer Feature; see the end of this file.)
    /// </summary>
    void BuildDisplay()
    {
        canvasGO = new GameObject("PixelArtCanvas", typeof(Canvas));
        var canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; // drawn on top of the 3D view, in raw screen pixels
        canvas.sortingOrder = -1000; // very low = behind any normal HUD canvas you add later

        // Background bars. 'raycastTarget = false' stops it from swallowing mouse clicks meant for your HUD.
        var bgGO = new GameObject("Letterbox", typeof(RectTransform), typeof(Image));
        bgGO.transform.SetParent(canvasGO.transform, false);
        letterbox = bgGO.GetComponent<Image>();
        letterbox.color = letterboxColor;
        letterbox.raycastTarget = false;
        var bgRect = (RectTransform)bgGO.transform;
        bgRect.anchorMin = Vector2.zero;   // stretch to all four corners of the canvas
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = bgRect.offsetMax = Vector2.zero;

        // The picture itself.
        var viewGO = new GameObject("PixelArtView", typeof(RectTransform), typeof(RawImage));
        viewGO.transform.SetParent(canvasGO.transform, false);
        view = viewGO.GetComponent<RawImage>();
        view.raycastTarget = false;
        viewRect = (RectTransform)viewGO.transform;
        // Anchor AND pivot at the bottom-left corner so that anchoredPosition / sizeDelta are plain
        // pixel coordinates measured from the bottom-left of the screen (easy to reason about).
        viewRect.anchorMin = viewRect.anchorMax = viewRect.pivot = Vector2.zero;
    }

    /// <summary>
    /// Works out how big to draw the image on screen and where, whenever the window size changes.
    /// </summary>
    void UpdateLayout()
    {
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;

        // How many times could the low-res image be enlarged before it touches the screen edges?
        float fit = Mathf.Min((float)lastScreenW / targetWidth, (float)lastScreenH / targetHeight);

        // Integer mode: round DOWN to a whole number (minimum 1) so every texel is exactly NxN screen pixels.
        // Otherwise use the exact fractional fit (fills more of the screen but gives uneven pixels).
        float scale = integerScale ? Mathf.Max(1f, Mathf.Floor(fit)) : fit;

        float w = targetWidth * scale;
        float h = targetHeight * scale;

        viewRect.sizeDelta = new Vector2(w, h);

        // Centre the image. Mathf.Floor keeps the position on a WHOLE screen pixel; a half-pixel offset
        // would make the GPU resample the picture and blur it.
        viewRect.anchoredPosition = new Vector2(
            Mathf.Floor((lastScreenW - w) * 0.5f),
            Mathf.Floor((lastScreenH - h) * 0.5f));
    }

    // =================================================================================================
    //  COMMON PITFALLS
    // =================================================================================================
    //   * Moving objects (player, enemies) that are NOT snapped have crawling/flickering edge pixels, because
    //     they are rasterised at a different sub-texel offset every frame. Add PixelSnapObject to their
    //     visual child. Rotating objects can't be snapped; their pixels will still change as they turn.
    //   * Moving the camera object directly  -> it gets overwritten every frame. Move the PIVOT instead.
    //   * Changing pixelsPerUnit continuously (smooth zoom) -> the grid resizes each frame and shimmers.
    //     Zoom in whole steps (e.g. 16 -> 32 -> 64), or change the render texture size instead.
    //   * Rotating the camera is fine, but do it BEFORE this script's LateUpdate (it reads right/up
    //     each frame). Rotating smoothly will still cause some shimmering: rotation can't be snapped
    //     away like translation can, so prefer fixed angles or discrete rotation steps.
    //   * Anti-aliasing anywhere (URP asset MSAA, camera AA, post-process AA) blurs the pixels.
    //   * Game view zoom ("Scale" slider) and non-1.0 Retina/DPI scaling break integer scaling in the editor.
    //   * Textures: set Filter Mode = Point, no mip-maps, no compression, or your pixel art looks soft.
    //
    //
    // =================================================================================================
    //  IDEAS FOR EXTENDING  (good first features to build)
    // =================================================================================================
    //   1. SNAP MOVING OBJECTS TOO   (implemented: see PixelSnapObject.cs and SnapToTexelGrid above)
    //      Next step: a smoothed camera follow, or snapping object ROTATION in discrete steps.
    //
    //   2. CANCEL THE SNAP IN SHADERS
    //      SubTexelOffset is public so other systems can use it. A custom full-screen effect that
    //      samples the low-res image (e.g. outlines drawn at high resolution) would need to apply the
    //      same offset to line up with the shifted picture.
    //
    //   3. OUTLINES (the signature look)
    //      Add a URP "Full Screen Pass Renderer Feature" (or write a ScriptableRendererFeature +
    //      ScriptableRenderPass). A RENDER PASS is just "run this shader over the whole image at this
    //      point in the frame". The shader reads the depth texture and a normals texture, compares each
    //      texel with its 4 neighbours (up/down/left/right) and paints an outline where they differ a lot.
    //      Because the camera already renders at low-res, the pass runs at low-res too, so lines are
    //      automatically exactly one texel thick. Use the _PixelArtParams global (published above)
    //      to compute "one texel" in UV units: float2 texel = _PixelArtParams.zw;
    //
    //   4. DISCRETE ZOOM
    //      Expose a method that switches pixelsPerUnit between preset values (e.g. 24, 32, 48) and
    //      call it on a key press. Never interpolate between them.
    //
    //   5. SMOOTH-ISH UPSCALING FOR ARBITRARY WINDOW SIZES
    //      Disable integerScale and replace the plain point sampling with a "sharp bilinear" shader
    //      (point-sample at the texel centre, blend only along the texel borders). This avoids uneven
    //      pixel widths at fractional scales while staying crisp.
    //
    //   6. REPLACE THE UI DISPLAY WITH A BLIT
    //      The RawImage trick is easy and works on every URP version. For maximum control (colour
    //      palettes, dithering, CRT effects) draw the render texture yourself with a custom
    //      Renderer Feature that "blits" it to the screen through your own shader.
    //
    //   7. COLOUR PALETTE / LUT
    //      After the scene is rendered, remap every colour to a fixed palette using a look-up
    //      texture (LUT) in a full-screen pass for a retro limited-colour look.
    //
    //   8. PERFORMANCE
    //      The expensive part of a frame is normally shading millions of pixels. At 640x360 there are
    //      only ~230,000, so fill-rate is cheap; cost moves to geometry, shadows and full-screen passes.
    //      Merge several full-screen effects into one shader where possible.
    //
    // =================================================================================================
}