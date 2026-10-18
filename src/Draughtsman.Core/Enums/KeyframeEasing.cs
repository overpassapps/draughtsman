namespace Draughtsman.Core.Enums;

/// <summary>
/// How the camera travels to a keyframe: <see cref="Cut"/> jumps, the rest ease the progress of a smooth fly
/// (web/src/canvas/camera.ts). Absent on a keyframe means <see cref="EaseInOut"/>.
/// </summary>
public enum KeyframeEasing
{
    Cut,
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}
