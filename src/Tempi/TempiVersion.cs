namespace Tempi;

/// <summary>Version de l'application, exposée par <c>--version</c> et <c>/api/health</c>.</summary>
/// <remarks>
/// Constante, et non lue depuis <c>AssemblyInformationalVersionAttribute</c> : la CI y
/// ajoute un suffixe <c>+&lt;sha&gt;</c> qui se retrouverait dans les réponses de l'API.
/// </remarks>
internal static class TempiVersion
{
    public const string Value = "2.0.0";
}
