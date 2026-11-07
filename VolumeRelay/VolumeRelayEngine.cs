using NAudio.CoreAudioApi;

namespace VolumeRelay;

/// <summary>What the app is actually doing right now, for the tray tooltip.</summary>
public enum RelayMode
{
    /// <summary>Redirecting the pipe's deltas onto the target.</summary>
    Active,
    Paused,
    /// <summary>The Windows default IS the target — the knob controls it natively.</summary>
    DefaultIsTarget,
    /// <summary>The default device is physical, so there is no pipe to intercept.</summary>
    DefaultNotVirtual,
    /// <summary>Auto has nothing to aim at (GG unavailable, or routing is split).</summary>
    NoTarget,
}

/// <summary>
/// Delta-based redirect engine: intercepts volume/mute changes on the default
/// (virtual Sonar) endpoint, applies the delta to a single physical target,
/// and resets the pipe to the pin value (~98%).
/// </summary>
public class VolumeRelayEngine : IDisposable
{
    const float DefaultPin = 0.98f;

    /// <summary>
    /// How far the pipe may sit from the pin and still count as a hotkey detent.
    /// One detent is 0.02 (measured); the margin absorbs a coalesced pair. Detents
    /// were measured to never coalesce, so any larger displacement is somebody
    /// setting an absolute level rather than nudging.
    /// </summary>
    const float KnobStepTolerance = 0.05f;
    const float Epsilon = 0.001f;
    const int SuppressWindowMs = 500;

    readonly GgClient _gg = new();
    MMDeviceEnumerator? _enumerator;
    DeviceWatcher? _watcher;
    readonly object _lock = new();
    readonly AudioEndpointVolumeNotificationDelegate _pipeHandler;
    System.Timers.Timer? _ggTimer;

    // Pipe (default / virtual device)
    MMDevice? _pipeDevice;
    AudioEndpointVolume? _pipeVolume;
    string? _pipeId;
    float _lastPipeVolume = DefaultPin;
    bool _lastPipeMute;
    DateTime _lastPipeWrite = DateTime.MinValue;
    float _pinValue = DefaultPin;

    // Target
    MMDevice? _targetDevice;
    AudioEndpointVolume? _targetVolume;
    string _targetId = "auto";   // session only; always starts on Auto
    string? _resolvedAutoTargetId;

    // State
    bool _paused;
    List<DeviceInfo> _devices = new();

    // Events (fired from COM / threadpool threads — UI must Invoke)
    public event Action? StateChanged;
    public event Action? VolumeChanged;

    public string TargetId => _targetId;
    public bool Paused => _paused;
    public List<DeviceInfo> Devices => _devices;

    /// <summary>The actual endpoint ID the target resolves to (null = no target).</summary>
    public string? ResolvedTargetId
        => _targetId == "auto" ? _resolvedAutoTargetId : (_targetId == "" ? null : _targetId);

    public float TargetVolume
    {
        get { lock (_lock) return _targetVolume?.MasterVolumeLevelScalar ?? 0f; }
    }

    public bool TargetMuted
    {
        get { lock (_lock) return _targetVolume?.Mute ?? false; }
    }

    public string? TargetName
    {
        get
        {
            var id = ResolvedTargetId;
            if (id == null) return _targetId == "auto" ? "auto" : "none";
            return _devices.FirstOrDefault(d => d.Id == id)?.Name ?? id;
        }
    }

    /// <summary>
    /// What the app is doing right now. Passive states must not be reported as if
    /// redirection were happening — the tooltip is the only window into the engine.
    /// </summary>
    public RelayMode Mode
    {
        get
        {
            lock (_lock)
            {
                if (_paused) return RelayMode.Paused;

                var targetId = _targetId == "auto" ? _resolvedAutoTargetId : _targetId;
                if (targetId != null && targetId != "" && targetId == _pipeId)
                    return RelayMode.DefaultIsTarget;

                if (!PipeIsVirtualLocked()) return RelayMode.DefaultNotVirtual;
                if (targetId == null || targetId == "") return RelayMode.NoTarget;
                return RelayMode.Active;
            }
        }
    }

    /// <summary>Name of the current Windows default (render) device, if known.</summary>
    public string? DefaultDeviceName
    {
        get
        {
            lock (_lock)
            {
                if (_pipeId == null) return null;
                return _devices.FirstOrDefault(d => d.Id == _pipeId)?.Name;
            }
        }
    }

    /// <summary>Reason there's no auto target, or null if a target is resolved.</summary>
    public string? AutoStatus
    {
        get
        {
            if (_targetId != "auto" || _resolvedAutoTargetId != null) return null;
            return _gg.LastError ?? "no target";
        }
    }

    public VolumeRelayEngine()
    {
        _pipeHandler = e => OnPipeChanged(e);
    }

    public void Start()
    {
        // Both must outlive Start(): the registration is only as durable as the
        // managed callback object and the enumerator's RCW. As locals they were
        // collectable after the first GC, which would silently kill hotplug and
        // default-change events.
        _enumerator = new MMDeviceEnumerator();
        _watcher = new DeviceWatcher(
            onAdded: _ => QueueRefresh(),
            onRemoved: _ => QueueRefresh(),
            onStateChanged: _ => QueueRefresh(),
            onDefaultChanged: OnDefaultDeviceChanged);
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        catch
        {
            // Hotplug tracking degrades; volume and mute relaying keep working.
        }

        RefreshDevices();
        AnchorPipe();

        // Starts on Auto: GG's routing is the target by design. There is no
        // persisted manual choice, so nothing to validate or fall back from.
        lock (_lock)
        {
            if (_targetId != "auto")
                ResolveTargetLocked();
        }

        // Gentle startup: set the pipe to the pin value, but only if the default
        // really is our virtual pipe (see PipeIsVirtualLocked).
        lock (_lock)
        {
            if (!IsPassiveLocked() && PipeIsVirtualLocked() && _pipeVolume != null)
            {
                _lastPipeWrite = DateTime.Now;
                _pipeVolume.MasterVolumeLevelScalar = _pinValue;
                _lastPipeVolume = _pinValue;
            }
        }

        // Start GG polling
        _ggTimer = new System.Timers.Timer(3000) { AutoReset = true };
        _ggTimer.Elapsed += (s, e) => PollAutoTarget();
        _ggTimer.Start();

        // Immediate poll for auto targets
        if (_targetId == "auto")
            PollAutoTarget();

        StateChanged?.Invoke();
    }
    // ---- Pipe (default device) ----

    int _refreshQueued;

    /// <summary>
    /// Device notifications arrive on a Core Audio callback thread. Re-entering
    /// Core Audio from there can block indefinitely (creating enumerators,
    /// disposing devices, activating endpoints), so callbacks only mark work
    /// dirty and return; the refresh runs on a pool thread and coalesces bursts.
    /// </summary>
    void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(150); // let a plug/unplug burst settle
                Interlocked.Exchange(ref _refreshQueued, 0);
                RefreshDevices();
            }
            catch
            {
                // a failed refresh must not take down the pool thread
            }
        });
    }

    void AnchorPipe()
    {
        lock (_lock)
        {
            UnanchorPipeLocked();
            try
            {
                _pipeDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                _pipeId = _pipeDevice.ID;
                _pipeVolume = _pipeDevice.AudioEndpointVolume;
                _lastPipeVolume = _pipeVolume.MasterVolumeLevelScalar;
                _lastPipeMute = _pipeVolume.Mute;
                _pipeVolume.OnVolumeNotification += _pipeHandler;
            }
            catch
            {
                _pipeDevice = null;
                _pipeVolume = null;
                _pipeId = null;
            }
        }
    }

    void UnanchorPipeLocked()
    {
        if (_pipeVolume != null)
        {
            _pipeVolume.OnVolumeNotification -= _pipeHandler;
            _pipeVolume.Dispose();
            _pipeVolume = null;
        }
        _pipeDevice?.Dispose();
        _pipeDevice = null;
        _pipeId = null;
    }

    void OnPipeChanged(AudioVolumeNotificationData data)
    {
        lock (_lock)
        {
            if (_paused || IsPassiveLocked() || !PipeIsVirtualLocked()) return;

            float newVol = data.MasterVolume;
            bool newMute = data.Muted;

            // Suppress our own writes (pipe reset to pin value)
            if ((DateTime.Now - _lastPipeWrite).TotalMilliseconds < SuppressWindowMs &&
                Math.Abs(newVol - _pinValue) < Epsilon)
            {
                _lastPipeVolume = newVol;
                _lastPipeMute = newMute;
                return;
            }

            bool volumeChanged = Math.Abs(newVol - _lastPipeVolume) > Epsilon;
            bool muteChanged = newMute != _lastPipeMute;

            if (volumeChanged)
            {
                // A slider position is an absolute level; a knob detent is a
                // relative step. Detected by size, not by the caller's event-context
                // GUID: those GUIDs are per-caller and undocumented (the flyout and
                // the Settings app use different ones), so a whitelist would need
                // every UI surface enumerated forever. A detent can only ever land
                // one step from the pin, which no UI drag ever does.
                //
                // The context check is the safety net: a hotkey change carries no
                // context, so even a coalesced multi-detent event stays relative.
                // Reading one as absolute could jump the target to full volume.
                bool knobSized = Math.Abs(newVol - _pinValue) <= KnobStepTolerance;
                bool fromSlider = data.EventContext != Guid.Empty && !knobSized;
                float delta = newVol - _lastPipeVolume;
                _lastPipeVolume = newVol;

                if (_targetVolume != null)
                {
                    try
                    {
                        float newTarget = fromSlider
                            ? newVol
                            : _targetVolume.MasterVolumeLevelScalar + delta;
                        _targetVolume.MasterVolumeLevelScalar = Math.Clamp(newTarget, 0f, 1f);
                    }
                    catch { }
                }

                // Reset pipe to pin value
                if (_pipeVolume != null)
                {
                    try
                    {
                        _lastPipeWrite = DateTime.Now;
                        _pipeVolume.MasterVolumeLevelScalar = _pinValue;
                        _lastPipeVolume = _pinValue;
                    }
                    catch { }
                }
            }

            if (muteChanged)
            {
                _lastPipeMute = newMute;
                if (_targetVolume != null)
                {
                    try { _targetVolume.Mute = newMute; } catch { }
                }
            }

            if (volumeChanged || muteChanged)
                VolumeChanged?.Invoke();
        }
    }

    // ---- Default device change ----

    void OnDefaultDeviceChanged(string flow, string role, string newId)
    {
        if (flow != "Render" || role != "Console") return;
        QueueRefresh();
    }

    // ---- Target management ----

    void ResolveTargetLocked()
    {
        if (_targetId == "auto")
        {
            _resolvedAutoTargetId = QueryGgTarget() ?? HeuristicTarget();
        }
        else
        {
            _resolvedAutoTargetId = null;
        }
        ResolveTargetDeviceLocked();
    }

    void PollAutoTarget()
    {
        bool changed = false;
        lock (_lock)
        {
            if (_paused || _targetId != "auto") return;
            var id = QueryGgTarget() ?? HeuristicTarget();
            if (id != _resolvedAutoTargetId)
            {
                _resolvedAutoTargetId = id;
                ResolveTargetDeviceLocked();
                // No volume carryover — the new target keeps its own volume
                changed = true;
            }
        }
        if (changed) StateChanged?.Invoke();
    }

    string? QueryGgTarget()
    {
        var id = _gg.GetSelectedOutput();
        return id != null && _devices.Any(d => d.Id == id) ? id : null;
    }

    /// <summary>Fallback when GG is unavailable: the single active physical non-default device.</summary>
    string? HeuristicTarget()
    {
        var phys = _devices.Where(d => !d.IsVirtual && d.Id != _pipeId).ToList();
        return phys.Count == 1 ? phys[0].Id : null;
    }

    public void SetTarget(string id)
    {
        lock (_lock)
        {
            _targetId = id;
            if (id == "auto")
                _resolvedAutoTargetId = QueryGgTarget() ?? HeuristicTarget();
            else
                _resolvedAutoTargetId = null;
            ResolveTargetDeviceLocked();
        }
        StateChanged?.Invoke();
    }

    void ResolveTargetDeviceLocked()
    {
        var id = ResolvedTargetId;
        try { _targetDevice?.Dispose(); } catch { }
        _targetDevice = null;
        _targetVolume = null;
        if (id == null) return;
        try
        {
            _targetDevice = new MMDeviceEnumerator().GetDevice(id);
            _targetVolume = _targetDevice.AudioEndpointVolume;
        }
        catch
        {
            // device may be gone; TargetVolume then reads 0 / no-op writes
        }
    }

    /// <summary>True when the default device IS the target → no pinning/redirect.</summary>
    bool IsPassiveLocked()
    {
        var targetId = _targetId == "auto" ? _resolvedAutoTargetId : _targetId;
        return targetId != null && targetId != "" && targetId == _pipeId;
    }

    /// <summary>
    /// A pipe only exists when the Windows default is a *virtual* device. If the
    /// default is physical, the knob already controls something real: redirecting
    /// would attenuate twice (physical × physical) and pinning it would overwrite
    /// a deliberately chosen level (a monitor at 40%, IEMs at 10%).
    /// This matters at logon, where the default may not be Sonar yet — we must not
    /// pin whatever physical device happens to be default and then walk away.
    /// </summary>
    bool PipeIsVirtualLocked()
    {
        if (_pipeId == null) return false;
        return _devices.Any(d => d.Id == _pipeId && d.IsVirtual);
    }

    // ---- Devices ----

    public void RefreshDevices()
    {
        lock (_lock)
        {
            var (defId, list) = DeviceHelper.GetRenderDevices();
            _devices = list;

            // Re-anchor pipe if the default changed
            if (defId != _pipeId)
            {
                UnanchorPipeLocked();
                try
                {
                    _pipeDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    _pipeId = _pipeDevice.ID;
                    _pipeVolume = _pipeDevice.AudioEndpointVolume;
                    _lastPipeVolume = _pipeVolume.MasterVolumeLevelScalar;
                    _lastPipeMute = _pipeVolume.Mute;
                    _pipeVolume.OnVolumeNotification += _pipeHandler;

                    // Reset new default to pin value (virtual pipe only)
                    if (!IsPassiveLocked() && PipeIsVirtualLocked())
                    {
                        _lastPipeWrite = DateTime.Now;
                        _pipeVolume.MasterVolumeLevelScalar = _pinValue;
                        _lastPipeVolume = _pinValue;
                    }
                }
                catch
                {
                    _pipeDevice = null;
                    _pipeVolume = null;
                    _pipeId = null;
                }
            }

            // Re-resolve target device (it may have come back)
            ResolveTargetDeviceLocked();
        }

        // A UI subscriber must never break device tracking.
        try { StateChanged?.Invoke(); } catch { }
    }

    // ---- Pause ----

    public void SetPaused(bool paused)
    {
        lock (_lock) _paused = paused;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _ggTimer?.Dispose();

        if (_watcher != null && _enumerator != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        }

        lock (_lock)
        {
            UnanchorPipeLocked();
            _targetDevice?.Dispose();
        }
    }
}
