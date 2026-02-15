using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BtInputInterceptor.Logging;

namespace BtInputInterceptor.Actions;

public class SendKeystrokeAction : IAction
{
    private readonly string _keystroke;

    // VK code mappings for modifier and special keys
    private static readonly Dictionary<string, byte> VirtualKeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Modifiers
        ["Ctrl"] = 0x11,     // VK_CONTROL
        ["Control"] = 0x11,
        ["Alt"] = 0x12,      // VK_MENU
        ["Shift"] = 0x10,    // VK_SHIFT
        ["Win"] = 0x5B,      // VK_LWIN
        ["LWin"] = 0x5B,
        ["RWin"] = 0x5C,     // VK_RWIN

        // Special keys
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Tab"] = 0x09,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Space"] = 0x20,
        ["Backspace"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["PrintScreen"] = 0x2C,
        ["ScrollLock"] = 0x91,
        ["Pause"] = 0x13,
        ["NumLock"] = 0x90,
        ["CapsLock"] = 0x14,

        // Function keys
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E, ["F16"] = 0x7F,

        // Media keys
        ["VolumeUp"] = 0xAF,
        ["VolumeDown"] = 0xAE,
        ["VolumeMute"] = 0xAD,
        ["MediaNext"] = 0xB0,
        ["MediaPrev"] = 0xB1,
        ["MediaStop"] = 0xB2,
        ["MediaPlayPause"] = 0xB3,

        // Punctuation / OEM keys
        ["."] = 0xBE,        // VK_OEM_PERIOD
        [","] = 0xBC,        // VK_OEM_COMMA
        ["/"] = 0xBF,        // VK_OEM_2 (question mark key)
        [";"] = 0xBA,        // VK_OEM_1 (semicolon)
        ["'"] = 0xDE,        // VK_OEM_7 (single quote)
        ["["] = 0xDB,        // VK_OEM_4
        ["]"] = 0xDD,        // VK_OEM_6
        ["\\"] = 0xDC,       // VK_OEM_5 (backslash)
        ["-"] = 0xBD,        // VK_OEM_MINUS
        ["="] = 0xBB,        // VK_OEM_PLUS (equals key)
        ["`"] = 0xC0,        // VK_OEM_3 (backtick)
    };

    private static readonly HashSet<string> ModifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl", "Control", "Alt", "Shift", "Win", "LWin", "RWin"
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // On x64, INPUT is 40 bytes. The union starts at offset 8 due to
    // alignment of IntPtr (8 bytes) inside KEYBDINPUT/MOUSEINPUT.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public SendKeystrokeAction(string keystroke)
    {
        _keystroke = keystroke;
    }

    public Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            var keys = _keystroke.Split('+', StringSplitOptions.TrimEntries);

            Debug.WriteLine($"[BtInput][SENDKEY] Parsing keystroke: '{_keystroke}' → {keys.Length} key(s)");

            var inputs = new List<INPUT>();

            // Press modifiers and main key
            foreach (var key in keys)
            {
                byte vk = ResolveVirtualKeyCode(key);
                Debug.WriteLine($"[BtInput][SENDKEY]   Key '{key}' → VK=0x{vk:X2}, action=DOWN");
                inputs.Add(CreateKeyInput(vk, down: true));
            }

            // Release in reverse order
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                byte vk = ResolveVirtualKeyCode(keys[i]);
                Debug.WriteLine($"[BtInput][SENDKEY]   Key '{keys[i]}' → VK=0x{vk:X2}, action=UP");
                inputs.Add(CreateKeyInput(vk, down: false));
            }

            var inputArray = inputs.ToArray();
            Debug.WriteLine($"[BtInput][SENDKEY] Calling SendInput with {inputArray.Length} events...");

            uint sent = SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf<INPUT>());

            if (sent == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Instance.Error($"SendInput FAILED for '{_keystroke}': returned 0, Win32 error={error}");
                Debug.WriteLine($"[BtInput][SENDKEY] *** FAILED *** SendInput returned 0, GetLastError={error}");
            }
            else if (sent < inputArray.Length)
            {
                Logger.Instance.Warning($"SendInput partial: sent {sent}/{inputArray.Length} for '{_keystroke}'");
                Debug.WriteLine($"[BtInput][SENDKEY] PARTIAL: sent {sent}/{inputArray.Length}");
            }
            else
            {
                Logger.Instance.Info($"Sent keystroke: {_keystroke} ({sent} events)");
                Debug.WriteLine($"[BtInput][SENDKEY] SUCCESS: '{_keystroke}' — {sent} events injected");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to send keystroke: {_keystroke}", ex);
            Debug.WriteLine($"[BtInput][SENDKEY] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private static byte ResolveVirtualKeyCode(string key)
    {
        if (VirtualKeyCodes.TryGetValue(key, out byte vk))
            return vk;

        // Single character — use its VK code (A-Z, 0-9)
        if (key.Length == 1)
        {
            char c = char.ToUpper(key[0]);
            if (c is >= 'A' and <= 'Z')
                return (byte)c;
            if (c is >= '0' and <= '9')
                return (byte)c;
        }

        throw new ArgumentException($"Unknown key: {key}");
    }

    private static INPUT CreateKeyInput(byte vk, bool down)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = down ? 0 : KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
    }
}
