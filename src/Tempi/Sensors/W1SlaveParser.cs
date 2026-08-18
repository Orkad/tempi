using System.Globalization;
using Tempi.Configuration;

namespace Tempi.Sensors;

/// <summary>
/// Analyse du fichier <c>w1_slave</c> exposé par le noyau.
/// </summary>
/// <remarks>
/// Format historique, sur deux lignes. La première se termine par <c>YES</c> ou
/// <c>NO</c> selon que le CRC de la trame est valide ; la seconde contient
/// <c>t=&lt;millidegrés&gt;</c>. On lui préfère le fichier <c>temperature</c> des
/// noyaux récents parce qu'il est disponible partout et permet de distinguer une
/// erreur de CRC d'une erreur d'entrée/sortie.
/// </remarks>
public static class W1SlaveParser
{
    public static double Parse(string payload)
    {
        var lines = payload
            .Split('\n')
            .Where(line => line.Trim().Length > 0)
            .ToArray();

        if (lines.Length < 2)
        {
            throw new SensorException($"trame incomplète : {PythonRepr.Quote(payload)}");
        }

        if (!lines[0].TrimEnd().EndsWith("YES", StringComparison.Ordinal))
        {
            throw new CrcException("CRC invalide");
        }

        var marker = lines[1].IndexOf("t=", StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new SensorException($"champ 't=' absent : {PythonRepr.Quote(lines[1])}");
        }

        var raw = lines[1][(marker + 2)..].Trim();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millidegrees))
        {
            throw new SensorException($"valeur de température illisible : {PythonRepr.Quote(raw)}");
        }

        return millidegrees / 1000.0;
    }
}
