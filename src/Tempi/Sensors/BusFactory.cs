using Microsoft.Extensions.Logging;
using Tempi.Configuration;

namespace Tempi.Sensors;

/// <summary>Construit le bus correspondant à la configuration.</summary>
public static class BusFactory
{
    public static ITemperatureBus Create(
        TempiConfig config,
        TimeProvider? time = null,
        ILogger? log = null)
    {
        if (config.Simulate)
        {
            return new SimulatedBus(time: time);
        }

        return new W1Bus(
            w1Dir: config.W1Dir,
            retries: config.ReadRetries,
            allowResetValue: config.AllowResetValue,
            time: time,
            log: log);
    }
}
