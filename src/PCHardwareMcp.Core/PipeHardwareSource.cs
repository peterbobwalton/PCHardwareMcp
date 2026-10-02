using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace PCHardwareMcp.Core;

/// <summary>Client side of the pipe: one persistent connection, one request at a time, reconnects on failure.</summary>
public sealed class PipeHardwareSource(TimeSpan connectTimeout) : IHardwareSource, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _nextId;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<JsonNode?> QueryAsync(string method, JsonObject? args = null, CancellationToken ct = default)
        => await QueryAsync(method, args, DefaultTimeout, ct);

    public async Task<JsonNode?> QueryAsync(string method, JsonObject? args, TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(ct);
                    return await RoundTripAsync(method, args, timeout, ct);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException && attempt == 0)
                {
                    // Service restarted or the pipe broke: reconnect once.
                    Reset();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Reset();
            throw new ServiceUnavailableException($"Lost connection to the {PipeProtocol.ServiceName} service: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            // Garbled stream: nothing after this point can be trusted to line up.
            Reset();
            throw new HardwareException($"Malformed response from the service ({ex.Message}); connection reset.", ex);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled mid-request: the late response would desync the next call, so drop the connection.
            Reset();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }
        Reset();
        var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(connectTimeout);
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync();
            throw new ServiceUnavailableException($"The {PipeProtocol.ServiceName} service is not running (pipe \\\\.\\pipe\\{PipeProtocol.PipeName} did not answer).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            await pipe.DisposeAsync();
            throw new ServiceUnavailableException($"Cannot connect to the {PipeProtocol.ServiceName} service: {ex.Message}", ex);
        }
        if (VerifyServer(pipe) is { } refusal)
        {
            await pipe.DisposeAsync();
            throw new ServiceUnavailableException(refusal);
        }
        _pipe = pipe;
        _reader = new StreamReader(pipe, PipeProtocol.Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, PipeProtocol.Utf8, bufferSize: 64 * 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
    }

    private async Task<JsonNode?> RoundTripAsync(string method, JsonObject? args, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject { ["id"] = id, ["method"] = method };
        if (args is not null)
        {
            request["args"] = args.DeepClone();
        }
        await _writer!.WriteLineAsync(PipeProtocol.ToLine(request).AsMemory(), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string? line;
        try
        {
            line = await _reader!.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The response may still arrive later and would desync the stream: drop the connection.
            Reset();
            throw new HardwareException($"The service did not answer '{method}' within {timeout.TotalSeconds:0}s.");
        }
        if (line is null)
        {
            throw new IOException("The service closed the pipe.");
        }

        var response = JsonNode.Parse(line) as JsonObject ?? throw new IOException("Malformed response from the service.");
        if (response["id"]?.GetValue<long>() != id)
        {
            Reset();
            throw new HardwareException("Out-of-order response from the service; connection reset.");
        }
        if (response["error"] is JsonNode error)
        {
            throw new HardwareException(error.GetValue<string>());
        }
        return response["result"]?.DeepClone();
    }

    // ------------------------------------------------------------------ server identity

    /// <summary>Set to 1 to accept any process serving the pipe, e.g. the service running under the VS debugger.</summary>
    public const string TrustAnyServerVariable = "PCHARDWAREMCP_TRUST_ANY_SERVICE";

    public static string InstalledServiceExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PCHardwareMcp", "PCHardwareMcp.Service.exe");

    /// <summary>
    /// Anyone can create a pipe called PCHardwareMcp while the service is stopped. Only trust the end that is the
    /// installed service binary (Program Files is admin-writable only), so a squatter can't feed Claude fake data or
    /// injected text. Returns null when trusted, otherwise the reason.
    /// </summary>
    private static string? VerifyServer(NamedPipeClientStream pipe)
    {
        if (Environment.GetEnvironmentVariable(TrustAnyServerVariable) == "1")
        {
            return null;
        }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
        {
            return $"Cannot identify the process serving the pipe (error {Marshal.GetLastWin32Error()}).";
        }
        var image = ProcessImage(pid);
        if (image is null)
        {
            return $"Cannot read the image path of pipe server process {pid}.";
        }
        return string.Equals(Path.GetFullPath(image), Path.GetFullPath(InstalledServiceExe), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Pipe \\.\pipe\{PipeProtocol.PipeName} is served by '{image}' (pid {pid}), not the installed service; ignoring it. " +
              $"Set {TrustAnyServerVariable}=1 to allow a debug build.";
    }

    private static string? ProcessImage(uint pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            return null;
        }
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    /// <summary>Drop the connection. Clears the fields first and swallows dispose errors: disposing a StreamWriter on a
    /// broken pipe (service restarted) flushes and throws, and must not leave the dead writer behind for the next call.</summary>
    private void Reset()
    {
        var (reader, writer, pipe) = (_reader, _writer, _pipe);
        _reader = null;
        _writer = null;
        _pipe = null;
        Quietly(() => writer?.Dispose());
        Quietly(() => reader?.Dispose());
        Quietly(() => pipe?.Dispose());

        static void Quietly(Action dispose)
        {
            try
            {
                dispose();
            }
            catch
            {
                // already broken
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
