using System.Text.RegularExpressions;

namespace Tempi.Tests;

public sealed class VersionTests
{
    [Fact]
    public void La_version_est_celle_que_le_tag_de_release_devra_porter()
    {
        // release.yml refuse de publier un tag qui ne correspond pas à cette
        // constante : c'est ici, et nulle part ailleurs, que la version se change.
        // /api/health et « --version » l'exposent, et le golden master la compare.
        Assert.Equal("2.1.0", TempiVersion.Value);
    }

    /// <summary>
    /// Directory.Build.props porte sa propre propriété <c>Version</c>, qui alimente
    /// les métadonnées de l'assembly (FileVersion, etc.). Rien ne la synchronise
    /// automatiquement avec <see cref="TempiVersion.Value"/> — sans ce test, un oubli
    /// à la préparation d'une release ne serait détecté qu'à l'œil, et seulement en
    /// inspectant les métadonnées du binaire publié.
    /// </summary>
    [Fact]
    public void Directory_Build_props_porte_la_meme_version_que_TempiVersion()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        var props = File.ReadAllText(Path.Combine(directory, "Directory.Build.props"));
        var match = Regex.Match(props, @"<Version>([^<]+)</Version>");

        Assert.True(match.Success, "propriété <Version> introuvable dans Directory.Build.props");
        Assert.Equal(TempiVersion.Value, match.Groups[1].Value);
    }
}
