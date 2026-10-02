using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace PCHardwareMcp.Core;

/// <summary>Static (non-sensor) inventory from WMI and .NET: what CPU-Z shows on its Mainboard, Memory, SPD, Graphics tabs.</summary>
public static class SystemInfo
{
    public static JsonNode Processors() => Wmi.Rows("SELECT * FROM Win32_Processor", r => new JsonObject
    {
        ["name"] = r.Str("Name"),
        ["manufacturer"] = r.Str("Manufacturer"),
        ["socket"] = r.Str("SocketDesignation"),
        ["cores"] = r.Long("NumberOfCores"),
        ["enabledCores"] = r.Long("NumberOfEnabledCore"),
        ["threads"] = r.Long("NumberOfLogicalProcessors"),
        ["baseClockMHz"] = r.Long("MaxClockSpeed"),
        ["busClockMHz"] = r.Long("ExtClock"),
        ["l2CacheKB"] = r.Long("L2CacheSize"),
        ["l3CacheKB"] = r.Long("L3CacheSize"),
        ["processorId"] = r.Str("ProcessorId"),
        ["revision"] = r.Long("Revision"),
        ["virtualizationFirmwareEnabled"] = r.Bool("VirtualizationFirmwareEnabled"),
    });

    public static JsonObject Memory()
    {
        var node = new JsonObject();
        var os = Wmi.First("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem", r => new JsonObject
        {
            ["totalGB"] = Gb(r.Long("TotalVisibleMemorySize") * 1024),
            ["availableGB"] = Gb(r.Long("FreePhysicalMemory") * 1024),
        });
        if (os is not null)
        {
            node["os"] = os;
        }
        // MaxCapacityEx is left out: boards routinely report placeholder values (petabytes).
        node["slotCount"] = Wmi.First("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray WHERE Use = 3", r => new JsonObject
        {
            ["count"] = r.Long("MemoryDevices"),
        })?["count"]?.DeepClone();

        var modules = Wmi.Rows("SELECT * FROM Win32_PhysicalMemory", r =>
        {
            var dataWidth = r.Long("DataWidth");
            var totalWidth = r.Long("TotalWidth");
            return new JsonObject
            {
                ["slot"] = r.Str("DeviceLocator"),
                ["bank"] = r.Str("BankLabel"),
                ["sizeGB"] = Gb(r.Long("Capacity")),
                ["type"] = MemoryType(r.Long("SMBIOSMemoryType")),
                ["formFactor"] = FormFactor(r.Long("FormFactor")),
                ["ratedSpeedMTs"] = r.Long("Speed"),
                ["configuredSpeedMTs"] = r.Long("ConfiguredClockSpeed"),
                ["configuredVoltageV"] = MilliVolts(r.Long("ConfiguredVoltage")),
                ["minVoltageV"] = MilliVolts(r.Long("MinVoltage")),
                ["maxVoltageV"] = MilliVolts(r.Long("MaxVoltage")),
                ["ranks"] = r.Long("Attributes") is > 0 and var ranks ? ranks : null,
                ["ecc"] = dataWidth is > 0 && totalWidth > dataWidth,
                ["manufacturer"] = r.Str("Manufacturer"),
                ["partNumber"] = r.Str("PartNumber"),
                ["serial"] = r.Str("SerialNumber"),
            };
        });
        node["modules"] = modules;

        if (modules is JsonArray list && list.Count > 0)
        {
            // "4 x 32 GB DDR5-6000 (rated 6400)" - the headline CPU-Z shows on its Memory tab.
            var groups = list.OfType<JsonObject>()
                .GroupBy(m => (size: m["sizeGB"]?.ToString(), type: m["type"]?.ToString(), cfg: m["configuredSpeedMTs"]?.ToString(), rated: m["ratedSpeedMTs"]?.ToString()))
                .Select(g =>
                {
                    var rated = g.Key.rated != g.Key.cfg && g.Key.rated is not null ? $" (rated {g.Key.rated})" : "";
                    return $"{g.Count()} x {g.Key.size} GB {g.Key.type}-{g.Key.cfg}{rated}";
                });
            node["summary"] = string.Join(", ", groups);
        }
        return node;
    }

    public static JsonNode VideoControllers() => Wmi.Rows("SELECT * FROM Win32_VideoController", r => new JsonObject
    {
        ["name"] = r.Str("Name"),
        ["processor"] = r.Str("VideoProcessor"),
        ["driverVersion"] = r.Str("DriverVersion"),
        ["driverDate"] = r.Date("DriverDate"),
        ["resolution"] = r.Long("CurrentHorizontalResolution") is { } w && r.Long("CurrentVerticalResolution") is { } h ? $"{w}x{h}" : null,
        ["refreshHz"] = r.Long("CurrentRefreshRate"),
        ["pnpId"] = r.Str("PNPDeviceID"),
        ["status"] = r.Str("Status"),
    });

    public static JsonObject Board()
    {
        var node = new JsonObject
        {
            ["system"] = Wmi.First("SELECT Manufacturer, Model, SystemType, SystemFamily FROM Win32_ComputerSystem", r => new JsonObject
            {
                ["manufacturer"] = r.Str("Manufacturer"),
                ["model"] = r.Str("Model"),
                ["family"] = r.Str("SystemFamily"),
                ["type"] = r.Str("SystemType"),
            }),
            ["baseboard"] = Wmi.First("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard", r => new JsonObject
            {
                ["manufacturer"] = r.Str("Manufacturer"),
                ["model"] = r.Str("Product"),
                ["revision"] = r.Str("Version"),
                ["serial"] = r.Str("SerialNumber"),
            }),
            ["bios"] = Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS", r => new JsonObject
            {
                ["vendor"] = r.Str("Manufacturer"),
                ["version"] = r.Str("SMBIOSBIOSVersion"),
                ["date"] = r.Date("ReleaseDate"),
                ["smbios"] = r.Long("SMBIOSMajorVersion") is { } major ? $"{major}.{r.Long("SMBIOSMinorVersion")}" : null,
            }),
        };
        return node;
    }

    public static JsonObject? OperatingSystem() =>
        Wmi.First("SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, CSName FROM Win32_OperatingSystem", r => new JsonObject
        {
            ["name"] = r.Str("Caption"),
            ["version"] = r.Str("Version"),
            ["build"] = r.Str("BuildNumber"),
            ["architecture"] = r.Str("OSArchitecture"),
            ["computerName"] = r.Str("CSName"),
            ["lastBoot"] = r.Str("LastBootUpTime") is { } boot ? BootTime(boot) : null,
        });

    public static JsonObject Storage()
    {
        var node = new JsonObject
        {
            ["physicalDisks"] = Wmi.Rows("SELECT * FROM MSFT_PhysicalDisk", r => new JsonObject
            {
                ["name"] = r.Str("FriendlyName"),
                ["model"] = r.Str("Model"),
                ["bus"] = BusType(r.Long("BusType")),
                ["media"] = r.Long("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" },
                ["sizeGB"] = Gb(r.Long("Size")),
                ["health"] = r.Long("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                ["firmware"] = r.Str("FirmwareVersion"),
                ["serial"] = r.Str("SerialNumber"),
                ["deviceId"] = r.Str("DeviceId"),
            }, Wmi.Storage),
        };

        node["volumes"] = Volumes();
        return node;
    }

    /// <summary>
    /// Mounted volumes as the CALLING user sees them. Drive letters are per logon session: the SYSTEM service sees
    /// neither mapped network drives nor per-user mounts (Google Drive...), so the MCP server calls this itself.
    /// Each drive gets its own time budget because IsReady on a dead network share can block for a long time.
    /// </summary>
    public static JsonArray Volumes()
    {
        var volumes = new JsonArray();
        foreach (var d in DriveInfo.GetDrives())
        {
            var probe = Task.Run(() => Volume(d));
            JsonObject? node = null;
            try
            {
                node = probe.Wait(TimeSpan.FromSeconds(2)) ? probe.Result : new JsonObject
                {
                    ["drive"] = d.Name,
                    ["type"] = d.DriveType.ToString(),
                    ["error"] = "not responding (timed out after 2 s)",
                };
            }
            catch (AggregateException)
            {
                // drive vanished or access denied: skip it
            }
            if (node is not null)
            {
                volumes.Add(node);
            }
        }
        return volumes;

        static JsonObject? Volume(DriveInfo d)
        {
            try
            {
                if (!d.IsReady)
                {
                    return null;
                }
                return new JsonObject
                {
                    ["drive"] = d.Name,
                    ["label"] = string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel,
                    ["type"] = d.DriveType.ToString(),
                    ["fileSystem"] = d.DriveFormat,
                    ["sizeGB"] = Gb(d.TotalSize),
                    ["freeGB"] = Gb(d.AvailableFreeSpace),
                    ["usedPercent"] = d.TotalSize > 0 ? Math.Round(100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize, 1) : null,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // removable/network drive vanished between enumeration and query, or no access
                return null;
            }
        }
    }

    public static JsonArray Network(bool includeDown)
    {
        var list = new JsonArray();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsFilterBinding(nic.Name) ||
                (!includeDown && nic.OperationalStatus != OperationalStatus.Up))
            {
                continue;
            }
            var props = nic.GetIPProperties();
            list.Add(new JsonObject
            {
                ["name"] = nic.Name,
                ["description"] = nic.Description,
                ["type"] = nic.NetworkInterfaceType.ToString(),
                ["status"] = nic.OperationalStatus.ToString(),
                ["linkSpeedMbps"] = nic.Speed > 0 ? nic.Speed / 1_000_000 : null,
                ["mac"] = FormatMac(nic.GetPhysicalAddress()),
                ["ipv4"] = new JsonArray(props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => (JsonNode)$"{a.Address}/{a.PrefixLength}").ToArray()),
                ["ipv6"] = new JsonArray(props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(a => (JsonNode)a.Address.ToString()).ToArray()),
                ["gateways"] = new JsonArray(props.GatewayAddresses.Select(g => (JsonNode)g.Address.ToString()).ToArray()),
                ["dns"] = new JsonArray(props.DnsAddresses.Select(d => (JsonNode)d.ToString()).ToArray()),
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>NDIS filter bindings ("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "...-QoS Packet Scheduler-0000")
    /// show up as extra adapters with the parent's MAC; they are not real NICs.</summary>
    public static bool IsFilterBinding(string name) =>
        name.Contains("-WFP ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Filter Driver-", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-Npcap Packet Driver", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-VirtualBox NDIS", StringComparison.OrdinalIgnoreCase);

    public static double? Gb(long? bytes) => bytes is { } b ? Math.Round(b / (1024.0 * 1024 * 1024), 1) : null;

    private static double? MilliVolts(long? mv) => mv is > 0 ? mv / 1000.0 : null;

    private static string? BootTime(string cim)
    {
        try
        {
            return System.Management.ManagementDateTimeConverter.ToDateTime(cim).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return cim;
        }
    }

    private static string? FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private static string? MemoryType(long? smbios) => smbios switch
    {
        null or 0 => null,
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => $"SMBIOS type {smbios}",
    };

    private static string? FormFactor(long? ff) => ff switch
    {
        8 => "DIMM",
        12 => "SO-DIMM",
        13 => "SO-DIMM",
        null or 0 => null,
        _ => $"form factor {ff}",
    };

    private static string BusType(long? bus) => bus switch
    {
        1 => "SCSI",
        3 => "ATA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => $"bus {bus}",
    };
}
