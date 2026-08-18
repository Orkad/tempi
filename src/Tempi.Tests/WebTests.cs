using System.Net;
using System.Text;
using System.Text.Json;
using Tempi.Configuration;
using Tempi.Storage;
using Tempi.Tests.Support;
using Tempi.Web;

namespace Tempi.Tests;

/// <summary>Fonctions pures d'analyse des paramètres — portage de <c>ParsingTests</c>.</summary>
public sealed class QueryParsingTests
{
    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("24h", 86400)]
    [InlineData("7d", 604800)]
    [InlineData("2w", 1209600)]
    [InlineData(" 1.5h ", 5400)]
    public void Les_durees_sont_analysees(string value, long expected)
    {
        Assert.Equal(expected, WindowResolverAccess.ParseDuration(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10")]
    [InlineData("5y")]
    [InlineData("-3h")]
    public void Une_duree_invalide_est_rejetee(string value)
    {
        Assert.Throws<BadRequestException>(() => WindowResolverAccess.ParseDuration(value));
    }

    [Theory]
    [InlineData("1700000000")]
    [InlineData("2023-11-14T22:13:20Z")]
    [InlineData("2023-11-14T22:13:20+00:00")]
    public void Les_horodatages_sont_analyses(string value)
    {
        Assert.Equal(1700000000, WindowResolverAccess.ParseTimestamp(value));
    }

    [Fact]
    public void Une_date_invalide_est_rejetee()
    {
        var error = Assert.Throws<BadRequestException>(() => WindowResolverAccess.ParseTimestamp("hier"));
        Assert.Equal("date invalide : 'hier'", error.Message);
    }

    [Fact]
    public void Le_message_de_duree_invalide_est_celui_du_Python()
    {
        var error = Assert.Throws<BadRequestException>(() => WindowResolverAccess.ParseDuration("demain"));
        Assert.Equal("durée invalide : 'demain' (exemples : 30m, 24h, 7d)", error.Message);
    }
}

/// <summary>Arrondi : la divergence la plus discrète du portage.</summary>
public sealed class PythonRoundingTests
{
    [Theory]
    // Deux cas où Math.Round(double, int) donne un résultat différent de Python.
    [InlineData(16.99375, 16.9937)]
    [InlineData(0.12345, 0.1235)]
    // Et quelques cas où les deux s'accordent, pour vérifier qu'on n'a rien cassé.
    [InlineData(21.40325, 21.4032)]
    [InlineData(1.00005, 1.0001)]
    [InlineData(22.8374, 22.8374)]
    public void L_arrondi_reproduit_celui_de_Python(double value, double expected)
    {
        Assert.Equal(expected, PythonRepr.Round(value, 4));
    }

    [Theory]
    [InlineData(5.0, "5.0")]
    [InlineData(-2.0, "-2.0")]
    [InlineData(21.493, "21.493")]
    public void Les_flottants_entiers_gardent_leur_decimale(double value, string expected)
    {
        // str(5.0) vaut « 5.0 » en Python : l'API et le CSV le reproduisent.
        Assert.Equal(expected, PythonRepr.Number(value));
    }
}

public sealed class WindowTests
{
    private static (long Start, long End) Resolve(TempiStorage storage, string query)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query);
        return WindowResolverAccess.Resolve(context.Request.Query, storage, TimeProvider.System);
    }

    [Fact]
    public void Sans_parametre_la_fenetre_couvre_les_dernieres_24_heures()
    {
        using var storage = new TempiStorage(":memory:");
        var (start, end) = Resolve(storage, string.Empty);
        Assert.InRange(end - start, 86398, 86402);
    }

    [Fact]
    public void Une_plage_relative_est_prise_en_compte()
    {
        using var storage = new TempiStorage(":memory:");
        var (start, end) = Resolve(storage, "?range=6h");
        Assert.InRange(end - start, 21598, 21602);
    }

    [Fact]
    public void Des_bornes_explicites_sont_reprises_telles_quelles()
    {
        using var storage = new TempiStorage(":memory:");
        Assert.Equal((100L, 200L), Resolve(storage, "?from=100&to=200"));
    }

    [Fact]
    public void La_plage_all_couvre_l_etendue_stockee()
    {
        using var storage = new TempiStorage(":memory:");
        storage.Record([
            new Sensors.Reading("28-aaaa", 20.0, 500),
            new Sensors.Reading("28-aaaa", 21.0, 900),
        ]);

        Assert.Equal((500L, 900L), Resolve(storage, "?range=all"));
    }

    [Fact]
    public void La_plage_all_sur_base_vide_retombe_sur_24_heures()
    {
        using var storage = new TempiStorage(":memory:");
        var (start, end) = Resolve(storage, "?range=all");
        Assert.InRange(end - start, 86398, 86402);
    }

    [Fact]
    public void Des_bornes_inversees_sont_rejetees()
    {
        using var storage = new TempiStorage(":memory:");
        var error = Assert.Throws<BadRequestException>(() => Resolve(storage, "?from=900&to=100"));
        Assert.Equal("'from' est postérieur à 'to'", error.Message);
    }

    [Fact]
    public void Un_parametre_vide_equivaut_a_un_parametre_absent()
    {
        // parse_qs élimine les valeurs vides avant que le routage ne les voie : « ?range= »
        // ne doit pas déclencher l'analyse d'une durée vide, donc pas de 400.
        using var storage = new TempiStorage(":memory:");
        var (start, end) = Resolve(storage, "?range=");
        Assert.InRange(end - start, 86398, 86402);
    }
}

[Collection(nameof(ApiCollection))]
public sealed class ApiTests(ApiFixture fixture)
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Get(string path)
    {
        var response = await fixture.Client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, body, response);
    }

    [Fact]
    public async Task L_interface_est_servie_a_la_racine()
    {
        var (status, body, response) = await Get("/");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Contains("<title>tempi", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task L_etat_du_service_est_publie()
    {
        var (_, body, _) = await Get("/api/health");
        var payload = Parse(body);

        Assert.Equal("ok", payload.GetProperty("status").GetString());
        Assert.Equal(2, payload.GetProperty("storage").GetProperty("sensors").GetInt32());
        Assert.Equal(120, payload.GetProperty("storage").GetProperty("readings").GetInt32());
        // Sans collecteur ni source extérieure, les blocs correspondants sont absents.
        Assert.False(payload.TryGetProperty("collector", out _));
        Assert.False(payload.TryGetProperty("outdoor", out _));
    }

    [Fact]
    public async Task Les_capteurs_sont_listes_tries_avec_leur_libelle()
    {
        var (_, body, _) = await Get("/api/sensors");
        var sensors = Parse(body).GetProperty("sensors");

        Assert.Equal("28-aaaa", sensors[0].GetProperty("address").GetString());
        Assert.Equal("Salon", sensors[0].GetProperty("label").GetString());
        Assert.Equal("28-bbbb", sensors[1].GetProperty("address").GetString());
    }

    [Fact]
    public async Task La_derniere_mesure_porte_l_horloge_du_serveur()
    {
        var (_, body, _) = await Get("/api/latest");
        var payload = Parse(body);

        Assert.InRange(payload.GetProperty("now").GetInt64(), fixture.Now - 5, fixture.Now + 60);
        Assert.Equal(20.0, payload.GetProperty("sensors")[0].GetProperty("celsius").GetDouble());
    }

    [Fact]
    public async Task La_serie_rend_les_deux_capteurs_avec_leur_libelle()
    {
        var (_, body, _) = await Get("/api/series?range=6h");
        var series = Parse(body).GetProperty("series");

        Assert.Equal(2, series.GetArrayLength());
        Assert.Equal("Salon", series[0].GetProperty("label").GetString());
        Assert.True(series[0].GetProperty("points").GetArrayLength() > 0);
    }

    [Fact]
    public async Task La_serie_se_filtre_par_capteur()
    {
        var (_, body, _) = await Get("/api/series?range=6h&sensor=28-bbbb");
        Assert.Equal(1, Parse(body).GetProperty("series").GetArrayLength());
    }

    [Fact]
    public async Task Le_mode_brut_rend_les_points_un_a_un()
    {
        var (_, body, _) = await Get("/api/series?range=6h&bucket=raw");
        var payload = Parse(body);

        Assert.Equal(0, payload.GetProperty("bucket").GetInt32());
        Assert.Equal(60, payload.GetProperty("series")[0].GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task Un_regroupement_explicite_agrege()
    {
        var (_, body, _) = await Get("/api/series?range=6h&bucket=3600");
        var payload = Parse(body);

        Assert.Equal(3600, payload.GetProperty("bucket").GetInt32());
        var points = payload.GetProperty("series")[0].GetProperty("points");
        Assert.True(points.GetArrayLength() < 60);
        Assert.Contains(
            points.EnumerateArray(),
            p => p.GetProperty("samples").GetInt32() > 1);
    }

    [Fact]
    public async Task Le_regroupement_accepte_une_duree()
    {
        var (_, body, _) = await Get("/api/series?range=6h&bucket=10m");
        Assert.Equal(600, Parse(body).GetProperty("bucket").GetInt32());
    }

    [Fact]
    public async Task Le_resume_donne_les_extremes()
    {
        var (_, body, _) = await Get("/api/summary?range=6h");
        var entry = Parse(body).GetProperty("summary").GetProperty("28-bbbb");

        Assert.Equal(5.0, entry.GetProperty("min").GetDouble());
        Assert.Equal(5.0, entry.GetProperty("max").GetDouble());
        Assert.Equal(60, entry.GetProperty("samples").GetInt32());
    }

    [Fact]
    public async Task L_export_CSV_porte_son_en_tete_et_ses_lignes()
    {
        var response = await fixture.Client.GetAsync(
            "/api/export.csv?range=6h&sensor=28-bbbb", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/csv", response.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            "attachment; filename=\"tempi-export.csv\"",
            response.Content.Headers.ContentDisposition!.ToString());

        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CsvExport.Header, lines[0]);
        Assert.Equal(61, lines.Length);
    }

    [Fact]
    public async Task L_export_est_la_seule_reponse_sans_Cache_Control()
    {
        // Python écrit les en-têtes de l'export à la main et n'y pose pas cet en-tête ;
        // toutes les autres réponses le portent, et l'interface en dépend.
        var export = await fixture.Client.GetAsync(
            "/api/export.csv?range=6h", TestContext.Current.CancellationToken);
        var latest = await fixture.Client.GetAsync("/api/latest", TestContext.Current.CancellationToken);

        Assert.Empty(export.Headers.CacheControl?.ToString() ?? string.Empty);
        Assert.Equal("no-store", latest.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Une_requete_invalide_donne_400_et_un_objet_erreur()
    {
        var (status, body, _) = await Get("/api/series?range=demain");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(
            "durée invalide : 'demain' (exemples : 30m, 24h, 7d)",
            Parse(body).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Une_route_inconnue_donne_404()
    {
        var (status, body, _) = await Get("/api/inexistant");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("route inconnue : /api/inexistant", Parse(body).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Un_slash_final_ne_change_pas_la_route()
    {
        var (status, _, _) = await Get("/api/sensors/");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task HEAD_rend_les_memes_en_tetes_que_GET_sans_corps()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "/api/latest");
        var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Empty(body);
    }

    [Fact]
    public async Task Un_capteur_se_renomme_puis_se_deshabille()
    {
        using var content = new StringContent(
            """{"label":"Congélateur"}""", Encoding.UTF8, "application/json");
        var response = await fixture.Client.PostAsync(
            "/api/sensors/28-bbbb/label", content, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Congélateur", Parse(body).GetProperty("label").GetString());

        // Remise en état : la collection partage le serveur entre tous les tests.
        fixture.Storage.SetLabel("28-bbbb", null);
    }

    [Fact]
    public async Task Renommer_un_capteur_inconnu_donne_404()
    {
        using var content = new StringContent("""{"label":"X"}""", Encoding.UTF8, "application/json");
        var response = await fixture.Client.PostAsync(
            "/api/sensors/28-zzzz/label", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Un_POST_vers_une_route_de_lecture_donne_404_et_non_405()
    {
        // Python ne connaît pas la notion de méthode non autorisée : toute route qui
        // ne correspond pas est une route inconnue.
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await fixture.Client.PostAsync(
            "/api/health", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
