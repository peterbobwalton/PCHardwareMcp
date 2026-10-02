using System.Text.Json;
using System.Text.Json.Nodes;
using PCHardwareMcp.Core;
using Microsoft.Extensions.Logging;

namespace PCHardwareMcp.Server;

/// <summary>
/// Routes queries to the elevated PCHardwareMcp service when it is running, otherwise to an in-process
/// (unelevated) collector so the tools still work - with fewer sensors.
/// </summary>
public sealed class HardwareSource(ILogger<HardwareSource> log) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RetryServiceAfter = TimeSpan.FromSeconds(30);

    private readonly PipeHardwareSource _pipe = new(ConnectTimeout);
    private readonly Lazy<LocalHardwareSource> _local = new(() => new LocalHardwareSource("in-process"));
    private DateTime _serviceDownUntil = DateTime.MinValue;
    private string? _lastServiceError;

    /// <summary>"service" or "in-process" for the most recent query (single-caller diagnostics like --selftest;
    /// concurrent tool calls use <see cref="QueryWithSourceAsync"/>).</summary>
    public string LastVia { get; private set; } = "none";

    public string? LastServiceError => _lastServiceError;

    public async Task<JsonNode?> QueryAsync(string method, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken ct = default)
        => (await QueryWithSourceAsync(method, args, timeout, ct)).Result;

    /// <summary>Query and report which path answered, without racing other concurrent calls.</summary>
    public async Task<(JsonNode? Result, string Via, string? ServiceError)> QueryWithSourceAsync(
        string method, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (DateTime.UtcNow >= _serviceDownUntil)
        {
            try
            {
                var result = await _pipe.QueryAsync(method, args, timeout ?? PipeHardwareSource.DefaultTimeout, ct);
                LastVia = "service";
                _lastServiceError = null;
                if (method == "storage" && result is JsonObject storage)
                {
                    // The service runs as SYSTEM and can't see this user's mapped or per-user drives.
                    storage["volumes"] = await Task.Run(SystemInfo.Volumes, ct);
                }
                return (result, "service", null);
            }
            catch (Exception ex) when (ex is ServiceUnavailableException or IOException or ObjectDisposedException)
            {
                if (_lastServiceError != ex.Message)
                {
                    log.LogWarning("Sensor service unavailable, using in-process fallback: {Message}", ex.Message);
                }
                _lastServiceError = ex.Message;
                _serviceDownUntil = DateTime.UtcNow + RetryServiceAfter;
            }
        }

        var serviceError = _lastServiceError;
        LastVia = "in-process";
        // Round-trip the args through text so the local path sees exactly what the service would.
        var normalized = args is null ? null : JsonNode.Parse(args.ToJsonString()) as JsonObject;
        var local = await _local.Value.QueryAsync(method, normalized, ct);
        if (local is JsonObject obj && method != "status")
        {
            obj["source"] = "in-process (service not running, not elevated: CPU temps/voltages, fans and SPD may be missing)";
        }
        return (local, "in-process", serviceError);
    }

    /// <summary>Force the next query to try the service again (after installing/starting it).</summary>
    public void RetryService() => _serviceDownUntil = DateTime.MinValue;

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync();
        if (_local.IsValueCreated)
        {
            _local.Value.Dispose();
        }
    }
}

public static class Results
{
    public static string Text(JsonNode? node) => node?.ToJsonString(PipeProtocol.Compact) ?? "null";

    public static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, PipeProtocol.Compact);
}
