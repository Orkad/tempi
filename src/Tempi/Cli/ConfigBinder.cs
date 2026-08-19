using System.CommandLine;
using Tempi.Configuration;

namespace Tempi.Cli;

/// <summary>Options d'une sous-commande susceptibles de surcharger la configuration.</summary>
internal sealed class OptionSet
{
    public Option<double?>? Interval { get; init; }
    public Option<double?>? MinDelta { get; init; }
    public Option<double?>? MaxInterval { get; init; }
    public Option<int?>? RetentionDays { get; init; }
    public Option<string?>? Host { get; init; }
    public Option<int?>? Port { get; init; }
    public Option<string?>? OutdoorProvider { get; init; }
    public Option<string?>? OutdoorStation { get; init; }
    public Option<double?>? OutdoorLat { get; init; }
    public Option<double?>? OutdoorLon { get; init; }
    public Option<string?>? OutdoorLabel { get; init; }
    public Option<double?>? OutdoorInterval { get; init; }
}

/// <summary>
/// Construit la configuration effective.
/// </summary>
/// <remarks>
/// Ordre de priorité : défauts codés en dur, puis variables d'environnement, puis
/// options de la ligne de commande. Une option absente ne doit rien écraser, d'où le
/// test sur le <c>null</c> de chaque valeur.
/// </remarks>
internal static class ConfigBinder
{
    public static TempiConfig Build(ParseResult parsed, OptionSet? options = null)
    {
        var config = TempiConfig.FromEnvironment();

        if (parsed.GetValue(CliOptions.Db) is { Length: > 0 } db)
        {
            config.DbPath = db;
        }

        if (parsed.GetValue(CliOptions.W1Dir) is { Length: > 0 } w1)
        {
            config.W1Dir = w1;
        }

        // --simulate ne peut que forcer, jamais annuler : c'est un drapeau, pas un
        // booléen à trois états.
        if (parsed.GetValue(CliOptions.Simulate))
        {
            config.Simulate = true;
        }

        if (options is not null)
        {
            Apply(parsed, options.Interval, v => config.Interval = v);
            Apply(parsed, options.MinDelta, v => config.MinDelta = v);
            Apply(parsed, options.MaxInterval, v => config.MaxInterval = v);
            Apply(parsed, options.RetentionDays, v => config.RetentionDays = v);
            Apply(parsed, options.Host, v => config.Host = v);
            Apply(parsed, options.Port, v => config.Port = v);
            Apply(parsed, options.OutdoorProvider, v => config.OutdoorProvider = v);
            Apply(parsed, options.OutdoorStation, v => config.OutdoorStation = v);
            Apply(parsed, options.OutdoorLat, v => config.OutdoorLatitude = v);
            Apply(parsed, options.OutdoorLon, v => config.OutdoorLongitude = v);
            Apply(parsed, options.OutdoorLabel, v => config.OutdoorLabel = v);
            Apply(parsed, options.OutdoorInterval, v => config.OutdoorInterval = v);
        }

        config.Validate();
        return config;
    }

    private static void Apply<T>(ParseResult parsed, Option<T?>? option, Action<T> set)
        where T : struct
    {
        if (option is not null && parsed.GetValue(option) is { } value)
        {
            set(value);
        }
    }

    private static void Apply(ParseResult parsed, Option<string?>? option, Action<string> set)
    {
        if (option is not null && parsed.GetValue(option) is { Length: > 0 } value)
        {
            set(value);
        }
    }
}
