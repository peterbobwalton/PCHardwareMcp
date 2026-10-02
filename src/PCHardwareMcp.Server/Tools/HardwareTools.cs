using System.ComponentModel;
using System.Text.Json.Nodes;
using PCHardwareMcp.Core;
using ModelContextProtocol.Server;

namespace PCHardwareMcp.Server.Tools;

[McpServerToolType]
public static class HardwareTools
{
    private static async Task<string> Run(HardwareSource hw, string method, JsonObject? args, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return Results.Text(await hw.QueryAsync(method, args, timeout, ct));
        }
        catch (HardwareException ex)
        {
            return Results.Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surface the real cause instead of the SDK's generic "An error occurred invoking ...".
            return Results.Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static JsonObject Args(params (string key, JsonNode? value)[] items)
    {
        var o = new JsonObject();
        foreach (var (k, v) in items)
        {
            if (v is not null)
            {
                o[k] = v;
            }
        }
        return o;
    }

    [McpServerTool(Name = "hardware_status", ReadOnly = true, Idempotent = true, Title = "Hardware status")]
    [Description("Whether the elevated sensor service is running, elevation, PawnIO driver presence, LibreHardwareMonitor version and what is missing. Check this when sensors look incomplete.")]
    public static async Task<string> Status(HardwareSource hw, CancellationToken ct)
    {
        try
        {
            hw.RetryService();
            var (result, via, serviceError) = await hw.QueryWithSourceAsync("status", null, null, ct);
            var status = result as JsonObject ?? new JsonObject();
            status["via"] = via;
            if (serviceError is not null)
            {
                status["serviceError"] = serviceError;
            }
            return Results.Text(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "get_summary", ReadOnly = true, Title = "System summary")]
    [Description("One-shot overview: CPU (name, cores/threads, clocks, temperature, load, power), memory (installed DIMMs, used), board, BIOS, OS, GPUs and drives with temperatures. Start here.")]
    public static Task<string> Summary(HardwareSource hw, CancellationToken ct) => Run(hw, "summary", null, ct);

    [McpServerTool(Name = "get_cpu", ReadOnly = true, Title = "CPU details")]
    [Description("CPU-Z CPU tab: CPUID vendor/brand/family/model/stepping, instruction sets, packages/dies/cores/threads (P/E core classes), cache hierarchy, plus live per-core clocks, temperatures, load, power and voltages.")]
    public static Task<string> Cpu(HardwareSource hw,
        [Description("Include live sensors (default true)")] bool? sensors = null,
        CancellationToken ct = default) => Run(hw, "cpu", Args(("sensors", sensors)), ct);

    [McpServerTool(Name = "get_memory", ReadOnly = true, Title = "Memory details")]
    [Description("Installed DIMMs (slot, size, DDR type, rated vs configured speed, voltage, ranks, ECC, manufacturer, part number), slot count, total/available RAM, and memory sensors including per-DIMM temperatures when available.")]
    public static Task<string> Memory(HardwareSource hw,
        [Description("Include live sensors (default true)")] bool? sensors = null,
        CancellationToken ct = default) => Run(hw, "memory", Args(("sensors", sensors)), ct);

    [McpServerTool(Name = "get_gpu", ReadOnly = true, Title = "GPU details")]
    [Description("Graphics adapters (driver version/date, resolution, refresh) and live GPU sensors: core/memory clocks, temperatures incl. hot spot, load, power, fan, VRAM used/total.")]
    public static Task<string> Gpu(HardwareSource hw,
        [Description("Include live sensors (default true)")] bool? sensors = null,
        CancellationToken ct = default) => Run(hw, "gpu", Args(("sensors", sensors)), ct);

    [McpServerTool(Name = "get_motherboard", ReadOnly = true, Title = "Motherboard details")]
    [Description("System/baseboard model, BIOS vendor/version/date, SMBIOS, OS, and Super I/O / embedded controller sensors: fan speeds, voltages, board temperatures, plus coolers and PSUs LibreHardwareMonitor supports.")]
    public static Task<string> Motherboard(HardwareSource hw,
        [Description("Include live sensors (default true)")] bool? sensors = null,
        CancellationToken ct = default) => Run(hw, "motherboard", Args(("sensors", sensors)), ct);

    [McpServerTool(Name = "get_storage", ReadOnly = true, Title = "Storage details")]
    [Description("Physical disks (bus, SSD/HDD, size, health, firmware), volumes with free space, and drive sensors: temperatures, remaining life, data read/written, throughput.")]
    public static Task<string> Storage(HardwareSource hw,
        [Description("Include live sensors (default true)")] bool? sensors = null,
        CancellationToken ct = default) => Run(hw, "storage", Args(("sensors", sensors)), ct);

    [McpServerTool(Name = "get_network", ReadOnly = true, Title = "Network adapters")]
    [Description("Network adapters (type, link speed, MAC, IPv4/IPv6, gateways, DNS) and live throughput/utilisation sensors.")]
    public static Task<string> Network(HardwareSource hw,
        [Description("Include live sensors (default true)")] bool? sensors = null,
        [Description("Also list adapters that are down (default false)")] bool? includeDown = null,
        CancellationToken ct = default) => Run(hw, "network", Args(("sensors", sensors), ("include_down", includeDown)), ct);

    [McpServerTool(Name = "get_sensors", ReadOnly = true, Title = "Query sensors")]
    [Description("Flat list of live sensors across all hardware with current/min/max, filtered by any combination of hardware, type and name (case-insensitive). Example: type='Temperature'; hardware='gpu', type='Power'; name='Core #1'.")]
    public static Task<string> Sensors(HardwareSource hw,
        [Description("Hardware name or type substring: cpu, gpu, memory, motherboard, superio, storage, network, or a device name")] string? hardware = null,
        [Description("Sensor type: Temperature, Load, Clock, Power, Voltage, Current, Fan, Control, Data, SmallData, Throughput, Level, Factor, Frequency, Energy, TimeSpan")] string? type = null,
        [Description("Sensor name substring, e.g. 'Package', 'Hot Spot', 'Core #3'")] string? name = null,
        CancellationToken ct = default) => Run(hw, "sensors", Args(("hardware", hardware), ("type", type), ("name", name)), ct);

    [McpServerTool(Name = "list_hardware", ReadOnly = true, Title = "List hardware")]
    [Description("Tree of every device LibreHardwareMonitor detected with the sensor types and counts each has. Use it to discover names for get_sensors filters.")]
    public static Task<string> ListHardware(HardwareSource hw, CancellationToken ct) => Run(hw, "hardware", null, ct);

    [McpServerTool(Name = "sample_sensors", ReadOnly = true, Title = "Sample sensors over time")]
    [Description("Poll matching sensors repeatedly and return min/avg/max/last per sensor. Use while something runs (game, build, benchmark) to catch peaks, throttling or fan behaviour. Narrow it with filters; sampling everything is large.")]
    public static Task<string> Sample(HardwareSource hw,
        [Description("How long to sample, 1-120 seconds (default 10)")] double? durationSeconds = null,
        [Description("Interval between samples, 250-10000 ms (default 1000)")] double? intervalMs = null,
        [Description("Hardware name or type substring (see get_sensors)")] string? hardware = null,
        [Description("Sensor type (see get_sensors)")] string? type = null,
        [Description("Sensor name substring")] string? name = null,
        CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(durationSeconds ?? 10, 1, 120) + 60);
        return Run(hw, "sample", Args(("duration_seconds", durationSeconds), ("interval_ms", intervalMs),
            ("hardware", hardware), ("type", type), ("name", name)), ct, timeout);
    }

    [McpServerTool(Name = "reset_min_max", ReadOnly = false, Destructive = false, Idempotent = true, Title = "Reset min/max")]
    [Description("Reset the min/max recorded for every sensor, e.g. before starting a load test, so get_sensors min/max cover only what follows.")]
    public static Task<string> ResetMinMax(HardwareSource hw, CancellationToken ct) => Run(hw, "reset_min_max", null, ct);

    [McpServerTool(Name = "get_report", ReadOnly = true, Title = "LibreHardwareMonitor report")]
    [Description("Raw LibreHardwareMonitor report text (sensor tree plus low-level chip dumps: Super I/O registers, SMBus, NVMe/SMART, GPU details). Large; for diagnosing unsupported hardware or missing sensors.")]
    public static Task<string> Report(HardwareSource hw,
        [Description("Truncate to this many characters (default 60000)")] int? maxChars = null,
        CancellationToken ct = default) => Run(hw, "report", Args(("max_chars", maxChars)), ct);
}
