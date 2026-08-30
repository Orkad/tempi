namespace Tempi.Sensors;

/// <summary>Une mesure horodatée.</summary>
public readonly record struct Reading(string Address, double Celsius, long Ts);

/// <summary>Un capteur qui n'a pas répondu, et pourquoi.</summary>
public readonly record struct BusFailure(string Address, SensorException Error);

/// <summary>
/// Résultat d'un cycle de lecture : les succès d'un côté, les échecs de l'autre.
/// </summary>
/// <remarks>Un capteur défaillant ne doit jamais interrompre la collecte des autres.</remarks>
public readonly record struct BusScan(
    IReadOnlyList<Reading> Readings,
    IReadOnlyList<BusFailure> Failures);
