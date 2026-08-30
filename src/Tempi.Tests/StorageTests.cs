using Microsoft.Data.Sqlite;
using Tempi.Sensors;
using Tempi.Storage;
using Tempi.Tests.Support;

namespace Tempi.Tests;

/// <summary>Portage de <c>tests/test_storage.py</c>.</summary>
public sealed class StorageTests
{
    [Fact]
    public void Le_repertoire_parent_est_cree()
    {
        using var db = new TempDatabase();
        Assert.True(Directory.Exists(Path.GetDirectoryName(db.Path)));
    }

    [Fact]
    public void Les_mesures_sont_relues_telles_qu_ecrites()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 21.5, 1000), new Reading("28-bbbb", 4.25, 1000)]);

        var series = db.Storage.Series(0, 2000);
        Assert.Equal(21.5, series["28-aaaa"][0].Celsius);
        Assert.Equal(4.25, series["28-bbbb"][0].Celsius);
    }

    [Fact]
    public void Un_capteur_n_est_cree_qu_une_fois()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 1000)]);
        db.Storage.Record([new Reading("28-aaaa", 20.5, 1060)]);

        var sensor = Assert.Single(db.Storage.Sensors());
        Assert.Equal(2, sensor.Count);
    }

    [Fact]
    public void Un_horodatage_en_double_ecrase_la_valeur_precedente()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 1000)]);
        db.Storage.Record([new Reading("28-aaaa", 21.0, 1000)]);

        var points = db.Storage.Series(0, 2000)["28-aaaa"];
        Assert.Equal(21.0, Assert.Single(points).Celsius);
    }

    [Fact]
    public void La_derniere_vue_retient_la_mesure_la_plus_recente()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 2000)]);
        db.Storage.Record([new Reading("28-aaaa", 20.0, 1000)]);

        Assert.Equal(2000, db.Storage.Sensors()[0].LastSeen);
    }

    [Fact]
    public void Les_bornes_de_plage_sont_incluses()
    {
        using var db = new TempDatabase();
        foreach (var ts in (long[])[100, 200, 300])
        {
            db.Storage.Record([new Reading("28-aaaa", 20.0, ts)]);
        }

        Assert.Equal(2, db.Storage.Series(200, 300)["28-aaaa"].Count);
        Assert.Empty(db.Storage.Series(0, 50));
    }

    [Fact]
    public void La_serie_se_filtre_par_adresse()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 100), new Reading("28-bbbb", 10.0, 100)]);

        var series = db.Storage.Series(0, 200, ["28-bbbb"]);
        Assert.Equal(["28-bbbb"], series.Keys);
    }

    [Fact]
    public void Le_regroupement_agrege_moyenne_extremes_et_effectif()
    {
        using var db = new TempDatabase();
        db.Storage.Record(
        [
            new Reading("28-aaaa", 20.0, 0),
            new Reading("28-aaaa", 22.0, 30),
            new Reading("28-aaaa", 30.0, 60),
        ]);

        var points = db.Storage.Series(0, 120, bucket: 60)["28-aaaa"];
        Assert.Equal(2, points.Count);

        var first = points[0];
        Assert.Equal(0, first.Ts);
        Assert.Equal(21.0, first.Celsius);
        Assert.Equal(20.0, first.Min);
        Assert.Equal(22.0, first.Max);
        Assert.Equal(2, first.Samples);
    }

    [Fact]
    public void Un_point_brut_ne_porte_qu_un_echantillon()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 10)]);

        var point = db.Storage.Series(0, 100, bucket: 0)["28-aaaa"][0];
        Assert.Equal(1, point.Samples);
        Assert.Equal(point.Min, point.Max);
    }

    [Fact]
    public void Le_resume_donne_min_max_moyenne_et_effectif()
    {
        using var db = new TempDatabase();
        foreach (var (ts, celsius) in ((long Ts, double C)[])[(10, 18.0), (20, 22.0), (30, 20.0)])
        {
            db.Storage.Record([new Reading("28-aaaa", celsius, ts)]);
        }

        var summary = db.Storage.Summary(0, 100)["28-aaaa"];
        Assert.Equal(18.0, summary.Min);
        Assert.Equal(22.0, summary.Max);
        Assert.Equal(20.0, summary.Avg);
        Assert.Equal(3, summary.Samples);
    }

    [Fact]
    public void La_derniere_mesure_est_bien_la_plus_recente()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 100), new Reading("28-aaaa", 25.0, 200)]);

        var latest = db.Storage.Latest()[0];
        Assert.Equal(25.0, latest.Celsius);
        Assert.Equal(200, latest.Ts);
    }

    [Fact]
    public void Un_capteur_sans_mesure_ressort_avec_des_valeurs_nulles()
    {
        using var db = new TempDatabase();
        db.Storage.SensorId("28-aaaa");

        var latest = Assert.Single(db.Storage.Latest());
        Assert.Null(latest.Celsius);
        Assert.Null(latest.Ts);
    }

    [Fact]
    public void Un_capteur_se_renomme_et_un_inconnu_ne_se_cree_pas()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 100)]);

        Assert.True(db.Storage.SetLabel("28-aaaa", "Salon"));
        Assert.Equal("Salon", db.Storage.Sensors()[0].Label);
        Assert.False(db.Storage.SetLabel("28-inconnu", "Cave"));
    }

    [Fact]
    public void La_purge_supprime_les_mesures_anterieures()
    {
        using var db = new TempDatabase();
        foreach (var ts in (long[])[100, 200, 300])
        {
            db.Storage.Record([new Reading("28-aaaa", 20.0, ts)]);
        }

        Assert.Equal(2, db.Storage.Prune(250));
        Assert.Equal(1, db.Storage.Stats().Readings);
    }

    [Fact]
    public void Une_base_vide_n_a_pas_de_plage()
    {
        using var db = new TempDatabase();
        Assert.Equal((null, null), db.Storage.TimeRange());
    }

    [Fact]
    public void Les_lignes_d_export_sont_triees_par_horodatage_puis_adresse()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-bbbb", 5.0, 200), new Reading("28-aaaa", 20.0, 100)]);

        var rows = new List<(long Ts, string Address)>();
        db.Storage.ForEachRow(0, 1000, null, row => rows.Add((row.Ts, row.Address)));

        Assert.Equal([(100L, "28-aaaa"), (200L, "28-bbbb")], rows);
    }

    [Fact]
    public void Les_donnees_survivent_a_une_reouverture()
    {
        using var db = new TempDatabase();
        db.Storage.Record([new Reading("28-aaaa", 20.0, 100)]);

        Assert.Equal(1, db.Reopen().Stats().Readings);
    }

    [Fact]
    public void Une_base_en_memoire_reste_vivante_entre_deux_operations()
    {
        // Sous pooling ADO.NET, une base « :memory: » privée disparaîtrait dès la
        // fermeture de la connexion — donc entre deux appels. Le cache partagé et la
        // connexion maintenue ouverte sont ce qui rend ce test possible.
        using var storage = new TempiStorage(":memory:");
        storage.Record([new Reading("28-aaaa", 20.0, 100)]);

        Assert.Equal(1, storage.Stats().Readings);
        Assert.Equal(":memory:", storage.Stats().DbPath);
        Assert.Equal(0, storage.Stats().DbBytes);
    }

    [Fact]
    public void Un_nom_pose_par_l_utilisateur_n_est_jamais_ecrase()
    {
        using var db = new TempDatabase();
        db.Storage.EnsureSensor("outdoor-metar-LFLY", "Extérieur");
        db.Storage.SetLabel("outdoor-metar-LFLY", "Jardin");

        // Au redémarrage, le collecteur extérieur rappelle EnsureSensor avec son
        // libellé par défaut : le renommage doit survivre.
        db.Storage.EnsureSensor("outdoor-metar-LFLY", "Extérieur");

        Assert.Equal("Jardin", db.Storage.Sensors()[0].Label);
    }
}

/// <summary>
/// Compatibilité avec la base produite par l'implémentation Python.
/// </summary>
/// <remarks>
/// C'est le seul contrat dont la rupture serait irréversible : une base existante
/// doit être lue sans conversion. Le test l'ouvre réellement plutôt que de comparer
/// des chaînes de DDL.
/// </remarks>
public sealed class ReferenceDatabaseTests
{
    private static string ReferencePath()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        return Path.Combine(directory, "tests", "golden", "reference.db");
    }

    /// <summary>Copie la base de référence : l'ouvrir crée des fichiers -wal à côté.</summary>
    private static string CopyToTemp()
    {
        var directory = Directory.CreateTempSubdirectory("tempi-ref-").FullName;
        var copy = Path.Combine(directory, "reference.db");
        File.Copy(ReferencePath(), copy);
        return copy;
    }

    [Fact]
    public void La_base_de_reference_s_ouvre_sans_conversion()
    {
        var path = CopyToTemp();
        using var storage = new TempiStorage(path);

        var stats = storage.Stats();
        Assert.Equal(5679, stats.Readings);
        Assert.Equal(4, stats.Sensors);
        Assert.Equal(1767225600, stats.FirstTs);
        Assert.Equal(1767398400, stats.LastTs);
    }

    [Fact]
    public void Le_schema_est_bien_en_version_1()
    {
        var path = CopyToTemp();
        using var storage = new TempiStorage(path);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        Assert.Equal("1", command.ExecuteScalar());
    }

    [Fact]
    public void Les_capteurs_et_leurs_libelles_sont_relus()
    {
        var path = CopyToTemp();
        using var storage = new TempiStorage(path);

        var sensors = storage.Sensors();
        Assert.Equal(
            ["28-000005e2fdc3", "28-000005e30a1b", "28-0000ffffffff", "outdoor-metar-LFLY"],
            sensors.Select(s => s.Address));

        Assert.Equal("Salon", sensors[0].Label);
        Assert.Equal("Extérieur", sensors[3].Label);

        // Le capteur connu sans aucune mesure doit ressortir avec des valeurs nulles.
        var orphan = storage.Latest().Single(l => l.Address == "28-0000ffffffff");
        Assert.Null(orphan.Ts);
        Assert.Null(orphan.Celsius);
    }
}

public sealed class BucketTests
{
    [Fact]
    public void Une_plage_courte_garde_une_resolution_fine()
    {
        Assert.True(Buckets.Choose(3600, 800) <= 5);
    }

    [Fact]
    public void Une_plage_longue_est_sous_echantillonnee()
    {
        var bucket = Buckets.Choose(30 * 86400.0, 800);
        Assert.True(bucket >= 3600);
        Assert.True(30 * 86400.0 / bucket <= 800 * 1.2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Une_plage_degeneree_ne_donne_aucun_regroupement(double span)
    {
        Assert.Equal(0, Buckets.Choose(span));
    }
}
