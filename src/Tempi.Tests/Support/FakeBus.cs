using Tempi.Sensors;

namespace Tempi.Tests.Support;

/// <summary>
/// Bus scriptable : chaque appel consomme le lot suivant.
/// </summary>
/// <remarks>
/// Le double Python n'implémentait que <c>discover</c> et <c>read_all</c>, le typage
/// canard suffisant. Ici l'interface doit être satisfaite en entier, mais
/// <see cref="Read"/> n'est jamais appelé : le collecteur passe par
/// <see cref="ReadAll"/>.
/// </remarks>
internal class FakeBus(IEnumerable<BusScan> batches) : ITemperatureBus
{
    private readonly Queue<BusScan> _batches = new(batches);

    public int Calls { get; protected set; }

    public bool Available => true;

    public IReadOnlyList<string> Discover() => ["28-aaaa"];

    public double Read(string address) => throw new NotSupportedException();

    public virtual BusScan ReadAll(IReadOnlyList<string>? addresses = null)
    {
        Calls++;
        return _batches.Count == 0 ? new BusScan([], []) : _batches.Dequeue();
    }

    /// <summary>Raccourci : un lot d'une seule mesure réussie.</summary>
    public static BusScan One(string address, double celsius, long ts) =>
        new([new Reading(address, celsius, ts)], []);
}
