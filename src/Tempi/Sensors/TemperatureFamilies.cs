namespace Tempi.Sensors;

/// <summary>Constantes du DS18B20 et de ses compatibles.</summary>
public static class TemperatureFamilies
{
    /// <summary>Codes de famille 1-Wire correspondant à des capteurs de température.</summary>
    public static readonly string[] All = ["28", "10", "22", "3b", "42"];

    /// <summary>Plage de mesure du DS18B20, d'après la fiche technique.</summary>
    public const double MinCelsius = -55.0;

    /// <inheritdoc cref="MinCelsius"/>
    public const double MaxCelsius = 125.0;

    /// <summary>
    /// Valeur du registre de température après une mise sous tension.
    /// </summary>
    /// <remarks>
    /// Une lecture à exactement 85 °C traduit presque toujours une conversion
    /// interrompue.
    /// </remarks>
    public const int ResetValueMillidegrees = 85000;

    public static bool IsTemperature(string family) => Array.IndexOf(All, family) >= 0;
}
