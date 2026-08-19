using System.CommandLine;

namespace Tempi.Cli;

/// <summary>
/// Options partagées entre les sous-commandes.
/// </summary>
/// <remarks>
/// Toutes les options surchargeables sont <b>nullables</b>, et c'est essentiel : c'est
/// la seule façon de distinguer « absente » de « valeur par défaut ». Une
/// <c>Option&lt;double&gt;</c> vaudrait zéro en l'absence de l'option et écraserait la
/// variable d'environnement, cassant l'ordre défauts &lt; environnement &lt; ligne de
/// commande. Le pendant Python est le <c>default=None</c> systématique d'argparse.
/// </remarks>
internal static class CliOptions
{
    // -- options globales ---------------------------------------------------

    public static readonly Option<string?> Db = new("--db")
    {
        Description = "chemin de la base SQLite (défaut : $TEMPI_DB)",
        Recursive = true,
    };

    public static readonly Option<string?> W1Dir = new("--w1-dir")
    {
        Description = "répertoire des périphériques 1-Wire",
        Recursive = true,
    };

    public static readonly Option<bool> Simulate = new("--simulate")
    {
        Description = "utilise un capteur simulé",
        Recursive = true,
    };

    public static readonly Option<bool> Verbose = new("--verbose", "-v")
    {
        Description = "journalisation détaillée",
        Recursive = true,
    };

    // -- collecte -----------------------------------------------------------

    public static Option<double?> Interval() => new("--interval", "-i")
    {
        Description = "secondes entre deux relevés",
    };

    public static Option<double?> MinDelta() => new("--min-delta")
    {
        Description = "n'enregistre que si l'écart avec la dernière mesure atteint cette valeur (°C)",
    };

    public static Option<double?> MaxInterval() => new("--max-interval")
    {
        Description = "durée maximale sans enregistrement malgré la bande morte",
    };

    public static Option<int?> RetentionDays() => new("--retention-days")
    {
        Description = "supprime les mesures plus anciennes que N jours",
    };

    public static Option<int?> Cycles() => new("--cycles", "-n")
    {
        Description = "s'arrête après N cycles (utile pour tester)",
    };

    // -- serveur ------------------------------------------------------------

    public static Option<string?> Host() => new("--host") { Description = "adresse d'écoute" };

    public static Option<int?> Port() => new("--port", "-p") { Description = "port d'écoute" };

    // -- fenêtre temporelle -------------------------------------------------

    public static Option<string?> From() => new("--from") { Description = "borne de début (epoch ou ISO 8601)" };

    public static Option<string?> To() => new("--to") { Description = "borne de fin (epoch ou ISO 8601)" };

    public static Option<string?> Range() => new("--range")
    {
        Description = "fenêtre relative à maintenant, par exemple 7d",
    };

    public static Option<string[]> Sensor() => new("--sensor")
    {
        Description = "limite à une adresse (répétable)",
        Arity = ArgumentArity.ZeroOrMore,
    };

    // -- source extérieure --------------------------------------------------

    // Pas d'option pour la clé : les arguments d'un processus sont lisibles par tous
    // dans /proc. Elle ne se donne que par TEMPI_OUTDOOR_TOKEN.
    public static Option<string?> OutdoorProvider() => new("--outdoor-provider")
    {
        Description = "metar, infoclimat, open-meteo ou none",
    };

    public static Option<string?> OutdoorStation() => new("--outdoor-station")
    {
        Description = "code OACI ou identifiant de station",
    };

    public static Option<double?> OutdoorLat() => new("--outdoor-lat") { Description = "latitude" };

    public static Option<double?> OutdoorLon() => new("--outdoor-lon") { Description = "longitude" };

    public static Option<string?> OutdoorLabel() => new("--outdoor-label")
    {
        Description = "nom du pseudo-capteur extérieur",
    };

    public static Option<double?> OutdoorInterval() => new("--outdoor-interval")
    {
        Description = "secondes entre deux interrogations (minimum 60)",
    };
}
