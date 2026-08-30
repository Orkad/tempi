using System.Globalization;

namespace Tempi.Configuration;

/// <summary>
/// Lecture des variables d'environnement, avec la sémantique de <c>config.py</c>.
/// </summary>
/// <remarks>
/// Deux règles à respecter scrupuleusement : une variable définie à la chaîne vide
/// est traitée comme absente, et les nombres sont analysés en culture invariante.
/// Sans cette seconde règle, un Raspberry Pi en <c>fr_FR.UTF-8</c> rejetterait
/// <c>TEMPI_MIN_DELTA=0.5</c>.
/// </remarks>
internal static class EnvReader
{
    private static string? Raw(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static string? String(string name, string? fallback) => Raw(name) ?? fallback;

    public static double Double(string name, double fallback)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return fallback;
        }

        return ParseDouble(name, raw);
    }

    public static double? OptionalDouble(string name)
    {
        var raw = Raw(name);
        return raw is null ? null : ParseDouble(name, raw);
    }

    public static int Int(string name, int fallback)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ConfigException($"{name} doit être un entier, reçu {PythonRepr.Quote(raw)}");
        }

        return value;
    }

    public static bool Bool(string name, bool fallback)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on" or "oui";
    }

    private static double ParseDouble(string name, string raw)
    {
        // float() de Python accepte « inf » et « nan » ; NumberStyles.Float ne les
        // prend pas, mais aucune des variables concernées n'a de sens avec ces
        // valeurs et validate() les rejetterait de toute façon.
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ConfigException($"{name} doit être un nombre, reçu {PythonRepr.Quote(raw)}");
        }

        return value;
    }
}
