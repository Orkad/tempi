namespace Tempi.Tests;

public sealed class VersionTests
{
    [Fact]
    public void La_version_est_celle_que_le_tag_de_release_devra_porter()
    {
        // release.yml refuse de publier un tag qui ne correspond pas à cette
        // constante : c'est ici, et nulle part ailleurs, que la version se change.
        // /api/health et « --version » l'exposent, et le golden master la compare.
        Assert.Equal("2.0.0", TempiVersion.Value);
    }
}
