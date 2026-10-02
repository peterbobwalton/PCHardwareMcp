using System.IO.Pipes;
using System.Text.Json.Nodes;
using PCHardwareMcp.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PCHardwareMcp.Service;

/// <summary>Serves hardware queries to local users over \\.\pipe\PCHardwareMcp. Read-only: no method changes the machine.</summary>
public sealed class PipeServer(LocalHardwareSource source, ILogger<PipeServer> log) : BackgroundService
{
    private const int MaxClients = 16;
    private readonly SemaphoreSlim _slots = new(MaxClients, MaxClients);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Open LibreHardwareMonitor in the background so the first request doesn't pay for device enumeration.
        _ = Task.Run(() =>
        {
            try
            {
                source.WarmUp();
                source.Hub.StartPolling(TimeSpan.FromSeconds(1));
                log.LogInformation("LibreHardwareMonitor {Version} ready", SensorHub.LibraryVersion);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Opening LibreHardwareMonitor failed");
            }
        }, stoppingToken);

        var security = PipeProtocol.CreateSecurity();
        log.LogInformation("Listening on \\\\.\\pipe\\{Pipe}", PipeProtocol.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _slots.WaitAsync(stoppingToken);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(PipeProtocol.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 64 * 1024, outBufferSize: 64 * 1024, security);
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                _slots.Release();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _slots.Release();
                log.LogError(ex, "Pipe listen failed; retrying");
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            var connected = pipe;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(connected, stoppingToken);
                }
                finally
                {
                    await connected.DisposeAsync();
                    _slots.Release();
                }
            }, stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, PipeProtocol.Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, PipeProtocol.Utf8, bufferSize: 64 * 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    return;
                }
                if (line.Length > PipeProtocol.MaxMessageChars)
                {
                    log.LogWarning("Dropping client: request of {Length} chars", line.Length);
                    return;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var response = await HandleAsync(line, ct);
                await writer.WriteLineAsync(PipeProtocol.ToLine(response).AsMemory(), ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // client went away or the service is stopping
        }
    }

    private async Task<JsonObject> HandleAsync(string line, CancellationToken ct)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(line) as JsonObject ?? throw new HardwareException("Request must be a JSON object.");
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? throw new HardwareException("Missing 'method'.");
            var args = request["args"] as JsonObject;
            var result = await source.QueryAsync(method, args, ct);
            return new JsonObject { ["id"] = id, ["result"] = result };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not HardwareException)
            {
                log.LogWarning(ex, "Request failed");
            }
            return new JsonObject { ["id"] = id, ["error"] = ex.Message };
        }
    }
}
