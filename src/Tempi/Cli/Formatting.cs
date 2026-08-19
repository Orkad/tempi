using System.Globalization;

namespace Tempi.Cli;

/// <summary>Mises en forme de la sortie console, reprises de <c>cli.py</c>.</summary>
internal static class Formatting
{
    /// <summary>Horodatage epoch UTC rendu en heure locale, ou tiret cadratin s'il est absent.</summary>
    public static string Timestamp(long? ts) =>
        ts is null
            ? "—"
            : DateTimeOffset.FromUnixTimeSeconds(ts.Value).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Taille en unités binaires françaises.
    /// </summary>
    /// <remarks>
    /// En dessous du kibioctet la valeur est entière — « 512 o » et non « 512.0 o » —
    /// et le point décimal est celui de la culture invariante, comme les f-strings
    /// de Python.
    /// </remarks>
    public static string Size(long bytes)
    {
        string[] units = ["o", "Kio", "Mio", "Gio"];
        var value = (double)bytes;

        foreach (var unit in units)
        {
            if (value < 1024 || unit == "Gio")
            {
                return unit == "o"
                    ? $"{(long)value} {unit}"
                    : $"{value.ToString("F1", CultureInfo.InvariantCulture)} {unit}";
            }

            value /= 1024;
        }

        return $"{value.ToString("F1", CultureInfo.InvariantCulture)} Gio";
    }

    /// <summary>Nom d'un capteur suivi de son libellé entre guillemets français, s'il en a un.</summary>
    public static string WithLabel(string address, string? label) =>
        string.IsNullOrEmpty(label) ? address : $"{address} « {label} »";

    /// <summary>Température alignée sur six colonnes, au dixième de degré.</summary>
    public static string Celsius(double value) =>
        value.ToString("F1", CultureInfo.InvariantCulture).PadLeft(6) + " °C";
}
