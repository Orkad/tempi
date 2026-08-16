"""Configuration de tempi.

Toute la configuration passe par des variables d'environnement (pratique avec
``EnvironmentFile=`` de systemd) et peut être surchargée par les options de la
ligne de commande.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

#: Répertoire exposé par le noyau pour les périphériques 1-Wire.
DEFAULT_W1_DIR = Path("/sys/bus/w1/devices")

#: Emplacement utilisé quand le service tourne en tant que démon système.
SYSTEM_DB_PATH = Path("/var/lib/tempi/tempi.db")


def default_db_path() -> Path:
    """Choisit une base de données par défaut utilisable sans configuration.

    On privilégie l'emplacement système ``/var/lib/tempi`` quand il est
    accessible en écriture (cas du service systemd), sinon on retombe sur le
    répertoire de données de l'utilisateur.
    """
    system_dir = SYSTEM_DB_PATH.parent
    if os.access(system_dir, os.W_OK):
        return SYSTEM_DB_PATH
    if os.access(system_dir.parent, os.W_OK) and not system_dir.exists():
        return SYSTEM_DB_PATH

    xdg = os.environ.get("XDG_DATA_HOME")
    base = Path(xdg) if xdg else Path.home() / ".local" / "share"
    return base / "tempi" / "tempi.db"


def _env_str(name: str, default: str | None) -> str | None:
    value = os.environ.get(name)
    return value if value not in (None, "") else default


def _env_float(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw in (None, ""):
        return default
    try:
        return float(raw)
    except ValueError as exc:
        raise ValueError(f"{name} doit être un nombre, reçu {raw!r}") from exc


def _env_int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw in (None, ""):
        return default
    try:
        return int(raw)
    except ValueError as exc:
        raise ValueError(f"{name} doit être un entier, reçu {raw!r}") from exc


def _env_bool(name: str, default: bool) -> bool:
    raw = os.environ.get(name)
    if raw in (None, ""):
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on", "oui"}


@dataclass
class Config:
    """Paramètres effectifs de l'application."""

    # Stockage
    db_path: Path

    # Capteur
    w1_dir: Path
    simulate: bool
    read_retries: int
    #: 85.0 °C est la valeur de reset du DS18B20 : elle signale presque toujours
    #: une conversion ratée (alimentation insuffisante, câble trop long).
    allow_reset_value: bool

    # Collecte
    interval: float
    min_delta: float
    max_interval: float

    # Rétention
    retention_days: int

    # Serveur web
    host: str
    port: int

    # Chiffrement. Les deux vont de pair : sans eux, l'interface répond en clair.
    tls_cert: Path | None = None
    tls_key: Path | None = None

    @property
    def tls(self) -> bool:
        return self.tls_cert is not None and self.tls_key is not None

    @property
    def scheme(self) -> str:
        return "https" if self.tls else "http"

    @classmethod
    def from_env(cls) -> "Config":
        db = _env_str("TEMPI_DB", None)
        tls_cert = _env_str("TEMPI_TLS_CERT", None)
        tls_key = _env_str("TEMPI_TLS_KEY", None)
        return cls(
            db_path=Path(db) if db else default_db_path(),
            w1_dir=Path(_env_str("TEMPI_W1_DIR", str(DEFAULT_W1_DIR))),
            simulate=_env_bool("TEMPI_SIMULATE", False),
            read_retries=_env_int("TEMPI_READ_RETRIES", 3),
            allow_reset_value=_env_bool("TEMPI_ALLOW_RESET_VALUE", False),
            interval=_env_float("TEMPI_INTERVAL", 60.0),
            min_delta=_env_float("TEMPI_MIN_DELTA", 0.0),
            max_interval=_env_float("TEMPI_MAX_INTERVAL", 900.0),
            retention_days=_env_int("TEMPI_RETENTION_DAYS", 0),
            host=_env_str("TEMPI_HOST", "127.0.0.1"),
            port=_env_int("TEMPI_PORT", 8080),
            tls_cert=Path(tls_cert) if tls_cert else None,
            tls_key=Path(tls_key) if tls_key else None,
        )

    def validate(self) -> None:
        if self.interval < 1:
            # Les horodatages sont stockés à la seconde ; en dessous, deux
            # mesures partageraient la même clé et s'écraseraient. Le DS18B20
            # demande de toute façon jusqu'à 750 ms par conversion.
            raise ValueError("l'intervalle de collecte doit valoir au moins 1 seconde")
        if self.read_retries < 1:
            raise ValueError("le nombre de tentatives de lecture doit valoir au moins 1")
        if self.min_delta < 0:
            raise ValueError("min-delta ne peut pas être négatif")
        if self.max_interval < 0:
            raise ValueError("max-interval ne peut pas être négatif")
        if self.retention_days < 0:
            raise ValueError("la rétention ne peut pas être négative")
        if not 1 <= self.port <= 65535:
            raise ValueError("le port doit être compris entre 1 et 65535")
        if (self.tls_cert is None) != (self.tls_key is None):
            raise ValueError(
                "le certificat et la clé vont de pair : précisez TEMPI_TLS_CERT et TEMPI_TLS_KEY"
            )
        # Vérifié ici plutôt qu'à l'ouverture du socket : le service échoue au
        # démarrage, avec le chemin fautif, au lieu de refuser les connexions.
        for label, path in (("certificat", self.tls_cert), ("clé privée", self.tls_key)):
            if path is not None and not path.is_file():
                raise ValueError(f"{label} introuvable : {path}")
