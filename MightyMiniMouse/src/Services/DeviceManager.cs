using System;
using System.Collections.Generic;
using System.Linq;
using MightyMiniMouse.Config;
using MightyMiniMouse.Hooks;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Services;

public class DeviceManager
{
    private readonly AppConfig _config;
    private readonly RawInputManager _rawInputManager;

    public DeviceManager(AppConfig config, RawInputManager rawInputManager)
    {
        _config = config;
        _rawInputManager = rawInputManager;
    }

    /// <summary>
    /// Enumerates all currently connected devices, extracting their VID/PID and combining
    /// with any configured nickname to produce a friendly display name.
    /// </summary>
    public List<DisplayDeviceInfo> GetKnownAndConnectedDevices()
    {
        var rawDevices = _rawInputManager.EnumerateDevices();
        
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var displayDevices = new List<DisplayDeviceInfo>();

        foreach (var dev in rawDevices)
        {
            string vidPid = RawInputManager.ExtractVidPid(dev.DevicePath) ?? dev.DevicePath;
            if (!seen.Add(vidPid)) continue; // Deduplicate by VID/PID

            var known = _config.KnownDevices.FirstOrDefault(d => d.DeviceId == vidPid);
            
            displayDevices.Add(new DisplayDeviceInfo
            {
                DeviceId = vidPid,
                HardwareName = dev.FriendlyName,
                Nickname = known?.Nickname ?? "",
                IsBluetooth = dev.IsBluetooth,
                IsActiveTarget = _rawInputManager.IsTargetDeviceId(vidPid)
            });
        }

        return displayDevices;
    }

    /// <summary>
    /// Gets the friendly display name for a given device ID.
    /// Prefers the configured nickname, falling back to basic device identification.
    /// </summary>
    public string GetDisplayName(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return "Any Device";

        var known = _config.KnownDevices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (known != null && !string.IsNullOrWhiteSpace(known.Nickname))
            return known.Nickname;

        // Optionally, try to find the hardware name if it's currently connected
        // but not explicitly nicknamed
        var rawDevices = _rawInputManager.EnumerateDevices();
        foreach(var dev in rawDevices)
        {
            if (deviceId == (RawInputManager.ExtractVidPid(dev.DevicePath) ?? dev.DevicePath))
            {
                 return dev.FriendlyName;
            }
        }

        return deviceId; // Fallback to raw ID
    }

    /// <summary>
    /// Updates or adds a nickname for the specified device ID and saves the configuration.
    /// </summary>
    public void SaveNickname(string deviceId, string nickname)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        var known = _config.KnownDevices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (known == null)
        {
            known = new MouseDevice { DeviceId = deviceId };
            _config.KnownDevices.Add(known);
        }

        known.Nickname = nickname.Trim();
        ConfigManager.Save(_config);
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryDevice, $"Saved nickname '{known.Nickname}' for device {deviceId}");
    }

    /// <summary>
    /// Removes a known device entry from the configuration by device ID and saves.
    /// </summary>
    /// <returns>True if an entry was found and removed; false if no matching entry existed.</returns>
    public bool RemoveDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        int removed = _config.KnownDevices.RemoveAll(d =>
            string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            ConfigManager.Save(_config);
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryDevice, $"Removed device entry '{deviceId}' from known devices.");
            return true;
        }

        return false;
    }
}

public class DisplayDeviceInfo
{
    public string DeviceId { get; init; } = "";
    public string HardwareName { get; init; } = "";
    public string Nickname { get; init; } = "";
    public bool IsBluetooth { get; init; }
    public bool IsActiveTarget { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? HardwareName : $"{Nickname} ({HardwareName})";
}
