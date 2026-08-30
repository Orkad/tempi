using Microsoft.Extensions.Logging.Abstractions;

namespace Tempi.Tests;

/// <summary>Journal muet, pour les tests qui provoquent volontairement des erreurs.</summary>
internal static class NullLoggerShim
{
    public static NullLogger Instance => NullLogger.Instance;
}
