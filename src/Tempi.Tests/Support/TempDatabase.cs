using Tempi.Storage;

namespace Tempi.Tests.Support;

/// <summary>
/// Base de mesures dans un répertoire temporaire, détruite à la fin du test.
/// </summary>
/// <remarks>
/// Le chemin passe volontairement par un sous-dossier inexistant : c'est ainsi que le
/// test Python vérifie que l'arborescence est créée à l'ouverture.
/// </remarks>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _root;

    public TempDatabase(TimeProvider? time = null)
    {
        _root = Directory.CreateTempSubdirectory("tempi-db-").FullName;
        Path = System.IO.Path.Combine(_root, "sous-dossier", "tempi.db");
        Storage = new TempiStorage(Path, time);
    }

    public string Path { get; }

    public TempiStorage Storage { get; private set; }

    /// <summary>Ferme puis rouvre la base, pour vérifier la persistance.</summary>
    public TempiStorage Reopen()
    {
        Storage.Dispose();
        Storage = new TempiStorage(Path);
        return Storage;
    }

    public void Dispose()
    {
        Storage.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
