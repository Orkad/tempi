using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tempi.Configuration;

namespace Tempi.Outdoor;

/// <summary>La température extérieure n'a pas pu être obtenue.</summary>
public sealed class OutdoorException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Une température extérieure, telle que publiée par la station.</summary>
/// <param name="Ts">Instant de la mesure (epoch UTC), et non celui de la requête.</param>
/// <param name="Station">Station ou point de grille d'origine, pour les journaux.</param>
public readonly record struct Observation(double Celsius, long Ts, string Station);

/// <summary>Une source de température extérieure.</summary>
public interface IOutdoorSource
{
    /// <summary>Identifiant du fournisseur, tel qu'il apparaît dans l'adresse du capteur.</summary>
    string Name { get; }

    /// <summary>Partie variable de l'adresse : station ou coordonnées.</summary>
    string Slug { get; }

    /// <summary>Description lisible, pour les journaux et <c>/api/health</c>.</summary>
    string Describe();

    /// <summary>URL à interroger.</summary>
    string Url();

    /// <summary>Extrait la dernière observation de la réponse.</summary>
    Observation Parse(byte[] payload);
}

/// <summary>Outils partagés par les trois fournisseurs.</summary>
internal static partial class OutdoorPrimitives
{
    /// <summary>Intervalle minimal entre deux interrogations.</summary>
    /// <remarks>
    /// Les stations ne publient pas plus vite, et marteler une API publique gratuite
    /// est le meilleur moyen de se faire bloquer.
    /// </remarks>
    public const double MinInterval = 60.0;

    /// <summary>Plage de validité d'une température extérieure.</summary>
    /// <remarks>
    /// Un relevé hors de ces bornes traduit une erreur de format (millidegrés, degrés
    /// Fahrenheit) plutôt qu'une canicule.
    /// </remarks>
    public const double MinCelsius = -90.0;

    /// <inheritdoc cref="MinCelsius"/>
    public const double MaxCelsius = 60.0;

    public static readonly string UserAgent =
        $"tempi/{TempiVersion.Value} (+https://github.com/Orkad/tempi)";

    /// <summary>
    /// Caractères autorisés dans une adresse de capteur par la route de renommage.
    /// </summary>
    /// <remarks>
    /// Le jeu correspond exactement à celui qu'accepte <c>/api/sensors/&lt;adresse&gt;/label</c> :
    /// le pseudo-capteur extérieur doit rester renommable depuis l'interface.
    /// </remarks>
    [GeneratedRegex("[^0-9A-Za-z._-]+")]
    private static partial Regex UnsafeInAddress();

    /// <summary>Rend une chaîne utilisable dans une adresse de pseudo-capteur.</summary>
    public static string Slug(string value)
    {
        var cleaned = UnsafeInAddress().Replace(value.Trim(), "-").Trim('-');
        return cleaned.Length == 0 ? "inconnu" : cleaned;
    }

    /// <summary>Convertit une date ISO 8601 (ou <c>YYYY-MM-DD HH:MM:SS</c>) en epoch UTC.</summary>
    /// <remarks>
    /// Une date sans fuseau est supposée UTC, puis le décalage est retranché ; une date
    /// qui porte déjà son fuseau ignore le décalage. C'est la sémantique de
    /// <c>_iso_to_epoch</c>, et les deux branches sont distinctes à dessein.
    /// </remarks>
    public static long IsoToEpoch(string value, int offsetSeconds = 0)
    {
        var text = value.Trim().Replace("Z", "+00:00", StringComparison.Ordinal);

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var aware)
            && HasExplicitOffset(text))
        {
            return aware.ToUnixTimeSeconds();
        }

        if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var naive))
        {
            return new DateTimeOffset(naive, TimeSpan.Zero).ToUnixTimeSeconds() - offsetSeconds;
        }

        throw new OutdoorException($"date illisible : {PythonRepr.Quote(value)}");
    }

    private static bool HasExplicitOffset(string text)
    {
        // « 2024-01-14T12:00:00+01:00 » porte son fuseau ; « 2024-01-14 12:00:00 » non.
        // On cherche le signe après la partie heure, pas celui d'une année négative.
        var timePart = text.IndexOf('T') >= 0 ? text[(text.IndexOf('T') + 1)..] : text;
        var space = timePart.LastIndexOf(' ');
        if (space >= 0)
        {
            timePart = timePart[(space + 1)..];
        }

        return timePart.Contains('+') || timePart.LastIndexOf('-') > 0;
    }

    /// <summary>Valide une température brute issue d'une réponse JSON.</summary>
    public static double AsCelsius(JsonElement value, string field)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0))
        {
            throw new OutdoorException($"température absente de la réponse ({field})");
        }

        double celsius;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                celsius = value.GetDouble();
                break;
            case JsonValueKind.String:
                if (!double.TryParse(
                        value.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out celsius))
                {
                    throw new OutdoorException(
                        $"température illisible ({field}) : {PythonRepr.Quote(value.GetString())}");
                }

                break;
            default:
                throw new OutdoorException($"température illisible ({field}) : {value.GetRawText()}");
        }

        if (celsius < MinCelsius || celsius > MaxCelsius)
        {
            throw new OutdoorException(
                $"{PythonRepr.Number(celsius)} °C hors de la plage plausible ({field})");
        }

        return celsius;
    }

    /// <summary>Valide une température déjà extraite d'un texte (repli METAR brut).</summary>
    public static double AsCelsius(double value, string field)
    {
        if (value < MinCelsius || value > MaxCelsius)
        {
            throw new OutdoorException(
                $"{PythonRepr.Number(value)} °C hors de la plage plausible ({field})");
        }

        return value;
    }

    /// <summary>
    /// Analyse une réponse JSON.
    /// </summary>
    /// <remarks>
    /// <c>JsonDocument</c> plutôt que des types source-générés : les trois API ont des
    /// formes variables — une liste ou un objet selon le cas, un dictionnaire indexé
    /// par station — que des DTO figés ne décriraient qu'au prix de contorsions.
    /// L'analyse en document reste sans réflexion, donc compatible trimming.
    /// </remarks>
    public static JsonDocument LoadJson(byte[] payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException exc)
        {
            throw new OutdoorException($"réponse JSON invalide : {exc.Message}", exc);
        }
    }

    /// <summary>Compose une chaîne de requête à la manière d'<c>urllib.parse.urlencode</c>.</summary>
    public static string UrlEncode(params (string Key, string Value)[] parameters)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(QuotePlus(key)).Append('=').Append(QuotePlus(value));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Encodage de <c>quote_plus</c> : l'espace devient <c>+</c>, le reste est
    /// pourcent-encodé. C'est ce qui produit <c>stations%5B%5D</c>, qu'un test verrouille.
    /// </summary>
    private static string QuotePlus(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
}
