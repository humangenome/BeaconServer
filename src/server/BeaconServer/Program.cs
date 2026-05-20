using BeaconServer.Configuration;
using BeaconServer.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace BeaconServer;

public static class Program
{
    public static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                formatter: new Serilog.Formatting.Compact.CompactJsonFormatter(),
                path: "logs/beacon-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateBootstrapLogger();

        try
        {
            PrintStartupBanner();

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog();

            builder.Services.Configure<BeaconServerOptions>(builder.Configuration.GetSection("Beacon"));

            builder.Services.AddSingleton<InstanceIdentityProvider>();
            builder.Services.AddSingleton<HmacKeyService>();
            builder.Services.AddSingleton<PipeServerState>();
            builder.Services.AddSingleton<Sn2RestartCoordinator>();

            builder.Services.AddSingleton<SaveOrchestratorService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SaveOrchestratorService>());
            builder.Services.AddHostedService<NamedPipeServerService>();
            builder.Services.AddHostedService<HeartbeatWatchdogService>();
            builder.Services.AddHostedService<SourceQueryHostedService>();
            builder.Services.AddHostedService<RconHostedService>();
            builder.Services.AddHostedService<SnProcessSupervisorService>();
            builder.Services.AddHostedService<Sn2LogTailService>();
            builder.Services.AddHostedService<BeaconHttpService>();
            builder.Services.AddHostedService<RosterFileWatcherService>();

            var host = builder.Build();

            // Emit the per-instance identity line now that DI has bound options.
            var opts = host.Services.GetRequiredService<IOptions<BeaconServerOptions>>().Value;
            Log.Information("Instance {Instance} | gameplay:{GP} query:{QP} rcon:{RP} pipe:{Pipe}",
                opts.InstanceId, opts.GameplayPort, opts.QueryPort, opts.RconPort, opts.PipeName);
            Log.Information("Save dir: {Dir}", opts.SaveDir);
            Log.Information("Subnautica 2 user dir: {Dir}", opts.SnUserDir);

            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "BeaconServer terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void PrintStartupBanner()
    {
        var beaconVer = BeaconVersionInfo.BeaconVersion;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var dotnetVer = Environment.Version.ToString();
        var host = Environment.MachineName;

        Log.Information("==========================================================");
        Log.Information("  Beacon Server v{Version}  (open-source Subnautica 2 dedicated host)", beaconVer);
        Log.Information("  https://github.com/humangenome/Beacon");
        Log.Information("  Officially supported by https://www.survivalservers.com");
        Log.Information("----------------------------------------------------------");
        Log.Information("  host:    {Host}", host);
        Log.Information("  os:      {Os}", os);
        Log.Information("  runtime: .NET {DotNet}", dotnetVer);
        Log.Information("  sn2:     build detected from host log (see [Subnautica 2] lines)");
        Log.Information("==========================================================");
    }
}
