using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Gestures;

public class GestureEngine : IDisposable
{
    private readonly List<GestureDefinition> _gestures;
    private readonly Dictionary<string, GestureTracker> _trackers = new();
    private readonly Dictionary<string, SequenceTracker> _sequenceTrackers = new();
    private readonly System.Timers.Timer _holdCheckTimer;
    private string _modeName;

    // Tracks currently held buttons/keys
    private readonly HashSet<string> _currentlyHeld = new();

    // Pending single-press timers for disambiguation with multi-press
    private readonly Dictionary<string, System.Threading.Timer> _singlePressTimers = new();

    public event Action<GestureDefinition>? OnGestureRecognized;

    private bool _disposed;

    public GestureEngine(List<GestureDefinition> gestures, string modeName = "Unknown")
    {
        _modeName = modeName;
        // Prioritize device-specific gestures over wildcard (Any Device) gestures
        _gestures = gestures.OrderByDescending(g => !string.IsNullOrEmpty(g.TargetDeviceId)).ToList();

        // Initialize sequence trackers for each sequence gesture
        foreach (var gesture in gestures.Where(g => g.Type == GestureType.Sequence))
        {
            _sequenceTrackers[gesture.Id] = new SequenceTracker();
        }

        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"GestureEngine created for mode '{_modeName}' with {_gestures.Count} gesture(s):");
        foreach (var g in _gestures)
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"  - {g.Name}: {string.Join("+", g.InputKeys)} → {g.Action.Type}:{g.Action.Keystroke}");

        // Timer ticks every 50ms to check for long-hold gestures
        _holdCheckTimer = new System.Timers.Timer(50);
        _holdCheckTimer.Elapsed += CheckHoldGestures;
        _holdCheckTimer.Start();
    }

    /// <summary>
    /// Feed every input event into this method. Returns true if the event
    /// should be suppressed (swallowed).
    /// </summary>
    public bool ProcessInput(InputEvent input)
    {
        bool shouldSuppress = false;

        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"ProcessInput: mode='{_modeName}', gestureCount={_gestures.Count}, key={input.InputKey}, state={input.State}, device={input.DeviceId}");

        if (input.State == PressState.Down)
        {
            _currentlyHeld.Add(input.InputKey);
            RecordPressDown(input);
            shouldSuppress |= CheckChordGestures(input);
        }
        else // Up
        {
            _currentlyHeld.Remove(input.InputKey);
            shouldSuppress |= CheckPressGestures(input);
        }

        shouldSuppress |= CheckSequenceGestures(input);

        // If any gesture involving this key has SuppressInput, suppress on down events too
        if (input.State == PressState.Down && !shouldSuppress)
        {
            shouldSuppress = ShouldSuppressDown(input);
        }

        return shouldSuppress;
    }

    public void UpdateGestures(List<GestureDefinition> newGestures, string modeName = "Unknown")
    {
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"UpdateGestures START: old='{_modeName}' ({_gestures.Count} gestures) → new='{modeName}' ({newGestures.Count} gestures)");

        _modeName = modeName;
        _gestures.Clear();
        // Prioritize device-specific gestures over wildcard (Any Device) gestures
        _gestures.AddRange(newGestures.OrderByDescending(g => !string.IsNullOrEmpty(g.TargetDeviceId)));
        _trackers.Clear();
        _sequenceTrackers.Clear();

        // Clear held-button state so stale holds don't bleed across modes
        _currentlyHeld.Clear();

        // Dispose pending single-press timers to prevent stale callbacks
        foreach (var timer in _singlePressTimers.Values)
            timer.Dispose();
        _singlePressTimers.Clear();

        foreach (var gesture in newGestures.Where(g => g.Type == GestureType.Sequence))
        {
            _sequenceTrackers[gesture.Id] = new SequenceTracker();
        }

        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"Gesture engine updated for mode '{_modeName}' with {newGestures.Count} gestures.");
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"UpdateGestures DONE: mode='{_modeName}', loaded {_gestures.Count} gesture(s):");
        foreach (var g in _gestures)
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"  [{g.Id[..8]}] {g.Name}: {string.Join("+", g.InputKeys)} → {g.Action.Type}:{g.Action.Keystroke} (device={g.TargetDeviceId ?? "Any"})");
    }

    private void RecordPressDown(InputEvent input)
    {
        if (!_trackers.TryGetValue(input.InputKey, out var tracker))
        {
            tracker = new GestureTracker();
            _trackers[input.InputKey] = tracker;
        }

        // Reset press count if too much time has elapsed since last press
        uint multiPressWindow = GetMultiPressWindow(input.InputKey);
        if (tracker.LastUpTimestamp > 0 && input.Timestamp - tracker.LastUpTimestamp > multiPressWindow)
        {
            tracker.PressCount = 0;
        }

        tracker.LastDownTimestamp = input.Timestamp;
        tracker.PressCount++;
        tracker.HoldFired = false;
        tracker.DeviceId = input.DeviceId;
    }

    private bool IsDeviceMatch(GestureDefinition gesture, string deviceId)
    {
        return string.IsNullOrEmpty(gesture.TargetDeviceId) || gesture.TargetDeviceId == deviceId;
    }

    private bool CheckPressGestures(InputEvent input)
    {
        if (!_trackers.TryGetValue(input.InputKey, out var tracker))
            return false;

        tracker.LastUpTimestamp = input.Timestamp;
        uint holdDuration = input.Timestamp - tracker.LastDownTimestamp;

        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"CheckPress: mode='{_modeName}', key={input.InputKey}, pressCount={tracker.PressCount}, holdDuration={holdDuration}ms, device={tracker.DeviceId}");

        // Check for MultiPress gestures first (higher priority)
        foreach (var gesture in _gestures.Where(g =>
            g.Type == GestureType.MultiPress && g.InputKeys.Contains(input.InputKey) && IsDeviceMatch(g, tracker.DeviceId)))
        {
            if (tracker.PressCount >= gesture.PressCount)
            {
                // Cancel any pending single-press timer for this key
                CancelSinglePressTimer(input.InputKey);

                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Recognized: {gesture.Name} (MultiPress x{gesture.PressCount}) [Device: {tracker.DeviceId}] [Mode: {_modeName}]");
                DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"★ MATCH: {gesture.Name} (MultiPress x{gesture.PressCount}) [Mode: {_modeName}]");
                OnGestureRecognized?.Invoke(gesture);
                tracker.Reset();
                return gesture.SuppressInput;
            }
        }

        // Check for SinglePress — but delay if there's also a MultiPress on the same key
        foreach (var gesture in _gestures.Where(g =>
            g.Type == GestureType.SinglePress && g.InputKeys.Contains(input.InputKey) && IsDeviceMatch(g, tracker.DeviceId)))
        {
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"SinglePress candidate: '{gesture.Name}', pressCount={tracker.PressCount}, holdDuration={holdDuration}ms vs threshold=300ms");
            if (tracker.PressCount == 1 && holdDuration < 300)
            {
                // Check if there's a MultiPress gesture on the same key for the same device
                bool hasMultiPress = _gestures.Any(g2 =>
                    g2.Type == GestureType.MultiPress && g2.InputKeys.Contains(input.InputKey) && IsDeviceMatch(g2, tracker.DeviceId));

                if (hasMultiPress)
                {
                    // Delay recognition to disambiguate from double-press
                    uint window = GetMultiPressWindow(input.InputKey);
                    ScheduleSinglePress(gesture, input.InputKey, tracker.DeviceId, (int)window);
                    return gesture.SuppressInput;
                }
                else
                {
                    DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Recognized: {gesture.Name} (SinglePress) [Device: {tracker.DeviceId}] [Mode: {_modeName}]");
                    DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"★ MATCH: {gesture.Name} (SinglePress) [Mode: {_modeName}]");
                    OnGestureRecognized?.Invoke(gesture);
                    tracker.Reset();
                    return gesture.SuppressInput;
                }
            }
        }

        // Log why no gesture matched
        var candidateGestures = _gestures.Where(g =>
            (g.Type == GestureType.SinglePress || g.Type == GestureType.MultiPress) &&
            g.InputKeys.Contains(input.InputKey)).ToList();
        if (candidateGestures.Count > 0)
        {
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"NO MATCH for key={input.InputKey} in mode='{_modeName}'. Candidates:");
            foreach (var g in candidateGestures)
            {
                bool deviceMatch = IsDeviceMatch(g, tracker.DeviceId);
                DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryGestureEngine, $"  - '{g.Name}': type={g.Type}, gestureDevice={g.TargetDeviceId ?? "Any"}, inputDevice={tracker.DeviceId}, deviceMatch={deviceMatch}, pressCount={tracker.PressCount}/{g.PressCount}");
            }
        }

        return false;
    }

    private void CheckHoldGestures(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        uint now = (uint)Environment.TickCount;

        foreach (var gesture in _gestures.Where(g => g.Type == GestureType.LongHold))
        {
            string key = gesture.InputKeys[0];
            if (_currentlyHeld.Contains(key) && _trackers.TryGetValue(key, out var tracker))
            {
                if (!IsDeviceMatch(gesture, tracker.DeviceId)) continue;

                uint held = now - tracker.LastDownTimestamp;
                if (held >= (uint)gesture.TimeWindowMs && !tracker.HoldFired)
                {
                    tracker.HoldFired = true;
                    DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Recognized: {gesture.Name} (LongHold {held}ms) [Device: {tracker.DeviceId}] [Mode: {_modeName}]");
                    OnGestureRecognized?.Invoke(gesture);
                }
            }
        }
    }

    private bool CheckChordGestures(InputEvent input)
    {
        foreach (var gesture in _gestures.Where(g => g.Type == GestureType.Chord))
        {
            if (gesture.InputKeys.All(k => _currentlyHeld.Contains(k)))
            {
                 // We verify the device against the incoming input event (since chords trigger on final button down)
                 if (!IsDeviceMatch(gesture, input.DeviceId)) continue;

                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Recognized: {gesture.Name} (Chord) [Device: {input.DeviceId}]");
                OnGestureRecognized?.Invoke(gesture);
                return gesture.SuppressInput;
            }
        }
        return false;
    }

    private bool CheckSequenceGestures(InputEvent input)
    {
        if (input.State != PressState.Down) return false;

        foreach (var gesture in _gestures.Where(g => g.Type == GestureType.Sequence))
        {
            if (!_sequenceTrackers.TryGetValue(gesture.Id, out var seqTracker))
                continue;

            string expectedKey = gesture.InputKeys[seqTracker.CurrentIndex];

            if (input.InputKey == expectedKey)
            {
                // For sequences, the target device must match the FIRST key pressed,
                // and ideally all keys, but for now we'll match it on the current input.
                if (!IsDeviceMatch(gesture, input.DeviceId)) continue;

                // First key in sequence — record start time and device ID
                if (seqTracker.CurrentIndex == 0)
                {
                    seqTracker.StartTimestamp = input.Timestamp;
                    seqTracker.DeviceId = input.DeviceId;
                }
                else
                {
                    // Check if we've exceeded the time window or mismatched devices
                    uint elapsed = input.Timestamp - seqTracker.StartTimestamp;
                    if (elapsed > (uint)gesture.TimeWindowMs || seqTracker.DeviceId != input.DeviceId)
                    {
                        seqTracker.Reset();
                        // Re-check if this is the first key
                        if (input.InputKey == gesture.InputKeys[0] && IsDeviceMatch(gesture, input.DeviceId))
                        {
                            seqTracker.StartTimestamp = input.Timestamp;
                            seqTracker.DeviceId = input.DeviceId;
                            seqTracker.CurrentIndex = 1;
                        }
                        continue;
                    }
                }

                seqTracker.CurrentIndex++;

                // Sequence complete?
                if (seqTracker.CurrentIndex >= gesture.InputKeys.Count)
                {
                    DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Recognized: {gesture.Name} (Sequence) [Device: {input.DeviceId}]");
                    OnGestureRecognized?.Invoke(gesture);
                    seqTracker.Reset();
                    return gesture.SuppressInput;
                }
            }
            else
            {
                // Mismatch — reset this sequence
                seqTracker.Reset();

                // But check if this key starts the sequence
                if (input.InputKey == gesture.InputKeys[0] && IsDeviceMatch(gesture, input.DeviceId))
                {
                    seqTracker.StartTimestamp = input.Timestamp;
                    seqTracker.DeviceId = input.DeviceId;
                    seqTracker.CurrentIndex = 1;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Determines if a down event should be suppressed because it's part of a
    /// gesture that has SuppressInput enabled.
    /// </summary>
    private bool ShouldSuppressDown(InputEvent input)
    {
        return _gestures.Any(g =>
            g.SuppressInput &&
            g.InputKeys.Contains(input.InputKey) &&
            IsDeviceMatch(g, input.DeviceId) &&
            g.Type is GestureType.SinglePress or GestureType.MultiPress or GestureType.LongHold);
    }

    private uint GetMultiPressWindow(string inputKey)
    {
        var multiPress = _gestures.FirstOrDefault(g =>
            g.Type == GestureType.MultiPress && g.InputKeys.Contains(inputKey));
        return (uint)(multiPress?.TimeWindowMs ?? 400);
    }

    private void ScheduleSinglePress(GestureDefinition gesture, string inputKey, string deviceId, int delayMs)
    {
        CancelSinglePressTimer(inputKey);

        var timer = new System.Threading.Timer(_ =>
        {
            // If press count is still 1, the user didn't follow up with another press
            if (_trackers.TryGetValue(inputKey, out var tracker) && tracker.PressCount == 1)
            {
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Recognized: {gesture.Name} (SinglePress, deferred) [Device: {deviceId}]");
                OnGestureRecognized?.Invoke(gesture);
                tracker.Reset();
            }

            // Clean up the timer
            _singlePressTimers.Remove(inputKey);
        }, null, delayMs, System.Threading.Timeout.Infinite);

        _singlePressTimers[inputKey] = timer;
    }

    private void CancelSinglePressTimer(string inputKey)
    {
        if (_singlePressTimers.Remove(inputKey, out var existingTimer))
        {
            existingTimer.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _holdCheckTimer.Stop();
        _holdCheckTimer.Dispose();

        foreach (var timer in _singlePressTimers.Values)
            timer.Dispose();
        _singlePressTimers.Clear();
    }
}
