namespace Tempi.Sensors;

/// <summary>
/// Bus factice : permet de développer et de tester sans Raspberry Pi.
/// </summary>
/// <remarks>
/// Génère une température qui suit un cycle journalier auquel s'ajoute un bruit de
/// mesure, ce qui donne des courbes réalistes dans l'interface web. Les valeurs ne
/// reproduisent pas celles du bus simulé Python : celui-ci s'appuie sur le Mersenne
/// Twister de <c>random.Random</c>, qu'aucune autre plateforme n'imite. Ce n'est pas
/// un contrat — le test Python ne vérifiait que la plausibilité de la valeur — et les
/// tests de référence s'appuient sur une base figée plutôt que sur ce générateur.
/// </remarks>
public sealed class SimulatedBus : ITemperatureBus
{
    private static readonly string[] DefaultAddresses = ["28-000005e2fdc3", "28-000005e30a1b"];

    private readonly string[] _addresses;
    private readonly Random _random = new(1234);
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    public SimulatedBus(IReadOnlyList<string>? addresses = null, TimeProvider? time = null)
    {
        _addresses = (addresses ?? DefaultAddresses).ToArray();
        _time = time ?? TimeProvider.System;
    }

    public bool Available => true;

    public IReadOnlyList<string> Discover() => _addresses;

    public double Read(string address)
    {
        var offset = Array.IndexOf(_addresses, address);
        if (offset < 0)
        {
            throw new SensorException($"capteur simulé {address} inconnu");
        }

        var secondsOfDay = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0 % 86400;
        var daily = 6.0 * Math.Sin(2 * Math.PI * (secondsOfDay - (6 * 3600)) / 86400);

        double noise;
        // Random n'est pas sûr en concurrence, et rien ne garantit qu'un seul thread
        // appellera ce bus : le serveur web et le collecteur cohabitent.
        lock (_gate)
        {
            noise = (_random.NextDouble() * 0.3) - 0.15;
        }

        return Math.Round(19.0 + (offset * 1.5) + daily + noise, 3);
    }

    public BusScan ReadAll(IReadOnlyList<string>? addresses = null)
        => BusScanner.Run(this, addresses ?? Discover(), _time);
}
