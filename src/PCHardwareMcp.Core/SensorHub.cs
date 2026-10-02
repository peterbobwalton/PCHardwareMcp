using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;

namespace PCHardwareMcp.Core;

/// <summary>Which sensors a query wants. All parts are optional, case-insensitive, and combined with AND.</summary>
public sealed record SensorFilter(string? Hardware = null, string? Type = null, string? Name = null)
{
    public static SensorFilter From(JsonObject? args) => new(
        args?["hardware"]?.GetValue<string>(),
        args?["type"]?.GetValue<string>(),
        args?["name"]?.GetValue<string>());

    public bool Matches(ISensor sensor)
    {
        if (Type is { Length: > 0 } && !string.Equals(sensor.SensorType.ToString(), Type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (Name is { Length: > 0 } && !sensor.Name.Contains(Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (Hardware is { Length: > 0 })
        {
            // Match the sensor's hardware or any parent (e.g. 'motherboard' also finds Super I/O fans).
            for (var hw = sensor.Hardware; hw is not null; hw = hw.Parent)
            {
                if (hw.Name.Contains(Hardware, StringComparison.OrdinalIgnoreCase) ||
                    hw.HardwareType.ToString().Contains(Hardware, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        return true;
    }
}

/// <summary>
/// Thread-safe wrapper over LibreHardwareMonitor. Opening enumerates every device (a few seconds, and it loads
/// the PawnIO driver when present), so one hub lives for the lifetime of the process.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly Computer _computer;
    private readonly object _gate = new();
    private DateTime _lastUpdate = DateTime.MinValue;

    /// <summary>Readings younger than this are reused instead of polling the hardware again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(750);

    public SensorHub()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsPowerMonitorEnabled = true,
        };
        _computer.Open();
        Refresh(force: true);
    }

    public static string LibraryVersion =>
        typeof(Computer).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Readings older than this are refreshed twice: delta-based sensors (AMD package/core power from energy
    /// counters, effective clocks, throughput rates) are only meaningful over a short window, and a counter can wrap
    /// during a long idle gap.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrimeGap = TimeSpan.FromMilliseconds(200);

    private Timer? _poller;

    public void Refresh(bool force = false)
    {
        lock (_gate)
        {
            var age = DateTime.UtcNow - _lastUpdate;
            if (!force && age < MaxAge)
            {
                return;
            }
            UpdateAll();
            if (age > Stale)
            {
                Thread.Sleep(PrimeGap);
                UpdateAll();
            }
            _lastUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>Keep readings current in the background so min/max cover the whole run, not just the moments
    /// someone asked (the service does this; the in-process fallback polls on demand only).</summary>
    public void StartPolling(TimeSpan interval)
    {
        // One-shot timer re-armed after each poll: a slow refresh (spun-down disks, a hung USB device) delays the next
        // poll instead of queueing callbacks behind the lock and tying up thread-pool threads.
        _poller ??= new Timer(_ =>
        {
            try
            {
                Refresh(force: true);
            }
            catch
            {
                // never let the timer thread die
            }
            finally
            {
                try
                {
                    _poller?.Change(interval, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // disposed while polling
                }
            }
        }, null, interval, Timeout.InfiniteTimeSpan);
    }

    private void UpdateAll()
    {
        foreach (var hw in _computer.Hardware)
        {
            Update(hw);
        }

        static void Update(IHardware hw)
        {
            try
            {
                hw.Update();
            }
            catch
            {
                // One misbehaving device (a USB cooler unplugged mid-poll...) must not break the rest.
            }
            foreach (var sub in hw.SubHardware)
            {
                Update(sub);
            }
        }
    }

    /// <summary>Run <paramref name="body"/> against fresh readings while holding the hub lock.</summary>
    public T Read<T>(Func<IReadOnlyList<IHardware>, T> body, bool refresh = true)
    {
        if (refresh)
        {
            Refresh();
        }
        lock (_gate)
        {
            return body(Flatten(_computer.Hardware).ToList());
        }
    }

    public string Report()
    {
        lock (_gate)
        {
            return _computer.GetReport();
        }
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> roots)
    {
        foreach (var hw in roots)
        {
            yield return hw;
            foreach (var sub in Flatten(hw.SubHardware))
            {
                yield return sub;
            }
        }
    }

    // ------------------------------------------------------------------ JSON shaping

    /// <summary>Hardware whose type matches one of <paramref name="types"/> (e.g. "Gpu" matches GpuNvidia/GpuAmd/GpuIntel),
    /// as nested objects with their sensors grouped by type.</summary>
    public JsonArray HardwareOfType(params string[] types) => Read(all =>
    {
        var result = new JsonArray();
        foreach (var hw in all.Where(h => h.Parent is null || !Matches(h.Parent, types)))
        {
            if (Matches(hw, types))
            {
                result.Add(HardwareNode(hw, includeSensors: true));
            }
        }
        return result;
    });

    public static bool Matches(IHardware hw, string[] types) =>
        types.Any(t => hw.HardwareType.ToString().StartsWith(t, StringComparison.OrdinalIgnoreCase));

    public static JsonObject HardwareNode(IHardware hw, bool includeSensors)
    {
        var node = new JsonObject
        {
            ["name"] = hw.Name,
            ["type"] = hw.HardwareType.ToString(),
            ["id"] = hw.Identifier.ToString(),
        };
        if (includeSensors)
        {
            var groups = new JsonObject();
            foreach (var group in hw.Sensors.GroupBy(s => s.SensorType).OrderBy(g => g.Key.ToString()))
            {
                var unit = Unit(group.Key);
                var key = unit.Length > 0 ? $"{group.Key} ({unit})" : group.Key.ToString();
                var list = new JsonArray();
                foreach (var s in group.OrderBy(s => s.Index))
                {
                    list.Add(new JsonObject
                    {
                        ["name"] = s.Name,
                        ["value"] = Num(s.Value),
                        ["min"] = Num(s.Min),
                        ["max"] = Num(s.Max),
                    });
                }
                groups[key] = list;
            }
            node["sensors"] = groups;
        }
        if (hw.SubHardware.Length > 0)
        {
            node["subHardware"] = new JsonArray(hw.SubHardware.Select(s => (JsonNode)HardwareNode(s, includeSensors)).ToArray());
        }
        return node;
    }

    public static JsonObject SensorNode(ISensor s) => new()
    {
        ["hardware"] = s.Hardware.Name,
        ["hardwareType"] = s.Hardware.HardwareType.ToString(),
        ["name"] = s.Name,
        ["type"] = s.SensorType.ToString(),
        ["unit"] = Unit(s.SensorType),
        ["value"] = Num(s.Value),
        ["min"] = Num(s.Min),
        ["max"] = Num(s.Max),
        ["id"] = s.Identifier.ToString(),
    };

    public static JsonNode? Num(float? v) =>
        v is { } f && float.IsFinite(f) ? JsonValue.Create(Math.Round((double)f, 2)) : null;

    public static string Unit(SensorType type) => type.ToString() switch
    {
        "Voltage" => "V",
        "Current" => "A",
        "Power" => "W",
        "Clock" => "MHz",
        "Temperature" => "°C",
        "Load" => "%",
        "Frequency" => "Hz",
        "Fan" => "RPM",
        "Flow" => "L/h",
        "Control" => "%",
        "Level" => "%",
        "Humidity" => "%",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "B/s",
        "TimeSpan" => "s",
        "Energy" => "mWh",
        "Noise" => "dBA",
        "Conductivity" => "µS/cm",
        _ => "",
    };

    /// <summary>First sensor whose type matches and whose name contains one of the candidates (in priority order).</summary>
    public static float? Pick(IEnumerable<IHardware> hardware, string type, params string[] names)
    {
        var sensors = hardware.SelectMany(h => h.Sensors).Where(s => s.SensorType.ToString() == type).ToList();
        // Sensors LibreHardwareMonitor cannot read (no elevation/driver) report 0 rather than null; a real temperature or power is never exactly 0.
        var zeroIsMissing = type is "Temperature" or "Power" or "Voltage";
        foreach (var n in names)
        {
            var hit = sensors.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase) && s.Value is { } v && float.IsFinite(v) && !(zeroIsMissing && v == 0));
            if (hit is not null)
            {
                return hit.Value;
            }
        }
        return null;
    }

    public void ResetMinMax()
    {
        lock (_gate)
        {
            foreach (var s in Flatten(_computer.Hardware).SelectMany(h => h.Sensors))
            {
                s.ResetMin();
                s.ResetMax();
            }
        }
    }

    public void Dispose()
    {
        _poller?.Dispose();
        lock (_gate)
        {
            _computer.Close();
        }
    }
}
