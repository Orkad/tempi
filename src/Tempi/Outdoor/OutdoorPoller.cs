using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tempi.Configuration;
using Tempi.Sensors;
using Tempi.Storage;

namespace Tempi.Outdoor;

/// <summary>
/// Récupère le contenu d'une URL.
/// </summary>
/// <remarks>
/// Point de sortie réseau unique, exprimé comme une dépendance plutôt que comme un
/// appel direct : c'est ce qui permet aux tests de couvrir les trois fournisseurs
/// sans serveur factice ni accès réseau, exactement comme le <c>fetch</c> injecté
/// des tests Python.
/// </remarks>
public delegate Task<byte[]> OutdoorFetch(string url, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>Implémentation réelle de <see cref="OutdoorFetch"/>, sur <c>HttpClient</c>.</summary>
public sealed class HttpFetcher : IDisposable
{
    private readonly HttpClient _client = new();

    public HttpFetcher()
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(OutdoorPrimitives.UserAgent);
    }

    public void Dispose() => _client.Dispose();

    public async Task<byte[]> FetchAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        try
        {
            using var response = await _client.GetAsync(url, linked.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Le corps d'une erreur porte souvent le motif exact (clé invalide,
                // quota dépassé) : le perdre rendrait le diagnostic impossible.
                var detail = string.Empty;
                try
                {
                    var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    detail = body.Length > 500 ? body[..500].Trim() : body.Trim();
                }
                catch (Exception)
                {
                    // Dépend du serveur distant : l'absence de détail n'est pas une erreur.
                }

                var code = (int)response.StatusCode;
                throw new OutdoorException(
                    $"HTTP {code} sur {url}{(detail.Length > 0 ? " — " + detail : string.Empty)}");
            }

            return await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OutdoorException($"{url} injoignable : délai de {timeout.TotalSeconds:F0} s dépassé");
        }
        catch (HttpRequestException exc)
        {
            throw new OutdoorException($"{url} injoignable : {exc.Message}", exc);
        }
    }
}

/// <summary>Interroge la source distante et enregistre le résultat comme une mesure.</summary>
public sealed class OutdoorPoller
{
    private readonly TempiConfig _config;
    private readonly TempiStorage _storage;
    private readonly OutdoorFetch _fetch;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private long? _lastTs;
    private string? _silenced;

    public OutdoorPoller(
        TempiConfig config,
        TempiStorage storage,
        IOutdoorSource? source = null,
        OutdoorFetch? fetch = null,
        TimeProvider? time = null,
        ILogger? log = null,
        HttpFetcher? httpFetcher = null)
    {
        _config = config;
        _storage = storage;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;

        Source = source ?? OutdoorSources.Create(config, _time)
            ?? throw new OutdoorException("aucune source extérieure configurée");

        _fetch = fetch ?? (httpFetcher ?? new HttpFetcher()).FetchAsync;
        Address = OutdoorSources.AddressFor(Source);
    }

    public IOutdoorSource Source { get; }

    public string Address { get; }

    // Compteurs exposés par /api/health.
    public int Polls { get; private set; }

    public int Stored { get; private set; }

    public int Errors { get; private set; }

    public long? LastOkTs { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Récupère la dernière observation publiée, sans rien enregistrer.</summary>
    public async Task<Observation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _fetch(
                Source.Url(),
                TimeSpan.FromSeconds(_config.OutdoorTimeout),
                cancellationToken)
            .ConfigureAwait(false);

        return Source.Parse(payload);
    }

    /// <summary>
    /// Interroge la source et enregistre la mesure si elle est nouvelle.
    /// </summary>
    /// <returns>La mesure enregistrée, ou <c>null</c> si l'observation était déjà connue.</returns>
    /// <remarks>
    /// Les erreurs sont journalisées, jamais propagées : une API publique
    /// indisponible ne doit pas interrompre la collecte du DS18B20.
    /// </remarks>
    public async Task<Reading?> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        Polls++;

        Observation observation;
        try
        {
            observation = await ObserveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OutdoorException exc)
        {
            Errors++;
            LastError = exc.Message;

            // Une panne durable (clé révoquée, IP changée) répéterait le même message
            // à chaque cycle : on ne le journalise qu'au changement.
            if (_silenced == exc.Message)
            {
                _log.LogDebug("température extérieure indisponible : {Message}", exc.Message);
            }
            else
            {
                _log.LogWarning("température extérieure indisponible : {Message}", exc.Message);
                _silenced = exc.Message;
            }

            return null;
        }

        _silenced = null;
        LastError = null;
        LastOkTs = _time.GetUtcNow().ToUnixTimeSeconds();

        // Le dernier horodatage est relu en base au premier passage : c'est ce qui fait
        // survivre le filtre à un redémarrage.
        _lastTs ??= StoredTs();

        if (_lastTs is not null && observation.Ts <= _lastTs)
        {
            _log.LogDebug(
                "observation {Source} déjà enregistrée ({Ts})", Source.Describe(), observation.Ts);
            return null;
        }

        var reading = new Reading(Address, observation.Celsius, observation.Ts);
        _storage.EnsureSensor(Address, _config.OutdoorLabel);
        _storage.Record([reading]);
        _lastTs = observation.Ts;
        Stored++;

        _log.LogInformation(
            "extérieur ({Source}) = {Celsius:F1} °C, observé à {Observed}",
            Source.Describe(),
            observation.Celsius,
            DateTimeOffset.FromUnixTimeSeconds(observation.Ts)
                .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

        return reading;
    }

    /// <summary>Dernier horodatage déjà en base, pour survivre à un redémarrage.</summary>
    private long? StoredTs()
    {
        foreach (var sensor in _storage.Latest())
        {
            if (sensor.Address == Address)
            {
                return sensor.Ts;
            }
        }

        return null;
    }
}

/// <summary>Construction tolérante du relevé extérieur.</summary>
public static class OutdoorFactory
{
    /// <summary>
    /// Cadence effective : jamais en dessous du plancher.
    /// </summary>
    /// <remarks>Une valeur trop basse martèlerait une API publique gratuite.</remarks>
    public static TimeSpan IntervalFor(TempiConfig config) =>
        TimeSpan.FromSeconds(Math.Max(OutdoorPrimitives.MinInterval, config.OutdoorInterval));

    /// <summary>
    /// Construit le relevé extérieur si la configuration en décrit un.
    /// </summary>
    /// <remarks>
    /// Une configuration invalide est signalée sans empêcher le démarrage : mieux vaut
    /// enregistrer les DS18B20 sans la courbe extérieure que pas du tout.
    /// </remarks>
    public static OutdoorPoller? TryCreate(
        TempiConfig config,
        TempiStorage storage,
        OutdoorFetch? fetch = null,
        TimeProvider? time = null,
        ILogger? log = null)
    {
        try
        {
            if (OutdoorSources.Create(config, time) is null)
            {
                return null;
            }

            return new OutdoorPoller(config, storage, fetch: fetch, time: time, log: log);
        }
        catch (OutdoorException exc)
        {
            (log ?? NullLogger.Instance).LogWarning(
                "source extérieure ignorée : {Message}", exc.Message);
            return null;
        }
    }
}
