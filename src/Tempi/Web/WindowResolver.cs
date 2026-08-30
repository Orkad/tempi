using System.Globalization;
using System.Text.RegularExpressions;
using Tempi.Configuration;
using Tempi.Storage;

namespace Tempi.Web;

/// <summary>Paramètre de requête invalide.</summary>
public sealed class BadRequestException(string message) : Exception(message);

/// <summary>Analyse des durées, dates et fenêtres temporelles.</summary>
internal static partial class WindowResolver
{
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([smhdw])$", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    private static readonly Dictionary<char, long> Units = new()
    {
        ['s'] = 1,
        ['m'] = 60,
        ['h'] = 3600,
        ['d'] = 86400,
        ['w'] = 604800,
    };

    /// <summary>Convertit <c>90m</c>, <c>24h</c>, <c>7d</c>… en secondes.</summary>
    public static long ParseDuration(string value)
    {
        var match = Duration().Match(value.Trim());
        if (!match.Success)
        {
            throw new BadRequestException(
                $"durée invalide : {PythonRepr.Quote(value)} (exemples : 30m, 24h, 7d)");
        }

        var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = char.ToLowerInvariant(match.Groups[2].Value[0]);
        return (long)(amount * Units[unit]);
    }

    /// <summary>Accepte un epoch en secondes ou une date ISO 8601.</summary>
    public static long ParseTimestamp(string value)
    {
        var text = value.Trim();

        // int(float(value)) tronque vers zéro : « 1.9 » vaut 1.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch))
        {
            return (long)epoch;
        }

        var iso = text.Replace("Z", "+00:00", StringComparison.Ordinal);
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aware)
            && iso.Contains('+', StringComparison.Ordinal))
        {
            return aware.ToUnixTimeSeconds();
        }

        if (DateTime.TryParse(
                iso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var naive))
        {
            return new DateTimeOffset(naive, TimeSpan.Zero).ToUnixTimeSeconds();
        }

        throw new BadRequestException($"date invalide : {PythonRepr.Quote(value)}");
    }

    /// <summary>
    /// Détermine la fenêtre temporelle demandée.
    /// </summary>
    /// <remarks>
    /// Priorité : <c>from</c>/<c>to</c> explicites, puis <c>range</c> relatif à
    /// maintenant, et par défaut les 24 dernières heures.
    /// </remarks>
    public static (long Start, long End) Resolve(QueryBag query, TempiStorage storage, TimeProvider time)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();

        if (query.First("range") == "all")
        {
            var (first, last) = storage.TimeRange();
            return first is null ? (now - 86400, now) : (first.Value, last ?? now);
        }

        var end = query.First("to") is { } to ? ParseTimestamp(to) : now;

        long start;
        if (query.First("from") is { } from)
        {
            start = ParseTimestamp(from);
        }
        else if (query.First("range") is { } range)
        {
            start = end - ParseDuration(range);
        }
        else
        {
            start = end - 86400;
        }

        if (start > end)
        {
            throw new BadRequestException("'from' est postérieur à 'to'");
        }

        return (start, end);
    }
}
