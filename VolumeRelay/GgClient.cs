using System.IO;
using System.Text.Json;

namespace VolumeRelay;

/// <summary>
/// Best-effort client for the SteelSeries Sonar local HTTP API.
/// Discovery chain: coreProps.json → /subApps → sonar port → /classicRedirections.
/// The API is undocumented internal IPC; every stage is try/catch.
/// </summary>
public class GgClient
{
    readonly object _lock = new();
    HttpClient? _httpClient;
    string? _sonarUrl;
    DateTime _lastDiscovery = DateTime.MinValue;

    /// <summary>Reason the last call found no target, or null on success.</summary>
    public string? LastError { get; private set; }

    // Stage-by-stage record of the last discovery/API attempt, for diagnostics.
    // The API is undocumented and moves between GG versions; without this trace
    // a parse failure is indistinguishable from "GG is off".
    const int TraceCapacity = 60;
    const int RawJsonChars = 1500;
    readonly List<string> _trace = new();

    /// <summary>Multi-line trace of recent discovery/API activity (oldest first).</summary>
    public string TraceText
    {
        get { lock (_lock) return string.Join("\n", _trace); }
    }

    void Trace(string line)
    {
        lock (_lock)
        {
            _trace.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
            while (_trace.Count > TraceCapacity) _trace.RemoveAt(0);
        }
    }

    static string Truncate(string s) => s.Length <= RawJsonChars ? s : s[..RawJsonChars] + "…(truncated)";

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Returns the endpoint ID that all render channels map to (strict auto rule),
    /// or null if split / unavailable. Sets <see cref="LastError"/> on failure.
    /// </summary>
    public string? GetSelectedOutput()
    {
        lock (_lock)
        {
            try
            {
                if (!EnsureConnection())
                {
                    LastError = "GG unavailable";
                    Trace("getSelectedOutput: no connection (see discovery lines above)");
                    return null;
                }

                var json = _httpClient!.GetStringAsync(_sonarUrl! + "/classicRedirections")
                    .GetAwaiter().GetResult();
                Trace("classicRedirections body: " + Truncate(json));
                var result = ParseStrictAuto(json);
                LastError = result == null ? "split routing — pick a device" : null;
                Trace(result == null
                    ? "parse: no single target (no render channels, or split across devices)"
                    : "parse: target " + result);
                return result;
            }
            catch (Exception ex)
            {
                Reset();
                LastError = "GG unavailable";
                Trace("classicRedirections failed: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// Strict auto rule: consider only render channels (game/media/chat/aux).
    /// A channel counts if it has a non-empty deviceId (isRunning is ignored).
    /// All counting channels must map to one device; otherwise null (split).
    /// </summary>
    string? ParseStrictAuto(string json)
    {
        List<Redirection> list;
        try
        {
            list = JsonSerializer.Deserialize<List<Redirection>>(json, JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            Trace("parse: JSON shape not understood — " + ex.Message);
            return null;
        }

        var render = list.Where(r =>
            r.id is "game" or "media" or "chat" or "aux" &&
            !string.IsNullOrEmpty(r.deviceId)).ToList();

        Trace($"parse: {render.Count} render channels with a deviceId, " +
              $"devices=[{string.Join(", ", render.Select(r => r.deviceId).Distinct())}]");

        if (render.Count == 0) return null;
        var devices = render.Select(r => r.deviceId).Distinct().ToList();
        return devices.Count == 1 ? devices[0] : null;
    }

    bool EnsureConnection()
    {
        if (_httpClient != null && _sonarUrl != null) return true;
        if ((DateTime.Now - _lastDiscovery).TotalSeconds < 10) return false;
        _lastDiscovery = DateTime.Now;

        try
        {
            // 1. Read coreProps.json
            const string corePropsPath = @"C:\ProgramData\SteelSeries\GG\coreProps.json";
            if (!File.Exists(corePropsPath))
            {
                Trace("discovery: coreProps.json not found (GG not installed, or non-default install)");
                return false;
            }

            var corePropsRaw = File.ReadAllText(corePropsPath);
            Trace("coreProps.json: " + Truncate(corePropsRaw));
            var coreProps = JsonSerializer.Deserialize<CoreProps>(corePropsRaw, JsonOpts);
            var coreAddr = coreProps?.ggEncryptedAddress;
            if (string.IsNullOrEmpty(coreAddr))
            {
                Trace("discovery: ggEncryptedAddress missing/empty in coreProps.json");
                return false;
            }

            // 2. GET https://<core>/subApps (self-signed cert → skip validation)
            var discHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var discHttp = new HttpClient(discHandler) { Timeout = TimeSpan.FromSeconds(3) };
            var subAppsJson = discHttp.GetStringAsync(
                "https://" + coreAddr + "/subApps").GetAwaiter().GetResult();
            Trace("subApps body: " + Truncate(subAppsJson));
            var subApps = JsonSerializer.Deserialize<SubAppsResponse>(subAppsJson, JsonOpts);
            var sonarAddr = subApps?.subApps?.sonar?.metadata?.webServerAddress;
            if (string.IsNullOrEmpty(sonarAddr))
            {
                Trace("discovery: no sonar webServerAddress in /subApps");
                return false;
            }

            // 3. Store sonar URL for subsequent /classicRedirections calls
            _sonarUrl = sonarAddr;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            Trace("discovery: ok, sonar at " + sonarAddr);
            return true;
        }
        catch (Exception ex)
        {
            Trace("discovery failed: " + ex.Message);
            return false;
        }
    }

    void Reset()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        _sonarUrl = null;
    }

    // ---- JSON models ----

    class CoreProps
    {
        public string? ggEncryptedAddress { get; set; }
    }

    class SubAppsResponse
    {
        public SubAppsDict? subApps { get; set; }
    }

    class SubAppsDict
    {
        public SonarApp? sonar { get; set; }
    }

    class SonarApp
    {
        public Metadata? metadata { get; set; }
    }

    class Metadata
    {
        public string? webServerAddress { get; set; }
    }

    class Redirection
    {
        public string id { get; set; } = "";
        public string deviceId { get; set; } = "";
        public bool isRunning { get; set; }
    }
}
