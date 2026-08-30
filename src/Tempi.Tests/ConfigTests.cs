using Tempi.Configuration;
using Tempi.Tests.Support;

namespace Tempi.Tests;

/// <summary>
/// Lecture de l'environnement et validation.
/// </summary>
/// <remarks>
/// Le pendant Python de ces vérifications est dispersé : <c>ConfigValidationTests</c>
/// dans <c>test_outdoor.py</c> couvre la source extérieure, le reste n'était couvert
/// qu'indirectement. Le portage est l'occasion de le rassembler, d'autant que
/// l'analyse des nombres est ici sensible à la culture — un piège que Python n'a pas.
/// </remarks>
public sealed class ConfigTests
{
    private static TempiConfig Valid() => new()
    {
        DbPath = ":memory:",
        W1Dir = "/nonexistent",
    };

    [Fact]
    public void Sans_environnement_les_defauts_sont_ceux_du_Python()
    {
        using var _ = new EnvScope();
        var config = TempiConfig.FromEnvironment();

        Assert.Equal("/sys/bus/w1/devices", config.W1Dir);
        Assert.False(config.Simulate);
        Assert.Equal(3, config.ReadRetries);
        Assert.False(config.AllowResetValue);
        Assert.Equal(60.0, config.Interval);
        Assert.Equal(0.0, config.MinDelta);
        Assert.Equal(900.0, config.MaxInterval);
        Assert.Equal(0, config.RetentionDays);
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(8080, config.Port);
        Assert.Null(config.OutdoorProvider);
        Assert.Equal("Extérieur", config.OutdoorLabel);
        Assert.Equal(600.0, config.OutdoorInterval);
        Assert.Equal(10.0, config.OutdoorTimeout);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("oui")]
    [InlineData("  Oui  ")]
    public void Les_booleens_vrais_sont_ceux_de_la_liste(string raw)
    {
        using var _ = new EnvScope(("TEMPI_SIMULATE", raw));
        Assert.True(TempiConfig.FromEnvironment().Simulate);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("non")]
    [InlineData("n'importe quoi")]
    public void Toute_autre_valeur_booleenne_est_fausse_sans_erreur(string raw)
    {
        using var _ = new EnvScope(("TEMPI_SIMULATE", raw));
        Assert.False(TempiConfig.FromEnvironment().Simulate);
    }

    [Fact]
    public void Une_variable_vide_equivaut_a_une_variable_absente()
    {
        using var _ = new EnvScope(
            ("TEMPI_HOST", string.Empty),
            ("TEMPI_INTERVAL", string.Empty),
            ("TEMPI_OUTDOOR_LAT", string.Empty));

        var config = TempiConfig.FromEnvironment();
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(60.0, config.Interval);
        Assert.Null(config.OutdoorLatitude);
    }

    [Fact]
    public void Les_nombres_sont_lus_en_culture_invariante()
    {
        // Sur un Raspberry Pi en fr_FR, une lecture sensible à la culture attendrait
        // « 0,5 » et rejetterait la valeur documentée dans tempi.env.example.
        using var _ = new EnvScope(("TEMPI_MIN_DELTA", "0.5"));
        Assert.Equal(0.5, TempiConfig.FromEnvironment().MinDelta);
    }

    [Fact]
    public void Un_nombre_illisible_est_signale_avec_la_valeur_fautive()
    {
        using var _ = new EnvScope(("TEMPI_INTERVAL", "beaucoup"));
        var error = Assert.Throws<ConfigException>(TempiConfig.FromEnvironment);
        Assert.Equal("TEMPI_INTERVAL doit être un nombre, reçu 'beaucoup'", error.Message);
    }

    [Fact]
    public void Un_entier_illisible_est_signale_avec_la_valeur_fautive()
    {
        using var _ = new EnvScope(("TEMPI_PORT", "8080.5"));
        var error = Assert.Throws<ConfigException>(TempiConfig.FromEnvironment);
        Assert.Equal("TEMPI_PORT doit être un entier, reçu '8080.5'", error.Message);
    }

    [Theory]
    [InlineData(0.5, "l'intervalle de collecte doit valoir au moins 1 seconde")]
    public void Un_intervalle_trop_court_est_refuse(double interval, string message)
    {
        var config = Valid();
        config.Interval = interval;
        Assert.Equal(message, Assert.Throws<ConfigException>(config.Validate).Message);
    }

    [Fact]
    public void Les_bornes_simples_sont_verifiees()
    {
        var retries = Valid();
        retries.ReadRetries = 0;
        Assert.Equal(
            "le nombre de tentatives de lecture doit valoir au moins 1",
            Assert.Throws<ConfigException>(retries.Validate).Message);

        var delta = Valid();
        delta.MinDelta = -1;
        Assert.Equal("min-delta ne peut pas être négatif", Assert.Throws<ConfigException>(delta.Validate).Message);

        var retention = Valid();
        retention.RetentionDays = -1;
        Assert.Equal("la rétention ne peut pas être négative", Assert.Throws<ConfigException>(retention.Validate).Message);

        var port = Valid();
        port.Port = 70000;
        Assert.Equal("le port doit être compris entre 1 et 65535", Assert.Throws<ConfigException>(port.Validate).Message);
    }

    [Fact]
    public void Une_configuration_exterieure_correcte_passe()
    {
        var config = Valid();
        config.OutdoorProvider = "metar";
        config.OutdoorStation = "LFLY";
        config.Validate();
    }

    [Fact]
    public void Une_faute_de_frappe_sur_le_fournisseur_arrete_le_demarrage()
    {
        // Le comportement voulu est d'échouer, pas de désactiver silencieusement la
        // source : une source extérieure qu'on croit active et qui ne l'est pas ne
        // se remarque qu'au bout de plusieurs jours de courbe manquante.
        var config = Valid();
        config.OutdoorProvider = "metaar";
        Assert.Equal(
            "fournisseur extérieur inconnu : 'metaar' (attendu : metar, infoclimat, open-meteo)",
            Assert.Throws<ConfigException>(config.Validate).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("  NONE  ")]
    public void Sans_fournisseur_les_controles_exterieurs_sont_sautes(string? provider)
    {
        var config = Valid();
        config.OutdoorProvider = provider;
        config.OutdoorInterval = 5;   // invalide, mais jamais atteint
        config.OutdoorTimeout = -1;
        config.Validate();
    }

    [Fact]
    public void Un_intervalle_exterieur_trop_court_est_refuse()
    {
        var config = Valid();
        config.OutdoorProvider = "open-meteo";
        config.OutdoorInterval = 5;
        Assert.Equal(
            "l'intervalle extérieur doit valoir au moins 60 secondes",
            Assert.Throws<ConfigException>(config.Validate).Message);
    }

    [Fact]
    public void Un_delai_exterieur_nul_est_refuse()
    {
        var config = Valid();
        config.OutdoorProvider = "open-meteo";
        config.OutdoorTimeout = 0;
        Assert.Equal(
            "le délai d'attente extérieur doit être positif",
            Assert.Throws<ConfigException>(config.Validate).Message);
    }
}
