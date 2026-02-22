namespace MightyMiniMouse.Gestures;

/// <summary>
/// Tracks in-progress gesture recognition state for a single input key.
/// </summary>
internal class GestureTracker
{
    public uint LastDownTimestamp { get; set; }
    public uint LastUpTimestamp { get; set; }
    public int PressCount { get; set; }
    public bool HoldFired { get; set; }
    public string DeviceId { get; set; } = "";

    public void Reset()
    {
        PressCount = 0;
        HoldFired = false;
        DeviceId = "";
    }
}

/// <summary>
/// Tracks in-progress state for sequence gesture recognition.
/// Each sequence gesture gets its own tracker.
/// </summary>
internal class SequenceTracker
{
    public int CurrentIndex { get; set; }
    public uint StartTimestamp { get; set; }
    public string DeviceId { get; set; } = "";

    public void Reset()
    {
        CurrentIndex = 0;
        StartTimestamp = 0;
        DeviceId = "";
    }
}
