"""Stockage des mesures dans SQLite.

Le schéma est volontairement minimal : une table de capteurs et une table de
mesures. Les horodatages sont des entiers (secondes epoch UTC), ce qui rend les
requêtes de plage et le regroupement par intervalle triviaux et compacts — un
point important sur la carte SD d'un Raspberry Pi.
"""

from __future__ import annotations

import logging
import sqlite3
import threading
import time
from pathlib import Path
from typing import Iterable, Iterator, Sequence

from .sensor import Reading

log = logging.getLogger(__name__)

SCHEMA_VERSION = 1

_SCHEMA = """
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
"""

#: Paliers de regroupement, en secondes, utilisés pour le sous-échantillonnage
#: automatique. Chaque palier correspond à une durée « ronde » lisible sur un axe.
BUCKET_STEPS = (
    1, 5, 10, 15, 30,
    60, 120, 300, 600, 900, 1800,
    3600, 7200, 10800, 21600, 43200,
    86400, 172800, 604800,
)


def choose_bucket(span_seconds: float, target_points: int = 800) -> int:
    """Choisit un intervalle de regroupement pour tenir dans ``target_points``.

    Sans cela, afficher un mois de mesures prises chaque minute reviendrait à
    transférer plus de 40 000 points au navigateur pour un graphique large de
    quelques centaines de pixels.
    """
    if span_seconds <= 0 or target_points <= 0:
        return 0
    ideal = span_seconds / target_points
    for step in BUCKET_STEPS:
        if step >= ideal:
            return step
    return BUCKET_STEPS[-1]


class Storage:
    """Accès à la base de mesures.

    Une connexion est ouverte par thread : SQLite interdit de partager une
    connexion entre threads, et le serveur web en utilise plusieurs.
    """

    def __init__(self, path: Path | str) -> None:
        self.path = Path(path)
        self._local = threading.local()
        self._lock = threading.Lock()
        if str(self.path) != ":memory:":
            self.path.parent.mkdir(parents=True, exist_ok=True)
        self._shared: sqlite3.Connection | None = None
        if str(self.path) == ":memory:":
            # Une base en mémoire est propre à sa connexion : on en garde une
            # seule, partagée, pour que les tests voient les mêmes données.
            self._shared = self._new_connection()
        self._init_schema()

    # -- connexions ---------------------------------------------------------

    def _new_connection(self) -> sqlite3.Connection:
        conn = sqlite3.connect(
            self.path,
            timeout=15.0,
            isolation_level=None,  # autocommit : on gère les transactions explicitement
            check_same_thread=False,
        )
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode=WAL")
        conn.execute("PRAGMA synchronous=NORMAL")
        conn.execute("PRAGMA foreign_keys=ON")
        conn.execute("PRAGMA busy_timeout=15000")
        return conn

    @property
    def conn(self) -> sqlite3.Connection:
        if self._shared is not None:
            return self._shared
        conn = getattr(self._local, "conn", None)
        if conn is None:
            conn = self._new_connection()
            self._local.conn = conn
        return conn

    def close(self) -> None:
        if self._shared is not None:
            self._shared.close()
            self._shared = None
            return
        conn = getattr(self._local, "conn", None)
        if conn is not None:
            conn.close()
            self._local.conn = None

    def __enter__(self) -> "Storage":
        return self

    def __exit__(self, *exc_info) -> None:
        self.close()

    # -- schéma -------------------------------------------------------------

    def _init_schema(self) -> None:
        with self._lock:
            self.conn.executescript(_SCHEMA)
            row = self.conn.execute(
                "SELECT value FROM meta WHERE key = 'schema_version'"
            ).fetchone()
            if row is None:
                self.conn.execute(
                    "INSERT INTO meta (key, value) VALUES ('schema_version', ?)",
                    (str(SCHEMA_VERSION),),
                )
            elif int(row["value"]) > SCHEMA_VERSION:
                raise RuntimeError(
                    f"la base {self.path} utilise le schéma v{row['value']}, "
                    f"plus récent que celui géré par cette version (v{SCHEMA_VERSION})"
                )

    # -- capteurs -----------------------------------------------------------

    def sensor_id(self, address: str, *, create: bool = True) -> int | None:
        """Retourne l'identifiant interne d'un capteur, en le créant au besoin."""
        row = self.conn.execute(
            "SELECT id FROM sensors WHERE address = ?", (address,)
        ).fetchone()
        if row is not None:
            return row["id"]
        if not create:
            return None
        now = int(time.time())
        self.conn.execute(
            "INSERT OR IGNORE INTO sensors (address, first_seen) VALUES (?, ?)",
            (address, now),
        )
        row = self.conn.execute(
            "SELECT id FROM sensors WHERE address = ?", (address,)
        ).fetchone()
        return row["id"] if row else None

    def set_label(self, address: str, label: str | None) -> bool:
        """Nomme un capteur (« Salon », « Congélateur »…)."""
        cursor = self.conn.execute(
            "UPDATE sensors SET label = ? WHERE address = ?", (label, address)
        )
        return cursor.rowcount > 0

    def sensors(self) -> list[dict]:
        rows = self.conn.execute(
            """
            SELECT s.id, s.address, s.label, s.first_seen, s.last_seen,
                   (SELECT COUNT(*) FROM readings r WHERE r.sensor_id = s.id) AS count
            FROM sensors s
            ORDER BY s.address
            """
        ).fetchall()
        return [dict(row) for row in rows]

    def latest(self) -> list[dict]:
        """Dernière mesure connue de chaque capteur."""
        rows = self.conn.execute(
            """
            SELECT s.address, s.label, r.ts, r.celsius
            FROM sensors s
            LEFT JOIN readings r ON r.sensor_id = s.id AND r.ts = (
                SELECT MAX(ts) FROM readings WHERE sensor_id = s.id
            )
            ORDER BY s.address
            """
        ).fetchall()
        return [dict(row) for row in rows]

    # -- écriture -----------------------------------------------------------

    def record(self, readings: Iterable[Reading]) -> int:
        """Enregistre des mesures et met à jour la date de dernière vue.

        ``INSERT OR REPLACE`` évite qu'une collision d'horodatage (deux lectures
        dans la même seconde) fasse échouer tout le lot.
        """
        readings = list(readings)
        if not readings:
            return 0

        with self._lock:
            conn = self.conn
            conn.execute("BEGIN IMMEDIATE")
            try:
                written = 0
                for reading in readings:
                    sid = self.sensor_id(reading.address)
                    conn.execute(
                        "INSERT OR REPLACE INTO readings (sensor_id, ts, celsius) VALUES (?, ?, ?)",
                        (sid, reading.ts, reading.celsius),
                    )
                    conn.execute(
                        "UPDATE sensors SET last_seen = MAX(COALESCE(last_seen, 0), ?) WHERE id = ?",
                        (reading.ts, sid),
                    )
                    written += 1
                conn.execute("COMMIT")
                return written
            except Exception:
                conn.execute("ROLLBACK")
                raise

    # -- lecture ------------------------------------------------------------

    def time_range(self) -> tuple[int | None, int | None]:
        row = self.conn.execute("SELECT MIN(ts) AS lo, MAX(ts) AS hi FROM readings").fetchone()
        return (row["lo"], row["hi"]) if row else (None, None)

    def series(
        self,
        start: int,
        end: int,
        addresses: Sequence[str] | None = None,
        bucket: int = 0,
    ) -> dict[str, list[dict]]:
        """Retourne les points d'une plage, par adresse de capteur.

        Avec ``bucket > 0``, les mesures sont regroupées par tranche de
        ``bucket`` secondes et chaque point porte la moyenne, le minimum et le
        maximum de la tranche — les extrêmes restent ainsi visibles même très
        sous-échantillonnés.
        """
        params: list = [start, end]
        filter_sql = ""
        if addresses:
            placeholders = ",".join("?" for _ in addresses)
            filter_sql = f" AND s.address IN ({placeholders})"
            params.extend(addresses)

        if bucket > 0:
            sql = f"""
                SELECT s.address AS address,
                       (r.ts / ?) * ? AS ts,
                       AVG(r.celsius) AS celsius,
                       MIN(r.celsius) AS min_celsius,
                       MAX(r.celsius) AS max_celsius,
                       COUNT(*) AS samples
                FROM readings r
                JOIN sensors s ON s.id = r.sensor_id
                WHERE r.ts >= ? AND r.ts <= ?{filter_sql}
                GROUP BY s.address, r.ts / ?
                ORDER BY s.address, ts
            """
            query_params = [bucket, bucket, *params, bucket]
        else:
            sql = f"""
                SELECT s.address AS address,
                       r.ts AS ts,
                       r.celsius AS celsius,
                       r.celsius AS min_celsius,
                       r.celsius AS max_celsius,
                       1 AS samples
                FROM readings r
                JOIN sensors s ON s.id = r.sensor_id
                WHERE r.ts >= ? AND r.ts <= ?{filter_sql}
                ORDER BY s.address, r.ts
            """
            query_params = params

        result: dict[str, list[dict]] = {}
        for row in self.conn.execute(sql, query_params):
            point = {
                "ts": int(row["ts"]),
                "celsius": round(row["celsius"], 4),
                "min": round(row["min_celsius"], 4),
                "max": round(row["max_celsius"], 4),
                "samples": int(row["samples"]),
            }
            result.setdefault(row["address"], []).append(point)
        return result

    def summary(self, start: int, end: int, addresses: Sequence[str] | None = None) -> dict[str, dict]:
        """Statistiques (min, max, moyenne, nombre de points) sur une plage."""
        params: list = [start, end]
        filter_sql = ""
        if addresses:
            placeholders = ",".join("?" for _ in addresses)
            filter_sql = f" AND s.address IN ({placeholders})"
            params.extend(addresses)

        rows = self.conn.execute(
            f"""
            SELECT s.address AS address,
                   MIN(r.celsius) AS min_celsius,
                   MAX(r.celsius) AS max_celsius,
                   AVG(r.celsius) AS avg_celsius,
                   COUNT(*) AS samples
            FROM readings r
            JOIN sensors s ON s.id = r.sensor_id
            WHERE r.ts >= ? AND r.ts <= ?{filter_sql}
            GROUP BY s.address
            """,
            params,
        ).fetchall()
        return {
            row["address"]: {
                "min": round(row["min_celsius"], 4),
                "max": round(row["max_celsius"], 4),
                "avg": round(row["avg_celsius"], 4),
                "samples": int(row["samples"]),
            }
            for row in rows
        }

    def iter_rows(
        self, start: int, end: int, addresses: Sequence[str] | None = None
    ) -> Iterator[sqlite3.Row]:
        """Itère les mesures brutes, pour l'export."""
        params: list = [start, end]
        filter_sql = ""
        if addresses:
            placeholders = ",".join("?" for _ in addresses)
            filter_sql = f" AND s.address IN ({placeholders})"
            params.extend(addresses)

        yield from self.conn.execute(
            f"""
            SELECT r.ts AS ts, s.address AS address, s.label AS label, r.celsius AS celsius
            FROM readings r
            JOIN sensors s ON s.id = r.sensor_id
            WHERE r.ts >= ? AND r.ts <= ?{filter_sql}
            ORDER BY r.ts, s.address
            """,
            params,
        )

    # -- entretien ----------------------------------------------------------

    def prune(self, before_ts: int) -> int:
        """Supprime les mesures antérieures à ``before_ts`` et retourne leur nombre."""
        with self._lock:
            cursor = self.conn.execute("DELETE FROM readings WHERE ts < ?", (before_ts,))
            return cursor.rowcount

    def vacuum(self) -> None:
        """Compacte le fichier après une purge importante."""
        self.conn.execute("VACUUM")

    def stats(self) -> dict:
        lo, hi = self.time_range()
        count = self.conn.execute("SELECT COUNT(*) AS n FROM readings").fetchone()["n"]
        size = self.path.stat().st_size if self.path.exists() and str(self.path) != ":memory:" else 0
        return {
            "db_path": str(self.path),
            "db_bytes": size,
            "sensors": len(self.sensors()),
            "readings": count,
            "first_ts": lo,
            "last_ts": hi,
        }
