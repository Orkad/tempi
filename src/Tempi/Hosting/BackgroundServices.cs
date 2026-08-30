using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tempi.Collect;
using Tempi.Configuration;
using Tempi.Outdoor;
using Tempi.Storage;

namespace Tempi.Hosting;

/// <summary>Fait tourner la boucle de collecte en tâche de fond.</summary>
internal sealed class CollectorService(
    Collector collector,
    IHostApplicationLifetime lifetime,
    int? maxCycles = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sans ce Yield, ExecuteAsync s'exécute en synchrone jusqu'au premier await et
        // retarde le démarrage de l'hôte d'un cycle entier — jusqu'à 750 ms par
        // capteur avec les tentatives.
        await Task.Yield();

        await collector.RunAsync(maxCycles, stoppingToken);

        if (maxCycles is not null)
        {
            // « collect -n 3 » doit rendre la main : l'hôte ne s'arrête pas tout seul.
            lifetime.StopApplication();
        }
    }
}

/// <summary>Applique la rétention une fois par jour.</summary>
internal sealed class RetentionService(
    TempiConfig config,
    TempiStorage storage,
    TimeProvider time,
    ILoggerFactory? loggerFactory = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var log = TempiLog.For(loggerFactory, TempiLog.Collector);

        // « appliquer puis attendre », et non l'inverse : la purge doit avoir lieu au
        // démarrage. C'est ce que fait le Python, qui appelle apply_retention avant de
        // lancer le fil d'exécution — ici l'ordre suffit, sans le double appel.
        do
        {
            try
            {
                Retention.Apply(config, storage, time, log);
            }
            catch (Exception exc)
            {
                log.LogError(exc, "application de la rétention en échec");
            }

            try
            {
                await Task.Delay(TimeSpan.FromDays(1), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        while (!stoppingToken.IsCancellationRequested);
    }
}

/// <summary>
/// Interroge la source extérieure en tâche de fond.
/// </summary>
/// <remarks>
/// Séparée de la collecte, et non ajoutée à son cycle : un appel réseau peut bloquer
/// plusieurs secondes, ce qui décalerait les relevés du DS18B20, et la cadence utile
/// n'est pas la même — une station publie toutes les 6 à 60 minutes là où le capteur
/// est lu chaque minute.
/// </remarks>
internal sealed class OutdoorService(
    TempiConfig config,
    OutdoorPoller poller,
    TimeProvider time,
    ILoggerFactory? loggerFactory = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var log = TempiLog.For(loggerFactory, TempiLog.Outdoor);
        var interval = OutdoorFactory.IntervalFor(config);

        log.LogInformation(
            "température extérieure : {Source} toutes les {Interval:F0} s, capteur {Address}",
            poller.Source.Describe(), interval.TotalSeconds, poller.Address);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await poller.PollOnceAsync(stoppingToken);
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                log.LogError(exc, "relevé extérieur en échec");
            }

            try
            {
                await Task.Delay(interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
