using System.Text.Encodings.Web;
using System.Text.Json;
using Tempi.Configuration;
using Tempi.Diagnostics;
using Tempi.Sensors;
using Tempi.Web;

namespace Tempi.Cli;

/// <summary>Diagnostic du montage 1-Wire, du stockage et du service.</summary>
internal static class DoctorCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        // ensure_ascii=False côté Python : les remèdes contiennent des accents et le
        // symbole ohm, qui doivent rester lisibles.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly TempiJsonContext Context = new(JsonOptions);

    public static int Run(TempiConfig config, bool asJson)
    {
        if (config.Simulate)
        {
            Console.Error.WriteLine("Mode simulé : les vérifications matérielles sont sans objet.\n");
        }

        var checks = new List<Check>();

        var (overlay, gpio) = SystemProbes.CheckOverlay();
        checks.Add(overlay);
        checks.Add(SystemProbes.CheckModules());

        var bus = new W1Bus(
            w1Dir: config.W1Dir,
            retries: config.ReadRetries,
            allowResetValue: config.AllowResetValue);

        checks.Add(bus.Available
            ? new Check("Répertoire 1-Wire", true, config.W1Dir)
            : new Check(
                "Répertoire 1-Wire",
                false,
                $"{config.W1Dir} absent",
                "Le bus n'est pas monté : activez l'overlay puis redémarrez.",
                Critical: true));

        var inventory = BusDiagnostics.ClassifyDevices(SystemProbes.ListDevices(config.W1Dir));

        // Un second balayage ne sert que si le bus ne renvoie que des fantômes : c'est
        // leur stabilité qui distingue une ligne à la masse d'une ligne flottante.
        BusInventory? second = null;
        if (inventory.Phantoms.Count > 0 && inventory.Sensors.Count == 0)
        {
            if (SystemProbes.Rescan(config.W1Dir))
            {
                Thread.Sleep(TimeSpan.FromSeconds(1.5));
                second = BusDiagnostics.ClassifyDevices(SystemProbes.ListDevices(config.W1Dir));
            }
            else
            {
                checks.Add(new Check(
                    "Second balayage",
                    null,
                    "droits insuffisants pour relancer une recherche",
                    "Relancez avec sudo pour distinguer une ligne à la masse d'une "
                    + "ligne flottante."));
            }
        }

        var (level, function) = SystemProbes.ReadGpio(gpio);
        checks.Add(BusDiagnostics.DiagnoseBus(inventory, second, level, function));
        checks.Add(BusDiagnostics.DiagnoseGpio(level, function));
        checks.AddRange(SystemProbes.CheckSensorReads(bus, inventory.Sensors));
        checks.Add(SystemProbes.CheckStorage(config));
        checks.Add(SystemProbes.CheckService());

        var (ok, message) = BusDiagnostics.Summarise(checks);

        if (asJson)
        {
            var report = new DoctorReportDto(
                ok,
                message,
                checks.Select(c => new DoctorCheckDto(c.Name, c.Ok, c.Detail, c.RemedyOrNull, c.Critical))
                    .ToArray());

            Console.WriteLine(JsonSerializer.Serialize(report, Context.DoctorReportDto));
            return ok ? 0 : 1;
        }

        var width = checks.Max(c => c.Name.Length);
        foreach (var check in checks)
        {
            Console.WriteLine($"  {check.Symbol}  {check.Name.PadRight(width)}  {check.Detail}");
        }

        Console.WriteLine();
        if (ok)
        {
            Console.WriteLine(message);
        }
        else
        {
            Console.WriteLine("Cause la plus probable");
            Console.WriteLine("──────────────────────");
            Console.WriteLine(message);
        }

        return ok ? 0 : 1;
    }
}
