# PCHardwareMcp

A C# MCP server that gives Claude CPU-Z / HWiNFO-style hardware information about this PC: identification
(CPUID, caches, core topology, board, BIOS, DIMMs, GPUs, drives, NICs) and live sensors (clocks, temperatures,
load, power, voltages, fans, drive health) via [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

## Architecture

```
Claude Desktop ──stdio──► PCHardwareMcp.Server.exe  (per user, %APPDATA%\PCHardwareMcp\server)
                               │  \\.\pipe\PCHardwareMcp  (NDJSON, local authenticated users only)
                               ▼
                          PCHardwareMcp.Service.exe  (LocalSystem service, %ProgramFiles%\PCHardwareMcp)
                               └─ LibreHardwareMonitorLib + PawnIO driver, WMI, CPUID
```

Most sensors (CPU temperatures and voltages via MSRs, Super I/O fans/voltages, SMBus DIMM SPD) need admin rights
and the PawnIO driver, but Claude starts MCP servers unelevated. So the readings come from a small LocalSystem
service, and the MCP server talks to it over a named pipe. If the service isn't running, the server falls back to
collecting in-process: everything still works, minus the elevated sensors (`hardware_status` shows which).

The service binary is copied to Program Files before it is registered. A SYSTEM service must never run from a
user-writable folder like `%APPDATA%`. The pipe denies network logons and exposes read-only queries only.

| Project | What |
|---|---|
| `src/PCHardwareMcp.Core` | LibreHardwareMonitor wrapper, CPUID/topology decoding, WMI inventory, pipe protocol + client |
| `src/PCHardwareMcp.Service` | Windows service hosting the collector behind the pipe; `--install` / `--uninstall` |
| `src/PCHardwareMcp.Server` | stdio MCP server (ModelContextProtocol SDK), `--register` / `--unregister` / `--selftest` |

## Tools

| Tool | |
|---|---|
| `hardware_status` | Service running? elevated? PawnIO present? what's missing |
| `get_summary` | One-shot overview: CPU, memory, board, BIOS, OS, GPUs, drives with key readings |
| `get_cpu` | CPUID (family/model/stepping, instruction sets), cores/threads/P-E classes, caches, live per-core sensors |
| `get_memory` | DIMMs (slot, size, DDR type, rated vs configured speed, voltage, ranks, ECC, part number) + memory sensors |
| `get_gpu` | Adapters (driver, resolution) + clocks, temps incl. hot spot, load, power, fan, VRAM |
| `get_motherboard` | System/board/BIOS/SMBIOS/OS + Super I/O & EC fans, voltages, temps |
| `get_storage` | Physical disks (bus, media, health, firmware), volumes, drive temps/life/throughput |
| `get_network` | Adapters (speed, MAC, IPs, gateways, DNS) + throughput |
| `get_sensors` | Flat filtered sensor list (`hardware`, `type`, `name`) |
| `list_hardware` | Device tree with sensor counts |
| `sample_sensors` | min/avg/max over a time window (load tests, throttling) |
| `reset_min_max` | Reset recorded min/max |
| `get_report` | Raw LibreHardwareMonitor report for diagnosing unsupported hardware |

Memory timings (tCL/tRCD/...) are not exposed: LibreHardwareMonitor reads DIMM temperatures from SPD hubs over SMBus,
not the timing tables. On the TRX50 AI TOP (Threadripper 7000, DDR5 RDIMM) its report has no SMBus section at all. The SPD
hubs there most likely sit on the CPU's I3C controller, which LibreHardwareMonitor/RAMSPDToolkit don't drive, so there are
no per-DIMM temperatures either. The DIMM inventory (size, speed, voltage, ranks, ECC, part numbers) comes from SMBIOS/WMI
and works everywhere.

The service polls every second, so min/max cover the whole run, not just the moments a tool was called. A refresh after
more than 2 s idle reads twice, 200 ms apart, because delta-based sensors (AMD package power from energy counters,
effective clocks, throughput) are meaningless across a long gap. The MCP server reconnects on its own if the service
restarts, and falls back to in-process collection if the service is gone.

## Build / debug (Visual Studio 2026)

Open `PCHardwareMcp.slnx`. To debug the service, set `PCHardwareMcp.Service` as the startup project and run it.
Its manifest asks for admin, so VS will offer to restart elevated. It then serves the pipe from the console.
Stop the installed service first (`sc stop PCHardwareMcp`) or the pipe name is taken.
The server only trusts a pipe served by `%ProgramFiles%\PCHardwareMcp\PCHardwareMcp.Service.exe` (anything else could be a
squatter feeding Claude fake data). The `selftest` launch profile sets `PCHARDWAREMCP_TRUST_ANY_SERVICE=1` so it accepts the
debug build.
Then run `PCHardwareMcp.Server --selftest` to see what the server gets through the pipe.

## Install

Developer install on this PC:

```powershell
.\Install-PCHardwareMcp.ps1 -WithPawnIo     # publish, register in Claude config, install service (UAC prompt)
.\Install-PCHardwareMcp.ps1 -Uninstall
```

Installer for other PCs (needs .NET 10 SDK + Inno Setup 6):

```powershell
.\Build-Installer.ps1    # -> installer\Output\PCHardwareMcpSetup-<version>.exe
```

The setup installs per-user into `%APPDATA%\PCHardwareMcp`, registers `pc-hardware` in
`claude_desktop_config.json` (classic and Store builds, other servers preserved, `.bak` kept), and optionally, with
one UAC prompt, installs the sensor service and the PawnIO driver (`winget install namazso.PawnIO`).
Restart Claude Desktop afterwards. To verify a PC, run `Check-PCHardwareMcp.ps1` there (no admin needed): it checks the
server, service, PawnIO and Claude config, runs the selftest and prints PASS/WARN/FAIL. Install log for the elevated step: `%ProgramData%\PCHardwareMcp\install.log`.
