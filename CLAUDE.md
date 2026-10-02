# PCHardwareMcp

Repo: https://github.com/peterbobwalton/PCHardwareMcp (private)

C# (.NET 10, `net10.0-windows`) MCP server exposing CPU-Z/HWiNFO-style hardware info via LibreHardwareMonitor.
See README.md for architecture (stdio server -> named pipe -> LocalSystem service) and the tool list.

## Build / verify

- Build: `dotnet build PCHardwareMcp.slnx` (or Visual Studio 2026).
- No test project. Verify with `PCHardwareMcp.Server --selftest` against the installed service, and
  `Check-PCHardwareMcp.ps1` (PASS/WARN/FAIL, no admin needed).
- Dev install: `Install-PCHardwareMcp.ps1 -WithPawnIo` (UAC). Installer: `Build-Installer.ps1` (needs Inno Setup 6).

## Rules

- Work on this PC in `C:\source\Claude\PCHardwareMcp`, not OneDrive or a cloud container: real sensors need admin,
  the PawnIO driver and the installed service.
- All MCP tools stay read-only. The service runs as LocalSystem only from `%ProgramFiles%\PCHardwareMcp`; the pipe
  stays local-only and read-only.
- Keep the server's trust check on the service path. `PCHARDWAREMCP_TRUST_ANY_SERVICE=1` is for debugging only.
- Before changing sensor code, check with `--selftest` and note which readings need elevation.
- Stop the installed service (`sc stop PCHardwareMcp`) before debugging the service in VS; the pipe name is shared.
- Scratch scripts and logs go in `artifacts/` (gitignored).
- Ask before anything that needs UAC, installs drivers, or copies to the `Z:\Software\Claude` share.
- Keep the README tool table and known limits (e.g. no DIMM timings on TRX50) current when tools change.
