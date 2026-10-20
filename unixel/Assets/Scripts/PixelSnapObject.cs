using UnityEngine;

/// <summary>
/// Draws a moving object at a position snapped to the pixel-art texel grid, so its pixels stay
/// perfectly stable while it moves (no crawling, flickering or "shaking" edge pixels).
///
/// WHY IS THIS NEEDED?
///   The low-res image is a grid of texels. An object sitting at a smooth, arbitrary position is
///   rasterised (turned into texels) differently every frame: a texel on its edge is "covered" in
///   one frame and "not covered" in the next, so the edge pixels boil and flicker. Static scenery
///   doesn't have this problem because the camera is snapped. A moving object needs the same
///   treatment: snap where it is DRAWN to the texel grid. Then it always rasterises identically and
///   simply moves one whole texel at a time, exactly like a classic pixel-art sprite.
///
/// HIERARCHY (important):
///
///     Player              <- LOGICAL object: your movement script, collider, Rigidbody go here.
///      +- Visual          <- THIS script + the mesh/renderer go here. Its position is overwritten
///                            every frame with a snapped copy of where it "should" be.
///
///   The script needs a parent because it overwrites its own world position; the parent is the
///   unsnapped, smooth "truth" it reads from. Never put physics or movement logic on the Visual.
///
/// CAMERA FOLLOW:
///   Rigid, perfectly crisp follow: assign the VISUAL as PixelArtCamera.pivot. Camera and object
///   snap to the same grid point, so they step together and nothing wobbles on screen.
///   (Assigning the unsnapped Player instead gives a smooth world scroll with a slightly stepped
///   player - see the notes in the answer/README.)
///
/// LIMITATIONS:
///   * Rotation cannot be snapped. A rotating object's pixels still change as it turns.
///   * Only translation along the camera's right/up axes is snapped (depth does not matter for an
///     orthographic camera).
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(900)] // must run BEFORE PixelArtCamera (1000) so the camera sees our snapped position
public class PixelSnapObject : MonoBehaviour
{
    [Tooltip("The pixel-art camera whose texel grid to snap to. Found automatically if left empty.")]
    public PixelArtCamera pixelCamera;

    /// <summary>Where this object sits relative to its parent when the game starts (usually zero).</summary>
    Vector3 localOffset;

    void Awake()
    {
        if (pixelCamera == null) pixelCamera = FindFirstObjectByType<PixelArtCamera>();
        localOffset = transform.localPosition;

        if (transform.parent == null)
            Debug.LogWarning("PixelSnapObject needs a parent: put your movement script on the parent and " +
                             "this script on the visual child. Nothing will be snapped.", this);
    }

    /// <summary>
    /// LateUpdate runs after every Update(), i.e. after your movement code has moved the parent.
    /// </summary>
    void LateUpdate()
    {
        Transform parent = transform.parent;
        if (parent == null || pixelCamera == null) return;

        // Where this object WOULD be, following the (smooth, unsnapped) parent exactly.
        Vector3 logical = parent.TransformPoint(localOffset);

        // Draw it at the nearest texel-grid position instead.
        transform.position = pixelCamera.SnapToTexelGrid(logical);
    }
}