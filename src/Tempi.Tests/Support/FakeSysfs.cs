namespace Tempi.Tests.Support;

/// <summary>
/// Fausse arborescence <c>/sys/bus/w1/devices</c> sur disque.
/// </summary>
/// <remarks>
/// Les tests Python écrivent de vrais fichiers <c>w1_slave</c> dans un répertoire
/// temporaire plutôt que de simuler le système de fichiers : c'est le chemin de
/// lecture réel qui est ainsi exercé, jusqu'à l'appel système. On garde ce choix.
/// </remarks>
internal sealed class FakeSysfs : IDisposable
{
    public FakeSysfs()
    {
        Root = Directory.CreateTempSubdirectory("tempi-w1-").FullName;
    }

    public string Root { get; }

    public void AddDevice(string address, string payload)
    {
        var directory = Path.Combine(Root, address);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "w1_slave"), payload);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
