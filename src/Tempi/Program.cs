using System.CommandLine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tempi;
using Tempi.Cli;
using Tempi.Configuration;
using Tempi.Hosting;

// « --version » est traité avant l'analyse : System.CommandLine en fournit une, mais
// elle imprime la version informationnelle de l'assembly, que la CI suffixe du sha.
if (args is ["--version"])
{
    Console.WriteLine($"tempi {TempiVersion.Value}");
    return 0;
}

var root = new RootCommand(
    "Enregistre et consulte l'évolution de la température d'un capteur DS18B20.");

root.Add(CliOptions.Db);
root.Add(CliOptions.W1Dir);
root.Add(CliOptions.Simulate);
root.Add(CliOptions.Verbose);

// -- commandes de lecture ---------------------------------------------------

var sensors = new Command("sensors", "liste les capteurs détectés et connus");
sensors.SetAction(parsed => Run(parsed, config => DataCommands.Sensors(config)));
root.Add(sensors);

var read = new Command("read", "effectue une lecture immédiate sans rien enregistrer");
read.SetAction(parsed => Run(parsed, DataCommands.Read));
root.Add(read);

var stats = new Command("stats", "affiche l'état de la base");
stats.SetAction(parsed => Run(parsed, DataCommands.Stats));
root.Add(stats);

// -- renommage --------------------------------------------------------------

var labelAddress = new Argument<string>("address")
{
    Description = "adresse 1-Wire, par exemple 28-000005e2fdc3",
};
var labelName = new Argument<string?>("name")
{
    Description = "nom à attribuer (omis : efface le nom)",
    Arity = ArgumentArity.ZeroOrOne,
};
var label = new Command("label", "donne un nom lisible à un capteur");
label.Add(labelAddress);
label.Add(labelName);
label.SetAction(parsed => Run(
    parsed,
    config => DataCommands.Label(config, parsed.GetRequiredValue(labelAddress), parsed.GetValue(labelName))));
root.Add(label);

// -- purge ------------------------------------------------------------------

var pruneRetention = CliOptions.RetentionDays();
var pruneVacuum = new Option<bool>("--vacuum") { Description = "compacte le fichier après la purge" };
var prune = new Command("prune", "supprime les mesures anciennes");
prune.Add(pruneRetention);
prune.Add(pruneVacuum);
prune.SetAction(parsed => Run(
    parsed,
    config => DataCommands.Prune(config, parsed.GetValue(pruneVacuum)),
    new OptionSet { RetentionDays = pruneRetention }));
root.Add(prune);

// -- export -----------------------------------------------------------------

var exportFrom = CliOptions.From();
var exportTo = CliOptions.To();
var exportRange = CliOptions.Range();
var exportSensor = CliOptions.Sensor();
var exportOutput = new Option<string?>("--output", "-o") { Description = "fichier de destination" };
var export = new Command("export", "exporte les mesures au format CSV");
export.Add(exportFrom);
export.Add(exportTo);
export.Add(exportRange);
export.Add(exportSensor);
export.Add(exportOutput);
export.SetAction(parsed => Run(parsed, config => DataCommands.Export(
    config,
    parsed.GetValue(exportFrom),
    parsed.GetValue(exportTo),
    parsed.GetValue(exportRange),
    parsed.GetValue(exportSensor) ?? [],
    parsed.GetValue(exportOutput))));
root.Add(export);

// -- température extérieure -------------------------------------------------

var outdoorOptions = OutdoorOptionSet();
var outdoorStore = new Option<bool>("--store") { Description = "enregistre le relevé dans la base" };
var outdoor = new Command("outdoor", "vérifie la source de température extérieure");
AddOutdoorOptions(outdoor, outdoorOptions);
outdoor.Add(outdoorStore);
outdoor.SetAction((parsed, token) => RunAsync(
    parsed,
    config => DataCommands.Outdoor(config, parsed.GetValue(outdoorStore), token),
    outdoorOptions));
root.Add(outdoor);

// -- services ---------------------------------------------------------------

var collectOptions = CollectOptionSet();
var collectCycles = CliOptions.Cycles();
var collect = new Command("collect", "lance la boucle de collecte");
AddCollectOptions(collect, collectOptions);
collect.Add(collectCycles);
collect.SetAction((parsed, token) => RunAsync(parsed, async config =>
{
    using var host = TempiHost.BuildCollector(
        config, parsed.GetValue(collectCycles), parsed.GetValue(CliOptions.Verbose));
    await host.RunAsync(token);
    return 0;
}, collectOptions));
root.Add(collect);

var serveOptions = new OptionSet { Host = CliOptions.Host(), Port = CliOptions.Port() };
var serve = new Command("serve", "lance l'interface web et l'API");
serve.Add(serveOptions.Host!);
serve.Add(serveOptions.Port!);
serve.SetAction((parsed, token) => RunAsync(
    parsed, config => Serve(config, withCollector: false, parsed, token), serveOptions));
root.Add(serve);

var runOptions = CollectOptionSet();
runOptions = new OptionSet
{
    Interval = runOptions.Interval,
    MinDelta = runOptions.MinDelta,
    MaxInterval = runOptions.MaxInterval,
    RetentionDays = runOptions.RetentionDays,
    OutdoorProvider = runOptions.OutdoorProvider,
    OutdoorStation = runOptions.OutdoorStation,
    OutdoorLat = runOptions.OutdoorLat,
    OutdoorLon = runOptions.OutdoorLon,
    OutdoorLabel = runOptions.OutdoorLabel,
    OutdoorInterval = runOptions.OutdoorInterval,
    Host = CliOptions.Host(),
    Port = CliOptions.Port(),
};
var run = new Command("run", "lance la collecte et l'interface web dans un seul processus");
AddCollectOptions(run, runOptions);
run.Add(runOptions.Host!);
run.Add(runOptions.Port!);
run.SetAction((parsed, token) => RunAsync(
    parsed, config => Serve(config, withCollector: true, parsed, token), runOptions));
root.Add(run);

// -- diagnostic -------------------------------------------------------------

var doctorJson = new Option<bool>("--json") { Description = "sortie exploitable par un script" };
var doctor = new Command("doctor", "diagnostique le montage 1-Wire et l'installation");
doctor.Add(doctorJson);
doctor.SetAction(parsed => Run(parsed, config => DoctorCommand.Run(config, parsed.GetValue(doctorJson))));
root.Add(doctor);

var parseResult = root.Parse(args);
if (parseResult.Errors.Count > 0)
{
    foreach (var error in parseResult.Errors)
    {
        Console.Error.WriteLine($"tempi: error: {error.Message}");
    }

    // argparse rend 2 sur une ligne de commande invalide ; System.CommandLine rend 1.
    return 2;
}

return await parseResult.InvokeAsync();

// -- plomberie ---------------------------------------------------------------

static int Run(ParseResult parsed, Func<TempiConfig, int> body, OptionSet? options = null)
    => RunAsync(parsed, config => Task.FromResult(body(config)), options).GetAwaiter().GetResult();

static async Task<int> RunAsync(
    ParseResult parsed,
    Func<TempiConfig, Task<int>> body,
    OptionSet? options = null)
{
    TempiConfig config;
    try
    {
        config = ConfigBinder.Build(parsed, options);
    }
    catch (ConfigException exc)
    {
        // Python passe par parser.error(), qui imprime le bloc d'usage puis cette
        // ligne et sort avec 2. Le bloc d'usage n'est pas reproduit : le recopier
        // serait un littéral déguisé en sortie générée, qui se périmerait au premier
        // changement de sous-commande.
        Console.Error.WriteLine($"tempi: error: {exc.Message}");
        return 2;
    }

    try
    {
        return await body(config);
    }
    catch (OperationCanceledException)
    {
        return 130;   // 128 + SIGINT, convention shell
    }
}

static async Task<int> Serve(
    TempiConfig config, bool withCollector, ParseResult parsed, CancellationToken token)
{
    var app = TempiHost.BuildWeb(
        config, withCollector: withCollector, verbose: parsed.GetValue(CliOptions.Verbose));

    var host = config.Host is "0.0.0.0" or "::" ? "<toutes interfaces>" : config.Host;
    app.Logger.LogInformation("interface web disponible sur http://{Host}:{Port}/", host, config.Port);

    await app.RunAsync(token);
    return 0;
}

static OptionSet OutdoorOptionSet() => new()
{
    OutdoorProvider = CliOptions.OutdoorProvider(),
    OutdoorStation = CliOptions.OutdoorStation(),
    OutdoorLat = CliOptions.OutdoorLat(),
    OutdoorLon = CliOptions.OutdoorLon(),
    OutdoorLabel = CliOptions.OutdoorLabel(),
    OutdoorInterval = CliOptions.OutdoorInterval(),
};

static OptionSet CollectOptionSet()
{
    var outdoor = OutdoorOptionSet();
    return new OptionSet
    {
        Interval = CliOptions.Interval(),
        MinDelta = CliOptions.MinDelta(),
        MaxInterval = CliOptions.MaxInterval(),
        RetentionDays = CliOptions.RetentionDays(),
        OutdoorProvider = outdoor.OutdoorProvider,
        OutdoorStation = outdoor.OutdoorStation,
        OutdoorLat = outdoor.OutdoorLat,
        OutdoorLon = outdoor.OutdoorLon,
        OutdoorLabel = outdoor.OutdoorLabel,
        OutdoorInterval = outdoor.OutdoorInterval,
    };
}

static void AddOutdoorOptions(Command command, OptionSet options)
{
    command.Add(options.OutdoorProvider!);
    command.Add(options.OutdoorStation!);
    command.Add(options.OutdoorLat!);
    command.Add(options.OutdoorLon!);
    command.Add(options.OutdoorLabel!);
    command.Add(options.OutdoorInterval!);
}

static void AddCollectOptions(Command command, OptionSet options)
{
    command.Add(options.Interval!);
    command.Add(options.MinDelta!);
    command.Add(options.MaxInterval!);
    command.Add(options.RetentionDays!);
    AddOutdoorOptions(command, options);
}
