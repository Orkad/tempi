namespace Tempi.Tests.Support;

/// <summary>
/// Fixe des variables d'environnement le temps d'un test, puis les restaure.
/// </summary>
/// <remarks>
/// L'environnement d'un processus est un état global : deux tests qui le modifient
/// en parallèle se marcheraient dessus. Un verrou statique les sérialise, et seuls
/// les tests qui touchent à l'environnement sont concernés — le reste de la suite
/// continue de s'exécuter en parallèle.
/// </remarks>
internal sealed class EnvScope : IDisposable
{
    private static readonly Lock Gate = new();
    private readonly Dictionary<string, string?> _previous = [];

    public EnvScope(params (string Name, string? Value)[] variables)
    {
        Gate.Enter();

        // Toutes les variables TEMPI_* de l'hôte sont écartées : un test ne doit pas
        // dépendre de ce qui traîne dans l'environnement de la machine.
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (name.StartsWith("TEMPI_", StringComparison.Ordinal))
            {
                _previous[name] = (string?)entry.Value;
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        foreach (var (name, value) in variables)
        {
            _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Gate.Exit();
    }
}
