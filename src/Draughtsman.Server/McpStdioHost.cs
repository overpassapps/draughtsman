using System.Text.Json;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Mcp.Tools;
using Draughtsman.Server.Assets;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Export;
using Draughtsman.Server.Maintenance;
using Draughtsman.Sidecar;
using Draughtsman.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server;

/// <summary>
/// <c>draughtsman mcp --stdio</c>: the MCP tools over stdio for an agent on the same machine (build-spec-v0.1.md
/// section 9), reading the same draughtsman.yaml and data folder the web host does. Logging is forced to stderr -
/// stdout is the JSON-RPC channel, and anything else written there corrupts it.
/// </summary>
public static class McpStdioHost
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        string dataFolder = ConfigLoader.ResolveDataFolder(builder.Configuration[DraughtsmanConfig.DataFolderEnvironmentVariable]);
        Hardening.DataFolderPermissions.Prepare(dataFolder);
        DraughtsmanConfig config = ConfigLoader.Load(dataFolder);
        StorageOptions storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, dataFolder);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(config);
        builder.Services.AddDraughtsmanStorage(storage);
        builder.Services.AddSingleton(new DataFolder(dataFolder));
        builder.Services.AddScoped<AssetStore>();
        builder.Services.AddScoped<IAssetReader>(services => services.GetRequiredService<AssetStore>());
        builder.Services.AddSingleton(config.Export.Chromium);
        builder.Services.AddSingleton<IPdfRenderer, ChromiumPdfRenderer>();
        // No identity here by design (build-spec-v0.1.md section 9: same-machine trust); DraughtsmanTools' actor
        // argument stays fully self-reported, exactly as before local sign-in existed (security review F2).
        builder.Services.AddSingleton<IActorContext, NullActorContext>();

        TimeSpan layoutTimeout = SidecarConfig.ResolveTimeout(config.Sidecar.LayoutTimeoutSeconds, SidecarConfig.DefaultLayoutTimeoutSeconds);
        TimeSpan yjsTimeout = SidecarConfig.ResolveTimeout(config.Sidecar.YjsTimeoutSeconds, SidecarConfig.DefaultYjsTimeoutSeconds);
        builder.Services.AddSidecar(new SidecarOptions
        {
            NodePath = config.Sidecar.NodePath,
            ScriptPath = config.Sidecar.ScriptPath,
            LayoutTimeout = layoutTimeout,
            YjsTimeout = yjsTimeout,
        });

        // Tool arguments and results are serialised with the document model's conventions, but the MCP SDK's AI function
        // factory requires an explicit TypeInfoResolver on the options before it marks them read-only.
        var toolSerializerOptions = new JsonSerializerOptions(DocumentJson.Options)
        {
            TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver,
        };

        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<DraughtsmanTools>(toolSerializerOptions)
            .WithArgumentValidation();

        IHost host = builder.Build();

        using (IServiceScope scope = host.Services.CreateScope())
        {
            ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Draughtsman.Mcp.Stdio");
            if (!await StorageStartup.MigrateOrRefuseAsync(scope.ServiceProvider.GetRequiredService<DraughtsmanDbContext>(), storage, config, logger, cancellationToken))
            {
                return;
            }
        }

        await host.RunAsync(cancellationToken);
    }
}
