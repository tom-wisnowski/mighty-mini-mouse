using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using BtInputInterceptor.Logging;

namespace BtInputInterceptor.Gestures;

public class GestureEngine : IDisposable
{
    private readonly List<GestureDefinition> _gestures;
    private readonly Dictionary<string, GestureTracker> _trackers = new();
    private readonly Dictionary<string, SequenceTracker> _sequenceTrackers = new();
    private readonly System.Timers.Timer _holdCheckTimer;

    // Tracks currently held buttons/keys
    private readonly HashSet<string> _currentlyHeld = new();

    // Pending single-press timers for disambiguation with multi-press
    private readonly Dictionary<string, System.Threading.Timer> _singlePressTimers = new();

    public event Action<GestureDefinition>? OnGestureRecognized;

    private bool _disposed;

    public GestureEngine(List<GestureDefinition> gestures)
    {
        _gestures = gestures;

        // Initialize sequence trackers for each sequence gesture
        foreach (var gesture in gestures.Where(g => g.Type == GestureType.Sequence))
        {
            _sequenceTrackers[gesture.Id] = new SequenceTracker();
        }

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

    public void UpdateGestures(List<GestureDefinition> newGestures)
    {
        _gestures.Clear();
        _gestures.AddRange(newGestures);
        _trackers.Clear();
        _sequenceTrackers.Clear();

        foreach (var gesture in newGestures.Where(g => g.Type == GestureType.Sequence))
        {
            _sequenceTrackers[gesture.Id] = new SequenceTracker();
        }

        Logger.Instance.Info($"Gesture engine updated with {newGestures.Count} gestures.");
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
    }

    private bool CheckPressGestures(InputEvent input)
    {
        if (!_trackers.TryGetValue(input.InputKey, out var tracker))
            return false;

        tracker.LastUpTimestamp = input.Timestamp;
        uint holdDuration = input.Timestamp - tracker.LastDownTimestamp;

        // Check for MultiPress gestures first (higher priority)
        foreach (var gesture in _gestures.Where(g =>
            g.Type == GestureType.MultiPress && g.InputKeys.Contains(input.InputKey)))
        {
            if (tracker.PressCount >= gesture.PressCount)
            {
                // Cancel any pending single-press timer for this key
                CancelSinglePressTimer(input.InputKey);

                Logger.Instance.Debug($"Gesture recognized: {gesture.Name} (MultiPress x{gesture.PressCount})");
                OnGestureRecognized?.Invoke(gesture);
                tracker.Reset();
                return gesture.SuppressInput;
            }
        }

        // Check for SinglePress — but delay if there's also a MultiPress on the same key
        foreach (var gesture in _gestures.Where(g =>
            g.Type == GestureType.SinglePress && g.InputKeys.Contains(input.InputKey)))
        {
            if (tracker.PressCount == 1 && holdDuration < 300)
            {
                // Check if there's a MultiPress gesture on the same key
                bool hasMultiPress = _gestures.Any(g =>
                    g.Type == GestureType.MultiPress && g.InputKeys.Contains(input.InputKey));

                if (hasMultiPress)
                {
                    // Delay recognition to disambiguate from double-press
                    uint window = GetMultiPressWindow(input.InputKey);
                    ScheduleSinglePress(gesture, input.InputKey, (int)window);
                    return gesture.SuppressInput;
                }
                else
                {
                    Logger.Instance.Debug($"Gesture recognized: {gesture.Name} (SinglePress)");
                    OnGestureRecognized?.Invoke(gesture);
                    tracker.Reset();
                    return gesture.SuppressInput;
                }
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
                uint held = now - tracker.LastDownTimestamp;
                if (held >= (uint)gesture.TimeWindowMs && !tracker.HoldFired)
                {
                    tracker.HoldFired = true;
                    Logger.Instance.Debug($"Gesture recognized: {gesture.Name} (LongHold {held}ms)");
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
                Logger.Instance.Debug($"Gesture recognized: {gesture.Name} (Chord)");
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
                // First key in sequence — record start time
                if (seqTracker.CurrentIndex == 0)
                {
                    seqTracker.StartTimestamp = input.Timestamp;
                }
                else
                {
                    // Check if we've exceeded the time window
                    uint elapsed = input.Timestamp - seqTracker.StartTimestamp;
                    if (elapsed > (uint)gesture.TimeWindowMs)
                    {
                        seqTracker.Reset();
                        // Re-check if this is the first key
                        if (input.InputKey == gesture.InputKeys[0])
                        {
                            seqTracker.StartTimestamp = input.Timestamp;
                            seqTracker.CurrentIndex = 1;
                        }
                        continue;
                    }
                }

                seqTracker.CurrentIndex++;

                // Sequence complete?
                if (seqTracker.CurrentIndex >= gesture.InputKeys.Count)
                {
                    Logger.Instance.Debug($"Gesture recognized: {gesture.Name} (Sequence)");
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
                if (input.InputKey == gesture.InputKeys[0])
                {
                    seqTracker.StartTimestamp = input.Timestamp;
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
            g.Type is GestureType.SinglePress or GestureType.MultiPress or GestureType.LongHold);
    }

    private uint GetMultiPressWindow(string inputKey)
    {
        var multiPress = _gestures.FirstOrDefault(g =>
            g.Type == GestureType.MultiPress && g.InputKeys.Contains(inputKey));
        return (uint)(multiPress?.TimeWindowMs ?? 400);
    }

    private void ScheduleSinglePress(GestureDefinition gesture, string inputKey, int delayMs)
    {
        CancelSinglePressTimer(inputKey);

        var timer = new System.Threading.Timer(_ =>
        {
            // If press count is still 1, the user didn't follow up with another press
            if (_trackers.TryGetValue(inputKey, out var tracker) && tracker.PressCount == 1)
            {
                Logger.Instance.Debug($"Gesture recognized: {gesture.Name} (SinglePress, deferred)");
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
