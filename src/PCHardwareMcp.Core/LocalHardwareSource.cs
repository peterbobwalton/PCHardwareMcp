using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PCHardwareMcp.Core;

/// <summary>
/// Answers every query in this process. The elevated service wraps one of these behind the pipe; the MCP server
/// falls back to its own (unelevated) instance when the service is not installed.
/// </summary>
public sealed class LocalHardwareSource(string mode) : IHardwareSource, IDisposable
{
    private readonly Lazy<SensorHub> _hub = new(() => new SensorHub(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly DateTime _started = DateTime.UtcNow;

    // Static inventory barely changes; cache it so repeated tool calls stay fast.
    private readonly Lazy<JsonObject> _cpuId = new(CpuInfo.Identify);
    private readonly Lazy<JsonObject> _topology = new(CpuInfo.Topology);

    private static readonly System.Text.RegularExpressions.Regex CoreClock = new(@"^Core #\d+$");

    public string Mode { get; } = mode;

    public SensorHub Hub => _hub.Value;

    /// <summary>Open LibreHardwareMonitor now (takes a few seconds) instead of on the first query.</summary>
    public void WarmUp() => _ = _hub.Value;

    public Task<JsonNode?> QueryAsync(string method, JsonObject? args = null, CancellationToken ct = default) =>
        Task.Run(() => Invoke(method, args, ct), ct);

    public JsonNode? Invoke(string method, JsonObject? args, CancellationToken ct = default) => method switch
    {
        "ping" => new JsonObject { ["pong"] = true, ["mode"] = Mode },
        "status" => Status(),
        "summary" => Summary(),
        "cpu" => Cpu(Flag(args, "sensors", true)),
        "memory" => Memory(Flag(args, "sensors", true)),
        "gpu" => Gpu(Flag(args, "sensors", true)),
        "motherboard" => Motherboard(Flag(args, "sensors", true)),
        "storage" => Storage(Flag(args, "sensors", true)),
        "network" => Network(Flag(args, "sensors", true), Flag(args, "include_down", false)),
        "sensors" => Sensors(SensorFilter.From(args)),
        "hardware" => HardwareTree(),
        "sample" => Sample(args, ct),
        "reset_min_max" => ResetMinMax(),
        "report" => Report(args),
        _ => throw new HardwareException($"Unknown method '{method}'."),
    };

    // ------------------------------------------------------------------ queries

    private JsonObject Status()
    {
        var identity = WindowsIdentity.GetCurrent();
        var elevated = identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var node = new JsonObject
        {
            ["mode"] = Mode,
            ["account"] = identity.Name,
            ["elevated"] = elevated,
            ["pawnIoDriverInstalled"] = PawnIoInstalled(),
            ["libreHardwareMonitor"] = SensorHub.LibraryVersion,
            ["version"] = typeof(LocalHardwareSource).Assembly.GetName().Version?.ToString(3),
            ["processId"] = Environment.ProcessId,
            ["uptimeSeconds"] = (long)(DateTime.UtcNow - _started).TotalSeconds,
        };
        if (_hub.IsValueCreated)
        {
            Hub.Read(all =>
            {
                node["hardwareCount"] = all.Count;
                node["sensorCount"] = all.Sum(h => h.Sensors.Length);
                return 0;
            }, refresh: false);
        }
        var notes = new JsonArray();
        if (!elevated)
        {
            notes.Add("Not elevated: CPU temperatures/voltages, Super I/O fans and DIMM SPD data are unavailable. Install the PCHardwareMcp service.");
        }
        if (PawnIoInstalled() == false)
        {
            notes.Add("PawnIO driver not installed: LibreHardwareMonitor cannot read MSRs, Super I/O or SMBus. Install it with: winget install namazso.PawnIO");
        }
        if (notes.Count > 0)
        {
            node["notes"] = notes;
        }
        return node;
    }

    private JsonObject Summary()
    {
        var cpuId = _cpuId.Value;
        var topo = _topology.Value;
        var memory = SystemInfo.Memory();
        var board = SystemInfo.Board();

        var node = new JsonObject
        {
            ["cpu"] = new JsonObject
            {
                ["name"] = cpuId["brand"]?.DeepClone(),
                ["cores"] = topo["cores"]?.DeepClone(),
                ["threads"] = topo["threads"]?.DeepClone(),
                ["coreClasses"] = topo["coreClasses"]?.DeepClone(),
            },
            ["memory"] = new JsonObject
            {
                ["installed"] = memory["summary"]?.DeepClone(),
                ["totalGB"] = memory["os"]?["totalGB"]?.DeepClone(),
                ["availableGB"] = memory["os"]?["availableGB"]?.DeepClone(),
            },
            ["board"] = Join(board["baseboard"]?["manufacturer"], board["baseboard"]?["model"]),
            ["bios"] = Join(board["bios"]?["vendor"], board["bios"]?["version"], board["bios"]?["date"]),
            ["os"] = SystemInfo.OperatingSystem(),
        };

        Hub.Read(all =>
        {
            var cpu = all.Where(h => SensorHub.Matches(h, ["Cpu"])).ToList();
            var cpuNode = (JsonObject)node["cpu"]!;
            cpuNode["temperatureC"] = SensorHub.Num(SensorHub.Pick(cpu, "Temperature", "Package", "Tctl/Tdie", "Tdie", "Tctl", "Core Max", "Core Average"));
            cpuNode["loadPercent"] = SensorHub.Num(SensorHub.Pick(cpu, "Load", "CPU Total"));
            cpuNode["powerW"] = SensorHub.Num(SensorHub.Pick(cpu, "Power", "Package"));
            // Per-core reported clocks only ("Core #12"), not "(Effective)" ones (near 0 on parked cores) or averages.
            var clocks = cpu.SelectMany(h => h.Sensors)
                .Where(s => s.SensorType.ToString() == "Clock" && CoreClock.IsMatch(s.Name) && s.Value is > 0)
                .Select(s => s.Value!.Value).ToList();
            if (clocks.Count > 0)
            {
                cpuNode["clockMHz"] = new JsonObject
                {
                    ["min"] = SensorHub.Num(clocks.Min()),
                    ["avg"] = SensorHub.Num(clocks.Average()),
                    ["max"] = SensorHub.Num(clocks.Max()),
                };
            }

            // Physical RAM ("Total Memory", /ram), not "Virtual Memory" (/vram, commit charge) which comes first.
            var memLoad = SensorHub.Pick(all.Where(h => h.Identifier.ToString() == "/ram"), "Load", "Memory");
            ((JsonObject)node["memory"]!)["loadPercent"] = SensorHub.Num(memLoad);

            var gpus = new JsonArray();
            foreach (var gpu in all.Where(h => SensorHub.Matches(h, ["Gpu"])))
            {
                gpus.Add(new JsonObject
                {
                    ["name"] = gpu.Name,
                    ["temperatureC"] = SensorHub.Num(SensorHub.Pick([gpu], "Temperature", "GPU Core", "GPU Hot Spot", "Core")),
                    ["loadPercent"] = SensorHub.Num(SensorHub.Pick([gpu], "Load", "GPU Core", "D3D 3D")),
                    ["powerW"] = SensorHub.Num(SensorHub.Pick([gpu], "Power", "GPU Package", "GPU Power", "Package")),
                    ["memoryTotalMB"] = SensorHub.Num(SensorHub.Pick([gpu], "SmallData", "GPU Memory Total", "Memory Total")),
                });
            }
            node["gpus"] = gpus;

            var drives = new JsonArray();
            foreach (var disk in all.Where(h => SensorHub.Matches(h, ["Storage"])))
            {
                drives.Add(new JsonObject
                {
                    ["name"] = disk.Name,
                    ["temperatureC"] = SensorHub.Num(SensorHub.Pick([disk], "Temperature", "Composite", "Temperature")),
                });
            }
            node["drives"] = drives;
            return 0;
        });

        if (Mode != "service")
        {
            node["source"] = "in-process, not elevated: some sensors are missing";
        }
        return node;
    }

    private JsonObject Cpu(bool sensors)
    {
        var node = new JsonObject
        {
            ["cpuid"] = _cpuId.Value.DeepClone(),
            ["topology"] = _topology.Value.DeepClone(),
            ["wmi"] = SystemInfo.Processors(),
        };
        if (sensors)
        {
            node["sensors"] = Hub.HardwareOfType("Cpu");
        }
        return node;
    }

    private JsonObject Memory(bool sensors)
    {
        var node = SystemInfo.Memory();
        if (sensors)
        {
            // Includes per-DIMM SPD hubs (temperatures) when PawnIO + elevation allow SMBus access.
            node["sensors"] = Hub.HardwareOfType("Memory");
        }
        return node;
    }

    private JsonObject Gpu(bool sensors)
    {
        var node = new JsonObject { ["adapters"] = SystemInfo.VideoControllers() };
        if (sensors)
        {
            node["sensors"] = Hub.HardwareOfType("Gpu");
        }
        return node;
    }

    private JsonObject Motherboard(bool sensors)
    {
        var node = SystemInfo.Board();
        node["os"] = SystemInfo.OperatingSystem();
        if (sensors)
        {
            // Motherboard nests its Super I/O chips (fans, voltages, board temps); also include coolers, EC, PSUs, batteries.
            node["sensors"] = Hub.HardwareOfType("Motherboard", "SuperIO", "EmbeddedController", "Cooler", "Psu", "Battery");
        }
        return node;
    }

    private JsonObject Storage(bool sensors)
    {
        var node = SystemInfo.Storage();
        if (sensors)
        {
            node["sensors"] = Hub.HardwareOfType("Storage");
        }
        return node;
    }

    private JsonObject Network(bool sensors, bool includeDown)
    {
        var node = new JsonObject { ["adapters"] = SystemInfo.Network(includeDown) };
        if (sensors)
        {
            var list = Hub.HardwareOfType("Network");
            foreach (var n in list.OfType<JsonObject>().Where(n => SystemInfo.IsFilterBinding(n["name"]?.ToString() ?? "")).ToList())
            {
                list.Remove(n);
            }
            node["sensors"] = list;
        }
        return node;
    }

    private JsonObject Sensors(SensorFilter filter) => Hub.Read(all =>
    {
        var list = new JsonArray();
        foreach (var s in all.SelectMany(h => h.Sensors).Where(filter.Matches))
        {
            list.Add(SensorHub.SensorNode(s));
        }
        return new JsonObject { ["count"] = list.Count, ["sensors"] = list };
    });

    private JsonObject HardwareTree() => Hub.Read(all =>
    {
        var roots = new JsonArray();
        foreach (var hw in all.Where(h => h.Parent is null))
        {
            roots.Add(Tree(hw));
        }
        return new JsonObject { ["hardware"] = roots };

        static JsonObject Tree(IHardware hw)
        {
            var node = new JsonObject
            {
                ["name"] = hw.Name,
                ["type"] = hw.HardwareType.ToString(),
                ["id"] = hw.Identifier.ToString(),
                ["sensorTypes"] = string.Join(", ", hw.Sensors.GroupBy(s => s.SensorType).Select(g => $"{g.Key} x{g.Count()}")),
            };
            if (hw.SubHardware.Length > 0)
            {
                node["subHardware"] = new JsonArray(hw.SubHardware.Select(s => (JsonNode)Tree(s)).ToArray());
            }
            return node;
        }
    });

    /// <summary>Poll matching sensors repeatedly and return min/avg/max per sensor - for load tests and spotting throttling.</summary>
    private JsonObject Sample(JsonObject? args, CancellationToken ct)
    {
        var filter = SensorFilter.From(args);
        var duration = Math.Clamp(Number(args, "duration_seconds", 10), 1, 120);
        var interval = Math.Clamp(Number(args, "interval_ms", 1000), 250, 10_000);

        var stats = new Dictionary<string, (ISensor sensor, double min, double max, double sum, int n)>();
        var sw = Stopwatch.StartNew();
        var samples = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Hub.Refresh(force: true);
            Hub.Read(all =>
            {
                foreach (var s in all.SelectMany(h => h.Sensors).Where(filter.Matches))
                {
                    if (s.Value is not { } f || !float.IsFinite(f))
                    {
                        continue;
                    }
                    var key = s.Identifier.ToString();
                    stats[key] = stats.TryGetValue(key, out var st)
                        ? (s, Math.Min(st.min, f), Math.Max(st.max, f), st.sum + f, st.n + 1)
                        : (s, f, f, f, 1);
                }
                return 0;
            }, refresh: false);
            samples++;
            if (sw.Elapsed.TotalSeconds + interval / 1000.0 > duration)
            {
                break;
            }
            Thread.Sleep((int)interval);
        }

        var list = new JsonArray();
        foreach (var (_, st) in stats.OrderBy(k => k.Value.sensor.Hardware.Name).ThenBy(k => k.Value.sensor.SensorType).ThenBy(k => k.Value.sensor.Index))
        {
            list.Add(new JsonObject
            {
                ["hardware"] = st.sensor.Hardware.Name,
                ["name"] = st.sensor.Name,
                ["type"] = st.sensor.SensorType.ToString(),
                ["unit"] = SensorHub.Unit(st.sensor.SensorType),
                ["min"] = Math.Round(st.min, 2),
                ["avg"] = Math.Round(st.sum / st.n, 2),
                ["max"] = Math.Round(st.max, 2),
                ["last"] = SensorHub.Num(st.sensor.Value),
            });
        }
        return new JsonObject
        {
            ["samples"] = samples,
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["count"] = list.Count,
            ["sensors"] = list,
        };
    }

    private JsonObject ResetMinMax()
    {
        Hub.ResetMinMax();
        return new JsonObject { ["reset"] = true };
    }

    private JsonObject Report(JsonObject? args)
    {
        var max = (int)Math.Clamp(Number(args, "max_chars", 60_000), 1_000, 1_000_000);
        var text = Hub.Report();
        return new JsonObject
        {
            ["length"] = text.Length,
            ["truncated"] = text.Length > max,
            ["report"] = text.Length > max ? text[..max] : text,
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>PawnIO registers a kernel driver service named "PawnIO". null when the registry can't be read.</summary>
    public static bool? PawnIoInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Flag(JsonObject? args, string name, bool fallback) =>
        args?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static double Number(JsonObject? args, string name, double fallback) =>
        args?[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    private static string? Join(params JsonNode?[] parts)
    {
        var s = string.Join(" ", parts.Where(p => p is not null).Select(p => p!.ToString()));
        return s.Length == 0 ? null : s;
    }

    public void Dispose()
    {
        if (_hub.IsValueCreated)
        {
            _hub.Value.Dispose();
        }
    }
}
