using PCHardwareMcp.Core;
using PCHardwareMcp.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage:
//   PCHardwareMcp.Service                       run (as the Windows service, or in a console for debugging - needs admin)
//   PCHardwareMcp.Service --install [--with-pawnio]   (admin) copy to Program Files, create + start the service,
//                                                     optionally install the PawnIO driver with winget
//   PCHardwareMcp.Service --uninstall           (admin) stop + delete the service and its Program Files folder

if (args.Contains("--install"))
{
    return ServiceInstaller.Install(args.Contains("--with-pawnio"));
}
if (args.Contains("--uninstall"))
{
    return ServiceInstaller.Uninstall();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = PipeProtocol.ServiceName);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(_ => new LocalHardwareSource("service"));
builder.Services.AddHostedService<PipeServer>();

await builder.Build().RunAsync();
return 0;
