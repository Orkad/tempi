using Microsoft.Extensions.Time.Testing;
using Tempi.Collect;
using Tempi.Configuration;
using Tempi.Sensors;
using Tempi.Tests.Support;

namespace Tempi.Tests;

/// <summary>Portage de <c>tests/test_collector.py</c>.</summary>
public sealed class CollectorTests
{
    private static TempiConfig Config(double minDelta = 0, double maxInterval = 0, double interval = 0.01) =>
        new()
        {
            DbPath = ":memory:",
            W1Dir = "/nonexistent",
            Simulate = true,
            ReadRetries = 1,
            Interval = interval,
            MinDelta = minDelta,
            MaxInterval = maxInterval,
        };

    [Fact]
    public void Chaque_mesure_est_enregistree()
    {
        using var db = new TempDatabase();
        var bus = new FakeBus([FakeBus.One("28-aaaa", 20.0, 100), FakeBus.One("28-aaaa", 21.0, 160)]);
        var collector = new Collector(Config(), db.Storage, bus);

        collector.PollOnce();
        collector.PollOnce();

        Assert.Equal(2, collector.Stored);
        Assert.Equal(2, db.Storage.Stats().Readings);
    }

    [Fact]
    public void Un_echec_de_lecture_est_compte_et_non_propage()
    {
        using var db = new TempDatabase();
        var bus = new FakeBus([new BusScan([], [new BusFailure("28-aaaa", new SensorException("CRC"))])]);
        var collector = new Collector(Config(), db.Storage, bus);

        collector.PollOnce();

        Assert.Equal(1, collector.Errors);
        Assert.Equal(0, collector.Stored);
    }

    [Fact]
    public void La_bande_morte_ignore_les_valeurs_stables()
    {
        using var db = new TempDatabase();
        var bus = new FakeBus(
        [
            FakeBus.One("28-aaaa", 20.00, 100),
            FakeBus.One("28-aaaa", 20.10, 160),   // écart trop faible
            FakeBus.One("28-aaaa", 20.70, 220),   // écart suffisant
        ]);
        var collector = new Collector(Config(minDelta: 0.5, maxInterval: 0), db.Storage, bus);

        for (var i = 0; i < 3; i++)
        {
            collector.PollOnce();
        }

        Assert.Equal(2, collector.Stored);
        Assert.Equal(
            [100L, 220L],
            db.Storage.Series(0, 1000)["28-aaaa"].Select(p => p.Ts));
    }

    [Fact]
    public void L_intervalle_maximal_force_un_point_malgre_la_bande_morte()
    {
        using var db = new TempDatabase();
        var bus = new FakeBus(
        [
            FakeBus.One("28-aaaa", 20.0, 100),
            FakeBus.One("28-aaaa", 20.0, 150),   // trop tôt
            FakeBus.One("28-aaaa", 20.0, 200),   // 100 s écoulées
        ]);
        var collector = new Collector(Config(minDelta: 0.5, maxInterval: 100), db.Storage, bus);

        for (var i = 0; i < 3; i++)
        {
            collector.PollOnce();
        }

        Assert.Equal(
            [100L, 200L],
            db.Storage.Series(0, 1000)["28-aaaa"].Select(p => p.Ts));
    }

    [Fact]
    public void Une_bande_morte_nulle_enregistre_tout()
    {
        using var db = new TempDatabase();
        var bus = new FakeBus(
            ((long[])[100, 160, 220]).Select(ts => FakeBus.One("28-aaaa", 20.0, ts)));
        var collector = new Collector(Config(minDelta: 0), db.Storage, bus);

        for (var i = 0; i < 3; i++)
        {
            collector.PollOnce();
        }

        Assert.Equal(3, collector.Stored);
    }

    [Fact]
    public async Task La_boucle_s_arrete_apres_le_nombre_de_cycles_demande()
    {
        using var db = new TempDatabase();
        var time = new FakeTimeProvider();
        var bus = new FakeBus(
            Enumerable.Range(0, 5).Select(i => FakeBus.One("28-aaaa", 20.0, 100 + i)));
        var collector = new Collector(Config(interval: 60), db.Storage, bus, time);

        // Avec une horloge factice la boucle n'attend pas réellement : le test est
        // instantané et déterministe, là où le test Python devait descendre
        // l'intervalle à la milliseconde et se garder d'un blocage.
        var run = collector.RunAsync(maxCycles: 3, CancellationToken.None);
        for (var i = 0; i < 3 && !run.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
        }

        await run;
        Assert.Equal(3, collector.Cycles);
    }

    [Fact]
    public async Task Un_cycle_en_echec_n_interrompt_pas_la_boucle()
    {
        using var db = new TempDatabase();
        var time = new FakeTimeProvider();
        var bus = new ExplodingBus();
        var collector = new Collector(Config(interval: 60), db.Storage, bus, time);

        var run = collector.RunAsync(maxCycles: 2, CancellationToken.None);
        for (var i = 0; i < 2 && !run.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
        }

        await run;
        Assert.Equal(1, collector.Errors);
        Assert.Equal(1, collector.Stored);
    }

    [Fact]
    public async Task Une_annulation_prealable_empeche_tout_cycle()
    {
        using var db = new TempDatabase();
        var bus = new FakeBus([FakeBus.One("28-aaaa", 20.0, 100)]);
        var collector = new Collector(Config(interval: 30), db.Storage, bus);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await collector.RunAsync(null, cts.Token);

        Assert.Equal(0, collector.Cycles);
    }

    [Fact]
    public async Task Les_echeances_manquees_sont_sautees_et_non_rattrapees()
    {
        // Cas que les tests Python ne pouvaient pas atteindre : un cycle plus long que
        // l'intervalle. La cadence est absolue, donc les échéances dépassées sont
        // abandonnées plutôt que rejouées en rafale.
        using var db = new TempDatabase();
        var time = new FakeTimeProvider();
        var bus = new SlowBus(time, TimeSpan.FromSeconds(25));
        var collector = new Collector(Config(interval: 10), db.Storage, bus, time);

        var run = collector.RunAsync(maxCycles: 3, CancellationToken.None);
        for (var i = 0; i < 10 && !run.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
        }

        await run;
        Assert.Equal(3, collector.Cycles);
    }

    /// <summary>Échoue au premier appel, puis se comporte normalement.</summary>
    private sealed class ExplodingBus() : FakeBus([])
    {
        public override BusScan ReadAll(IReadOnlyList<string>? addresses = null)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new InvalidOperationException("bus indisponible");
            }

            return One("28-aaaa", 20.0, 100);
        }
    }

    /// <summary>Consomme plus de temps qu'un intervalle à chaque lecture.</summary>
    private sealed class SlowBus(FakeTimeProvider time, TimeSpan duration) : FakeBus([])
    {
        public override BusScan ReadAll(IReadOnlyList<string>? addresses = null)
        {
            Calls++;
            time.Advance(duration);
            return One("28-aaaa", 20.0, 100 + Calls);
        }
    }
}

public sealed class RetentionTests
{
    private static TempiConfig Config(int retentionDays) => new()
    {
        DbPath = ":memory:",
        W1Dir = "/nonexistent",
        RetentionDays = retentionDays,
    };

    [Fact]
    public void Seules_les_mesures_anciennes_sont_supprimees()
    {
        using var db = new TempDatabase();
        var time = TimeProvider.System;
        var now = time.GetUtcNow().ToUnixTimeSeconds();

        db.Storage.Record(
        [
            new Reading("28-aaaa", 20.0, now - (10 * 86400)),
            new Reading("28-aaaa", 21.0, now - 86400),
        ]);

        Assert.Equal(1, Retention.Apply(Config(7), db.Storage, time, NullLoggerShim.Instance));
        Assert.Equal(1, db.Storage.Stats().Readings);
    }

    [Fact]
    public void Une_retention_desactivee_ne_fait_rien()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 1)]);

        Assert.Equal(0, Retention.Apply(Config(0), db.Storage, TimeProvider.System, NullLoggerShim.Instance));
        Assert.Equal(1, db.Storage.Stats().Readings);
    }
}
