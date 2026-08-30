namespace Tempi.Diagnostics;

/// <summary>Contenu de <c>/sys/bus/w1/devices</c>, trié par nature.</summary>
public sealed class BusInventory
{
    public List<string> Sensors { get; } = [];
    public List<string> Phantoms { get; } = [];
    public List<string> Masters { get; } = [];
}
