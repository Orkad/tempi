using Tempi.Cli;

namespace Tempi.Tests;

/// <summary>Mises en forme de la console : peu de code, mais des règles arbitraires.</summary>
public sealed class FormattingTests
{
    [Theory]
    // En dessous du kibioctet, la valeur est entière — « 512 o » et non « 512.0 o ».
    [InlineData(0, "0 o")]
    [InlineData(512, "512 o")]
    [InlineData(1023, "1023 o")]
    [InlineData(1024, "1.0 Kio")]
    [InlineData(200704, "196.0 Kio")]
    [InlineData(2516582, "2.4 Mio")]
    public void Les_tailles_suivent_les_unites_binaires_francaises(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.Size(bytes));
    }

    [Fact]
    public void Un_horodatage_absent_donne_un_tiret_cadratin()
    {
        Assert.Equal("—", Formatting.Timestamp(null));
    }

    [Theory]
    [InlineData("28-aaaa", null, "28-aaaa")]
    [InlineData("28-aaaa", "", "28-aaaa")]
    [InlineData("28-aaaa", "Salon", "28-aaaa « Salon »")]
    public void Le_libelle_est_encadre_de_guillemets_francais(string address, string? label, string expected)
    {
        Assert.Equal(expected, Formatting.WithLabel(address, label));
    }

    [Theory]
    [InlineData(21.5, "  21.5 °C")]
    [InlineData(-5.25, "  -5.2 °C")]
    [InlineData(100.0, " 100.0 °C")]
    public void La_temperature_est_alignee_sur_six_colonnes(double value, string expected)
    {
        Assert.Equal(expected, Formatting.Celsius(value));
    }
}
