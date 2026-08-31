namespace Tempi;

/// <summary>Version de l'application, exposée par <c>--version</c> et <c>/api/health</c>.</summary>
/// <remarks>
/// Constante, et non lue depuis <c>AssemblyInformationalVersionAttribute</c> : la CI y
/// ajoute un suffixe <c>+&lt;sha&gt;</c> qui se retrouverait dans les réponses de l'API.
///
/// Numérotée selon SemVer (https://semver.org). C'est ici, et nulle part ailleurs,
/// que l'on change de version pour préparer une release :
/// 1. Monter cette constante à la version visée.
/// 2. Reporter la même valeur dans la propriété <c>Version</c> de
///    <c>Directory.Build.props</c> — <c>VersionTests.cs</c> échoue sinon, dès la CI
///    normale, avant même de tagger.
/// 3. Committer, puis poser un tag <c>vX.Y.Z</c> identique et le pousser :
///    release.yml refuse de publier si le tag ne correspond pas à cette constante.
/// </remarks>
internal static class TempiVersion
{
    public const string Value = "2.0.0";
}
