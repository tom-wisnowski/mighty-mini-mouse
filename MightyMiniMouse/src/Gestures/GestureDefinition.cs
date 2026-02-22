using MightyMiniMouse.Actions;

namespace MightyMiniMouse.Gestures;

public enum GestureType
{
    SinglePress,
    MultiPress,
    LongHold,
    Sequence,
    Chord
}

public class GestureDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public GestureType Type { get; set; }

    /// <summary>
    /// Optional device scope. If set, this gesture only triggers for this specific originating mouse device.
    /// </summary>
    public string? TargetDeviceId { get; set; }

    /// <summary>
    /// The input keys involved. For SinglePress/LongHold: one key.
    /// For MultiPress: one key (pressed N times). For Sequence: ordered list.
    /// For Chord: set of keys that must be held simultaneously.
    /// </summary>
    public List<string> InputKeys { get; set; } = [];

    /// <summary>
    /// For MultiPress: how many presses. Default 2 (double press).
    /// </summary>
    public int PressCount { get; set; } = 2;

    /// <summary>
    /// Time window in milliseconds.
    /// For MultiPress: max gap between presses.
    /// For LongHold: minimum hold duration.
    /// For Sequence: max total duration.
    /// </summary>
    public int TimeWindowMs { get; set; } = 400;

    /// <summary>
    /// The action to execute when this gesture is recognized.
    /// </summary>
    public ActionConfig Action { get; set; } = new();

    /// <summary>
    /// Whether to suppress (swallow) the input events that make up this gesture.
    /// </summary>
    public bool SuppressInput { get; set; } = true;
}
