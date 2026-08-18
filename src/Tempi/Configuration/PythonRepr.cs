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
