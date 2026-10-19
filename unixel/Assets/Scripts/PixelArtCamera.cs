using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

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
[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)] // run after anything that moves the pivot
public class PixelArtCamera : MonoBehaviour
{
    [Header("Low-res target (visible area, in texels)")]
    [Min(16)] public int targetWidth = 640;
    [Min(16)] public int targetHeight = 360;

    [Header("Scale")]
    [Tooltip("Texels per world unit. Keep constant (or change only in whole steps), otherwise the pixel grid shimmers.")]
    [Min(1f)] public float pixelsPerUnit = 32f;

    [Header("Follow")]
    [Tooltip("Smooth, UNSNAPPED point the camera looks at. Move this, not the camera. Auto-created if empty.")]
    public Transform pivot;
    [Tooltip("Orthographic: only affects near/far clipping. Keep it larger than half the depth of your scene.")]
    public float distance = 50f;

    [Header("Presentation")]
    [Tooltip("Integer scale = every texel is exactly NxN screen pixels (letterboxed). Off = fill as much as possible (uneven pixels).")]
    public bool integerScale = true;
    [Tooltip("Compensate the snap error when upscaling. Turn off to see raw snapped (steppy) movement.")]
    public bool subTexelSmoothing = true;
    public Color letterboxColor = Color.black;

    /// <summary>One texel of padding on every side hides the sub-texel shift.</summary>
    const int Pad = 1;

    Camera cam;
    RenderTexture rt;
    RawImage view;
    RectTransform viewRect;
    Image letterbox;
    GameObject canvasGO;
    int lastScreenW, lastScreenH;

    static readonly int PixelArtParamsId = Shader.PropertyToID("_PixelArtParams");

    /// <summary>Current snap error in texels (x = camera right, y = camera up), range [-0.5, 0.5].</summary>
    public Vector2 SubTexelOffset { get; private set; }
    public float TexelWorldSize => 1f / pixelsPerUnit;
    public RenderTexture LowResTarget => rt;

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.orthographic = true;
        cam.allowMSAA = false;              // any AA smears pixels
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = distance * 2f;
        cam.GetUniversalAdditionalCameraData().antialiasing = AntialiasingMode.None;

        if (pivot == null)
        {
            var go = new GameObject("PixelCameraPivot");
            go.transform.position = transform.position + transform.forward * distance;
            pivot = go.transform;
        }

        BuildDisplayCamera();
        BuildDisplay();
        BuildRenderTexture();
        UpdateLayout();
    }

    /// <summary>
    /// Our main camera renders into a texture, so nothing is rendering to the actual screen.
    /// Unity's Game view then warns "No cameras rendering", and in a build the backbuffer is never
    /// cleared. This empty camera clears the screen to the letterbox color and fixes both.
    /// </summary>
    void BuildDisplayCamera()
    {
        var go = new GameObject("PixelArtDisplayCamera");
        go.transform.SetParent(transform, false);

        var dc = go.AddComponent<Camera>();
        dc.cullingMask = 0;                       // draws nothing
        dc.clearFlags = CameraClearFlags.SolidColor;
        dc.backgroundColor = letterboxColor;
        dc.depth = -100;
        dc.allowMSAA = false;
        dc.allowHDR = false;

        var data = dc.GetUniversalAdditionalCameraData();
        data.renderShadows = false;
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
    }

    void OnDestroy()
    {
        if (cam != null) cam.targetTexture = null;
        if (rt != null) { rt.Release(); Destroy(rt); }
        if (canvasGO != null) Destroy(canvasGO);
    }

    void LateUpdate()
    {
        if (Screen.width != lastScreenW || Screen.height != lastScreenH) UpdateLayout();
        if (letterbox != null) letterbox.color = letterboxColor;

        float texel = TexelWorldSize;

        // Ortho size is half the vertical world height of the render target (padding included).
        cam.orthographicSize = rt.height * texel * 0.5f;

        Transform t = transform;
        Vector3 right = t.right;
        Vector3 up = t.up;
        Vector3 forward = t.forward;

        // Where the camera WOULD be if we didn't snap.
        Vector3 ideal = pivot.position - forward * distance;

        // Snap in camera space. The grid is fixed to the world origin (projected on right/up),
        // so a given world point always lands on the same texel phase. Movement along
        // 'forward' is irrelevant for an orthographic camera.
        float x = Vector3.Dot(ideal, right);
        float y = Vector3.Dot(ideal, up);
        float sx = Mathf.Round(x / texel) * texel;
        float sy = Mathf.Round(y / texel) * texel;

        t.position = ideal + right * (sx - x) + up * (sy - y);

        // The snapped camera sees the world shifted by (x - sx, y - sy) relative to the ideal one.
        // Sampling the texture shifted by +error moves the image by -error, cancelling it out.
        SubTexelOffset = new Vector2(x - sx, y - sy) / texel;
        ApplyUV(subTexelSmoothing ? SubTexelOffset : Vector2.zero);
    }

    void ApplyUV(Vector2 errTexels)
    {
        view.uvRect = new Rect(
            (Pad + errTexels.x) / rt.width,
            (Pad + errTexels.y) / rt.height,
            (float)targetWidth / rt.width,
            (float)targetHeight / rt.height);
    }

    void BuildRenderTexture()
    {
        if (rt != null) { cam.targetTexture = null; rt.Release(); Destroy(rt); }

        rt = new RenderTexture(targetWidth + 2 * Pad, targetHeight + 2 * Pad, 24)
        {
            name = "PixelArt_LowRes",
            filterMode = FilterMode.Point,       // the whole point
            wrapMode = TextureWrapMode.Clamp,
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();

        cam.targetTexture = rt;
        view.texture = rt;

        // Handy for outline / post shaders later: (width, height, 1/width, 1/height).
        Shader.SetGlobalVector(PixelArtParamsId,
            new Vector4(rt.width, rt.height, 1f / rt.width, 1f / rt.height));
    }

    void BuildDisplay()
    {
        canvasGO = new GameObject("PixelArtCanvas", typeof(Canvas));
        var canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -1000; // behind any normal HUD canvas

        var bgGO = new GameObject("Letterbox", typeof(RectTransform), typeof(Image));
        bgGO.transform.SetParent(canvasGO.transform, false);
        letterbox = bgGO.GetComponent<Image>();
        letterbox.color = letterboxColor;
        letterbox.raycastTarget = false;
        var bgRect = (RectTransform)bgGO.transform;
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = bgRect.offsetMax = Vector2.zero;

        var viewGO = new GameObject("PixelArtView", typeof(RectTransform), typeof(RawImage));
        viewGO.transform.SetParent(canvasGO.transform, false);
        view = viewGO.GetComponent<RawImage>();
        view.raycastTarget = false;
        viewRect = (RectTransform)viewGO.transform;
        viewRect.anchorMin = viewRect.anchorMax = viewRect.pivot = Vector2.zero; // bottom-left, pixel units
    }

    void UpdateLayout()
    {
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;

        float fit = Mathf.Min((float)lastScreenW / targetWidth, (float)lastScreenH / targetHeight);
        float scale = integerScale ? Mathf.Max(1f, Mathf.Floor(fit)) : fit;

        float w = targetWidth * scale;
        float h = targetHeight * scale;

        viewRect.sizeDelta = new Vector2(w, h);
        // Floor to whole pixels so the image never straddles a screen-pixel boundary.
        viewRect.anchoredPosition = new Vector2(
            Mathf.Floor((lastScreenW - w) * 0.5f),
            Mathf.Floor((lastScreenH - h) * 0.5f));
    }
}