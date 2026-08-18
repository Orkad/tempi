using Microsoft.Extensions.Logging;
using Tempi.Configuration;
using Tempi.Storage;

namespace Tempi.Collect;

/// <summary>Purge des mesures au-delà de la durée de rétention configurée.</summary>
public static class Retention
{
    public static int Apply(TempiConfig config, TempiStorage storage, TimeProvider time, ILogger log)
    {
        if (config.RetentionDays <= 0)
        {
            return 0;
        }

        var cutoff = time.GetUtcNow().ToUnixTimeSeconds() - ((long)config.RetentionDays * 86400);
        var removed = storage.Prune(cutoff);

        if (removed > 0)
        {
            log.LogInformation(
                "rétention : {Removed} mesure(s) de plus de {Days} jour(s) supprimée(s)",
                removed, config.RetentionDays);
        }

        return removed;
    }
}
