using System.Globalization;
using Tempi.Configuration;
using Tempi.Outdoor;
using Tempi.Sensors;
using Tempi.Storage;
using Tempi.Web;

namespace Tempi.Cli;

/// <summary>Sous-commandes qui lisent ou modifient la base sans démarrer de service.</summary>
internal static class DataCommands
{
    public static int Stats(TempiConfig config)
    {
        using var storage = new TempiStorage(config.DbPath);
        var stats = storage.Stats();

        // La colonne d'étiquettes est alignée à onze caractères, comme en Python.
        Console.WriteLine($"Base       : {stats.DbPath} ({Formatting.Size(stats.DbBytes)})");
        Console.WriteLine($"Capteurs   : {stats.Sensors}");
        Console.WriteLine($"Mesures    : {stats.Readings}");
        Console.WriteLine($"Première   : {Formatting.Timestamp(stats.FirstTs)}");
        Console.WriteLine($"Dernière   : {Formatting.Timestamp(stats.LastTs)}");

        foreach (var sensor in storage.Sensors())
        {
            Console.WriteLine(
                $"  {Formatting.WithLabel(sensor.Address, sensor.Label)} : {sensor.Count} mesure(s)");
        }

        return 0;
    }

    public static int Sensors(TempiConfig config)
    {
        var bus = BusFactory.Create(config);
        var detected = bus.Discover();

        if (detected.Count == 0)
        {
            Console.Error.WriteLine("Aucun capteur détecté.");
            if (!config.Simulate)
            {
                Console.Error.WriteLine(
                    $"Vérifiez que le bus 1-Wire est activé et que {config.W1Dir} existe "
                    + "(voir la section « Câblage » du README).");
            }
        }
        else
        {
            Console.WriteLine($"{detected.Count} capteur(s) détecté(s) sur le bus :");
            foreach (var address in detected)
            {
                string value;
                try
                {
                    value = Formatting.Celsius(bus.Read(address));
                }
                catch (SensorException exc)
                {
                    value = $"erreur : {exc.Message}";
                }

                Console.WriteLine($"  {address}  {value}");
            }
        }

        using var storage = new TempiStorage(config.DbPath);
        var known = storage.Sensors();
        if (known.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Capteurs enregistrés dans {config.DbPath} :");
            foreach (var sensor in known)
            {
                Console.WriteLine(
                    $"  {Formatting.WithLabel(sensor.Address, sensor.Label)}  {sensor.Count} mesure(s), "
                    + $"dernière vue {Formatting.Timestamp(sensor.LastSeen)}");
            }
        }

        return 0;
    }

    public static int Read(TempiConfig config)
    {
        var scan = BusFactory.Create(config).ReadAll();

        foreach (var reading in scan.Readings)
        {
            Console.WriteLine($"{reading.Address}  {Formatting.Celsius(reading.Celsius)}");
        }

        foreach (var failure in scan.Failures)
        {
            Console.Error.WriteLine($"{failure.Address}  erreur : {failure.Error.Message}");
        }

        if (scan.Readings.Count == 0 && scan.Failures.Count == 0)
        {
            Console.Error.WriteLine("Aucun capteur détecté.");
            return 1;
        }

        // Un succès partiel reste un succès : au moins un capteur a répondu.
        return scan.Failures.Count > 0 && scan.Readings.Count == 0 ? 1 : 0;
    }

    public static int Label(TempiConfig config, string address, string? name)
    {
        using var storage = new TempiStorage(config.DbPath);

        if (!storage.SetLabel(address, name))
        {
            Console.Error.WriteLine($"Capteur inconnu : {address}");
            return 1;
        }

        Console.WriteLine(
            string.IsNullOrEmpty(name)
                ? $"Nom effacé pour {address}"
                : $"{address} → « {name} »");
        return 0;
    }

    public static int Prune(TempiConfig config, bool vacuum)
    {
        if (config.RetentionDays <= 0)
        {
            Console.Error.WriteLine(
                "Aucune rétention configurée : précisez --retention-days ou TEMPI_RETENTION_DAYS.");
            return 2;
        }

        using var storage = new TempiStorage(config.DbPath);
        var removed = Collect.Retention.Apply(
            config, storage, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        if (vacuum)
        {
            storage.Vacuum();
        }

        Console.WriteLine($"{removed} mesure(s) supprimée(s).");
        return 0;
    }

    public static int Export(
        TempiConfig config,
        string? from,
        string? to,
        string? range,
        string[] sensors,
        string? output)
    {
        using var storage = new TempiStorage(config.DbPath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        long start;
        if (!string.IsNullOrEmpty(from))
        {
            start = WindowResolver.ParseTimestamp(from);
        }
        else if (!string.IsNullOrEmpty(range))
        {
            start = now - WindowResolver.ParseDuration(range);
        }
        else
        {
            // Sans borne ni plage, on exporte depuis la première mesure : donc tout.
            start = storage.TimeRange().First ?? now;
        }

        var end = string.IsNullOrEmpty(to) ? now : WindowResolver.ParseTimestamp(to);
        var addresses = sensors.Length > 0 ? sensors : null;

        int rows;
        if (string.IsNullOrEmpty(output))
        {
            rows = CsvExport.Write(storage, start, end, addresses, Console.Out);
        }
        else
        {
            using var writer = new StreamWriter(output, append: false, System.Text.Encoding.UTF8);
            rows = CsvExport.Write(storage, start, end, addresses, writer);
            // Le compte part sur stderr : sur stdout il polluerait le CSV.
            Console.Error.WriteLine($"{rows} mesure(s) exportée(s) vers {output}");
        }

        return 0;
    }

    public static async Task<int> Outdoor(TempiConfig config, bool store, CancellationToken cancellationToken)
    {
        IOutdoorSource? source;
        try
        {
            source = OutdoorSources.Create(config);
        }
        catch (OutdoorException exc)
        {
            Console.Error.WriteLine($"Configuration invalide : {exc.Message}");
            return 2;
        }

        if (source is null)
        {
            Console.Error.WriteLine(
                "Aucune source extérieure configurée : précisez --outdoor-provider "
                + "ou TEMPI_OUTDOOR_PROVIDER (metar, infoclimat, open-meteo).");
            return 2;
        }

        Console.WriteLine($"Source   : {source.Describe()}");
        Console.WriteLine($"Capteur  : {OutdoorSources.AddressFor(source)} « {config.OutdoorLabel} »");

        using var storage = new TempiStorage(config.DbPath);
        var poller = new OutdoorPoller(config, storage, source);

        long observed;
        double celsius;
        try
        {
            if (store)
            {
                var reading = await poller.PollOnceAsync(cancellationToken);
                if (reading is null && poller.LastError is { } error)
                {
                    throw new OutdoorException(error);
                }

                if (reading is null)
                {
                    Console.WriteLine("Observation déjà enregistrée, rien à ajouter.");
                    return 0;
                }

                observed = reading.Value.Ts;
                celsius = reading.Value.Celsius;
            }
            else
            {
                var observation = await poller.ObserveAsync(cancellationToken);
                observed = observation.Ts;
                celsius = observation.Celsius;
            }
        }
        catch (OutdoorException exc)
        {
            Console.Error.WriteLine($"Relevé impossible : {exc.Message}");
            return 1;
        }

        var age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - observed);
        Console.WriteLine($"Mesure   : {celsius.ToString("F1", CultureInfo.InvariantCulture)} °C");
        Console.WriteLine($"Observée : {Formatting.Timestamp(observed)} (il y a {age / 60} min)");

        if (store)
        {
            Console.WriteLine($"Enregistrée dans {config.DbPath}.");
        }

        return 0;
    }
}
