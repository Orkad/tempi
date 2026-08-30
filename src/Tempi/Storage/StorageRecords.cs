namespace Tempi.Storage;

/// <summary>Un capteur connu de la base, avec son nombre de mesures.</summary>
public sealed record SensorRow(long Id, string Address, string? Label, long FirstSeen, long? LastSeen, long Count);

/// <summary>Dernière mesure connue d'un capteur. <c>Ts</c> et <c>Celsius</c> sont nuls s'il n'en a aucune.</summary>
public sealed record LatestRow(string Address, string? Label, long? Ts, double? Celsius);

/// <summary>Un point de série, agrégé ou brut.</summary>
public sealed record SeriesPoint(long Ts, double Celsius, double Min, double Max, int Samples);

/// <summary>Statistiques d'un capteur sur une plage.</summary>
public sealed record SummaryStats(double Min, double Max, double Avg, int Samples);

/// <summary>État de la base.</summary>
public sealed record StorageStats(
    string DbPath,
    long DbBytes,
    int Sensors,
    long Readings,
    long? FirstTs,
    long? LastTs);

/// <summary>Une ligne brute, pour l'export.</summary>
public readonly record struct ExportRow(long Ts, string Address, string? Label, double Celsius);
