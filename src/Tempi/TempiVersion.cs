namespace Tempi;

/// <summary>Version de l'application, exposée par <c>--version</c> et <c>/api/health</c>.</summary>
/// <remarks>
/// Constante, et non lue depuis <c>AssemblyInformationalVersionAttribute</c> : la CI y
/// ajoute un suffixe <c>+&lt;sha&gt;</c> qui se retrouverait dans les réponses de l'API.
///
/// Numérotée selon SemVer (https://semver.org). Chaque push sur main publie une
/// release automatiquement (continuous-release.yml) :
/// <list type="bullet">
/// <item>Si cette constante n'a pas changé depuis la dernière release, le PATCH
/// est incrémenté tout seul — rien à faire.</item>
/// <item>Pour un bump MINOR ou MAJOR (nouvelle fonctionnalité visible, changement
/// cassant), montez cette constante à la main dans la PR, et reportez la même
/// valeur dans la propriété <c>Version</c> de <c>Directory.Build.props</c> —
/// <c>VersionTests.cs</c> échoue sinon, dès la CI normale. continuous-release.yml
/// respecte alors cette valeur au lieu d'incrémenter.</item>
/// </list>
/// Dans les deux cas, le commit poussé sur main est committé, tagué et publié tel
/// quel — publish-release.yml refuse de publier si le tag ne correspond pas à
/// cette constante.
/// </remarks>
internal static class TempiVersion
{
    public const string Value = "2.1.2";
}
