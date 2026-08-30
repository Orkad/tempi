using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tempi.Configuration;

namespace Tempi.Outdoor;

/// <summary>
/// Observations METAR diffusées par aviationweather.gov (NOAA).
/// </summary>
/// <remarks>
/// Les stations sont désignées par leur code OACI à quatre lettres : <c>LFLY</c>
/// (Lyon-Bron), <c>LFLL</c> (Lyon-Saint-Exupéry), <c>LFPG</c>… Mesure réelle sous
/// abri normalisé, aucune clé d'API, mais les aérodromes sont souvent excentrés.
/// </remarks>
public sealed partial class MetarSource : IOutdoorSource
{
    /// <summary>
    /// Groupe température/point de rosée d'un METAR brut, en secours si le champ
    /// numérique manque. <c>M</c> préfixe les valeurs négatives.
    /// </summary>
    [GeneratedRegex(@"\s(M?\d{2})/(M?\d{2})\s")]
    private static partial Regex RawTemp();

    public MetarSource(string station)
    {
        Station = station.Trim().ToUpperInvariant();
        if (Station.Length == 0)
        {
            throw new OutdoorException(
                "le fournisseur metar demande un code OACI (TEMPI_OUTDOOR_STATION)");
        }
    }

    public string Station { get; }

    public string Name => "metar";

    public string Slug => OutdoorPrimitives.Slug(Station);

    public string Describe() => $"METAR {Station}";

    public string Url() =>
        "https://aviationweather.gov/api/data/metar?"
        + OutdoorPrimitives.UrlEncode(("ids", Station), ("format", "json"));

    public Observation Parse(byte[] payload)
    {
        using var document = OutdoorPrimitives.LoadJson(payload);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            root = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data
                : root.TryGetProperty("features", out var features) ? features : default;
        }

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            throw new OutdoorException($"aucune observation pour la station {Station}");
        }

        // L'API renvoie les rapports du plus récent au plus ancien, mais rien ne le
        // garantit : on trie explicitement.
        JsonElement? report = null;
        var best = double.NegativeInfinity;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = SortKey(item);
            if (report is null || key > best)
            {
                report = item;
                best = key;
            }
        }

        if (report is null)
        {
            throw new OutdoorException($"aucune observation pour la station {Station}");
        }

        var found = report.Value;

        if (!found.TryGetProperty("obsTime", out var rawTs)
            || rawTs.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || (rawTs.ValueKind == JsonValueKind.String && rawTs.GetString()!.Length == 0))
        {
            throw new OutdoorException("horodatage d'observation absent");
        }

        var ts = rawTs.ValueKind == JsonValueKind.Number
            ? rawTs.GetInt64()
            : long.Parse(rawTs.GetString()!, CultureInfo.InvariantCulture);

        double celsius;
        var hasTemp = found.TryGetProperty("temp", out var temp)
                      && temp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                      && !(temp.ValueKind == JsonValueKind.String && temp.GetString()!.Length == 0);

        if (hasTemp)
        {
            celsius = OutdoorPrimitives.AsCelsius(temp, "temp");
        }
        else
        {
            var raw = found.TryGetProperty("rawOb", out var rawOb) ? rawOb.GetString() ?? "" : "";
            var fallback = FromRaw(raw)
                ?? throw new OutdoorException("température absente de la réponse (temp)");
            celsius = OutdoorPrimitives.AsCelsius(fallback, "temp");
        }

        var station = found.TryGetProperty("icaoId", out var icao) && icao.ValueKind == JsonValueKind.String
            ? icao.GetString()!
            : Station;

        return new Observation(celsius, ts, station);
    }

    /// <summary>Classe les rapports par ancienneté, sans se fier au type d'<c>obsTime</c>.</summary>
    private static double SortKey(JsonElement report)
    {
        if (!report.TryGetProperty("obsTime", out var value))
        {
            return 0.0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0.0,
        };
    }

    private static double? FromRaw(string raw)
    {
        var match = RawTemp().Match($" {raw} ");
        if (!match.Success)
        {
            return null;
        }

        var group = match.Groups[1].Value;
        return group.StartsWith('M')   // M pour « minus »
            ? -int.Parse(group[1..], CultureInfo.InvariantCulture)
            : int.Parse(group, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Réseau StatIC d'Infoclimat, via son API open data.
/// </summary>
/// <remarks>
/// La clé est délivrée gratuitement après création d'un compte, mais elle est
/// <b>liée à l'adresse IP appelante</b> et cesse de fonctionner quand l'IP change.
/// Les données sont sous Licence Ouverte ou Creative Commons selon la station :
/// citer « Infoclimat (StatIC) » lors de toute rediffusion.
/// </remarks>
public sealed class InfoclimatSource : IOutdoorSource
{
    private readonly TimeProvider _time;

    public InfoclimatSource(string station, string token, TimeProvider? time = null)
    {
        Station = station.Trim();
        Token = token.Trim();
        _time = time ?? TimeProvider.System;

        if (Station.Length == 0)
        {
            throw new OutdoorException(
                "le fournisseur infoclimat demande un identifiant de station "
                + "(TEMPI_OUTDOOR_STATION)");
        }

        if (Token.Length == 0)
        {
            throw new OutdoorException(
                "le fournisseur infoclimat demande une clé (TEMPI_OUTDOOR_TOKEN)");
        }
    }

    public string Station { get; }

    public string Token { get; }

    public string Name => "infoclimat";

    public string Slug => OutdoorPrimitives.Slug(Station);

    public string Describe() => $"Infoclimat StatIC {Station}";

    public string Url()
    {
        // L'API impose une plage explicite. Demander la veille et le jour même suffit
        // à englober la dernière observation, y compris juste après minuit UTC ou au
        // retour d'une coupure réseau.
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        return "https://www.infoclimat.fr/opendata/?"
            + OutdoorPrimitives.UrlEncode(
                ("method", "get"),
                ("format", "json"),
                ("stations[]", Station),
                ("start", today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("end", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("token", Token));
    }

    public Observation Parse(byte[] payload)
    {
        using var document = OutdoorPrimitives.LoadJson(payload);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new OutdoorException("réponse inattendue de l'API Infoclimat");
        }

        var hasHourly = root.TryGetProperty("hourly", out var hourly)
                        && hourly.ValueKind is JsonValueKind.Array or JsonValueKind.Object;

        var message = FirstString(root, "message", "err", "error");
        if (message is not null && !hasHourly)
        {
            throw new OutdoorException($"Infoclimat a refusé la requête : {message}");
        }

        var records = Records(hourly, hasHourly);
        if (records.Count == 0)
        {
            throw new OutdoorException($"aucune mesure pour la station {Station}");
        }

        // Comparaison lexicographique des horodatages, comme le max() Python sur
        // str(item["dh_utc"]) : le format ISO la rend équivalente à un tri temporel.
        var latest = records
            .OrderBy(r => r.TryGetProperty("dh_utc", out var d) ? d.ToString() : string.Empty,
                     StringComparer.Ordinal)
            .Last();

        var stamp = latest.TryGetProperty("dh_utc", out var raw) ? raw.ToString() : null;
        if (string.IsNullOrEmpty(stamp))
        {
            throw new OutdoorException("horodatage 'dh_utc' absent de la réponse Infoclimat");
        }

        latest.TryGetProperty("temperature", out var temperature);
        var celsius = OutdoorPrimitives.AsCelsius(temperature, "temperature");

        return new Observation(celsius, OutdoorPrimitives.IsoToEpoch(stamp), Station);
    }

    private static string? FirstString(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// Extrait la liste de relevés de la station demandée.
    /// </summary>
    /// <remarks>
    /// Les mesures sont regroupées par station sous <c>hourly</c>. On accepte que la
    /// clé diffère de l'identifiant demandé (casse, préfixe) tant qu'une seule station
    /// a été demandée — d'où la fusion de repli.
    /// </remarks>
    private List<JsonElement> Records(JsonElement hourly, bool hasHourly)
    {
        if (!hasHourly)
        {
            return [];
        }

        if (hourly.ValueKind == JsonValueKind.Array)
        {
            return Objects(hourly);
        }

        foreach (var key in new[] { Station, Station.ToUpperInvariant(), Station.ToLowerInvariant() })
        {
            if (hourly.TryGetProperty(key, out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                return Objects(entries);
            }
        }

        var merged = new List<JsonElement>();
        foreach (var property in hourly.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                merged.AddRange(Objects(property.Value));
            }
        }

        return merged;
    }

    private static List<JsonElement> Objects(JsonElement array) =>
        array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList();
}

/// <summary>
/// Grille Open-Meteo : sortie de modèle, pas une mesure.
/// </summary>
/// <remarks>
/// Aucune clé, aucune inscription, couverture mondiale — mais la valeur est
/// interpolée sur une maille de quelques kilomètres. Le libellé par défaut le
/// rappelle, pour éviter de la confondre avec un relevé de station.
/// </remarks>
public sealed class OpenMeteoSource : IOutdoorSource
{
    public OpenMeteoSource(double latitude, double longitude)
    {
        if (latitude is < -90.0 or > 90.0)
        {
            throw new OutdoorException($"latitude hors bornes : {PythonRepr.Number(latitude)}");
        }

        if (longitude is < -180.0 or > 180.0)
        {
            throw new OutdoorException($"longitude hors bornes : {PythonRepr.Number(longitude)}");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    public string Name => "open-meteo";

    public string Slug => OutdoorPrimitives.Slug(
        $"{Latitude.ToString("F4", CultureInfo.InvariantCulture)}_"
        + $"{Longitude.ToString("F4", CultureInfo.InvariantCulture)}");

    public string Describe() =>
        $"Open-Meteo {Latitude.ToString("F4", CultureInfo.InvariantCulture)},"
        + $"{Longitude.ToString("F4", CultureInfo.InvariantCulture)}";

    public string Url() =>
        "https://api.open-meteo.com/v1/forecast?"
        + OutdoorPrimitives.UrlEncode(
            ("latitude", Latitude.ToString("F4", CultureInfo.InvariantCulture)),
            ("longitude", Longitude.ToString("F4", CultureInfo.InvariantCulture)),
            ("current", "temperature_2m"));

    public Observation Parse(byte[] payload)
    {
        using var document = OutdoorPrimitives.LoadJson(payload);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new OutdoorException("réponse inattendue de l'API Open-Meteo");
        }

        if (root.TryGetProperty("error", out var error)
            && error.ValueKind is JsonValueKind.True
                or JsonValueKind.String or JsonValueKind.Number)
        {
            var reason = root.TryGetProperty("reason", out var r) ? r.ToString() : "?";
            throw new OutdoorException($"Open-Meteo a refusé la requête : {reason}");
        }

        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
        {
            throw new OutdoorException("bloc 'current' absent de la réponse Open-Meteo");
        }

        current.TryGetProperty("temperature_2m", out var temperature);
        var celsius = OutdoorPrimitives.AsCelsius(temperature, "temperature_2m");

        var stamp = current.TryGetProperty("time", out var time) ? time.ToString() : null;
        if (string.IsNullOrEmpty(stamp))
        {
            throw new OutdoorException("horodatage 'time' absent de la réponse Open-Meteo");
        }

        // Sans paramètre « timezone » la réponse est en UTC ; le décalage est retranché
        // au cas où une version future en ajouterait un.
        var offset = root.TryGetProperty("utc_offset_seconds", out var o)
                     && o.ValueKind == JsonValueKind.Number
            ? o.GetInt32()
            : 0;

        return new Observation(celsius, OutdoorPrimitives.IsoToEpoch(stamp, offset), Describe());
    }
}

/// <summary>Fabrique des sources à partir de la configuration.</summary>
public static class OutdoorSources
{
    /// <summary>Construit le fournisseur décrit par la configuration, ou <c>null</c>.</summary>
    public static IOutdoorSource? Create(TempiConfig config, TimeProvider? time = null)
    {
        var provider = (config.OutdoorProvider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider.Length == 0 || provider == "none")
        {
            return null;
        }

        switch (provider)
        {
            case "metar":
                return new MetarSource(config.OutdoorStation ?? string.Empty);

            case "infoclimat":
                return new InfoclimatSource(
                    config.OutdoorStation ?? string.Empty,
                    config.OutdoorToken ?? string.Empty,
                    time);

            case "open-meteo":
                if (config.OutdoorLatitude is null || config.OutdoorLongitude is null)
                {
                    throw new OutdoorException(
                        "le fournisseur open-meteo demande des coordonnées "
                        + "(TEMPI_OUTDOOR_LAT / TEMPI_OUTDOOR_LON)");
                }

                return new OpenMeteoSource(config.OutdoorLatitude.Value, config.OutdoorLongitude.Value);

            default:
                throw new OutdoorException(
                    $"fournisseur inconnu : {PythonRepr.Quote(provider)} "
                    + $"(attendu : {string.Join(", ", TempiConfig.OutdoorProviders)})");
        }
    }

    /// <summary>Adresse du pseudo-capteur correspondant à un fournisseur.</summary>
    /// <remarks>
    /// La forme <c>outdoor-&lt;fournisseur&gt;-&lt;station&gt;</c> ne peut jamais entrer
    /// en collision avec une adresse 1-Wire, qui commence toujours par un code de
    /// famille hexadécimal.
    /// </remarks>
    public static string AddressFor(IOutdoorSource source) => $"outdoor-{source.Name}-{source.Slug}";
}
