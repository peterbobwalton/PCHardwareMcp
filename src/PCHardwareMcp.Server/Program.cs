using PCHardwareMcp.Core;
using PCHardwareMcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Usage:
//   PCHardwareMcp.Server                      MCP server on stdio (what Claude Desktop runs)
//   PCHardwareMcp.Server --selftest [--out file.txt]   print status + summary as JSON (and save to a file), exit
//   PCHardwareMcp.Server --register [--name pc-hardware]     add to Claude Desktop config
//   PCHardwareMcp.Server --unregister [--name pc-hardware]   remove from Claude Desktop config

if (args.Contains("--register") || args.Contains("--unregister"))
{
    return ClaudeConfig.Run(args);
}

if (args.Contains("--selftest"))
{
    var i = Array.IndexOf(args, "--out");
    return await SelfTest.RunAsync(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<HardwareSource>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "pc-hardware", Version = typeof(SelfTest).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
        o.ServerInstructions =
            "Reads this PC's hardware like CPU-Z/HWiNFO: identification (CPUID, caches, topology, board, BIOS, DIMMs, GPUs, drives, NICs) " +
            "and live sensors (clocks, temperatures, load, power, voltages, fans, drive health) via LibreHardwareMonitor. " +
            "Start with get_summary. Use the per-area tools (get_cpu, get_memory, get_gpu, get_motherboard, get_storage, get_network) for detail, " +
            "get_sensors to filter across everything, and sample_sensors to watch values over time (load tests, throttling). " +
            "Full sensor coverage needs the elevated PCHardwareMcp service; hardware_status says whether it is running and what is missing. " +
            "All tools are read-only.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

internal static class SelfTest
{
    public static async Task<int> RunAsync(string? outFile)
    {
        var log = new StringWriter();
        var code = await RunAsync(log);
        Console.Write(log.ToString());
        if (outFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            await File.WriteAllTextAsync(outFile, log.ToString());
        }
        return code;
    }

    private static async Task<int> RunAsync(TextWriter Console)
    {
        await using var source = new HardwareSource(NullLogger<HardwareSource>.Instance);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var status = await source.QueryAsync("status");
            Console.WriteLine($"status via {source.LastVia} ({sw.ElapsedMilliseconds} ms):");
            Console.WriteLine(Results.Text(status));
            if (source.LastServiceError is { } err)
            {
                Console.WriteLine($"  service: {err}");
            }

            sw.Restart();
            var summary = await source.QueryAsync("summary");
            Console.WriteLine($"summary via {source.LastVia} ({sw.ElapsedMilliseconds} ms):");
            Console.WriteLine(Results.Text(summary));

            sw.Restart();
            var sensors = await source.QueryAsync("sensors");
            Console.WriteLine($"sensors via {source.LastVia} ({sw.ElapsedMilliseconds} ms): {sensors?["count"]}");

            foreach (var area in new[] { "cpu", "memory", "gpu", "motherboard", "storage", "network", "hardware" })
            {
                sw.Restart();
                var result = await source.QueryAsync(area);
                Console.WriteLine($"{area} via {source.LastVia} ({sw.ElapsedMilliseconds} ms):");
                Console.WriteLine(Results.Text(result));
            }
            Console.WriteLine("SELFTEST OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELFTEST FAILED: {ex}");
            return 1;
        }
    }
}
