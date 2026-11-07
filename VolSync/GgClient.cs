using System.IO;
using System.Text.Json;

namespace VolSync;

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
                    return null;
                }

                var json = _httpClient!.GetStringAsync(_sonarUrl! + "/classicRedirections")
                    .GetAwaiter().GetResult();
                var result = ParseStrictAuto(json);
                LastError = result == null ? "split routing — pick a device" : null;
                return result;
            }
            catch
            {
                Reset();
                LastError = "GG unavailable";
                return null;
            }
        }
    }

    /// <summary>
    /// Strict auto rule: consider only render channels (game/media/chat/aux).
    /// A channel counts if it has a non-empty deviceId (isRunning is ignored).
    /// All counting channels must map to one device; otherwise null (split).
    /// </summary>
    static string? ParseStrictAuto(string json)
    {
        var list = JsonSerializer.Deserialize<List<Redirection>>(json, JsonOpts) ?? new();
        var render = list.Where(r =>
            r.id is "game" or "media" or "chat" or "aux" &&
            !string.IsNullOrEmpty(r.deviceId)).ToList();

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
            if (!File.Exists(corePropsPath)) return false;

            var coreProps = JsonSerializer.Deserialize<CoreProps>(
                File.ReadAllText(corePropsPath), JsonOpts);
            var coreAddr = coreProps?.ggEncryptedAddress;
            if (string.IsNullOrEmpty(coreAddr)) return false;

            // 2. GET https://<core>/subApps (self-signed cert → skip validation)
            var discHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var discHttp = new HttpClient(discHandler) { Timeout = TimeSpan.FromSeconds(3) };
            var subAppsJson = discHttp.GetStringAsync(
                "https://" + coreAddr + "/subApps").GetAwaiter().GetResult();
            var subApps = JsonSerializer.Deserialize<SubAppsResponse>(subAppsJson, JsonOpts);
            var sonarAddr = subApps?.subApps?.sonar?.metadata?.webServerAddress;
            if (string.IsNullOrEmpty(sonarAddr)) return false;

            // 3. Store sonar URL for subsequent /classicRedirections calls
            _sonarUrl = sonarAddr;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return true;
        }
        catch
        {
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
