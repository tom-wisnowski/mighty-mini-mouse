using MightyMiniMouse.Hooks;

namespace MightyMiniMouse.Gestures;

public enum InputType { MouseButton, KeyPress }
public enum PressState { Down, Up }

public record InputEvent
{
    public InputType Type { get; init; }
    public PressState State { get; init; }

    /// <summary>For mouse events.</summary>
    public MouseButton? Button { get; init; }

    /// <summary>For keyboard events.</summary>
    public uint? VirtualKeyCode { get; set; }

    /// <summary>Originating device identifier (VID/PID key).</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>OS tick count timestamp from the hook struct.</summary>
    public uint Timestamp { get; init; }

    /// <summary>
    /// A unified string key for use in gesture matching.
    /// Examples: "Mouse.XButton1", "Mouse.Right", "Key.VolumeUp", "Key.F13"
    /// </summary>
    public string InputKey => Type switch
    {
        InputType.MouseButton => $"Mouse.{Button}",
        InputType.KeyPress => $"Key.{(ConsoleKey)VirtualKeyCode!}",
        _ => "Unknown"
    };
}
