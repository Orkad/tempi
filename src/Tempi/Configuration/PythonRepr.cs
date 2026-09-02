using System.Globalization;
using System.Text;

namespace Tempi.Configuration;

/// <summary>
/// Reproduit <c>repr()</c> de Python pour une chaîne.
/// </summary>
/// <remarks>
/// Les messages d'erreur de <c>config.py</c> interpolent la valeur fautive avec
/// <c>{raw!r}</c>, ce qui l'entoure d'apostrophes. Ces messages remontent jusqu'à
/// l'utilisateur : les reproduire suppose d'imiter la règle de Python, qui bascule
/// sur des guillemets doubles lorsque la chaîne contient une apostrophe mais pas de
/// guillemet.
/// </remarks>
internal static class PythonRepr
{
    /// <summary>
    /// Reproduit <c>round()</c> de Python.
    /// </summary>
    /// <remarks>
    /// <c>Math.Round(double, int)</c> ne convient pas : il met la valeur à l'échelle
    /// avant d'arrondir, ce qui lui fait prendre pour un milieu exact une valeur qui
    /// n'en est pas un. La moyenne 16,993749999999998 est arrondie à 16,9938 par
    /// <c>Math.Round</c> alors que Python donne 16,9937 — et Python a raison, la valeur
    /// est sous le milieu. Le formatage « F » de .NET arrondit correctement la valeur
    /// binaire réelle depuis .NET Core 3.0 : formater puis relire reproduit donc la
    /// sémantique de Python. Vérifié sur les cas limites, dont 0,12345 où
    /// <c>Math.Round</c> se trompe aussi.
    /// </remarks>
    public static double Round(double value, int digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }

        return double.Parse(
            value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }

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
