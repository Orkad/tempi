namespace Tempi.Sensors;

/// <summary>Erreur de lecture d'un capteur.</summary>
public class SensorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>La trame lue est corrompue (CRC invalide).</summary>
public sealed class CrcException(string message) : SensorException(message);

/// <summary>Le capteur a renvoyé sa valeur de reset (85 °C), typiquement une conversion ratée.</summary>
public sealed class ResetValueException(string message) : SensorException(message);

/// <summary>La valeur lue sort de la plage physique du capteur.</summary>
public sealed class OutOfRangeException(string message) : SensorException(message);
