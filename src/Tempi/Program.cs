using Microsoft.Extensions.Logging;
using Tempi;
using Tempi.Configuration;
using Tempi.Hosting;

// Les onze sous-commandes arrivent avec le portage de cli.py. « serve » est câblé
// dès maintenant : c'est la commande dont le golden master a besoin pour comparer
// l'API .NET à l'API Python.
if (args is ["--version"])
{
    Console.WriteLine($"tempi {TempiVersion.Value}");
    return 0;
}

if (args is ["serve", ..])
{
    TempiConfig config;
    try
    {
        config = TempiConfig.FromEnvironment();
        config.Validate();
    }
    catch (ConfigException exc)
    {
        Console.Error.WriteLine($"tempi : {exc.Message}");
        return 2;
    }

    var app = TempiHost.BuildWeb(config);
    var host = config.Host is "0.0.0.0" or "::" ? "<toutes interfaces>" : config.Host;
    app.Logger.LogInformation(
        "interface web disponible sur http://{Host}:{Port}/", host, config.Port);

    await app.RunAsync();
    return 0;
}

Console.Error.WriteLine("tempi : portage .NET en cours, seul « serve » est câblé.");
return 2;
