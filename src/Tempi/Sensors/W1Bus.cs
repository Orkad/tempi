using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tempi.Configuration;

namespace Tempi.Sensors;

/// <summary>Accès aux capteurs de température branchés sur le bus 1-Wire.</summary>
/// <remarks>
/// La classe n'est pas <c>sealed</c> et <see cref="ReadFrame"/> est virtuelle : c'est
/// la couture par laquelle les tests comptent les tentatives, là où le test Python
/// remplaçait une méthode d'instance.
/// </remarks>
public class W1Bus : ITemperatureBus
{
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public W1Bus(
        string? w1Dir = null,
        int retries = 3,
        bool allowResetValue = false,
        TimeSpan? retryDelay = null,
        TimeProvider? time = null,
        ILogger? log = null)
    {
        W1Dir = w1Dir ?? DefaultPaths.DefaultW1Dir;
        Retries = Math.Max(1, retries);
        AllowResetValue = allowResetValue;
        RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public string W1Dir { get; }
    public int Retries { get; }
    public bool AllowResetValue { get; }
    public TimeSpan RetryDelay { get; }

    public bool Available => Directory.Exists(W1Dir);

    public IReadOnlyList<string> Discover()
    {
        if (!Available)
        {
            return [];
        }

        return new DirectoryInfo(W1Dir)
            .EnumerateFileSystemInfos()
            .Select(entry => entry.Name)
            .Where(name => TemperatureFamilies.IsTemperature(name.Split('-', 2)[0].ToLowerInvariant()))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Lit le contenu brut du fichier <c>w1_slave</c> d'un capteur.</summary>
    protected virtual string ReadFrame(string address)
    {
        var path = Path.Combine(W1Dir, address, "w1_slave");
        try
        {
            return File.ReadAllText(path);
        }
        catch (FileNotFoundException exc)
        {
            throw new SensorException($"capteur {address} introuvable ({path})", exc);
        }
        catch (DirectoryNotFoundException exc)
        {
            throw new SensorException($"capteur {address} introuvable ({path})", exc);
        }
        catch (IOException exc)
        {
            // Le pilote renvoie EIO lorsque la conversion échoue.
            throw new SensorException($"lecture de {path} impossible : {exc.Message}", exc);
        }
        catch (UnauthorizedAccessException exc)
        {
            throw new SensorException($"lecture de {path} impossible : {exc.Message}", exc);
        }
    }

    private double ReadOnce(string address)
    {
        var celsius = W1SlaveParser.Parse(ReadFrame(address));

        if (!AllowResetValue
            && (int)Math.Round(celsius * 1000) == TemperatureFamilies.ResetValueMillidegrees)
        {
            throw new ResetValueException(
                "valeur de reset 85 °C — vérifiez l'alimentation et la résistance de tirage");
        }

        if (celsius < TemperatureFamilies.MinCelsius || celsius > TemperatureFamilies.MaxCelsius)
        {
            throw new OutOfRangeException($"{PythonRepr.Number(celsius)} °C hors de la plage du capteur");
        }

        return celsius;
    }

    /// <summary>
    /// Lit un capteur, en réessayant sur erreur transitoire.
    /// </summary>
    /// <remarks>
    /// Les erreurs de CRC et les valeurs de reset sont fréquentes sur un câblage
    /// long ; une nouvelle tentative suffit généralement.
    /// </remarks>
    public double Read(string address)
    {
        SensorException? lastError = null;
        for (var attempt = 1; attempt <= Retries; attempt++)
        {
            try
            {
                return ReadOnce(address);
            }
            catch (SensorException exc)
            {
                lastError = exc;
                _log.LogDebug(
                    "lecture de {Address} échouée (tentative {Attempt}/{Retries}) : {Message}",
                    address, attempt, Retries, exc.Message);

                if (attempt < Retries && RetryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        throw lastError!;
    }

    public BusScan ReadAll(IReadOnlyList<string>? addresses = null)
    {
        return BusScanner.Run(this, addresses ?? Discover(), _time);
    }
}

/// <summary>Boucle de lecture partagée par les deux bus.</summary>
internal static class BusScanner
{
    public static BusScan Run(ITemperatureBus bus, IReadOnlyList<string> targets, TimeProvider time)
    {
        var readings = new List<Reading>();
        var failures = new List<BusFailure>();

        foreach (var address in targets)
        {
            try
            {
                var celsius = bus.Read(address);
                readings.Add(new Reading(address, celsius, time.GetUtcNow().ToUnixTimeSeconds()));
            }
            catch (SensorException exc)
            {
                failures.Add(new BusFailure(address, exc));
            }
        }

        return new BusScan(readings, failures);
    }
}
