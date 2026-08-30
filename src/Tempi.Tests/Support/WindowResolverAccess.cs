using Microsoft.AspNetCore.Http;
using Tempi.Storage;
using Tempi.Web;

namespace Tempi.Tests;

/// <summary>Accès aux fonctions d'analyse, internes au projet applicatif.</summary>
internal static class WindowResolverAccess
{
    public static long ParseDuration(string value) => WindowResolver.ParseDuration(value);

    public static long ParseTimestamp(string value) => WindowResolver.ParseTimestamp(value);

    public static (long Start, long End) Resolve(IQueryCollection query, TempiStorage storage, TimeProvider time)
        => WindowResolver.Resolve(new QueryBag(query), storage, time);
}
