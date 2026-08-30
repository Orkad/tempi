using System.Text;
using Tempi.Outdoor;

namespace Tempi.Tests.Support;

/// <summary>
/// Faux client HTTP : chaque appel consomme la réponse suivante.
/// </summary>
/// <remarks>
/// Aucun test ne sort sur le réseau — les réponses des trois API sont rejouées telles
/// qu'elles sont documentées, ce qui permet de lancer la suite hors ligne comme en
/// intégration continue. Une entrée <see cref="Exception"/> est levée, ce qui simule
/// une panne.
/// </remarks>
internal sealed class FakeFetch
{
    private readonly Queue<object> _remaining;
    private readonly object _last;

    public FakeFetch(params object[] payloads)
    {
        _remaining = new Queue<object>(payloads);
        _last = payloads[^1];
    }

    public List<string> Calls { get; } = [];

    public Task<byte[]> FetchAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add(url);
        var payload = _remaining.Count > 0 ? _remaining.Dequeue() : _last;

        return payload switch
        {
            Exception exception => Task.FromException<byte[]>(exception),
            byte[] bytes => Task.FromResult(bytes),
            string text => Task.FromResult(Encoding.UTF8.GetBytes(text)),
            _ => throw new ArgumentException($"charge utile non gérée : {payload.GetType()}"),
        };
    }

    public static implicit operator OutdoorFetch(FakeFetch fetch) => fetch.FetchAsync;
}
