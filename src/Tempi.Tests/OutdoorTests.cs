using System.Text;
using Tempi.Configuration;
using Tempi.Outdoor;
using Tempi.Tests.Support;

namespace Tempi.Tests;

/// <summary>Réponses de référence, recopiées de <c>tests/test_outdoor.py</c>.</summary>
internal static class OutdoorPayloads
{
    public const string Metar = """
        [{"icaoId": "LFLY", "obsTime": 1700000000, "temp": 12.4, "dewp": 8.0,
          "rawOb": "LFLY 141200Z 27008KT 9999 FEW030 12/08 Q1018"}]
        """;

    public const string Infoclimat = """
        {"hourly": {"000JT": [
            {"dh_utc": "2024-01-14 11:00:00", "temperature": "4.8"},
            {"dh_utc": "2024-01-14 12:00:00", "temperature": "5.6"}]}}
        """;

    public const string OpenMeteo = """
        {"latitude": 45.75, "longitude": 4.85, "utc_offset_seconds": 0,
         "current_units": {"temperature_2m": "°C"},
         "current": {"time": "2024-01-14T12:00", "interval": 900, "temperature_2m": 5.9}}
        """;

    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
}

public sealed class MetarTests
{
    [Fact]
    public void La_temperature_et_l_instant_d_observation_sont_lus()
    {
        var observation = new MetarSource("lfly").Parse(OutdoorPayloads.Bytes(OutdoorPayloads.Metar));

        Assert.Equal(12.4, observation.Celsius, 6);
        Assert.Equal(1_700_000_000, observation.Ts);
        Assert.Equal("LFLY", observation.Station);
    }

    [Fact]
    public void Le_code_station_est_normalise_dans_l_URL_et_l_adresse()
    {
        var source = new MetarSource(" lfly ");
        Assert.Contains("ids=LFLY", source.Url(), StringComparison.Ordinal);
        Assert.Equal("outdoor-metar-LFLY", OutdoorSources.AddressFor(source));
    }

    [Fact]
    public void Le_rapport_le_plus_recent_est_retenu()
    {
        const string payload = """
            [{"icaoId": "LFLY", "obsTime": 1700000000, "temp": 12.4},
             {"icaoId": "LFLY", "obsTime": 1700003600, "temp": 13.1}]
            """;

        Assert.Equal(13.1, new MetarSource("LFLY").Parse(OutdoorPayloads.Bytes(payload)).Celsius, 6);
    }

    [Fact]
    public void Le_METAR_brut_sert_de_repli_quand_le_champ_manque()
    {
        const string payload = """
            [{"icaoId": "LFLY", "obsTime": 1700000000, "temp": null,
              "rawOb": "LFLY 141200Z 27008KT 9999 FEW030 M03/M07 Q1018"}]
            """;

        // « M » préfixe les valeurs négatives dans un METAR.
        Assert.Equal(-3.0, new MetarSource("LFLY").Parse(OutdoorPayloads.Bytes(payload)).Celsius, 6);
    }

    [Fact]
    public void Une_reponse_vide_est_une_erreur()
    {
        Assert.Throws<OutdoorException>(() => new MetarSource("LFLY").Parse("[]"u8.ToArray()));
    }

    [Fact]
    public void La_station_est_obligatoire()
    {
        Assert.Throws<OutdoorException>(() => new MetarSource(string.Empty));
    }
}

public sealed class InfoclimatTests
{
    [Fact]
    public void Le_releve_le_plus_recent_est_retenu()
    {
        var observation = new InfoclimatSource("000JT", "cle")
            .Parse(OutdoorPayloads.Bytes(OutdoorPayloads.Infoclimat));

        Assert.Equal(5.6, observation.Celsius, 6);
        Assert.Equal(1_705_233_600, observation.Ts);   // 2024-01-14 12:00:00 UTC
    }

    [Fact]
    public void L_URL_porte_la_cle_et_une_plage_explicite()
    {
        var url = new InfoclimatSource("000JT", "cle").Url();

        Assert.Contains("token=cle", url, StringComparison.Ordinal);
        // Les crochets doivent être encodés : l'API attend « stations[] ».
        Assert.Contains("stations%5B%5D=000JT", url, StringComparison.Ordinal);
        Assert.Contains("start=", url, StringComparison.Ordinal);
        Assert.Contains("end=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Le_message_d_erreur_de_l_API_est_remonte()
    {
        var error = Assert.Throws<OutdoorException>(
            () => new InfoclimatSource("000JT", "cle")
                .Parse(OutdoorPayloads.Bytes("""{"message": "Token invalide"}""")));

        Assert.Contains("Token invalide", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void La_cle_est_obligatoire()
    {
        Assert.Throws<OutdoorException>(() => new InfoclimatSource("000JT", string.Empty));
    }
}

public sealed class OpenMeteoTests
{
    [Fact]
    public void Le_bloc_courant_est_lu()
    {
        var observation = new OpenMeteoSource(45.75, 4.85)
            .Parse(OutdoorPayloads.Bytes(OutdoorPayloads.OpenMeteo));

        Assert.Equal(5.9, observation.Celsius, 6);
        Assert.Equal(1_705_233_600, observation.Ts);
    }

    [Fact]
    public void Une_heure_locale_est_ramenee_en_UTC()
    {
        const string payload = """
            {"latitude": 45.75, "longitude": 4.85, "utc_offset_seconds": 3600,
             "current": {"time": "2024-01-14T13:00", "temperature_2m": 5.9}}
            """;

        Assert.Equal(
            1_705_233_600,
            new OpenMeteoSource(45.75, 4.85).Parse(OutdoorPayloads.Bytes(payload)).Ts);
    }

    [Fact]
    public void L_adresse_encode_les_coordonnees()
    {
        Assert.Equal(
            "outdoor-open-meteo-45.7500_4.8500",
            OutdoorSources.AddressFor(new OpenMeteoSource(45.75, 4.85)));
    }

    [Fact]
    public void Les_coordonnees_sont_verifiees()
    {
        Assert.Throws<OutdoorException>(() => new OpenMeteoSource(91.0, 4.85));
    }
}

public sealed class OutdoorValueTests
{
    [Fact]
    public void Une_temperature_invraisemblable_est_rejetee()
    {
        // Une API renvoyant des millidegrés passerait sinon inaperçue.
        const string payload = """[{"icaoId": "LFLY", "obsTime": 1700000000, "temp": 12400}]""";
        Assert.Throws<OutdoorException>(
            () => new MetarSource("LFLY").Parse(OutdoorPayloads.Bytes(payload)));
    }

    [Fact]
    public void Une_reponse_non_JSON_est_signalee()
    {
        Assert.Throws<OutdoorException>(
            () => new MetarSource("LFLY").Parse("<html>503</html>"u8.ToArray()));
    }
}

public sealed class OutdoorSourceFactoryTests
{
    private static TempiConfig Config(
        string? provider = null,
        string? station = null,
        string? token = null,
        double? latitude = null,
        double? longitude = null) => new()
    {
        DbPath = ":memory:",
        W1Dir = "/nonexistent",
        OutdoorProvider = provider,
        OutdoorStation = station,
        OutdoorToken = token,
        OutdoorLatitude = latitude,
        OutdoorLongitude = longitude,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    public void Sans_fournisseur_il_n_y_a_pas_de_source(string? provider)
    {
        Assert.Null(OutdoorSources.Create(Config(provider)));
    }

    [Fact]
    public void Chaque_fournisseur_est_construit()
    {
        Assert.IsType<MetarSource>(OutdoorSources.Create(Config("metar", station: "LFLY")));
        Assert.IsType<InfoclimatSource>(
            OutdoorSources.Create(Config("infoclimat", station: "000JT", token: "cle")));
        Assert.IsType<OpenMeteoSource>(
            OutdoorSources.Create(Config("open-meteo", latitude: 45.75, longitude: 4.85)));
    }

    [Fact]
    public void Un_fournisseur_inconnu_est_rejete()
    {
        Assert.Throws<OutdoorException>(() => OutdoorSources.Create(Config("meteo-france")));
    }

    [Fact]
    public void Open_meteo_exige_des_coordonnees()
    {
        Assert.Throws<OutdoorException>(() => OutdoorSources.Create(Config("open-meteo")));
    }

    [Fact]
    public void Une_configuration_invalide_n_empeche_pas_le_demarrage()
    {
        // Sans capteur extérieur exploitable, la collecte des DS18B20 doit continuer :
        // la fabrique signale et rend null plutôt que de lever.
        using var db = new TempDatabase();
        Assert.Null(OutdoorFactory.TryCreate(Config("metar"), db.Storage));
    }

    [Fact]
    public void La_cadence_ne_descend_jamais_sous_le_plancher()
    {
        var config = Config("metar", station: "LFLY");
        config.OutdoorInterval = 1.0;

        Assert.True(OutdoorFactory.IntervalFor(config) >= TimeSpan.FromSeconds(60));
    }
}

public sealed class OutdoorPollerTests
{
    private static TempiConfig Config() => new()
    {
        DbPath = ":memory:",
        W1Dir = "/nonexistent",
        OutdoorProvider = "metar",
        OutdoorStation = "LFLY",
    };

    private static OutdoorPoller Poller(TempDatabase db, params object[] payloads) =>
        new(Config(), db.Storage, fetch: new FakeFetch(payloads));

    [Fact]
    public async Task La_mesure_est_enregistree_comme_un_capteur()
    {
        using var db = new TempDatabase();
        var reading = await Poller(db, OutdoorPayloads.Metar).PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reading);
        Assert.Equal("outdoor-metar-LFLY", reading.Value.Address);
        // L'horodatage est celui de l'observation, pas celui de la requête.
        Assert.Equal(1_700_000_000, reading.Value.Ts);

        var sensor = Assert.Single(db.Storage.Sensors());
        Assert.Equal("outdoor-metar-LFLY", sensor.Address);
        Assert.Equal("Extérieur", sensor.Label);
        Assert.Equal(1, sensor.Count);
    }

    [Fact]
    public async Task La_meme_observation_n_est_pas_enregistree_deux_fois()
    {
        using var db = new TempDatabase();
        var poller = Poller(db, OutdoorPayloads.Metar, OutdoorPayloads.Metar);

        Assert.NotNull(await poller.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Null(await poller.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, poller.Stored);
        Assert.Equal(2, poller.Polls);
    }

    [Fact]
    public async Task Une_observation_plus_recente_est_enregistree()
    {
        const string newer = """[{"icaoId": "LFLY", "obsTime": 1700003600, "temp": 13.1}]""";

        using var db = new TempDatabase();
        var poller = Poller(db, OutdoorPayloads.Metar, newer);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        var reading = await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reading);
        Assert.Equal(13.1, reading.Value.Celsius, 6);
        Assert.Equal(2, poller.Stored);
    }

    [Fact]
    public async Task Un_redemarrage_ne_rejoue_pas_une_observation_connue()
    {
        using var db = new TempDatabase();
        await Poller(db, OutdoorPayloads.Metar).PollOnceAsync(TestContext.Current.CancellationToken);

        // Un nouveau relevé relit ce que la base contient déjà.
        var second = Poller(db, OutdoorPayloads.Metar);
        Assert.Null(await second.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, second.Stored);
    }

    [Fact]
    public async Task Une_panne_reseau_est_avalee()
    {
        using var db = new TempDatabase();
        var poller = Poller(db, new OutdoorException("réseau injoignable"));

        Assert.Null(await poller.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, poller.Errors);
        Assert.Contains("réseau", poller.LastError!, StringComparison.Ordinal);
        // La panne ne doit rien écrire.
        Assert.Empty(db.Storage.Sensors());
    }

    [Fact]
    public async Task La_reprise_apres_panne_efface_la_derniere_erreur()
    {
        using var db = new TempDatabase();
        var poller = Poller(db, new OutdoorException("panne"), OutdoorPayloads.Metar);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(await poller.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Null(poller.LastError);
        Assert.Equal(1, poller.Stored);
    }

    [Fact]
    public async Task Un_nom_pose_a_la_main_survit_a_un_nouveau_releve()
    {
        using var db = new TempDatabase();
        await Poller(db, OutdoorPayloads.Metar).PollOnceAsync(TestContext.Current.CancellationToken);
        db.Storage.SetLabel("outdoor-metar-LFLY", "Jardin");

        const string newer = """
            [{"icaoId": "LFLY", "obsTime": 1700003600, "temp": 12.4}]
            """;
        await Poller(db, newer).PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jardin", db.Storage.Sensors()[0].Label);
    }

    [Fact]
    public async Task Le_pseudo_capteur_ressort_des_requetes_ordinaires()
    {
        // Sans quoi l'interface web et l'export CSV l'ignoreraient.
        using var db = new TempDatabase();
        await Poller(db, OutdoorPayloads.Metar).PollOnceAsync(TestContext.Current.CancellationToken);

        var series = db.Storage.Series(1_699_999_000, 1_700_001_000);
        Assert.Single(series["outdoor-metar-LFLY"]);
    }

    [Fact]
    public async Task L_observation_ne_passe_par_le_reseau_qu_une_fois_par_appel()
    {
        using var db = new TempDatabase();
        var fetch = new FakeFetch(OutdoorPayloads.Metar);
        var poller = new OutdoorPoller(Config(), db.Storage, fetch: fetch);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken);

        var url = Assert.Single(fetch.Calls);
        Assert.Contains("aviationweather.gov", url, StringComparison.Ordinal);
    }
}
