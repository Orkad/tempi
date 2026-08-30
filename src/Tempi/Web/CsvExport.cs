using System.Globalization;
using System.Text;
using Tempi.Configuration;
using Tempi.Storage;

namespace Tempi.Web;

/// <summary>Export CSV des mesures brutes.</summary>
/// <remarks>
/// Écrit à la main plutôt qu'avec une bibliothèque : le format à reproduire est celui
/// du dialecte <c>excel</c> de Python, dont deux traits ne sont pas les valeurs par
/// défaut ailleurs — la terminaison de ligne est <c>CRLF</c>, et les flottants sont
/// rendus par <c>str()</c>, qui écrit « 20.0 » là où C# écrirait « 20 ».
/// </remarks>
public static class CsvExport
{
    public const string Header = "timestamp_utc,epoch,address,label,celsius";

    public static int Write(
        TempiStorage storage,
        long start,
        long end,
        IReadOnlyList<string>? addresses,
        TextWriter output)
    {
        output.Write(Header);
        output.Write("\r\n");

        return storage.ForEachRow(start, end, addresses, row =>
        {
            var iso = DateTimeOffset.FromUnixTimeSeconds(row.Ts)
                .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

            output.Write(Quote(iso));
            output.Write(',');
            output.Write(row.Ts.ToString(CultureInfo.InvariantCulture));
            output.Write(',');
            output.Write(Quote(row.Address));
            output.Write(',');
            output.Write(Quote(row.Label ?? string.Empty));
            output.Write(',');
            output.Write(PythonRepr.Number(row.Celsius));
            output.Write("\r\n");
        });
    }

    /// <summary>Guillemets minimaux, comme <c>QUOTE_MINIMAL</c>.</summary>
    private static string Quote(string field)
    {
        if (field.AsSpan().IndexOfAny(',', '"', '\r') < 0 && !field.Contains('\n'))
        {
            return field;
        }

        return new StringBuilder(field.Length + 2)
            .Append('"')
            .Append(field.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Append('"')
            .ToString();
    }
}
