using System.Runtime.InteropServices;

namespace Tempi.Configuration;

/// <summary>Emplacements par défaut, et test d'accès en écriture à la manière d'<c>os.access</c>.</summary>
internal static partial class DefaultPaths
{
    /// <summary>Répertoire exposé par le noyau pour les périphériques 1-Wire.</summary>
    public const string DefaultW1Dir = "/sys/bus/w1/devices";

    /// <summary>Emplacement utilisé quand le service tourne en tant que démon système.</summary>
    public const string SystemDbPath = "/var/lib/tempi/tempi.db";

    private const int WOk = 2;

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);

    /// <summary>
    /// Équivalent de <c>os.access(path, os.W_OK)</c>.
    /// </summary>
    /// <remarks>
    /// Il n'existe pas d'équivalent managé exact : <c>File.GetUnixFileMode</c>
    /// obligerait à comparer l'uid, le gid et les groupes secondaires, et se
    /// tromperait dès qu'une ACL entre en jeu. On appelle donc <c>access(2)</c>,
    /// qui est la question réellement posée. Hors Linux, on retombe sur une
    /// approximation — le cas ne se présente pas en production.
    /// </remarks>
    public static bool IsWritable(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            return Access(path, WOk) == 0;
        }

        return Directory.Exists(path) || File.Exists(path);
    }

    /// <summary>
    /// Choisit une base de données par défaut utilisable sans configuration.
    /// </summary>
    /// <remarks>
    /// On privilégie l'emplacement système <c>/var/lib/tempi</c> quand il est
    /// accessible en écriture (cas du service systemd), sinon on retombe sur le
    /// répertoire de données de l'utilisateur.
    /// </remarks>
    public static string DefaultDbPath()
    {
        var systemDir = Path.GetDirectoryName(SystemDbPath)!;
        if (IsWritable(systemDir))
        {
            return SystemDbPath;
        }

        var parent = Path.GetDirectoryName(systemDir)!;
        if (IsWritable(parent) && !Directory.Exists(systemDir))
        {
            return SystemDbPath;
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg;
        return Path.Combine(root, "tempi", "tempi.db");
    }
}
