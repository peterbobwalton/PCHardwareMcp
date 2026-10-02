using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json.Nodes;

namespace PCHardwareMcp.Core;

/// <summary>CPU-Z style processor identification: raw CPUID decoding plus the Windows core/cache topology.
/// Needs no elevation.</summary>
public static class CpuInfo
{
    public static JsonObject Identify()
    {
        var node = new JsonObject();
        if (!X86Base.IsSupported)
        {
            node["error"] = "CPUID is only available on x86/x64.";
            return node;
        }

        var (maxLeaf, b0, c0, d0) = X86Base.CpuId(0, 0);
        var vendor = Ascii(b0, d0, c0);
        var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);

        var (eax1, ebx1, ecx1, edx1) = X86Base.CpuId(1, 0);
        var stepping = eax1 & 0xF;
        var model = (eax1 >> 4) & 0xF;
        var family = (eax1 >> 8) & 0xF;
        var extModel = (eax1 >> 16) & 0xF;
        var extFamily = (eax1 >> 20) & 0xFF;
        var displayFamily = family == 0xF ? family + extFamily : family;
        var displayModel = family is 0x6 or 0xF ? (extModel << 4) + model : model;

        string? brand = null;
        if ((uint)maxExt >= 0x80000004)
        {
            var sb = new StringBuilder();
            for (var leaf = 0x80000002u; leaf <= 0x80000004u; leaf++)
            {
                var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), 0);
                sb.Append(Ascii(a, b, c, d));
            }
            brand = sb.ToString().TrimEnd('\0').Trim();
        }

        int ebx7 = 0, ecx7 = 0, edx7 = 0, eax71 = 0;
        if (maxLeaf >= 7)
        {
            (_, ebx7, ecx7, edx7) = X86Base.CpuId(7, 0);
            (eax71, _, _, _) = X86Base.CpuId(7, 1);
        }
        int ecxE1 = 0, edxE1 = 0;
        if ((uint)maxExt >= 0x80000001)
        {
            (_, _, ecxE1, edxE1) = X86Base.CpuId(unchecked((int)0x80000001), 0);
        }

        node["vendor"] = vendor;
        node["brand"] = brand;
        node["family"] = displayFamily;
        node["model"] = displayModel;
        node["stepping"] = stepping;
        node["familyHex"] = $"0x{displayFamily:X}";
        node["modelHex"] = $"0x{displayModel:X}";
        node["extFamily"] = extFamily;
        node["extModel"] = extModel;
        node["signature"] = $"0x{eax1:X8}";
        node["maxLeaf"] = maxLeaf;
        node["maxExtendedLeaf"] = $"0x{maxExt:X8}";
        node["hybrid"] = Bit(edx7, 15);
        node["hypervisorPresent"] = Bit(ecx1, 31);
        node["instructions"] = Features(vendor, ecx1, edx1, ebx7, ecx7, edx7, eax71, ecxE1, edxE1);
        return node;
    }

    private static string Features(string vendor, int ecx1, int edx1, int ebx7, int ecx7, int edx7, int eax71, int ecxE1, int edxE1)
    {
        var f = new List<string>();
        void Add(bool on, string name) { if (on) f.Add(name); }

        Add(Bit(edx1, 23), "MMX");
        Add(Bit(edx1, 25), "SSE");
        Add(Bit(edx1, 26), "SSE2");
        Add(Bit(ecx1, 0), "SSE3");
        Add(Bit(ecx1, 9), "SSSE3");
        Add(Bit(ecx1, 19), "SSE4.1");
        Add(Bit(ecx1, 20), "SSE4.2");
        Add(Bit(ecxE1, 6), "SSE4A");
        Add(Bit(edxE1, 29), "x86-64");
        Add(Bit(ecx1, 5), "VT-x");
        Add(Bit(ecxE1, 2), "AMD-V");
        Add(Bit(ecx1, 25), "AES");
        Add(Bit(ecx1, 1), "PCLMULQDQ");
        Add(Bit(ecx1, 28), "AVX");
        Add(Bit(ebx7, 5), "AVX2");
        Add(Bit(eax71, 4), "AVX-VNNI");
        var avx512 = new List<string>();
        void Add512(bool on, string name) { if (on) avx512.Add(name); }
        Add512(Bit(ebx7, 16), "F");
        Add512(Bit(ebx7, 17), "DQ");
        Add512(Bit(ebx7, 28), "CD");
        Add512(Bit(ebx7, 30), "BW");
        Add512(Bit(ebx7, 31), "VL");
        Add512(Bit(ebx7, 21), "IFMA");
        Add512(Bit(ecx7, 1), "VBMI");
        Add512(Bit(ecx7, 6), "VBMI2");
        Add512(Bit(ecx7, 11), "VNNI");
        Add512(Bit(ecx7, 12), "BITALG");
        Add512(Bit(ecx7, 14), "VPOPCNTDQ");
        Add512(Bit(eax71, 5), "BF16");
        Add512(Bit(edx7, 23), "FP16");
        if (avx512.Count > 0)
        {
            f.Add($"AVX-512 ({string.Join(", ", avx512)})");
        }
        Add(Bit(ecx1, 12), "FMA3");
        Add(Bit(ecx1, 29), "F16C");
        Add(Bit(ebx7, 3), "BMI1");
        Add(Bit(ebx7, 8), "BMI2");
        Add(Bit(ecxE1, 5), "LZCNT");
        Add(Bit(ecx1, 23), "POPCNT");
        Add(Bit(ecx1, 22), "MOVBE");
        Add(Bit(ebx7, 19), "ADX");
        Add(Bit(ebx7, 29), "SHA");
        Add(Bit(ecx7, 8), "GFNI");
        Add(Bit(ecx7, 9), "VAES");
        Add(Bit(ecx7, 10), "VPCLMULQDQ");
        Add(Bit(ecx1, 30), "RDRAND");
        Add(Bit(ebx7, 18), "RDSEED");
        Add(Bit(edxE1, 27), "RDTSCP");
        Add(Bit(edxE1, 20), "NX");
        _ = vendor;
        return string.Join(", ", f);
    }

    private static bool Bit(int value, int bit) => ((value >> bit) & 1) != 0;

    private static string Ascii(params int[] regs)
    {
        var bytes = new byte[regs.Length * 4];
        for (var i = 0; i < regs.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), regs[i]);
        }
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    // ------------------------------------------------------------------ topology

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationProcessorPackage = 3;
    private const int RelationProcessorDie = 5;
    private const int RelationAll = 0xFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    /// <summary>Packages, dies, cores (with SMT and P/E efficiency classes) and caches from GetLogicalProcessorInformationEx.</summary>
    public static JsonObject Topology()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref length);
        if (length == 0)
        {
            return new JsonObject { ["error"] = $"GetLogicalProcessorInformationEx failed ({Marshal.GetLastWin32Error()})" };
        }
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length))
            {
                return new JsonObject { ["error"] = $"GetLogicalProcessorInformationEx failed ({Marshal.GetLastWin32Error()})" };
            }

            int packages = 0, dies = 0, cores = 0, smtCores = 0, threads = 0;
            var byClass = new SortedDictionary<int, (int cores, int threads)>();
            var caches = new Dictionary<(int level, int type, uint size, int assoc, int line), int>();

            var offset = 0;
            while (offset < length)
            {
                var item = buffer + offset;
                var relationship = Marshal.ReadInt32(item, 0);
                var size = Marshal.ReadInt32(item, 4);
                switch (relationship)
                {
                    case RelationProcessorPackage:
                        packages++;
                        break;
                    case RelationProcessorDie:
                        dies++;
                        break;
                    case RelationProcessorCore:
                    {
                        var flags = Marshal.ReadByte(item, 8);
                        var efficiency = Marshal.ReadByte(item, 9);
                        var logical = CountMaskBits(item, groupCountOffset: 30, maskOffset: 32);
                        cores++;
                        threads += logical;
                        if ((flags & 1) != 0)
                        {
                            smtCores++;
                        }
                        byClass.TryGetValue(efficiency, out var c);
                        byClass[efficiency] = (c.cores + 1, c.threads + logical);
                        break;
                    }
                    case RelationCache:
                    {
                        var level = (int)Marshal.ReadByte(item, 8);
                        var assoc = (int)Marshal.ReadByte(item, 9);
                        var line = (int)(ushort)Marshal.ReadInt16(item, 10);
                        var cacheSize = (uint)Marshal.ReadInt32(item, 12);
                        var type = Marshal.ReadInt32(item, 16);
                        var key = (level, type, cacheSize, assoc, line);
                        caches[key] = caches.GetValueOrDefault(key) + 1;
                        break;
                    }
                }
                if (size <= 0)
                {
                    break;
                }
                offset += size;
            }

            var node = new JsonObject
            {
                ["packages"] = packages,
                ["dies"] = dies > 0 ? dies : null,
                ["cores"] = cores,
                ["threads"] = threads,
                ["smtCores"] = smtCores,
            };
            if (byClass.Count > 1)
            {
                // Higher efficiency class = faster core (P-cores on Intel hybrid, the 'classic' cores on AMD Zen 4c/5c mixes).
                var classes = new JsonArray();
                foreach (var (cls, v) in byClass.Reverse())
                {
                    classes.Add(new JsonObject
                    {
                        ["efficiencyClass"] = cls,
                        ["kind"] = cls == byClass.Keys.Max() ? "performance" : "efficiency",
                        ["cores"] = v.cores,
                        ["threads"] = v.threads,
                    });
                }
                node["coreClasses"] = classes;
            }

            var cacheList = new JsonArray();
            foreach (var ((level, type, size, assoc, line), count) in caches.OrderBy(k => k.Key.level).ThenBy(k => k.Key.type == 0 ? 9 : k.Key.type))
            {
                var typeName = type switch { 0 => "Unified", 1 => "Instruction", 2 => "Data", 3 => "Trace", _ => $"Type{type}" };
                var label = $"L{level} {(type == 0 ? "" : typeName)}".TrimEnd();
                cacheList.Add(new JsonObject
                {
                    ["level"] = level,
                    ["type"] = typeName,
                    ["instances"] = count,
                    ["sizeKB"] = size / 1024,
                    ["associativity"] = assoc == 0xFF ? "fully associative" : $"{assoc}-way",
                    ["lineSize"] = line,
                    ["summary"] = $"{label}: {count} x {FormatSize(size)}, {(assoc == 0xFF ? "fully assoc." : $"{assoc}-way")}, {line}-byte line",
                });
            }
            node["caches"] = cacheList;
            return node;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int CountMaskBits(IntPtr item, int groupCountOffset, int maskOffset)
    {
        var groups = (ushort)Marshal.ReadInt16(item, groupCountOffset);
        var total = 0;
        for (var g = 0; g < Math.Max((int)groups, 1); g++)
        {
            // GROUP_AFFINITY is { KAFFINITY Mask; WORD Group; WORD Reserved[3]; } = 16 bytes on x64
            var mask = (ulong)Marshal.ReadInt64(item, maskOffset + g * 16);
            total += BitOperations.PopCount(mask);
        }
        return total;
    }

    private static string FormatSize(uint bytes) =>
        bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";
}
