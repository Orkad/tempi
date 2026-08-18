namespace Tempi.Tests;

public sealed class VersionTests
{
    [Fact]
    public void La_version_correspond_a_celle_du_paquet_Python()
    {
        // tempi/__init__.py porte __version__ = "1.0.0". Les deux doivent rester
        // alignées tant que les deux implémentations cohabitent : /api/health et
        // « --version » l'exposent, et le golden master la compare.
        Assert.Equal("1.0.0", TempiVersion.Value);
    }
}
