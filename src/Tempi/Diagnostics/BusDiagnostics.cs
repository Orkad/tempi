using System.Text.RegularExpressions;
using Tempi.Sensors;

namespace Tempi.Diagnostics;

/// <summary>
/// Analyse de l'état du bus 1-Wire, sans aucun accès système.
/// </summary>
/// <remarks>
/// <para>
/// Tout est passé en argument : contenu de <c>config.txt</c>, de
/// <c>/proc/modules</c>, sortie de <c>pinctrl</c>, noms de périphériques. Les
/// effets de bord vivent dans la couche ligne de commande. C'est ce qui rend ce
/// module testable sur n'importe quelle machine, sans Raspberry Pi ni capteur.
/// </para>
/// <para>
/// Un <em>périphérique fantôme</em> est une ROM de famille <c>00</c> enregistrée
/// par le maître 1-Wire sur un bus en défaut : <c>00-800000000000</c> constant
/// signale une ligne à la masse, des ROM changeantes une ligne flottante.
/// </para>
/// </remarks>
public static partial class BusDiagnostics
{
    /// <summary>ROM lue quand le maître ne voit que des zéros.</summary>
    public const string StuckLowRom = "00-800000000000";

    /// <summary>Famille des périphériques inexistants.</summary>
    public const string PhantomFamily = "00";

    /// <summary>GPIO utilisé par <c>w1-gpio</c> sans paramètre <c>gpiopin</c>.</summary>
    public const int DefaultW1Gpio = 4;

    // Le groupe (,|$) est indispensable : \b ne suffirait pas, le tiret étant une
    // frontière de mot, « dtoverlay=w1-gpio-something-else » serait accepté à tort.
    [GeneratedRegex(@"^dtoverlay\s*=\s*w1-gpio(-pullup)?\s*(,|$)")]
    private static partial Regex OverlayLine();

    [GeneratedRegex(@"gpiopin\s*=\s*(\d+)")]
    private static partial Regex GpioPin();

    [GeneratedRegex(@"^\s*\d+:\s*(\S+)\s+(\S+)\s*\|\s*(hi|lo)", RegexOptions.Multiline)]
    private static partial Regex PinctrlModern();

    [GeneratedRegex(@"level=([01]).*?func=(\S+)")]
    private static partial Regex PinctrlLegacy();

    /// <summary>Cherche <c>dtoverlay=w1-gpio</c> dans un <c>config.txt</c>.</summary>
    public static (bool Enabled, int Gpio) ParseOverlay(string configText)
    {
        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !OverlayLine().IsMatch(line))
            {
                continue;
            }

            var pin = GpioPin().Match(line);
            return (true, pin.Success ? int.Parse(pin.Groups[1].Value) : DefaultW1Gpio);
        }

        return (false, DefaultW1Gpio);
    }

    /// <summary>Extrait les noms de modules du contenu de <c>/proc/modules</c>.</summary>
    public static HashSet<string> ParseModules(string procModules)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in procModules.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                names.Add(parts[0]);
            }
        }

        return names;
    }

    /// <summary>Extrait (fonction, niveau) de la sortie de <c>pinctrl</c> ou <c>raspi-gpio</c>.</summary>
    public static (string Function, string Level)? ParsePinctrl(string output)
    {
        var modern = PinctrlModern().Match(output);
        if (modern.Success)
        {
            // Les deux champs sont recollés avec un seul espace, quelle que soit
            // l'indentation d'origine : « 4: ip    pu | lo » donne « ip pu ».
            return ($"{modern.Groups[1].Value} {modern.Groups[2].Value}", modern.Groups[3].Value);
        }

        var legacy = PinctrlLegacy().Match(output);
        if (legacy.Success)
        {
            return (legacy.Groups[2].Value.ToLowerInvariant(), legacy.Groups[1].Value == "1" ? "hi" : "lo");
        }

        return null;
    }

    /// <summary>
    /// Range le contenu de <c>/sys/bus/w1/devices</c> par nature.
    /// </summary>
    /// <remarks>
    /// Contrairement à la découverte du bus, qui ne retient que les capteurs
    /// utilisables, on conserve ici les fantômes : ce sont eux qui portent le
    /// diagnostic.
    /// </remarks>
    public static BusInventory ClassifyDevices(IEnumerable<string> names)
    {
        var inventory = new BusInventory();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            if (name.StartsWith("w1_bus_master", StringComparison.Ordinal))
            {
                inventory.Masters.Add(name);
                continue;
            }

            var family = name.Split('-', 2)[0].ToLowerInvariant();
            if (TemperatureFamilies.IsTemperature(family))
            {
                inventory.Sensors.Add(name);
            }
            else if (family == PhantomFamily)
            {
                inventory.Phantoms.Add(name);
            }
        }

        return inventory;
    }

    /// <summary>
    /// Vrai si la ligne reste basse alors que le tirage interne est actif.
    /// </summary>
    /// <remarks>
    /// C'est la preuve la plus solide dont on dispose : le tirage interne du SoC
    /// vaut une cinquantaine de kilo-ohms, assez pour ramener au niveau haut une
    /// ligne simplement flottante. S'il n'y parvient pas, un chemin conducteur tire
    /// vers la masse. Cette observation prime sur la lecture des ROM parasites, qui
    /// n'est qu'un symptôme indirect.
    /// </remarks>
    public static bool HoldsLow(string? gpioLevel, string? gpioFunction)
    {
        if (gpioLevel != "lo" || string.IsNullOrEmpty(gpioFunction))
        {
            return false;
        }

        var function = gpioFunction.ToLowerInvariant();
        // « pu » est cherché comme champ entier et non comme sous-chaîne, sans quoi
        // « pull=none » contiendrait « pu ».
        return function.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("pu")
               || function.Contains("pull=up", StringComparison.Ordinal);
    }

    /// <summary>
    /// Nomme l'état du bus à partir de l'inventaire et, si possible, d'un second
    /// balayage et du niveau électrique de la ligne.
    /// </summary>
    /// <param name="secondScan">
    /// Permet de distinguer des fantômes stables — ligne tenue à la masse — de
    /// fantômes changeants, signature d'une ligne flottante.
    /// </param>
    public static Check DiagnoseBus(
        BusInventory inventory,
        BusInventory? secondScan = null,
        string? gpioLevel = null,
        string? gpioFunction = null)
    {
        const string name = "État du bus";

        if (inventory.Sensors.Count > 0)
        {
            var detail = $"{inventory.Sensors.Count} capteur(s) : {string.Join(", ", inventory.Sensors)}";
            if (inventory.Phantoms.Count > 0)
            {
                return new Check(
                    name,
                    true,
                    detail + $", plus {inventory.Phantoms.Count} ROM parasite(s)",
                    "Le capteur répond mais le bus est bruité : raccourcissez le câble, "
                    + "ou descendez la résistance de tirage à 2,2 kΩ.");
            }

            return new Check(name, true, detail);
        }

        if (inventory.Masters.Count == 0)
        {
            return new Check(
                name,
                false,
                "aucun maître 1-Wire",
                "Le bus n'est pas monté : vérifiez l'overlay puis redémarrez.",
                Critical: true);
        }

        if (inventory.Phantoms.Count == 0)
        {
            return new Check(
                name,
                false,
                "bus actif, aucun périphérique",
                "Le bus fonctionne mais ne voit rien : le fil de données n'atteint pas "
                + "le capteur, ou le capteur n'est pas alimenté.",
                Critical: true);
        }

        // Des fantômes, et aucun capteur : la ligne est en défaut. Reste à dire lequel.
        var roms = string.Join(", ", inventory.Phantoms);
        var stable = secondScan is not null
                     && secondScan.Phantoms.ToHashSet(StringComparer.Ordinal)
                         .SetEquals(inventory.Phantoms);
        var changing = secondScan is not null && !stable;
        var allStuck = inventory.Phantoms.All(rom => rom == StuckLowRom);
        var grounded = HoldsLow(gpioLevel, gpioFunction);

        // Le niveau électrique prime : des ROM changeantes évoquent une ligne
        // flottante, mais si le tirage interne ne parvient pas à la remonter, c'est
        // qu'elle est bel et bien reliée à la masse.
        if (grounded || allStuck || (stable && gpioLevel == "lo"))
        {
            var detail = grounded && changing
                ? $"ROM parasite(s) changeantes ({roms}), mais la ligne reste basse "
                  + "malgré le tirage interne"
                : allStuck || stable
                    ? $"ROM parasite(s) constante(s) : {roms}"
                    : $"ROM parasite(s) : {roms}";

            return new Check(
                name,
                false,
                detail,
                "Ligne de données reliée à la masse. Le fil de données et la masse "
                + "partagent une rangée, la résistance de tirage part sur la masse au lieu "
                + "du 3,3 V, ou le capteur est monté à l'envers. Débranchez le fil de "
                + "données côté platine : si la ligne reste basse, le défaut est côté "
                + "Raspberry Pi.",
                Critical: true);
        }

        if (changing)
        {
            return new Check(
                name,
                false,
                $"ROM parasite(s) changeantes : {roms}",
                "Ligne de données flottante : elle capte du bruit. La résistance de "
                + "4,7 kΩ ne relie pas la donnée au 3,3 V — pattes dans la mauvaise rangée, "
                + "ou valeur trop élevée (anneaux jaune, violet, rouge).",
                Critical: true);
        }

        return new Check(
            name,
            false,
            $"ROM parasite(s) : {roms}",
            "Aucun capteur ne répond, le bus lit du bruit. Vérifiez la résistance de "
            + "tirage entre la donnée et le 3,3 V, puis l'orientation du capteur.",
            Critical: true);
    }

    /// <summary>
    /// Interprète le niveau électrique de la ligne de données au repos.
    /// </summary>
    /// <remarks>
    /// Au repos, la résistance de tirage doit maintenir la ligne au niveau haut. Un
    /// niveau bas persistant signale un chemin conducteur vers la masse.
    /// </remarks>
    public static Check DiagnoseGpio(string? level, string? function)
    {
        const string name = "Niveau de la ligne";

        if (level is null)
        {
            return new Check(
                name,
                null,
                "pinctrl et raspi-gpio absents",
                "Installez raspi-utils pour cette vérification.");
        }

        if (level == "hi")
        {
            return new Check(name, true, $"niveau haut au repos ({function})");
        }

        if (!string.IsNullOrEmpty(function)
            && function.Contains("output", StringComparison.OrdinalIgnoreCase))
        {
            return new Check(
                name,
                null,
                $"niveau bas, mais la ligne est pilotée ({function})",
                "Le pilote 1-Wire était en train d'émettre : relancez pour obtenir "
                + "l'état au repos.");
        }

        return new Check(
            name,
            false,
            $"niveau bas au repos ({function})",
            "La résistance de tirage ne fait pas son travail, ou la ligne touche la masse.",
            Critical: true);
    }

    /// <summary>
    /// Retourne (tout va bien, message de synthèse).
    /// </summary>
    /// <remarks>
    /// Le message reprend le premier échec critique : c'est celui qui explique tous
    /// les suivants, et enchaîner les remèdes ferait perdre le fil.
    /// </remarks>
    public static (bool Ok, string Message) Summarise(IReadOnlyList<Check> checks)
    {
        var failures = checks.Where(c => c.Ok is false).ToList();
        if (failures.Count == 0)
        {
            var undetermined = checks.Count(c => c.Ok is null);
            return undetermined > 0
                ? (true, $"Aucun problème détecté ({undetermined} vérification(s) non concluante(s)).")
                : (true, "Tout est en ordre.");
        }

        var first = failures.FirstOrDefault(c => c.Critical) ?? failures[0];
        return (false, $"{first.Name} : {first.Detail}\n{first.Remedy}".TrimEnd());
    }
}
