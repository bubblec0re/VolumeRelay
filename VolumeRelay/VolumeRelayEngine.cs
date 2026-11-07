using NAudio.CoreAudioApi;

namespace VolumeRelay;

/// <summary>
/// Delta-based redirect engine: intercepts volume/mute changes on the default
/// (virtual Sonar) endpoint, applies the delta to a single physical target,
/// and resets the pipe to the pin value (~98%).
/// </summary>
public class VolumeRelayEngine : IDisposable
{
    const float DefaultPin = 0.98f;
    const float Epsilon = 0.001f;
    const int SuppressWindowMs = 500;

    readonly GgClient _gg = new();
    MMDeviceEnumerator? _enumerator;
    DeviceWatcher? _watcher;
    readonly object _lock = new();
    readonly AudioEndpointVolumeNotificationDelegate _pipeHandler;
    Settings _settings;
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
    string _targetId = "auto";
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

    /// <summary>Reason there's no auto target, or null if a target is resolved.</summary>
    public string? AutoStatus
    {
        get
        {
            if (_targetId != "auto" || _resolvedAutoTargetId != null) return null;
            return _gg.LastError ?? "no target";
        }
    }

    public VolumeRelayEngine(Settings settings)
    {
        _settings = settings;
        _targetId = settings.Target;
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
            // Hotplug tracking degrades; knob/mute sync keeps working.
        }

        RefreshDevices();
        AnchorPipe();

        // Resolve target (non-auto immediately; auto on first poll)
        lock (_lock)
        {
            // A saved target may no longer exist (driver uninstalled, different
            // port) or may have been a virtual device, which is never valid.
            if (_targetId != "auto" && !_devices.Any(d => d.Id == _targetId && !d.IsVirtual))
            {
                _targetId = "auto";
                _settings.Target = "auto";
                SettingsStore.Save(_settings);
            }

            if (_targetId != "auto")
                ResolveTargetLocked();
        }

        // Gentle startup: set pipe to pin value (only if not passive)
        lock (_lock)
        {
            if (!IsPassiveLocked() && _pipeVolume != null)
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
            if (_paused || IsPassiveLocked()) return;

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
                float delta = newVol - _lastPipeVolume;
                _lastPipeVolume = newVol;

                // Apply delta to target
                if (_targetVolume != null)
                {
                    try
                    {
                        float newTarget = Math.Clamp(_targetVolume.MasterVolumeLevelScalar + delta, 0f, 1f);
                        _targetVolume.MasterVolumeLevelScalar = newTarget;
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
            _settings.Target = id;
            SettingsStore.Save(_settings);
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

                    // Reset new default to pin value
                    if (!IsPassiveLocked())
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
