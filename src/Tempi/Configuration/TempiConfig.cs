namespace Tempi.Configuration;

/// <summary>
/// Paramètres effectifs de l'application.
/// </summary>
/// <remarks>
/// Classe mutable, et non <c>record</c> : la ligne de commande surcharge les champs
/// un à un après lecture de l'environnement, exactement comme le fait
/// <c>_config_from_args</c> sur la dataclass Python. L'ordre de priorité est
/// défauts &lt; environnement &lt; options.
/// </remarks>
public sealed class TempiConfig
{
    /// <summary>Fournisseurs de température extérieure reconnus.</summary>
    /// <remarks>
    /// Déclarés ici plutôt que dans le module réseau, pour que la validation de la
    /// configuration n'ait pas à le charger.
    /// </remarks>
    public static readonly string[] OutdoorProviders = ["metar", "infoclimat", "open-meteo"];

    // Stockage
    public required string DbPath { get; set; }

    // Capteur
    public required string W1Dir { get; set; }
    public bool Simulate { get; set; }
    public int ReadRetries { get; set; } = 3;

    /// <summary>Accepter la valeur de reset du DS18B20.</summary>
    /// <remarks>
    /// 85,0 °C est la valeur de reset : elle signale presque toujours une conversion
    /// ratée (alimentation insuffisante, câble trop long).
    /// </remarks>
    public bool AllowResetValue { get; set; }

    // Collecte
    public double Interval { get; set; } = 60.0;
    public double MinDelta { get; set; }
    public double MaxInterval { get; set; } = 900.0;

    // Rétention
    public int RetentionDays { get; set; }

    // Serveur web
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;

    // Source extérieure. Facultative : sans configuration, tempi se comporte
    // exactement comme avant et n'émet aucune requête réseau.
    public string? OutdoorProvider { get; set; }
    public string? OutdoorStation { get; set; }
    public double? OutdoorLatitude { get; set; }
    public double? OutdoorLongitude { get; set; }
    public string? OutdoorToken { get; set; }
    public string OutdoorLabel { get; set; } = "Extérieur";

    /// <summary>Secondes entre deux interrogations de la source extérieure.</summary>
    /// <remarks>
    /// Les stations publient toutes les 6 à 60 minutes : interroger plus souvent ne
    /// donne aucun point de plus et sollicite inutilement une API publique gratuite.
    /// </remarks>
    public double OutdoorInterval { get; set; } = 600.0;

    public double OutdoorTimeout { get; set; } = 10.0;

    public static TempiConfig FromEnvironment()
    {
        var db = EnvReader.String("TEMPI_DB", null);
        return new TempiConfig
        {
            DbPath = db ?? DefaultPaths.DefaultDbPath(),
            W1Dir = EnvReader.String("TEMPI_W1_DIR", DefaultPaths.DefaultW1Dir)!,
            Simulate = EnvReader.Bool("TEMPI_SIMULATE", false),
            ReadRetries = EnvReader.Int("TEMPI_READ_RETRIES", 3),
            AllowResetValue = EnvReader.Bool("TEMPI_ALLOW_RESET_VALUE", false),
            Interval = EnvReader.Double("TEMPI_INTERVAL", 60.0),
            MinDelta = EnvReader.Double("TEMPI_MIN_DELTA", 0.0),
            MaxInterval = EnvReader.Double("TEMPI_MAX_INTERVAL", 900.0),
            RetentionDays = EnvReader.Int("TEMPI_RETENTION_DAYS", 0),
            Host = EnvReader.String("TEMPI_HOST", "127.0.0.1")!,
            Port = EnvReader.Int("TEMPI_PORT", 8080),
            OutdoorProvider = EnvReader.String("TEMPI_OUTDOOR_PROVIDER", null),
            OutdoorStation = EnvReader.String("TEMPI_OUTDOOR_STATION", null),
            OutdoorLatitude = EnvReader.OptionalDouble("TEMPI_OUTDOOR_LAT"),
            OutdoorLongitude = EnvReader.OptionalDouble("TEMPI_OUTDOOR_LON"),
            // La clé reste hors de la ligne de commande : les arguments d'un
            // processus sont lisibles par tous dans /proc.
            OutdoorToken = EnvReader.String("TEMPI_OUTDOOR_TOKEN", null),
            OutdoorLabel = EnvReader.String("TEMPI_OUTDOOR_LABEL", "Extérieur")!,
            OutdoorInterval = EnvReader.Double("TEMPI_OUTDOOR_INTERVAL", 600.0),
            OutdoorTimeout = EnvReader.Double("TEMPI_OUTDOOR_TIMEOUT", 10.0),
        };
    }

    public void Validate()
    {
        if (Interval < 1)
        {
            // Les horodatages sont stockés à la seconde ; en dessous, deux mesures
            // partageraient la même clé et s'écraseraient. Le DS18B20 demande de
            // toute façon jusqu'à 750 ms par conversion.
            throw new ConfigException("l'intervalle de collecte doit valoir au moins 1 seconde");
        }

        if (ReadRetries < 1)
        {
            throw new ConfigException("le nombre de tentatives de lecture doit valoir au moins 1");
        }

        if (MinDelta < 0)
        {
            throw new ConfigException("min-delta ne peut pas être négatif");
        }

        if (MaxInterval < 0)
        {
            throw new ConfigException("max-interval ne peut pas être négatif");
        }

        if (RetentionDays < 0)
        {
            throw new ConfigException("la rétention ne peut pas être négative");
        }

        if (Port is < 1 or > 65535)
        {
            throw new ConfigException("le port doit être compris entre 1 et 65535");
        }

        var provider = (OutdoorProvider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider.Length == 0 || provider == "none")
        {
            return;
        }

        if (Array.IndexOf(OutdoorProviders, provider) < 0)
        {
            throw new ConfigException(
                $"fournisseur extérieur inconnu : {PythonRepr.Quote(OutdoorProvider)} "
                + $"(attendu : {string.Join(", ", OutdoorProviders)})");
        }

        if (OutdoorInterval < 60)
        {
            // Aucune station ne publie plus vite, et marteler une API publique
            // gratuite est le meilleur moyen de s'en faire bannir.
            throw new ConfigException("l'intervalle extérieur doit valoir au moins 60 secondes");
        }

        if (OutdoorTimeout <= 0)
        {
            throw new ConfigException("le délai d'attente extérieur doit être positif");
        }
    }
}
