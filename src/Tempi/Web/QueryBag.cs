using Microsoft.AspNetCore.Http;

namespace Tempi.Web;

/// <summary>
/// Paramètres de requête, avec la sémantique de <c>parse_qs</c>.
/// </summary>
/// <remarks>
/// <b>Python élimine les paramètres à valeur vide avant que le routage ne les voie.</b>
/// <c>?range=</c> donne un dictionnaire vide, donc la fenêtre par défaut s'applique et
/// la réponse est un 200. Un portage naïf verrait une chaîne vide, appellerait
/// l'analyse de durée et renverrait un 400. La différence est silencieuse : le front
/// n'émet jamais ce cas, mais une URL bricolée à la main le déclenche.
/// </remarks>
internal sealed class QueryBag
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public QueryBag(IQueryCollection query)
    {
        foreach (var (key, values) in query)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (!_values.TryGetValue(key, out var list))
                {
                    _values[key] = list = [];
                }

                list.Add(value);
            }
        }
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? First(string key) => _values.TryGetValue(key, out var list) ? list[0] : null;

    public IReadOnlyList<string>? All(string key) => _values.TryGetValue(key, out var list) ? list : null;
}
