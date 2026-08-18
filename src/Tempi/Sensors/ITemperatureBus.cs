namespace Tempi.Sensors;

/// <summary>Accès aux capteurs de température, réels ou simulés.</summary>
/// <remarks>
/// L'interface reste volontairement étroite : côté Python le typage canard suffisait,
/// et les doubles de test n'implémentaient que ce dont ils avaient besoin.
/// </remarks>
public interface ITemperatureBus
{
    /// <summary>Indique si le bus est monté sur ce système.</summary>
    bool Available { get; }

    /// <summary>Adresses des capteurs de température détectés, triées.</summary>
    IReadOnlyList<string> Discover();

    /// <summary>Lit un capteur, en réessayant sur erreur transitoire.</summary>
    double Read(string address);

    /// <summary>Lit plusieurs capteurs et sépare les succès des échecs.</summary>
    BusScan ReadAll(IReadOnlyList<string>? addresses = null);
}
