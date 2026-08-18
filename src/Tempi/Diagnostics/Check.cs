namespace Tempi.Diagnostics;

/// <summary>
/// Résultat d'une vérification.
/// </summary>
/// <param name="Ok">
/// Trois états, et non deux : <c>null</c> signifie « vérification non menée »
/// (outil absent, droits insuffisants). Un indéterminé n'est pas un échec.
/// </param>
public sealed record Check(
    string Name,
    bool? Ok,
    string Detail,
    string Remedy = "",
    bool Critical = false)
{
    public string Symbol => Ok switch
    {
        true => "✓",
        false => "✗",
        null => "?",
    };

    /// <summary>Remède normalisé pour la sérialisation : la chaîne vide devient <c>null</c>.</summary>
    public string? RemedyOrNull => string.IsNullOrEmpty(Remedy) ? null : Remedy;
}
