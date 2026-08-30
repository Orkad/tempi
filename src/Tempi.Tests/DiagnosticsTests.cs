using Tempi.Diagnostics;

namespace Tempi.Tests;

/// <summary>
/// Analyse du bus 1-Wire. Les cas de panne reproduisent des signatures relevées sur
/// un montage réel — portage de <c>tests/test_diagnostics.py</c>.
/// </summary>
public sealed class OverlayTests
{
    [Theory]
    [InlineData("dtparam=audio=on\n", false, 4)]
    [InlineData("dtparam=audio=on\ndtoverlay=w1-gpio\n", true, 4)]
    [InlineData("dtoverlay=w1-gpio,gpiopin=17\n", true, 17)]
    [InlineData("#dtoverlay=w1-gpio\n", false, 4)]
    [InlineData("  dtoverlay = w1-gpio,gpiopin = 22 \n", true, 22)]
    [InlineData("dtoverlay=w1-gpio-pullup\n", true, 4)]
    [InlineData("dtoverlay=w1-gpio-pullup,gpiopin=17\n", true, 17)]
    [InlineData("", false, 4)]
    public void L_overlay_est_reconnu(string text, bool enabled, int gpio)
    {
        Assert.Equal((enabled, gpio), BusDiagnostics.ParseOverlay(text));
    }

    [Fact]
    public void Un_autre_overlay_partageant_le_prefixe_n_est_pas_reconnu()
    {
        // Le tiret est une frontière de mot : sans le groupe (,|$) de l'expression
        // régulière, « w1-gpio-something-else » serait accepté à tort.
        Assert.Equal((false, 4), BusDiagnostics.ParseOverlay("dtoverlay=w1-gpio-something-else\n"));
    }
}

public sealed class ModulesTests
{
    [Fact]
    public void Les_noms_de_modules_sont_extraits()
    {
        const string proc = "w1_therm 32768 0 - Live 0x0\nw1_gpio 16384 0 - Live 0x0\ncfg80211 1044480 1\n";
        Assert.Equal(
            new HashSet<string> { "w1_therm", "w1_gpio", "cfg80211" },
            BusDiagnostics.ParseModules(proc));
    }

    [Fact]
    public void Un_contenu_vide_donne_un_ensemble_vide()
    {
        Assert.Empty(BusDiagnostics.ParseModules(string.Empty));
    }
}

public sealed class PinctrlTests
{
    [Theory]
    [InlineData("4: ip    pu | lo // GPIO4 = input", "ip pu", "lo")]
    [InlineData("4: ip    pn | hi // GPIO4 = input", "ip pn", "hi")]
    [InlineData("GPIO 4: level=0 func=OUTPUT pull=NONE", "output", "lo")]
    [InlineData("GPIO 4: level=1 func=INPUT pull=UP", "input", "hi")]
    public void Les_deux_formats_sont_analyses(string output, string function, string level)
    {
        Assert.Equal((function, level), BusDiagnostics.ParsePinctrl(output));
    }

    [Fact]
    public void Une_sortie_inanalysable_ne_donne_rien()
    {
        Assert.Null(BusDiagnostics.ParsePinctrl("commande introuvable"));
    }
}

public sealed class ClassifyTests
{
    [Fact]
    public void Le_contenu_du_bus_est_range_par_nature()
    {
        var inventory = BusDiagnostics.ClassifyDevices(
            ["28-000005e2fdc3", "00-800000000000", "w1_bus_master1", "10-000801f2ab34"]);

        Assert.Equal(["10-000801f2ab34", "28-000005e2fdc3"], inventory.Sensors);
        Assert.Equal(["00-800000000000"], inventory.Phantoms);
        Assert.Equal(["w1_bus_master1"], inventory.Masters);
    }

    [Fact]
    public void Une_liste_vide_donne_un_inventaire_vide()
    {
        var inventory = BusDiagnostics.ClassifyDevices([]);
        Assert.Empty(inventory.Sensors);
        Assert.Empty(inventory.Phantoms);
        Assert.Empty(inventory.Masters);
    }

    [Fact]
    public void Une_famille_inconnue_n_est_ni_capteur_ni_fantome()
    {
        var inventory = BusDiagnostics.ClassifyDevices(["81-0000abcdef01"]);
        Assert.Empty(inventory.Sensors);
        Assert.Empty(inventory.Phantoms);
    }
}

public sealed class DiagnoseBusTests
{
    private static BusInventory Devices(params string[] names) => BusDiagnostics.ClassifyDevices(names);

    [Fact]
    public void Un_bus_sain_est_reconnu()
    {
        var check = BusDiagnostics.DiagnoseBus(Devices("28-000005e2fdc3", "w1_bus_master1"));
        Assert.True(check.Ok);
        Assert.Contains("28-000005e2fdc3", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Un_capteur_present_sur_un_bus_bruite_reste_un_succes()
    {
        var check = BusDiagnostics.DiagnoseBus(
            Devices("28-000005e2fdc3", "00-1f8000000000", "w1_bus_master1"));

        Assert.True(check.Ok);
        Assert.Contains("parasite", check.Detail, StringComparison.Ordinal);
        Assert.Contains("2,2 kΩ", check.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void Sans_aucun_maitre_le_bus_n_est_pas_monte()
    {
        var check = BusDiagnostics.DiagnoseBus(Devices());
        Assert.False(check.Ok);
        Assert.True(check.Critical);
        Assert.Contains("overlay", check.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void Un_bus_actif_mais_vide_est_signale()
    {
        var check = BusDiagnostics.DiagnoseBus(Devices("w1_bus_master1"));
        Assert.False(check.Ok);
        Assert.Contains("ne voit rien", check.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void La_signature_de_la_ligne_a_la_masse_est_reconnue()
    {
        var check = BusDiagnostics.DiagnoseBus(Devices("00-800000000000", "w1_bus_master1"));
        Assert.False(check.Ok);
        Assert.True(check.Critical);
        Assert.Contains("masse", check.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void Des_ROM_changeantes_signalent_une_ligne_flottante()
    {
        var first = Devices("00-1f8000000000", "00-6f8000000000", "w1_bus_master1");
        var second = Devices("00-ef8000000000", "00-3a8000000000", "w1_bus_master1");

        var check = BusDiagnostics.DiagnoseBus(first, second);
        Assert.False(check.Ok);
        Assert.Contains("flottante", check.Remedy, StringComparison.Ordinal);
        Assert.Contains("4,7 kΩ", check.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void Des_fantomes_stables_au_niveau_bas_signalent_un_court_circuit()
    {
        var first = Devices("00-1f8000000000", "w1_bus_master1");
        var check = BusDiagnostics.DiagnoseBus(first, first, gpioLevel: "lo");

        Assert.False(check.Ok);
        Assert.Contains("masse", check.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void Le_niveau_bas_sous_tirage_interne_prime_sur_des_ROM_changeantes()
    {
        // Cas relevé sur le montage réel : les ROM changeaient d'un balayage à
        // l'autre, ce qui évoque une ligne flottante, mais la ligne restait basse
        // malgré le tirage interne. Le niveau électrique prime.
        var first = Devices("00-1f8000000000", "00-6f8000000000", "w1_bus_master1");
        var second = Devices("00-ef8000000000", "w1_bus_master1");

        var check = BusDiagnostics.DiagnoseBus(first, second, gpioLevel: "lo", gpioFunction: "ip pu");
        Assert.False(check.Ok);
        Assert.Contains("masse", check.Remedy, StringComparison.Ordinal);
        Assert.Contains("malgré le tirage interne", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Sans_preuve_de_tirage_des_ROM_changeantes_restent_une_ligne_flottante()
    {
        var first = Devices("00-1f8000000000", "w1_bus_master1");
        var second = Devices("00-ef8000000000", "w1_bus_master1");

        var check = BusDiagnostics.DiagnoseBus(first, second, gpioLevel: "lo", gpioFunction: "ip pn");
        Assert.Contains("flottante", check.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void Sans_second_balayage_le_diagnostic_reste_generique()
    {
        var check = BusDiagnostics.DiagnoseBus(Devices("00-1f8000000000", "w1_bus_master1"));
        Assert.False(check.Ok);
        Assert.Contains("tirage", check.Remedy, StringComparison.Ordinal);
    }
}

public sealed class HoldsLowTests
{
    [Theory]
    [InlineData("lo", "ip pu")]
    [InlineData("lo", "input pull=UP")]
    public void Une_ligne_basse_sous_tirage_interne_est_tenue_bas(string level, string function)
    {
        Assert.True(BusDiagnostics.HoldsLow(level, function));
    }

    [Theory]
    [InlineData("lo", "ip pn")]
    [InlineData("lo", null)]
    [InlineData("hi", "ip pu")]
    public void Les_autres_combinaisons_ne_prouvent_rien(string level, string? function)
    {
        Assert.False(BusDiagnostics.HoldsLow(level, function));
    }

    [Fact]
    public void Le_champ_pu_doit_etre_entier()
    {
        // « pull=none » ne doit pas être pris pour un tirage actif.
        Assert.False(BusDiagnostics.HoldsLow("lo", "input pull=NONE"));
    }
}

public sealed class DiagnoseGpioTests
{
    [Fact]
    public void Un_niveau_haut_est_sain()
    {
        Assert.True(BusDiagnostics.DiagnoseGpio("hi", "ip pu").Ok);
    }

    [Fact]
    public void Un_niveau_bas_en_entree_est_un_defaut()
    {
        var check = BusDiagnostics.DiagnoseGpio("lo", "ip pu");
        Assert.False(check.Ok);
        Assert.True(check.Critical);
    }

    [Fact]
    public void Un_niveau_bas_sur_une_ligne_pilotee_n_est_pas_concluant()
    {
        // Le pilote 1-Wire tire la ligne pendant une transaction : ce n'est pas
        // l'état au repos, on ne peut rien en conclure.
        Assert.Null(BusDiagnostics.DiagnoseGpio("lo", "OUTPUT").Ok);
    }

    [Fact]
    public void Sans_outil_de_lecture_la_verification_n_est_pas_menee()
    {
        Assert.Null(BusDiagnostics.DiagnoseGpio(null, null).Ok);
    }
}

public sealed class SummariseTests
{
    [Fact]
    public void Tout_bon_donne_le_message_court()
    {
        var (ok, message) = BusDiagnostics.Summarise(
            [new Check("a", true, "ok"), new Check("b", true, "ok")]);

        Assert.True(ok);
        Assert.Equal("Tout est en ordre.", message);
    }

    [Fact]
    public void Un_indetermine_n_est_pas_un_echec()
    {
        var (ok, message) = BusDiagnostics.Summarise(
            [new Check("a", true, "ok"), new Check("b", null, "inconnu")]);

        Assert.True(ok);
        Assert.Contains("non concluante", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Le_premier_echec_critique_est_rapporte_en_priorite()
    {
        Check[] checks =
        [
            new("mineur", false, "détail mineur", "remède mineur"),
            new("majeur", false, "détail majeur", "remède majeur", Critical: true),
        ];

        var (ok, message) = BusDiagnostics.Summarise(checks);
        Assert.False(ok);
        Assert.Contains("détail majeur", message, StringComparison.Ordinal);
        Assert.DoesNotContain("mineur", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_defaut_de_critique_le_premier_echec_est_rapporte()
    {
        var (ok, message) = BusDiagnostics.Summarise([new Check("seul", false, "détail", "remède")]);
        Assert.False(ok);
        Assert.Contains("détail", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Un_remede_vide_ne_laisse_pas_de_ligne_en_trop()
    {
        var (_, message) = BusDiagnostics.Summarise([new Check("seul", false, "détail")]);
        Assert.Equal("seul : détail", message);
    }
}
