using System.Text.RegularExpressions;

namespace Tempi.Tests;

public sealed class VersionTests
{
    /// <summary>
    /// continuous-release.yml monte cette constante à chaque push sur main — un
    /// littéral figé ici devrait être réédité à chaque publication, ce qui irait à
    /// l'encontre de l'automatisation. Seul le format compte : c'est lui que
    /// s'appuient release.yml et l'incrément de patch automatique.
    /// </summary>
    [Fact]
    public void La_version_est_un_gabarit_SemVer_MAJOR_MINOR_PATCH()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", TempiVersion.Value);
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
