namespace AmbiWin.Render;

/// <summary>Why the glow is hidden. Several can hold at once; the glow returns when none remain.</summary>
[Flags]
public enum SuspendReason
{
    None = 0,
    UserPause = 1,
    Fullscreen = 2,
    SessionLocked = 4,
    DisplayOff = 8,
}
