using UnityEngine;

/// <summary>
/// Slowly pans this transform back and forth. Put it on the camera PIVOT to check that
/// (a) there is no pixel creep/swimming and (b) motion is smooth rather than stepping.
/// Slow speeds reveal artefacts best. No input system required.
/// </summary>
public class PixelArtDebugPanner : MonoBehaviour
{
    public Vector3 axis = new Vector3(1f,0f,1f);
    public float amplitude = 2f;
    [Tooltip("Radians per second. Keep it low.")]
    public float speed = 0.25f;

    Vector3 start;

    void Start() => start = transform.position;

    void Update() =>
        transform.position = start + axis.normalized * (Mathf.Sin(Time.time * speed) * amplitude);
}
