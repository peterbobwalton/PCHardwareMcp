using System.Management;
using System.Text.Json.Nodes;

namespace PCHardwareMcp.Core;

/// <summary>Small WMI helper: every query returns plain JSON and never throws (errors become {"error": ...}).</summary>
public static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";

    public static List<Dictionary<string, object?>> Query(string wql, string scope = Cimv2)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var results = searcher.Get();
        foreach (var obj in results)
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties)
                {
                    row[p.Name] = p.Value;
                }
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>Query and project each row through <paramref name="map"/>; failures are reported, not thrown.</summary>
    public static JsonNode Rows(string wql, Func<Dictionary<string, object?>, JsonObject> map, string scope = Cimv2)
    {
        try
        {
            return new JsonArray(Query(wql, scope).Select(r => (JsonNode)map(r)).ToArray());
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = $"WMI query failed: {ex.Message}" };
        }
    }

    public static JsonObject? First(string wql, Func<Dictionary<string, object?>, JsonObject> map, string scope = Cimv2)
    {
        try
        {
            var row = Query(wql, scope).FirstOrDefault();
            return row is null ? null : map(row);
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = $"WMI query failed: {ex.Message}" };
        }
    }

    public static string? Str(this Dictionary<string, object?> row, string key)
    {
        var s = row.GetValueOrDefault(key)?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public static long? Long(this Dictionary<string, object?> row, string key)
    {
        var v = row.GetValueOrDefault(key);
        if (v is null)
        {
            return null;
        }
        try
        {
            return Convert.ToInt64(v);
        }
        catch
        {
            return null;
        }
    }

    public static bool? Bool(this Dictionary<string, object?> row, string key) =>
        row.GetValueOrDefault(key) is bool b ? b : null;

    /// <summary>CIM datetime ("20260101000000.000000+000") or date-only BIOS strings to ISO 8601.</summary>
    public static string? Date(this Dictionary<string, object?> row, string key)
    {
        var s = row.Str(key);
        if (s is null)
        {
            return null;
        }
        try
        {
            return ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd");
        }
        catch
        {
            return s;
        }
    }
}
