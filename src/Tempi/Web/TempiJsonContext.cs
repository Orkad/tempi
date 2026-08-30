using System.Text.Json.Serialization;

namespace Tempi.Web;

/// <summary>
/// Sérialisation JSON générée à la compilation.
/// </summary>
/// <remarks>
/// Obligatoire sous trimming : la sérialisation par réflexion serait élaguée et ne
/// se manifesterait qu'à l'exécution du binaire publié.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(SensorsDto))]
[JsonSerializable(typeof(LatestDto))]
[JsonSerializable(typeof(SeriesResponseDto))]
[JsonSerializable(typeof(SummaryResponseDto))]
[JsonSerializable(typeof(LabelRequestDto))]
[JsonSerializable(typeof(LabelResponseDto))]
[JsonSerializable(typeof(DoctorReportDto))]
public sealed partial class TempiJsonContext : JsonSerializerContext;
