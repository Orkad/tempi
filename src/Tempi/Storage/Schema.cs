namespace Tempi.Storage;

/// <summary>
/// Schéma de la base de mesures.
/// </summary>
/// <remarks>
/// Volontairement minimal : une table de capteurs et une table de mesures. Les
/// horodatages sont des entiers (secondes epoch UTC), ce qui rend les requêtes de
/// plage et le regroupement par intervalle triviaux et compacts — un point important
/// sur la carte SD d'un Raspberry Pi.
/// <para>
/// Le texte est repris au caractère près de <c>storage.py</c> : une base écrite par
/// l'une des deux implémentations doit être lue par l'autre sans conversion.
/// </para>
/// </remarks>
internal static class Schema
{
    public const int Version = 1;

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS sensors (
            id         INTEGER PRIMARY KEY,
            address    TEXT    NOT NULL UNIQUE,
            label      TEXT,
            first_seen INTEGER NOT NULL,
            last_seen  INTEGER
        );

        CREATE TABLE IF NOT EXISTS readings (
            sensor_id INTEGER NOT NULL REFERENCES sensors(id) ON DELETE CASCADE,
            ts        INTEGER NOT NULL,
            celsius   REAL    NOT NULL,
            PRIMARY KEY (sensor_id, ts)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS readings_ts ON readings (ts);

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}
