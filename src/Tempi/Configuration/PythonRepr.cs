using System.Globalization;
using System.Text;

namespace Tempi.Configuration;

/// <summary>
/// Reproduit <c>repr()</c> de Python pour une chaîne.
/// </summary>
/// <remarks>
/// Les messages d'erreur de <c>config.py</c> interpolent la valeur fautive avec
/// <c>{raw!r}</c>, ce qui l'entoure d'apostrophes. Ces messages remontent jusqu'à
/// l'utilisateur et sont comparés par le golden master : les reproduire suppose
/// d'imiter la règle de Python, qui bascule sur des guillemets doubles lorsque la
/// chaîne contient une apostrophe mais pas de guillemet.
/// </remarks>
internal static class PythonRepr
{
    /// <summary>
    /// Reproduit <c>str()</c> de Python pour un flottant.
    /// </summary>
    /// <remarks>
    /// Depuis Python 3.1, <c>str(float)</c> vaut <c>repr(float)</c> : la plus courte
    /// représentation qui relit à l'identique, mais toujours avec un point décimal.
    /// <c>str(200.0)</c> donne « 200.0 » là où <c>(200.0).ToString()</c> donne
    /// « 200 ». La différence est visible dans le message « … °C hors de la plage du
    /// capteur ».
    /// </remarks>
    public static string Number(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'E', 'e') >= 0 ? text : text + ".0";
    }

    public static string Quote(string? value)
    {
        if (value is null)
        {
            return "None";
        }

        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var builder = new StringBuilder(value.Length + 2).Append(quote);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c == quote)
                    {
                        builder.Append('\\');
                    }

                    builder.Append(c);
                    break;
            }
        }

        return builder.Append(quote).ToString();
    }
}
