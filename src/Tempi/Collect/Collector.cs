using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tempi.Configuration;
using Tempi.Sensors;
using Tempi.Storage;

namespace Tempi.Collect;

/// <summary>Interroge les capteurs et écrit les mesures en base.</summary>
public sealed class Collector
{
    private readonly record struct LastStored(long Ts, double Celsius);

    private readonly TempiConfig _config;
    private readonly TempiStorage _storage;
    private readonly ITemperatureBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private readonly Dictionary<string, LastStored> _last = new(StringComparer.Ordinal);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    private int _cycles;
    private int _stored;
    private int _errors;
    private long _lastCycleTs;

    public Collector(
        TempiConfig config,
        TempiStorage storage,
        ITemperatureBus? bus = null,
        TimeProvider? time = null,
        ILogger? log = null)
    {
        _config = config;
        _storage = storage;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _bus = bus ?? BusFactory.Create(config, _time, _log);
    }

    // Lus par /api/health depuis un thread Kestrel, écrits par le collecteur :
    // Python s'en remettait au GIL, C# n'a rien d'équivalent.
    public int Cycles => Volatile.Read(ref _cycles);

    public int Stored => Volatile.Read(ref _stored);

    public int Errors => Volatile.Read(ref _errors);

    public long? LastCycleTs
    {
        get
        {
            var value = Interlocked.Read(ref _lastCycleTs);
            return value == 0 ? null : value;
        }
    }

    // -- filtrage -----------------------------------------------------------

    /// <summary>
    /// Applique la bande morte : évite d'écrire des mesures identiques.
    /// </summary>
    /// <remarks>
    /// Sur une carte SD, réduire les écritures allonge nettement la durée de vie. On
    /// conserve toutefois un point au moins toutes les <c>max_interval</c> secondes
    /// pour que les courbes ne comportent pas de trous, et pour prouver que le
    /// capteur répond toujours.
    /// </remarks>
    private bool ShouldStore(Reading reading)
    {
        if (_config.MinDelta <= 0)
        {
            return true;
        }

        if (!_last.TryGetValue(reading.Address, out var previous))
        {
            return true;
        }

        if (Math.Abs(reading.Celsius - previous.Celsius) >= _config.MinDelta)
        {
            return true;
        }

        return _config.MaxInterval > 0 && reading.Ts - previous.Ts >= _config.MaxInterval;
    }

    // -- cycle --------------------------------------------------------------

    /// <summary>Effectue un cycle de lecture et retourne les mesures enregistrées.</summary>
    public IReadOnlyList<Reading> PollOnce()
    {
        var scan = _bus.ReadAll();

        foreach (var failure in scan.Failures)
        {
            Interlocked.Increment(ref _errors);
            _log.LogWarning("capteur {Address} : {Message}", failure.Address, failure.Error.Message);
        }

        foreach (var reading in scan.Readings)
        {
            if (_known.Add(reading.Address))
            {
                _log.LogInformation("capteur détecté : {Address}", reading.Address);
            }
        }

        if (scan.Readings.Count == 0 && scan.Failures.Count == 0)
        {
            _log.LogWarning(
                "aucun capteur détecté dans {Directory} — le bus 1-Wire est-il activé ?",
                _bus is W1Bus w1 ? w1.W1Dir : "?");
        }

        var toStore = scan.Readings.Where(ShouldStore).ToArray();
        if (toStore.Length > 0)
        {
            _storage.Record(toStore);
            Interlocked.Add(ref _stored, toStore.Length);
            foreach (var reading in toStore)
            {
                _last[reading.Address] = new LastStored(reading.Ts, reading.Celsius);
            }
        }

        Interlocked.Increment(ref _cycles);
        Interlocked.Exchange(ref _lastCycleTs, _time.GetUtcNow().ToUnixTimeSeconds());

        foreach (var reading in scan.Readings)
        {
            _log.LogDebug("{Address} = {Celsius:F3} °C", reading.Address, reading.Celsius);
        }

        return toStore;
    }

    // -- boucle -------------------------------------------------------------

    /// <summary>
    /// Boucle jusqu'à l'annulation, ou jusqu'à <paramref name="maxCycles"/> cycles.
    /// </summary>
    /// <remarks>
    /// Le rythme est calé sur une horloge absolue plutôt que sur une attente après
    /// chaque cycle : la lecture d'un DS18B20 prend jusqu'à 750 ms, et cumuler ce
    /// délai ferait dériver les horodatages. Quand un cycle prend plus que
    /// l'intervalle, on saute les échéances manquées au lieu de les rattraper toutes.
    /// </remarks>
    public async Task RunAsync(int? maxCycles, CancellationToken cancellationToken)
    {
        var interval = _config.Interval;
        var frequency = _time.TimestampFrequency;
        var ticksPerInterval = (long)(interval * frequency);

        _log.LogInformation(
            "collecte démarrée : intervalle {Interval:F1} s, base {Db}",
            interval, _storage.Path);

        var next = _time.GetTimestamp();
        var cycles = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
            catch (Exception exc)
            {
                // Une erreur ponctuelle ne doit pas tuer le service.
                Interlocked.Increment(ref _errors);
                _log.LogError(exc, "cycle de collecte en échec");
            }

            cycles++;
            if (maxCycles is not null && cycles >= maxCycles)
            {
                break;
            }

            next += ticksPerInterval;
            var remaining = next - _time.GetTimestamp();

            if (remaining < 0)
            {
                var late = -remaining / (double)frequency;
                var missed = (int)(late / interval) + 1;
                _log.LogDebug(
                    "collecte en retard de {Late:F1} s, {Missed} cycle(s) sautés", late, missed);

                next += missed * ticksPerInterval;
                remaining = Math.Max(0, next - _time.GetTimestamp());
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(remaining / (double)frequency), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation(
            "collecte arrêtée après {Cycles} cycle(s), {Stored} mesure(s) enregistrée(s), {Errors} erreur(s)",
            Cycles, Stored, Errors);
    }
}
