using System.Globalization;
using Microsoft.Data.Sqlite;
using Tempi.Configuration;
using Tempi.Sensors;

namespace Tempi.Storage;

/// <summary>
/// Accès à la base de mesures.
/// </summary>
/// <remarks>
/// <para>
/// Python ouvrait une connexion par thread : SQLite interdit de partager une
/// connexion entre threads, et le serveur web en utilise plusieurs. En ADO.NET le
/// problème ne se pose pas de la même façon — le pooling de Microsoft.Data.Sqlite
/// recycle les handles natifs — donc on ouvre une connexion par opération. C'est ce
/// qui rend cette classe utilisable en singleton sans affinité de thread, là où un
/// équivalent de <c>threading.local</c> fuirait une connexion par thread du pool
/// Kestrel, sans jamais la fermer.
/// </para>
/// <para>
/// Les écritures restent sérialisées par un verrou de processus. WAL et
/// <c>BEGIN IMMEDIATE</c> suffiraient en théorie, mais le verrou préserve la
/// sémantique Python à l'identique et évite des réessais inutiles : il n'y a qu'un
/// seul écrivain régulier, le collecteur.
/// </para>
/// </remarks>
public sealed class TempiStorage : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;
    private readonly Lock _writeGate = new();
    private readonly TimeProvider _time;

    public TempiStorage(string path, TimeProvider? time = null)
    {
        Path = path;
        _time = time ?? TimeProvider.System;

        if (IsMemory)
        {
            // Une base en mémoire privée disparaîtrait à la fermeture de sa
            // connexion, donc entre deux opérations. Le cache partagé la rend
            // visible à toutes les connexions, et une connexion maintenue ouverte
            // la garde vivante — c'est le même cas particulier que le « _shared »
            // de storage.py, pour une raison différente. Le nom est unique par
            // instance, sinon deux tests parallèles partageraient la même base.
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"tempi-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                ForeignKeys = true,
                DefaultTimeout = 15,
            }.ToString();

            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        else
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                ForeignKeys = true,
                DefaultTimeout = 15,
            }.ToString();
        }

        InitSchema();
    }

    /// <summary>Chemin tel qu'il a été demandé, y compris la valeur littérale <c>:memory:</c>.</summary>
    public string Path { get; }

    private bool IsMemory => Path == ":memory:";

    // -- connexions ---------------------------------------------------------

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // « Default Timeout » de Microsoft.Data.Sqlite est une boucle de réessai
        // gérée côté managé, pas le busy handler natif. Poser aussi le PRAGMA
        // couvre le cas où le verrou est pris pendant une instruction déjà démarrée.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    // -- schéma -------------------------------------------------------------

    private void InitSchema()
    {
        lock (_writeGate)
        {
            using var connection = Open();

            // Le mode WAL est inscrit dans l'en-tête du fichier : une seule fois suffit.
            using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=WAL";
                journal.ExecuteScalar();
            }

            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = Schema.Ddl;
                ddl.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
            var stored = read.ExecuteScalar() as string;

            if (stored is null)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO meta (key, value) VALUES ('schema_version', $v)";
                insert.Parameters.AddWithValue("$v", Schema.Version.ToString(CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }
            else if (int.Parse(stored, CultureInfo.InvariantCulture) > Schema.Version)
            {
                throw new InvalidOperationException(
                    $"la base {Path} utilise le schéma v{stored}, "
                    + $"plus récent que celui géré par cette version (v{Schema.Version})");
            }
        }
    }

    // -- capteurs -----------------------------------------------------------

    /// <summary>Retourne l'identifiant interne d'un capteur, en le créant au besoin.</summary>
    public long? SensorId(string address, bool create = true)
    {
        using var connection = Open();
        return SensorId(connection, null, address, create);
    }

    private long? SensorId(SqliteConnection connection, SqliteTransaction? tx, string address, bool create)
    {
        using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT id FROM sensors WHERE address = $a";
            select.Parameters.AddWithValue("$a", address);
            if (select.ExecuteScalar() is long existing)
            {
                return existing;
            }
        }

        if (!create)
        {
            return null;
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO sensors (address, first_seen) VALUES ($a, $t)";
            insert.Parameters.AddWithValue("$a", address);
            insert.Parameters.AddWithValue("$t", _time.GetUtcNow().ToUnixTimeSeconds());
            insert.ExecuteNonQuery();
        }

        using var again = connection.CreateCommand();
        again.Transaction = tx;
        again.CommandText = "SELECT id FROM sensors WHERE address = $a";
        again.Parameters.AddWithValue("$a", address);
        return again.ExecuteScalar() as long?;
    }

    /// <summary>
    /// Crée le capteur s'il est inconnu et lui donne un nom, sans jamais écraser
    /// celui qu'un utilisateur aurait posé.
    /// </summary>
    /// <remarks>
    /// Le <c>AND label IS NULL</c> est ce qui fait qu'un capteur renommé depuis
    /// l'interface web ne retrouve pas son libellé d'origine au redémarrage.
    /// </remarks>
    public long? EnsureSensor(string address, string? label = null)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            var id = SensorId(connection, null, address, create: true);

            if (!string.IsNullOrEmpty(label))
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE sensors SET label = $l WHERE id = $i AND label IS NULL";
                update.Parameters.AddWithValue("$l", label);
                update.Parameters.AddWithValue("$i", id!);
                update.ExecuteNonQuery();
            }

            return id;
        }
    }

    /// <summary>Nomme un capteur (« Salon », « Congélateur »…).</summary>
    public bool SetLabel(string address, string? label)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sensors SET label = $l WHERE address = $a";
            update.Parameters.AddWithValue("$l", (object?)label ?? DBNull.Value);
            update.Parameters.AddWithValue("$a", address);
            return update.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<SensorRow> Sensors()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.address, s.label, s.first_seen, s.last_seen,
                   (SELECT COUNT(*) FROM readings r WHERE r.sensor_id = s.id) AS count
            FROM sensors s
            ORDER BY s.address
            """;

        var rows = new List<SensorRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SensorRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    /// <summary>Dernière mesure connue de chaque capteur.</summary>
    public IReadOnlyList<LatestRow> Latest()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.address, s.label, r.ts, r.celsius
            FROM sensors s
            LEFT JOIN readings r ON r.sensor_id = s.id AND r.ts = (
                SELECT MAX(ts) FROM readings WHERE sensor_id = s.id
            )
            ORDER BY s.address
            """;

        var rows = new List<LatestRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LatestRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3)));
        }

        return rows;
    }

    // -- écriture -----------------------------------------------------------

    /// <summary>
    /// Enregistre des mesures et met à jour la date de dernière vue.
    /// </summary>
    /// <remarks>
    /// <c>INSERT OR REPLACE</c> évite qu'une collision d'horodatage (deux lectures
    /// dans la même seconde) fasse échouer tout le lot.
    /// </remarks>
    public int Record(IEnumerable<Reading> readings)
    {
        var batch = readings as IReadOnlyList<Reading> ?? readings.ToList();
        if (batch.Count == 0)
        {
            return 0;
        }

        lock (_writeGate)
        {
            using var connection = Open();

            // BeginTransaction() émet un BEGIN « deferred » ; storage.py émet
            // explicitement BEGIN IMMEDIATE pour prendre le verrou d'écriture tout
            // de suite plutôt qu'à la première écriture.
            using var tx = connection.BeginTransaction(deferred: false);

            var written = 0;
            foreach (var reading in batch)
            {
                var id = SensorId(connection, tx, reading.Address, create: true);

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText =
                        "INSERT OR REPLACE INTO readings (sensor_id, ts, celsius) VALUES ($s, $t, $c)";
                    insert.Parameters.AddWithValue("$s", id!);
                    insert.Parameters.AddWithValue("$t", reading.Ts);
                    insert.Parameters.AddWithValue("$c", reading.Celsius);
                    insert.ExecuteNonQuery();
                }

                using (var touch = connection.CreateCommand())
                {
                    touch.Transaction = tx;
                    touch.CommandText =
                        "UPDATE sensors SET last_seen = MAX(COALESCE(last_seen, 0), $t) WHERE id = $i";
                    touch.Parameters.AddWithValue("$t", reading.Ts);
                    touch.Parameters.AddWithValue("$i", id!);
                    touch.ExecuteNonQuery();
                }

                written++;
            }

            tx.Commit();
            return written;
        }
    }

    // -- lecture ------------------------------------------------------------

    public (long? First, long? Last) TimeRange()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(ts) AS lo, MAX(ts) AS hi FROM readings";

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null);
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    /// <summary>
    /// Retourne les points d'une plage, par adresse de capteur.
    /// </summary>
    /// <remarks>
    /// Avec <paramref name="bucket"/> positif, les mesures sont regroupées par
    /// tranche et chaque point porte la moyenne, le minimum et le maximum de la
    /// tranche — les extrêmes restent ainsi visibles même très sous-échantillonnés.
    /// </remarks>
    public IReadOnlyDictionary<string, List<SeriesPoint>> Series(
        long start,
        long end,
        IReadOnlyList<string>? addresses = null,
        int bucket = 0)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        var filter = AddressFilter(command, addresses);

        if (bucket > 0)
        {
            command.CommandText = $"""
                SELECT s.address AS address,
                       (r.ts / $b) * $b AS ts,
                       AVG(r.celsius) AS celsius,
                       MIN(r.celsius) AS min_celsius,
                       MAX(r.celsius) AS max_celsius,
                       COUNT(*) AS samples
                FROM readings r
                JOIN sensors s ON s.id = r.sensor_id
                WHERE r.ts >= $from AND r.ts <= $to{filter}
                GROUP BY s.address, r.ts / $b
                ORDER BY s.address, ts
                """;

            // Le paramètre doit être lié en entier : passé en flottant, SQLite ferait
            // une division réelle et tout le regroupement changerait sans erreur.
            command.Parameters.Add("$b", SqliteType.Integer).Value = (long)bucket;
        }
        else
        {
            command.CommandText = $"""
                SELECT s.address AS address,
                       r.ts AS ts,
                       r.celsius AS celsius,
                       r.celsius AS min_celsius,
                       r.celsius AS max_celsius,
                       1 AS samples
                FROM readings r
                JOIN sensors s ON s.id = r.sensor_id
                WHERE r.ts >= $from AND r.ts <= $to{filter}
                ORDER BY s.address, r.ts
                """;
        }

        command.Parameters.AddWithValue("$from", start);
        command.Parameters.AddWithValue("$to", end);

        var result = new Dictionary<string, List<SeriesPoint>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var address = reader.GetString(0);
            if (!result.TryGetValue(address, out var points))
            {
                result[address] = points = [];
            }

            points.Add(new SeriesPoint(
                reader.GetInt64(1),
                PythonRepr.Round(reader.GetDouble(2), 4),
                PythonRepr.Round(reader.GetDouble(3), 4),
                PythonRepr.Round(reader.GetDouble(4), 4),
                reader.GetInt32(5)));
        }

        return result;
    }

    /// <summary>Statistiques (min, max, moyenne, nombre de points) sur une plage.</summary>
    public IReadOnlyDictionary<string, SummaryStats> Summary(
        long start,
        long end,
        IReadOnlyList<string>? addresses = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        var filter = AddressFilter(command, addresses);
        command.CommandText = $"""
            SELECT s.address AS address,
                   MIN(r.celsius) AS min_celsius,
                   MAX(r.celsius) AS max_celsius,
                   AVG(r.celsius) AS avg_celsius,
                   COUNT(*) AS samples
            FROM readings r
            JOIN sensors s ON s.id = r.sensor_id
            WHERE r.ts >= $from AND r.ts <= $to{filter}
            GROUP BY s.address
            """;
        command.Parameters.AddWithValue("$from", start);
        command.Parameters.AddWithValue("$to", end);

        // L'ordre d'insertion est celui du GROUP BY, donc trié par adresse : le
        // dictionnaire le conserve tant qu'on n'en retire rien, et la sérialisation
        // JSON reproduit ainsi l'ordre du Python.
        var result = new Dictionary<string, SummaryStats>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new SummaryStats(
                PythonRepr.Round(reader.GetDouble(1), 4),
                PythonRepr.Round(reader.GetDouble(2), 4),
                PythonRepr.Round(reader.GetDouble(3), 4),
                reader.GetInt32(4));
        }

        return result;
    }

    /// <summary>
    /// Parcourt les mesures brutes d'une plage et les remet une à une.
    /// </summary>
    /// <remarks>
    /// Forme à rappel plutôt qu'énumérable paresseux : rendre un
    /// <c>IEnumerable</c> laisserait la connexion ouverte au-delà de la méthode,
    /// jusqu'à ce que l'appelant veuille bien finir d'énumérer.
    /// </remarks>
    public int ForEachRow(
        long start,
        long end,
        IReadOnlyList<string>? addresses,
        Action<ExportRow> visit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        var filter = AddressFilter(command, addresses);
        command.CommandText = $"""
            SELECT r.ts AS ts, s.address AS address, s.label AS label, r.celsius AS celsius
            FROM readings r
            JOIN sensors s ON s.id = r.sensor_id
            WHERE r.ts >= $from AND r.ts <= $to{filter}
            ORDER BY r.ts, s.address
            """;
        command.Parameters.AddWithValue("$from", start);
        command.Parameters.AddWithValue("$to", end);

        var count = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            visit(new ExportRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetDouble(3)));
            count++;
        }

        return count;
    }

    private static string AddressFilter(SqliteCommand command, IReadOnlyList<string>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
        {
            return string.Empty;
        }

        var names = new string[addresses.Count];
        for (var i = 0; i < addresses.Count; i++)
        {
            names[i] = $"$a{i}";
            command.Parameters.AddWithValue(names[i], addresses[i]);
        }

        return $" AND s.address IN ({string.Join(",", names)})";
    }

    // -- entretien ----------------------------------------------------------

    /// <summary>Supprime les mesures antérieures à <paramref name="beforeTs"/> et retourne leur nombre.</summary>
    public int Prune(long beforeTs)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM readings WHERE ts < $t";
            command.Parameters.AddWithValue("$t", beforeTs);
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>Compacte le fichier après une purge importante.</summary>
    public void Vacuum()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM";
        command.ExecuteNonQuery();
    }

    public StorageStats Stats()
    {
        var (first, last) = TimeRange();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) AS n FROM readings";
        var count = (long)command.ExecuteScalar()!;

        var size = !IsMemory && File.Exists(Path) ? new FileInfo(Path).Length : 0;

        return new StorageStats(Path, size, Sensors().Count, count, first, last);
    }
}
