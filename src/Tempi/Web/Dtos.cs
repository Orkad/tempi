using System.Text.Json.Serialization;

namespace Tempi.Web;

// Les noms JSON sont posés explicitement plutôt que déduits d'une convention : le
// contrat devient greppable, et renommer une propriété C# ne peut plus casser
// silencieusement l'interface web, qui est le seul consommateur.

public sealed record ErrorDto(
    [property: JsonPropertyName("error")] string Error);

public sealed record StorageStatsDto(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("db_bytes")] long DbBytes,
    [property: JsonPropertyName("sensors")] int Sensors,
    [property: JsonPropertyName("readings")] long Readings,
    [property: JsonPropertyName("first_ts")] long? FirstTs,
    [property: JsonPropertyName("last_ts")] long? LastTs);

public sealed record CollectorStatsDto(
    [property: JsonPropertyName("cycles")] int Cycles,
    [property: JsonPropertyName("stored")] int Stored,
    [property: JsonPropertyName("errors")] int Errors,
    [property: JsonPropertyName("last_cycle_ts")] long? LastCycleTs,
    [property: JsonPropertyName("interval")] double Interval);

public sealed record OutdoorStatsDto(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("polls")] int Polls,
    [property: JsonPropertyName("stored")] int Stored,
    [property: JsonPropertyName("errors")] int Errors,
    [property: JsonPropertyName("last_ok_ts")] long? LastOkTs,
    [property: JsonPropertyName("last_error")] string? LastError,
    [property: JsonPropertyName("interval")] double Interval);

public sealed record HealthDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("now")] long Now,
    [property: JsonPropertyName("storage")] StorageStatsDto Storage,
    [property: JsonPropertyName("collector")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CollectorStatsDto? Collector = null,
    [property: JsonPropertyName("outdoor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OutdoorStatsDto? Outdoor = null);

public sealed record SensorDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("first_seen")] long FirstSeen,
    [property: JsonPropertyName("last_seen")] long? LastSeen,
    [property: JsonPropertyName("count")] long Count);

public sealed record SensorsDto(
    [property: JsonPropertyName("sensors")] IReadOnlyList<SensorDto> Sensors);

public sealed record LatestSensorDto(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("ts")] long? Ts,
    [property: JsonPropertyName("celsius")] double? Celsius);

public sealed record LatestDto(
    [property: JsonPropertyName("now")] long Now,
    [property: JsonPropertyName("sensors")] IReadOnlyList<LatestSensorDto> Sensors);

public sealed record PointDto(
    [property: JsonPropertyName("ts")] long Ts,
    [property: JsonPropertyName("celsius")] double Celsius,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("samples")] int Samples);

public sealed record SeriesDto(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("points")] IReadOnlyList<PointDto> Points);

public sealed record SeriesResponseDto(
    // « from » est un mot-clé C# : la propriété s'appelle From et porte son nom JSON.
    [property: JsonPropertyName("from")] long From,
    [property: JsonPropertyName("to")] long To,
    [property: JsonPropertyName("bucket")] int Bucket,
    [property: JsonPropertyName("series")] IReadOnlyList<SeriesDto> Series);

public sealed record SummaryEntryDto(
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("avg")] double Avg,
    [property: JsonPropertyName("samples")] int Samples);

public sealed record SummaryResponseDto(
    [property: JsonPropertyName("from")] long From,
    [property: JsonPropertyName("to")] long To,
    // Objet indexé par adresse, et non tableau : l'interface fait Object.entries().
    [property: JsonPropertyName("summary")] IReadOnlyDictionary<string, SummaryEntryDto> Summary);

public sealed record LabelRequestDto(
    [property: JsonPropertyName("label")] string? Label);

public sealed record LabelResponseDto(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("label")] string? Label);

public sealed record DoctorCheckDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ok")] bool? Ok,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("remedy")] string? Remedy,
    [property: JsonPropertyName("critical")] bool Critical);

public sealed record DoctorReportDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("checks")] IReadOnlyList<DoctorCheckDto> Checks);
