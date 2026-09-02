using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Tempi.Hosting;

/// <summary>
/// Reproduit le format de journal de <c>logging.basicConfig</c>.
/// </summary>
/// <remarks>
/// Le format Python est <c>%(asctime)s %(levelname)-7s %(name)s: %(message)s</c> avec
/// <c>datefmt="%Y-%m-%dT%H:%M:%S"</c>. Le niveau est cadré sur sept colonnes, ce qui
/// aligne les messages quel que soit le niveau.
/// </remarks>
internal sealed class TempiConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "tempi";

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        textWriter.Write(stamp);
        textWriter.Write(' ');
        textWriter.Write(LevelName(logEntry.LogLevel).PadRight(7));
        textWriter.Write(' ');
        textWriter.Write(logEntry.Category);
        textWriter.Write(": ");
        textWriter.WriteLine(message);

        if (logEntry.Exception is not null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "DEBUG",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "INFO",
    };
}

/// <summary>Catégories de journal, reprises telles quelles du paquet Python.</summary>
public static class TempiLog
{
    public const string Root = "tempi";
    public const string Collector = "tempi.collector";
    public const string Storage = "tempi.storage";
    public const string Sensor = "tempi.sensor";
    public const string Web = "tempi.web";
    public const string Outdoor = "tempi.outdoor";

    public static ILogger For(ILoggerFactory? factory, string category) =>
        factory?.CreateLogger(category) ?? NullLogger.Instance;
}
