using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static MightyMiniMouse.Hooks.NativeMethods;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Hooks;

public class RawInputManager
{
    private IntPtr _targetDeviceHandle = IntPtr.Zero;
    private IntPtr _lastSeenDevice = IntPtr.Zero;
    private int _rawInputCount;

    // Path-based matching: extracted VID/PID key for the target device
    // e.g. "VID_045E&PID_07B2" — matches ALL HID collections from that device
    private string? _targetVidPid;
    // Cache: raw input handle → device path (looked up once per handle)
    private readonly Dictionary<IntPtr, string> _handlePathCache = new();

    /// <summary>
    /// Fired whenever a raw input event is processed, passing the handle of the source device.
    /// </summary>
    public event Action<IntPtr>? OnDeviceActivity;

    // Common manufacturer VID lookup
    private static readonly Dictionary<string, string> VendorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["045E"] = "Microsoft",
        ["046D"] = "Logitech",
        ["1532"] = "Razer",
        ["1038"] = "SteelSeries",
        ["0B05"] = "ASUS",
        ["2717"] = "Xiaomi",
        ["258A"] = "Glorious",
        ["3434"] = "Keychron",
        ["28DE"] = "Valve",
        ["054C"] = "Sony",
        ["057E"] = "Nintendo",
        ["2516"] = "Cooler Master",
        ["1B1C"] = "Corsair",
        ["1A2C"] = "China (Generic)",
        ["0461"] = "Primax",
        ["17EF"] = "Lenovo",
        ["413C"] = "Dell",
        ["03F0"] = "HP",
        ["04F3"] = "ELAN",
        ["06CB"] = "Synaptics",
        ["093A"] = "Pixart",
    };

    // Regex to extract VID and PID from device path
    private static readonly Regex VidPidRegex = new(
        @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Enumerate all HID devices. Bluetooth devices typically have paths containing
    /// "BTHLE" or "BTHENUM".
    /// </summary>
    public List<DeviceInfo> EnumerateDevices()
    {
        uint deviceCount = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref deviceCount, size);

        if (deviceCount == 0)
            return [];

        var devices = new RAWINPUTDEVICELIST[deviceCount];
        GetRawInputDeviceList(devices, ref deviceCount, size);

        var result = new List<DeviceInfo>();
        foreach (var device in devices)
        {
            string name = GetDeviceName(device.hDevice);
            bool isBt = name.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase);

            result.Add(new DeviceInfo
            {
                Handle = device.hDevice,
                Type = device.dwType,
                DevicePath = name,
                IsBluetooth = isBt,
                FriendlyName = BuildFriendlyName(name, device.dwType, isBt)
            });
        }
        return result;
    }

    /// <summary>
    /// Returns the handle of the last device that sent raw input.
    /// Used by DevicePickerDialog for auto-detection.
    /// </summary>
    public IntPtr GetLastSeenDeviceHandle() => _lastSeenDevice;

    /// <summary>
    /// Set which device handle we care about. Pass IntPtr.Zero to accept all devices.
    /// Also extracts VID/PID from the handle's device path for multi-collection matching.
    /// </summary>
    public void SetTargetDevice(IntPtr handle)
    {
        _targetDeviceHandle = handle;

        if (handle == IntPtr.Zero)
        {
            _targetVidPid = null;
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryRawInput, "Target device cleared — accepting all devices");
        }
        else
        {
            // Extract VID/PID from this handle's path for cross-collection matching
            string path = GetDeviceName(handle);
            var match = VidPidRegex.Match(path);
            _targetVidPid = match.Success ? match.Value.ToUpperInvariant() : null;
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Target device set: handle={handle}, vidpid={_targetVidPid ?? "(none)"}, path={path}");
        }
    }

    /// <summary>
    /// Set target device by matching a device path substring (e.g., VID/PID).
    /// This survives BT reconnections where the handle changes but the path stays the same.
    /// </summary>
    public bool SetTargetDeviceByPath(string devicePathSubstring)
    {
        // Extract VID/PID from the path substring for cross-collection matching
        var vpMatch = VidPidRegex.Match(devicePathSubstring);
        if (vpMatch.Success)
        {
            _targetVidPid = vpMatch.Value.ToUpperInvariant();
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Target device set by VID/PID: {_targetVidPid} (from path: {devicePathSubstring})");
        }

        // Also try to set the specific handle for backward compat
        var devices = EnumerateDevices();
        foreach (var device in devices)
        {
            if (device.DevicePath.Contains(devicePathSubstring, StringComparison.OrdinalIgnoreCase))
            {
                _targetDeviceHandle = device.Handle;
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Target device handle matched: {device.Handle}");
                return true;
            }
        }

        // VID/PID was set even if no exact handle match
        if (_targetVidPid != null)
        {
            _targetDeviceHandle = IntPtr.Zero; // No specific handle, rely on VID/PID
            return true;
        }

        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"No device found matching path substring: {devicePathSubstring}");
        return false;
    }

    /// <summary>
    /// Called when WM_INPUT arrives. Records the source device handle.
    /// Raw Input events arrive BEFORE hook callbacks on the same message queue,
    /// so _lastSeenDevice is set by the time the hook fires.
    /// </summary>
    public void ProcessRawInput(IntPtr hRawInput)
    {
        try
        {
            uint dataSize = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint res1 = GetRawInputData(hRawInput, (uint)RID_INPUT, IntPtr.Zero, ref dataSize, headerSize);

            if (dataSize == 0) 
            {
                DiagnosticOutput.LogError(DiagnosticOutput.CategoryRawInput, $"dataSize is 0. res1={res1}, Error={Marshal.GetLastWin32Error()}");
                return;
            }

            var buffer = Marshal.AllocHGlobal((int)dataSize);
            try
            {
                uint res2 = GetRawInputData(hRawInput, (uint)RID_INPUT, buffer, ref dataSize, headerSize);
                if (res2 == unchecked((uint)-1))
                {
                    DiagnosticOutput.LogError(DiagnosticOutput.CategoryRawInput, $"GetRawInputData failed. Error={Marshal.GetLastWin32Error()}");
                    return;
                }

                // Temporary Hex Dump for debugging
                int readSize = Math.Min((int)dataSize, 32);
                byte[] bytes = new byte[readSize];
                Marshal.Copy(buffer, bytes, 0, readSize);
                string hex = BitConverter.ToString(bytes);
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryRawInput, $"hRawInput={hRawInput}, size={dataSize}, hex={hex}");

                var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
                _lastSeenDevice = raw.header.hDevice;
                _rawInputCount++;
                
                OnDeviceActivity?.Invoke(_lastSeenDevice);

                // Log every event while debugging
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryRawInput, $"WM_INPUT #{_rawInputCount}: hRawInput={hRawInput}, res2={res2}, dataSize={dataSize}, hDevice={raw.header.hDevice}, dwType={raw.header.dwType}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryRawInput, "Exception processing raw input data", ex);
        }
    }

    /// <summary>
    /// Returns true if the most recent raw input came from our target device.
    /// Uses VID/PID path matching to handle multi-collection HID devices
    /// (e.g., finger mice that expose both mouse and keyboard HID collections).
    /// </summary>
    public bool IsFromTargetDevice()
    {
        // No filter = accept all
        if (_targetDeviceHandle == IntPtr.Zero && _targetVidPid == null)
            return true;

        // If we have a VID/PID target, match by VID/PID in the device path
        if (_targetVidPid != null && _lastSeenDevice != IntPtr.Zero)
        {
            // Look up the device path for this handle (cached)
            if (!_handlePathCache.TryGetValue(_lastSeenDevice, out var path))
            {
                path = GetDeviceName(_lastSeenDevice);
                _handlePathCache[_lastSeenDevice] = path;
            }

            return path.Contains(_targetVidPid, StringComparison.OrdinalIgnoreCase);
        }

        // Fallback to exact handle match
        if (_targetDeviceHandle != IntPtr.Zero)
            return _lastSeenDevice == _targetDeviceHandle;

        return true;
    }

    /// <summary>
    /// Checks if a given VID/PID string currently matches the active target filter.
    /// </summary>
    public bool IsTargetDeviceId(string vidPid)
    {
        if (_targetDeviceHandle == IntPtr.Zero && _targetVidPid == null)
            return false; // If no specific target, basically all devices are "a" target, but this is used to identify the selected one in UI
        
        if (_targetVidPid != null)
            return string.Equals(_targetVidPid, vidPid, StringComparison.OrdinalIgnoreCase);
        
        // Exact handle is matched natively but we can't easily resolve it backwards from just vidPid here without iterating
        // Devices usually have _targetVidPid matching.
        return false;
    }

    /// <summary>
    /// Register for Raw Input on the given window handle to receive WM_INPUT messages.
    /// </summary>
    public bool RegisterForRawInput(IntPtr hwnd)
    {
        var devices = new RAWINPUTDEVICE[]
        {
            // Mouse
            new()
            {
                UsagePage = 0x01,
                Usage = 0x02,
                Flags = (uint)RIDEV_INPUTSINK,
                Target = hwnd
            },
            // Keyboard
            new()
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = (uint)RIDEV_INPUTSINK,
                Target = hwnd
            },
            // Precision Touchpad (Digitizer)
            new()
            {
                UsagePage = 0x0D,
                Usage = 0x05,
                Flags = (uint)RIDEV_INPUTSINK,
                Target = hwnd
            }
        };

        bool success = RegisterRawInputDevices(devices, (uint)devices.Length,
            (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        if (!success)
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryRawInput, $"Failed to register raw input devices. Error: {Marshal.GetLastWin32Error()}");
        else
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryRawInput, "Raw input devices registered successfully.");

        return success;
    }

    // ── Friendly name generation ──

    /// <summary>
    /// Extract a normalized VID/PID key from a device path.
    /// Handles USB format (VID_045E&amp;PID_07B2) and BT format (VID&amp;02248a_PID&amp;8266).
    /// Returns null if no match found.
    /// </summary>
    public static string? ExtractVidPid(string path)
    {
        // 1. Bluetooth format with MAC
        // The MAC address (12 hex chars) is usually preceded and followed by non-alphanumeric chars (like '_' or '&' or '#')
        // e.g. \\?\HID#{...}_Dev_VID&02248a_PID&8266&Col01#a&d101ff200fb5&0&0000#{guid}
        var btMatch = Regex.Match(path, @"VID&([0-9A-Fa-f]+)_PID&([0-9A-Fa-f]+).*?[^a-zA-Z0-9]([0-9A-Fa-f]{12})[^a-zA-Z0-9]", RegexOptions.IgnoreCase);
        if (btMatch.Success)
        {
            string vid = btMatch.Groups[1].Value.PadLeft(4, '0');
            string pid = btMatch.Groups[2].Value.PadLeft(4, '0');
            string mac = btMatch.Groups[3].Value.ToLowerInvariant();
            return $"VID_{vid}&PID_{pid}_{mac}".ToUpperInvariant();
        }

        // 2. Standard USB format: VID_xxxx&PID_xxxx
        var usbMatch = Regex.Match(path, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
        if (usbMatch.Success)
        {
            // For USB, extract the instance ID to differentiate visually identical hardware
            // Example path: \\?\HID#VID_045E&PID_07B2&MI_01&Col01#8&12345678&0&0000#{guid}
            var parts = path.Split('#');
            if (parts.Length >= 3)
            {
                string instanceId = parts[2];
                // Strip the final numeric collection suffix so collections of the SAME mouse group together
                int lastAmp = instanceId.LastIndexOf('&');
                if (lastAmp > 0)
                {
                    instanceId = instanceId.Substring(0, lastAmp);
                }
                return $"VID_{usbMatch.Groups[1].Value}&PID_{usbMatch.Groups[2].Value}_{instanceId}".ToUpperInvariant();
            }

            return $"VID_{usbMatch.Groups[1].Value}&PID_{usbMatch.Groups[2].Value}".ToUpperInvariant();
        }

        // 3. Fallback Bluetooth format without MAC
        var btFallbackMatch = Regex.Match(path, @"VID&([0-9A-Fa-f]+)_PID&([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
        if (btFallbackMatch.Success)
        {
            string vid = btFallbackMatch.Groups[1].Value.PadLeft(4, '0');
            string pid = btFallbackMatch.Groups[2].Value.PadLeft(4, '0');
            return $"VID_{vid}&PID_{pid}".ToUpperInvariant();
        }

        return null;
    }

    private static string BuildFriendlyName(string devicePath, uint deviceType, bool isBluetooth)
    {
        if (deviceType == 2 && devicePath.Contains("000D_0005", StringComparison.OrdinalIgnoreCase))
        {
            return "Precision Touchpad (Virtual)";
        }

        string typeName = deviceType switch
        {
            0 => "Mouse",
            1 => "Keyboard",
            2 => "HID Device",
            _ => "Device"
        };

        var match = VidPidRegex.Match(devicePath);
        if (match.Success)
        {
            string vid = match.Groups[1].Value.ToUpperInvariant();

            string manufacturer = VendorNames.TryGetValue(vid, out var name)
                ? name
                : $"VID:{vid}";

            string connType = isBluetooth ? " (BT)" : "";
            return $"{manufacturer} {typeName}{connType}";
        }

        // Fallback for devices without VID/PID
        string connection = isBluetooth ? " (BT)" : "";
        return $"Unknown {typeName}{connection}";
    }

    public static string GetDeviceName(IntPtr hDevice)
    {
        uint nameSize = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref nameSize);

        if (nameSize == 0) return "";

        var namePtr = Marshal.AllocHGlobal((int)nameSize * 2);
        try
        {
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, namePtr, ref nameSize);
            return Marshal.PtrToStringAuto(namePtr) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }
}

public class DeviceInfo
{
    public IntPtr Handle { get; init; }
    public uint Type { get; init; }
    public string DevicePath { get; init; } = "";
    public bool IsBluetooth { get; init; }
    public string FriendlyName { get; init; } = "";

    public string TypeName => Type switch
    {
        0 => "Mouse",
        1 => "Keyboard",
        2 => "HID",
        _ => "Unknown"
    };

    public override string ToString() =>
        $"[{TypeName}] {(IsBluetooth ? "(BT) " : "")}{FriendlyName} — {DevicePath}";
}

