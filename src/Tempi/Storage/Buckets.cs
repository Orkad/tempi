namespace Tempi.Storage;

/// <summary>Choix du palier de regroupement pour le sous-échantillonnage.</summary>
public static class Buckets
{
    /// <summary>
    /// Paliers de regroupement, en secondes.
    /// </summary>
    /// <remarks>Chaque palier correspond à une durée « ronde » lisible sur un axe.</remarks>
    public static readonly int[] Steps =
    [
        1, 5, 10, 15, 30,
        60, 120, 300, 600, 900, 1800,
        3600, 7200, 10800, 21600, 43200,
        86400, 172800, 604800,
    ];

    /// <summary>
    /// Choisit un intervalle de regroupement pour tenir dans <paramref name="targetPoints"/>.
    /// </summary>
    /// <remarks>
    /// Sans cela, afficher un mois de mesures prises chaque minute reviendrait à
    /// transférer plus de 40 000 points au navigateur pour un graphique large de
    /// quelques centaines de pixels.
    /// </remarks>
    public static int Choose(double spanSeconds, int targetPoints = 800)
    {
        if (spanSeconds <= 0 || targetPoints <= 0)
        {
            return 0;
        }

        var ideal = spanSeconds / targetPoints;
        foreach (var step in Steps)
        {
            if (step >= ideal)
            {
                return step;
            }
        }

        return Steps[^1];
    }
}
