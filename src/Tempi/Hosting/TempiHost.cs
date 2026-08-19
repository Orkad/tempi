using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Tempi.Collect;
using Tempi.Configuration;
using Tempi.Outdoor;
using Tempi.Storage;
using Tempi.Web;

namespace Tempi.Hosting;

/// <summary>Composition des trois modes qui font tourner un service : serve, collect, run.</summary>
public static class TempiHost
{
    /// <summary>
    /// Construit l'application web, avec ou sans collecte.
    /// </summary>
    /// <param name="storage">
    /// Injecté par les tests, qui préparent la base avant de démarrer le serveur ; la
    /// ligne de commande laisse la valeur nulle et l'ouvre à partir de la configuration.
    /// </param>
    public static WebApplication BuildWeb(
        TempiConfig config,
        TempiStorage? storage = null,
        bool withCollector = false,
        bool verbose = false,
        TimeProvider? time = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = "tempi",
            EnvironmentName = Environments.Production,
        });

        // Toute la configuration vient de TempiConfig : ni appsettings.json, ni
        // variables ASPNETCORE_*, qui ne feraient qu'ajouter des sources de surprise.
        // Une source vide reste nécessaire : UseUrls écrit son réglage dans la
        // configuration, et refuse de le faire si aucune source ne peut l'accueillir.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        ConfigureLogging(builder.Logging, verbose);

        // Sans systemd, AddSystemd() est un no-op complet : la même binaire tourne en
        // interactif. Sous systemd, elle gère SIGTERM et l'annonce READY=1.
        builder.Services.AddSystemd();

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton(storage ?? new TempiStorage(config.DbPath, time));

        if (withCollector)
        {
            AddCollection(builder.Services, config, time, null);
        }

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
        builder.WebHost.UseUrls(ListenUrl(config.Host, config.Port));

        var app = builder.Build();
        app.Use(TempiEndpoints.Middleware);
        TempiEndpoints.Map(app);
        return app;
    }

    /// <summary>Construit l'hôte de « collect » : les mêmes services, sans serveur web.</summary>
    public static IHost BuildCollector(
        TempiConfig config,
        int? maxCycles,
        bool verbose = false,
        TimeProvider? time = null)
    {
        // DisableDefaults écarte appsettings.json, les variables DOTNET_*/ASPNETCORE_*
        // et le cycle de vie console par défaut : autant de sources de comportement
        // que la configuration de tempi ne contrôle pas.
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });

        ConfigureLogging(builder.Logging, verbose);
        builder.Services.AddSystemd();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton(new TempiStorage(config.DbPath, time));

        AddCollection(builder.Services, config, time, maxCycles);
        return builder.Build();
    }

    /// <summary>Enregistre la collecte, la rétention et la source extérieure.</summary>
    private static void AddCollection(
        IServiceCollection services,
        TempiConfig config,
        TimeProvider? time,
        int? maxCycles)
    {
        services.AddSingleton(sp => new Collector(
            config,
            sp.GetRequiredService<TempiStorage>(),
            time: sp.GetRequiredService<TimeProvider>(),
            log: TempiLog.For(sp.GetService<ILoggerFactory>(), TempiLog.Collector)));

        services.AddHostedService(sp => new CollectorService(
            sp.GetRequiredService<Collector>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            maxCycles));

        services.AddHostedService(sp => new RetentionService(
            config,
            sp.GetRequiredService<TempiStorage>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));

        // La source extérieure est facultative : sans fournisseur, aucun service n'est
        // enregistré et aucune requête réseau n'est émise.
        var probe = OutdoorSources.Create(config, time);
        if (probe is null)
        {
            return;
        }

        services.AddSingleton(sp => OutdoorFactory.TryCreate(
            config,
            sp.GetRequiredService<TempiStorage>(),
            time: sp.GetRequiredService<TimeProvider>(),
            log: TempiLog.For(sp.GetService<ILoggerFactory>(), TempiLog.Outdoor))!);

        services.AddHostedService(sp => new OutdoorService(
            config,
            sp.GetRequiredService<OutdoorPoller>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));
    }

    /// <summary>
    /// Compose l'URL d'écoute de Kestrel.
    /// </summary>
    /// <remarks>
    /// Une adresse IPv6 doit être encadrée de crochets : sans eux,
    /// <c>TEMPI_HOST=::</c> produirait <c>http://:::8080</c> et Kestrel refuserait de
    /// démarrer. Python n'avait pas le problème — <c>bind()</c> prend une adresse, pas
    /// une URL.
    /// </remarks>
    internal static string ListenUrl(string host, int port) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"http://[{host}]:{port}"
            : $"http://{host}:{port}";

    private static void ConfigureLogging(ILoggingBuilder logging, bool verbose)
    {
        logging.ClearProviders();

        // AddConsoleFormatter lierait les options du formateur depuis la
        // configuration, par réflexion : l'analyseur de trimming le signale, et la
        // liaison ne servirait à rien puisque les sources de configuration sont vides.
        // L'enregistrement direct évite les deux.
        logging.Services.AddSingleton<ConsoleFormatter, TempiConsoleFormatter>();

        logging.AddConsole(options =>
        {
            options.FormatterName = TempiConsoleFormatter.FormatterName;
            // Tout part sur stderr : « tempi export » écrit son CSV sur stdout, une
            // ligne de journal l'y corromprait.
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);
    }
}
