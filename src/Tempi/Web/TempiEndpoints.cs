using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tempi.Hosting;
using Tempi.Collect;
using Tempi.Configuration;
using Tempi.Outdoor;
using Tempi.Storage;

namespace Tempi.Web;

/// <summary>Routes HTTP : API JSON, export CSV et interface de consultation.</summary>
/// <remarks>
/// Le serveur n'a pas d'authentification : il écoute par défaut sur 127.0.0.1.
/// </remarks>
public static partial class TempiEndpoints
{
    [GeneratedRegex("^[0-9a-zA-Z._-]+$")]
    private static partial Regex AddressShape();

    /// <summary>
    /// Options de sérialisation communes.
    /// </summary>
    /// <remarks>
    /// <c>UnsafeRelaxedJsonEscaping</c> reproduit <c>ensure_ascii=False</c> : sans lui,
    /// « Congélateur » partirait en « Congu00e9lateur » et « durée invalide » serait
    /// illisible. Aucun risque ici, rien n'est inséré dans du HTML.
    /// </remarks>
    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        Converters = { new PythonDoubleConverter() },
    };

    /// <summary>
    /// Contexte lié aux options ci-dessus.
    /// </summary>
    /// <remarks>
    /// Sérialiser via <c>TempiJsonContext.Default</c> emploierait les options par
    /// défaut du générateur, donc l'échappement des non-ASCII : il faut une instance
    /// construite sur nos options pour que l'encodeur relâché s'applique réellement.
    /// </remarks>
    internal static readonly TempiJsonContext Context = new(Json);

    public static void Map(IEndpointRouteBuilder app)
    {
        string[] readMethods = [HttpMethods.Get, HttpMethods.Head];

        app.MapMethods("/", readMethods, IndexPage.Write);
        app.MapMethods("/api/health", readMethods, Health);
        app.MapMethods("/api/sensors", readMethods, Sensors);
        app.MapMethods("/api/latest", readMethods, Latest);
        app.MapMethods("/api/series", readMethods, Series);
        app.MapMethods("/api/summary", readMethods, Summary);
        app.MapMethods("/api/export.csv", readMethods, Export);
        app.MapMethods("/api/export", readMethods, Export);   // alias non documenté
        app.MapPost("/api/sensors/{address}/label", SetLabel);

        // Python renvoie 404 pour toute route inconnue, y compris un POST vers une
        // route qui n'accepte que GET — là où ASP.NET répondrait 405. Le repli
        // court-circuite la politique de méthode.
        app.MapFallback(NotFound);
    }

    /// <summary>
    /// Normalise le chemin et pose l'en-tête de cache.
    /// </summary>
    /// <remarks>
    /// <c>Cache-Control: no-store</c> n'est pas décoratif : l'interface interroge
    /// l'API toutes les 30 secondes sans le moindre paramètre anti-cache, elle en
    /// dépend entièrement. L'export CSV en est la seule exception — Python y écrit ses
    /// en-têtes à la main et n'y pose pas cet en-tête.
    /// </remarks>
    public static async Task Middleware(HttpContext context, RequestDelegate next)
    {
        var raw = context.Request.Path.Value;
        context.Items["tempi.raw-path"] = raw;

        if (raw is { Length: > 1 } && raw.EndsWith('/'))
        {
            var trimmed = raw.TrimEnd('/');
            context.Request.Path = trimmed.Length == 0 ? "/" : trimmed;
        }

        context.Response.Headers.CacheControl = "no-store";

        try
        {
            await next(context);
        }
        catch (BadRequestException exc)
        {
            await Fail(context, StatusCodes.Status400BadRequest, exc.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Le navigateur a fermé l'onglet en cours de transfert : rien à signaler.
        }
        catch (Exception exc)
        {
            var log = context.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
                ? factory.CreateLogger(TempiLog.Web)
                : (ILogger)NullLogger.Instance;

            log.LogError(exc, "erreur lors du traitement de {Path}", context.Request.Path);
            await Fail(context, StatusCodes.Status500InternalServerError, exc.Message);
        }
    }

    /// <summary>Rend une erreur au format attendu par l'interface : <c>{"error": …}</c>.</summary>
    private static async Task Fail(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = status;
        await WriteJson(context, new ErrorDto(message), Context.ErrorDto);
    }

    // -- points d'entrée ----------------------------------------------------

    private static async Task Health(HttpContext context)
    {
        var storage = Service<TempiStorage>(context);
        var config = Service<TempiConfig>(context);
        var time = Service<TimeProvider>(context);

        var stats = storage.Stats();
        var collector = context.RequestServices.GetService(typeof(Collector)) as Collector;
        var outdoor = context.RequestServices.GetService(typeof(OutdoorPoller)) as OutdoorPoller;

        var payload = new HealthDto(
            "ok",
            TempiVersion.Value,
            time.GetUtcNow().ToUnixTimeSeconds(),
            new StorageStatsDto(
                stats.DbPath, stats.DbBytes, stats.Sensors, stats.Readings, stats.FirstTs, stats.LastTs),
            collector is null
                ? null
                : new CollectorStatsDto(
                    collector.Cycles, collector.Stored, collector.Errors,
                    collector.LastCycleTs, config.Interval),
            outdoor is null
                ? null
                : new OutdoorStatsDto(
                    outdoor.Source.Describe(), outdoor.Address, outdoor.Polls, outdoor.Stored,
                    outdoor.Errors, outdoor.LastOkTs, outdoor.LastError,
                    OutdoorFactory.IntervalFor(config).TotalSeconds));

        await WriteJson(context, payload, Context.HealthDto);
    }

    private static async Task Sensors(HttpContext context)
    {
        var rows = Service<TempiStorage>(context).Sensors()
            .Select(s => new SensorDto(s.Id, s.Address, s.Label, s.FirstSeen, s.LastSeen, s.Count))
            .ToArray();

        await WriteJson(context, new SensorsDto(rows), Context.SensorsDto);
    }

    private static async Task Latest(HttpContext context)
    {
        var rows = Service<TempiStorage>(context).Latest()
            .Select(l => new LatestSensorDto(l.Address, l.Label, l.Ts, l.Celsius))
            .ToArray();

        var payload = new LatestDto(Service<TimeProvider>(context).GetUtcNow().ToUnixTimeSeconds(), rows);
        await WriteJson(context, payload, Context.LatestDto);
    }

    private static async Task Series(HttpContext context)
    {
        var storage = Service<TempiStorage>(context);
        var query = new QueryBag(context.Request.Query);
        var (start, end) = WindowResolver.Resolve(query, storage, Service<TimeProvider>(context));
        var addresses = query.All("sensor");

        var bucket = ResolveBucket(query, end - start);
        var series = storage.Series(start, end, addresses, bucket);
        var labels = storage.Sensors().ToDictionary(s => s.Address, s => s.Label, StringComparer.Ordinal);

        var payload = new SeriesResponseDto(
            start,
            end,
            bucket,
            series
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new SeriesDto(
                    entry.Key,
                    labels.GetValueOrDefault(entry.Key),
                    entry.Value
                        .Select(p => new PointDto(p.Ts, p.Celsius, p.Min, p.Max, p.Samples))
                        .ToArray()))
                .ToArray());

        await WriteJson(context, payload, Context.SeriesResponseDto);
    }

    private static int ResolveBucket(QueryBag query, long span)
    {
        if (query.First("bucket") is not { } raw)
        {
            return Buckets.Choose(span);
        }

        if (raw == "auto")
        {
            return Buckets.Choose(span);
        }

        if (raw == "raw")
        {
            return 0;
        }

        return int.TryParse(raw, out var seconds)
            ? Math.Max(0, seconds)
            : (int)WindowResolver.ParseDuration(raw);
    }

    private static async Task Summary(HttpContext context)
    {
        var storage = Service<TempiStorage>(context);
        var query = new QueryBag(context.Request.Query);
        var (start, end) = WindowResolver.Resolve(query, storage, Service<TimeProvider>(context));

        var summary = storage.Summary(start, end, query.All("sensor"))
            .ToDictionary(
                entry => entry.Key,
                entry => new SummaryEntryDto(entry.Value.Min, entry.Value.Max, entry.Value.Avg, entry.Value.Samples),
                StringComparer.Ordinal);

        await WriteJson(
            context, new SummaryResponseDto(start, end, summary), Context.SummaryResponseDto);
    }

    private static async Task Export(HttpContext context)
    {
        var storage = Service<TempiStorage>(context);
        var query = new QueryBag(context.Request.Query);
        var (start, end) = WindowResolver.Resolve(query, storage, Service<TimeProvider>(context));

        var buffer = new StringWriter();
        CsvExport.Write(storage, start, end, query.All("sensor"), buffer);
        var body = Encoding.UTF8.GetBytes(buffer.ToString());

        context.Response.ContentType = "text/csv; charset=utf-8";
        context.Response.ContentLength = body.Length;
        context.Response.Headers.ContentDisposition = "attachment; filename=\"tempi-export.csv\"";

        // Python écrit les en-têtes de l'export à la main et n'y pose pas
        // Cache-Control, contrairement à toutes les autres réponses. Reproduire cette
        // asymétrie n'a l'air de rien, mais c'est ce que compare le golden master.
        context.Response.Headers.Remove("Cache-Control");

        if (context.Request.Method != HttpMethods.Head)
        {
            await context.Response.Body.WriteAsync(body);
        }
    }

    private static async Task SetLabel(HttpContext context, string address)
    {
        // Les contraintes de route par expression régulière ne sont pas supportées
        // sous CreateSlimBuilder : la validation se fait ici, et une adresse mal
        // formée donne un 404 comme une route inconnue.
        if (!AddressShape().IsMatch(address))
        {
            await NotFound(context);
            return;
        }

        if (context.Request.ContentLength > 64 * 1024)
        {
            throw new BadRequestException("corps de requête trop volumineux");
        }

        LabelRequestDto? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body, Context.LabelRequestDto, context.RequestAborted);
        }
        catch (JsonException exc)
        {
            throw new BadRequestException($"JSON invalide : {exc.Message}");
        }

        var label = request?.Label;
        if (label is not null)
        {
            var trimmed = label.Trim();
            // Tranche des points de code, comme Python : une paire de substitution
            // ne doit pas être coupée en deux.
            if (trimmed.Length > 80)
            {
                var runes = trimmed.EnumerateRunes().Take(80);
                trimmed = string.Concat(runes.Select(r => r.ToString()));
            }

            label = trimmed.Length == 0 ? null : trimmed;
        }

        if (!Service<TempiStorage>(context).SetLabel(address, label))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await WriteJson(
                context, new ErrorDto($"capteur inconnu : {address}"), Context.ErrorDto);
            return;
        }

        await WriteJson(
            context, new LabelResponseDto(address, label), Context.LabelResponseDto);
    }

    private static async Task NotFound(HttpContext context)
    {
        var path = context.Items["tempi.raw-path"] as string ?? context.Request.Path.Value ?? "/";
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await WriteJson(context, new ErrorDto($"route inconnue : {path}"), Context.ErrorDto);
    }

    // -- utilitaires --------------------------------------------------------

    private static T Service<T>(HttpContext context)
        where T : class => (T)context.RequestServices.GetService(typeof(T))!;

    private static async Task WriteJson<T>(
        HttpContext context,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;

        if (context.Request.Method != HttpMethods.Head)
        {
            await context.Response.Body.WriteAsync(body);
        }
    }
}
