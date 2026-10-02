using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCHardwareMcp.Core;

/// <summary>
/// Wire format between the elevated sensor service and the per-user MCP server:
/// newline-delimited JSON over a local named pipe.
///   request : {"id":1,"method":"cpu","args":{...}}
///   response: {"id":1,"result":...}  or  {"id":1,"error":"message"}
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "PCHardwareMcp";
    public const string ServiceName = "PCHardwareMcp";
    public const int MaxMessageChars = 1 << 20;

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToLine(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>
    /// Local, authenticated users may read and write (send requests); only SYSTEM and Administrators may
    /// create pipe instances, and network logons are denied outright so the pipe is never reachable remotely.
    /// </summary>
    public static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }
}

/// <summary>A hardware query failed (bad arguments, service error, service unreachable).</summary>
public class HardwareException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The elevated service could not be reached over the pipe.</summary>
public sealed class ServiceUnavailableException(string message, Exception? inner = null) : HardwareException(message, inner);

/// <summary>Anything that can answer hardware queries: the in-process collector or the pipe client.</summary>
public interface IHardwareSource
{
    Task<JsonNode?> QueryAsync(string method, JsonObject? args = null, CancellationToken ct = default);
}
