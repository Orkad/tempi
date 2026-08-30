namespace Tempi.Configuration;

/// <summary>Configuration invalide : équivalent du <c>ValueError</c> de <c>config.py</c>.</summary>
public sealed class ConfigException(string message) : Exception(message);
