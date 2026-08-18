using Tempi.Sensors;
using Tempi.Tests.Support;

namespace Tempi.Tests;

/// <summary>Trames de référence, recopiées de <c>tests/test_sensor.py</c>.</summary>
internal static class Frames
{
    public const string Good =
        "a1 01 4b 46 7f ff 0c 10 5c : crc=5c YES\n"
        + "a1 01 4b 46 7f ff 0c 10 5c t=26062\n";

    public const string BadCrc =
        "a1 01 4b 46 7f ff 0c 10 5c : crc=5c NO\n"
        + "a1 01 4b 46 7f ff 0c 10 5c t=26062\n";

    public const string Reset =
        "50 05 4b 46 7f ff 0c 10 1c : crc=1c YES\n"
        + "50 05 4b 46 7f ff 0c 10 1c t=85000\n";

    public const string Negative =
        "6a fe 4b 46 7f ff 0c 10 3f : crc=3f YES\n"
        + "6a fe 4b 46 7f ff 0c 10 3f t=-5625\n";

    public static string WithMillidegrees(int value) =>
        $"a1 01 4b 46 7f ff 0c 10 5c : crc=5c YES\na1 01 4b 46 7f ff 0c 10 5c t={value}\n";
}

public sealed class ParseTests
{
    [Fact]
    public void La_temperature_est_lue() =>
        Assert.Equal(26.062, W1SlaveParser.Parse(Frames.Good), 6);

    [Fact]
    public void Une_temperature_negative_est_lue() =>
        Assert.Equal(-5.625, W1SlaveParser.Parse(Frames.Negative), 6);

    [Fact]
    public void Un_CRC_invalide_est_rejete() =>
        Assert.Throws<CrcException>(() => W1SlaveParser.Parse(Frames.BadCrc));

    [Fact]
    public void Une_trame_tronquee_est_rejetee() =>
        Assert.Throws<SensorException>(() => W1SlaveParser.Parse("a1 01 : crc=5c YES\n"));

    [Fact]
    public void Une_trame_sans_valeur_est_rejetee() =>
        Assert.Throws<SensorException>(() => W1SlaveParser.Parse("a1 : crc=5c YES\nrien ici\n"));
}

public sealed class W1BusTests
{
    /// <summary>Bus qui compte les lectures de trame, pour vérifier les tentatives.</summary>
    /// <remarks>
    /// Le test Python remplaçait la méthode d'instance <c>_read_once</c>. En C# on
    /// passe par la couture prévue : <c>ReadFrame</c> est virtuelle.
    /// </remarks>
    private sealed class CountingBus(string root, int retries, string frame)
        : W1Bus(root, retries, retryDelay: TimeSpan.Zero)
    {
        public int Calls { get; private set; }

        protected override string ReadFrame(string address)
        {
            Calls++;
            return frame;
        }
    }

    [Fact]
    public void Seules_les_familles_de_temperature_sont_decouvertes()
    {
        using var sysfs = new FakeSysfs();
        sysfs.AddDevice("28-000005e2fdc3", Frames.Good);
        sysfs.AddDevice("10-000801f2ab34", Frames.Good);
        sysfs.AddDevice("w1_bus_master1", Frames.Good);

        var bus = new W1Bus(sysfs.Root);
        Assert.Equal(["10-000801f2ab34", "28-000005e2fdc3"], bus.Discover());
    }

    [Fact]
    public void Un_repertoire_de_bus_absent_n_est_pas_fatal()
    {
        var bus = new W1Bus("/nonexistent");
        Assert.False(bus.Available);
        Assert.Empty(bus.Discover());
    }

    [Fact]
    public void La_lecture_rend_des_degres_Celsius()
    {
        using var sysfs = new FakeSysfs();
        sysfs.AddDevice("28-aaaa", Frames.Good);
        Assert.Equal(26.062, new W1Bus(sysfs.Root).Read("28-aaaa"), 6);
    }

    [Fact]
    public void La_valeur_de_reset_est_rejetee()
    {
        using var sysfs = new FakeSysfs();
        sysfs.AddDevice("28-aaaa", Frames.Reset);
        Assert.Throws<ResetValueException>(
            () => new W1Bus(sysfs.Root, retries: 1).Read("28-aaaa"));
    }

    [Fact]
    public void La_valeur_de_reset_peut_etre_acceptee()
    {
        using var sysfs = new FakeSysfs();
        sysfs.AddDevice("28-aaaa", Frames.Reset);
        Assert.Equal(85.0, new W1Bus(sysfs.Root, allowResetValue: true).Read("28-aaaa"));
    }

    [Fact]
    public void Une_valeur_hors_plage_est_rejetee()
    {
        using var sysfs = new FakeSysfs();
        sysfs.AddDevice("28-aaaa", Frames.WithMillidegrees(200000));

        var error = Assert.Throws<OutOfRangeException>(
            () => new W1Bus(sysfs.Root, retries: 1).Read("28-aaaa"));

        // str(200.0) vaut « 200.0 » en Python, pas « 200 » : le message est comparé.
        Assert.Equal("200.0 °C hors de la plage du capteur", error.Message);
    }

    [Fact]
    public void Les_tentatives_sont_epuisees_avant_d_echouer()
    {
        using var sysfs = new FakeSysfs();
        var bus = new CountingBus(sysfs.Root, retries: 3, frame: Frames.BadCrc);

        Assert.Throws<CrcException>(() => bus.Read("28-aaaa"));
        Assert.Equal(3, bus.Calls);
    }

    [Fact]
    public void Un_capteur_defaillant_n_empeche_pas_de_lire_les_autres()
    {
        using var sysfs = new FakeSysfs();
        sysfs.AddDevice("28-aaaa", Frames.Good);
        sysfs.AddDevice("28-bbbb", Frames.BadCrc);

        var scan = new W1Bus(sysfs.Root, retries: 1).ReadAll();

        Assert.Equal("28-aaaa", Assert.Single(scan.Readings).Address);
        Assert.Equal("28-bbbb", Assert.Single(scan.Failures).Address);
    }

    [Fact]
    public void Un_capteur_inexistant_leve_une_erreur()
    {
        using var sysfs = new FakeSysfs();
        Assert.Throws<SensorException>(() => new W1Bus(sysfs.Root, retries: 1).Read("28-absent"));
    }
}

public sealed class SimulatedBusTests
{
    [Fact]
    public void Les_valeurs_produites_sont_plausibles()
    {
        var scan = new SimulatedBus().ReadAll();

        Assert.Empty(scan.Failures);
        Assert.Equal(2, scan.Readings.Count);
        Assert.All(scan.Readings, reading =>
        {
            Assert.True(reading.Celsius > -10);
            Assert.True(reading.Celsius < 50);
        });
    }

    [Fact]
    public void Un_capteur_simule_inconnu_leve_une_erreur()
    {
        var error = Assert.Throws<SensorException>(() => new SimulatedBus().Read("28-absent"));
        Assert.Equal("capteur simulé 28-absent inconnu", error.Message);
    }
}
