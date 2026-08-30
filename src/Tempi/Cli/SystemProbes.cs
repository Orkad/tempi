using System.Diagnostics;
using Tempi.Configuration;
using Tempi.Diagnostics;
using Tempi.Sensors;

namespace Tempi.Cli;

/// <summary>
/// Effets de bord du diagnostic : lecture de fichiers système, appels de commandes.
/// </summary>
/// <remarks>
/// Cette séparation est celle du Python, et elle est délibérée : <c>diagnostics</c> ne
/// fait aucun accès système, tout lui est passé en argument. C'est ce qui rend son
/// analyse testable sur n'importe quelle machine, sans Raspberry Pi ni capteur.
/// </remarks>
internal static class SystemProbes
{
    /// <summary>Même ordre que <c>scripts/install.sh</c> : Bookworm d'abord, versions antérieures ensuite.</summary>
    private static readonly string[] BootConfigs =
    [
        "/boot/firmware/config.txt",
        "/boot/config.txt",
    ];

    public static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Vérifie l'activation du 1-Wire et retourne le GPIO effectivement utilisé.</summary>
    public static (Check Check, int Gpio) CheckOverlay()
    {
        foreach (var path in BootConfigs)
        {
            var text = ReadText(path);
            if (text is null)
            {
                continue;
            }

            var (enabled, gpio) = BusDiagnostics.ParseOverlay(text);
            if (enabled)
            {
                return (new Check("Overlay 1-Wire", true, $"GPIO {gpio}, déclaré dans {path}"), gpio);
            }

            return (
                new Check(
                    "Overlay 1-Wire",
                    false,
                    $"absent de {path}",
                    "Lancez « sudo raspi-config » (Interface Options > 1-Wire), ou ajoutez "
                    + "« dtoverlay=w1-gpio », puis redémarrez.",
                    Critical: true),
                gpio);
        }

        return (
            new Check("Overlay 1-Wire", null, "aucun config.txt lisible sur cette machine"),
            BusDiagnostics.DefaultW1Gpio);
    }

    public static Check CheckModules()
    {
        var text = ReadText("/proc/modules");
        if (text is null)
        {
            return new Check("Modules noyau", null, "/proc/modules illisible");
        }

        var loaded = BusDiagnostics.ParseModules(text);
        var missing = new[] { "w1_gpio", "w1_therm" }.Where(name => !loaded.Contains(name)).ToArray();

        if (missing.Length > 0)
        {
            return new Check(
                "Modules noyau",
                false,
                $"absent(s) : {string.Join(", ", missing)}",
                "« sudo modprobe w1-gpio w1-therm ». S'ils refusent de se charger, "
                + "l'overlay n'est pas actif : il faut redémarrer.",
                Critical: true);
        }

        return new Check("Modules noyau", true, "w1_gpio et w1_therm chargés");
    }

    public static IReadOnlyList<string> ListDevices(string w1Dir)
    {
        try
        {
            return new DirectoryInfo(w1Dir).EnumerateFileSystemInfos().Select(e => e.Name).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Force un nouveau balayage du bus. Retourne <c>false</c> si les droits manquent.</summary>
    public static bool Rescan(string w1Dir)
    {
        string[] masters;
        try
        {
            masters = Directory.GetDirectories(w1Dir, "w1_bus_master*").Order(StringComparer.Ordinal).ToArray();
        }
        catch (IOException)
        {
            return false;
        }

        if (masters.Length == 0)
        {
            return false;
        }

        foreach (var master in masters)
        {
            try
            {
                File.WriteAllText(Path.Combine(master, "w1_master_search"), "1\n");
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Retourne (niveau, fonction) de la ligne, via <c>pinctrl</c> ou <c>raspi-gpio</c>.</summary>
    public static (string? Level, string? Function) ReadGpio(int gpio)
    {
        foreach (var tool in (string[])["pinctrl", "raspi-gpio"])
        {
            if (Which(tool) is not { } binary)
            {
                continue;
            }

            if (RunCommand(binary, ["get", gpio.ToString(System.Globalization.CultureInfo.InvariantCulture)])
                is not { } output)
            {
                continue;
            }

            if (BusDiagnostics.ParsePinctrl(output) is { } parsed)
            {
                return (parsed.Level, parsed.Function);
            }
        }

        return (null, null);
    }

    /// <summary>Lit chaque capteur détecté et traduit l'échec éventuel en geste correctif.</summary>
    public static List<Check> CheckSensorReads(W1Bus bus, IReadOnlyList<string> addresses)
    {
        var checks = new List<Check>();

        foreach (var address in addresses)
        {
            var name = $"Lecture {address}";
            try
            {
                var celsius = bus.Read(address);

                // Trois décimales ici, contrairement au reste de l'application : le
                // diagnostic est le seul endroit où la quantification du capteur, par
                // pas de 0,0625 °C, porte une information — une valeur parfaitement
                // figée d'une lecture à l'autre trahit un capteur bloqué.
                checks.Add(new Check(
                    name,
                    true,
                    $"{celsius.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} °C"));
            }
            catch (CrcException)
            {
                checks.Add(new Check(
                    name,
                    false,
                    "CRC invalide à chaque tentative",
                    "Contact incertain ou câble trop long : raccourcissez la liaison, "
                    + "ou descendez la résistance de tirage à 2,2 kΩ.",
                    Critical: true));
            }
            catch (ResetValueException)
            {
                checks.Add(new Check(
                    name,
                    false,
                    "valeur de reset 85 °C",
                    "Alimentation insuffisante : alimentez le capteur en 3,3 V plutôt "
                    + "qu'en parasite.",
                    Critical: true));
            }
            catch (OutOfRangeException exc)
            {
                checks.Add(new Check(
                    name,
                    false,
                    exc.Message,
                    "Valeur hors de la plage du capteur : la ligne de données est perturbée.",
                    Critical: true));
            }
            catch (SensorException exc)
            {
                checks.Add(new Check(name, false, exc.Message, Critical: true));
            }
        }

        return checks;
    }

    public static Check CheckStorage(TempiConfig config)
    {
        if (File.Exists(config.DbPath))
        {
            return DefaultPaths.IsWritable(config.DbPath)
                ? new Check("Stockage", true, $"{config.DbPath} accessible en écriture")
                : new Check(
                    "Stockage",
                    false,
                    $"{config.DbPath} non inscriptible",
                    "Vérifiez le propriétaire du fichier.",
                    Critical: true);
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(config.DbPath)) ?? "/";
        if (Directory.Exists(parent) && DefaultPaths.IsWritable(parent))
        {
            return new Check("Stockage", true, $"{parent} accessible, base à créer");
        }

        return new Check(
            "Stockage",
            false,
            $"{parent} non inscriptible",
            "Créez ce répertoire ou corrigez ses droits.",
            Critical: true);
    }

    public static Check CheckService()
    {
        if (Which("systemctl") is not { } binary)
        {
            return new Check("Service", null, "systemctl absent");
        }

        if (RunCommand(binary, ["is-active", "tempi"]) is not { } output)
        {
            return new Check("Service", null, "état indéterminable");
        }

        var state = output.Trim();
        if (state.Length == 0)
        {
            state = "inconnu";
        }

        if (state == "active")
        {
            return new Check("Service", true, "tempi.service actif");
        }

        // Non critique : « tempi doctor » sert justement à préparer le terrain avant
        // de lancer le service.
        return new Check(
            "Service",
            null,
            $"tempi.service : {state}",
            "« sudo systemctl start tempi » si vous l'attendiez en marche.");
    }

    /// <summary>Équivalent de <c>shutil.which</c>.</summary>
    private static string? Which(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':'))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Exécute une commande et rend sa sortie standard, ou <c>null</c> si elle échoue.
    /// </summary>
    /// <remarks>
    /// Le code de retour n'est pas vérifié : seule la sortie compte, et l'analyse dira
    /// si elle est exploitable. C'est la sémantique du Python.
    /// </remarks>
    private static string? RunCommand(string binary, string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(binary)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
