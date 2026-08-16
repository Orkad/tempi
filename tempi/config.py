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

#: Fournisseurs de température extérieure reconnus. Déclarés ici plutôt que
#: dans ``outdoor`` pour que la validation de la configuration n'ait pas à
#: importer le module réseau.
OUTDOOR_PROVIDERS = ("metar", "infoclimat", "open-meteo")


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


def _env_optional_float(name: str) -> float | None:
    raw = os.environ.get(name)
    if raw in (None, ""):
        return None
    try:
        return float(raw)
    except ValueError as exc:
        raise ValueError(f"{name} doit être un nombre, reçu {raw!r}") from exc


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

    # Source extérieure. Facultative, d'où les valeurs par défaut : sans
    # configuration, tempi se comporte exactement comme avant.
    outdoor_provider: str | None = None
    outdoor_station: str | None = None
    outdoor_latitude: float | None = None
    outdoor_longitude: float | None = None
    outdoor_token: str | None = None
    outdoor_label: str = "Extérieur"
    #: Les stations publient toutes les 6 à 60 minutes : interroger plus
    #: souvent ne donne aucun point de plus et sollicite inutilement une API
    #: publique gratuite.
    outdoor_interval: float = 600.0
    outdoor_timeout: float = 10.0

    @classmethod
    def from_env(cls) -> "Config":
        db = _env_str("TEMPI_DB", None)
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
            outdoor_provider=_env_str("TEMPI_OUTDOOR_PROVIDER", None),
            outdoor_station=_env_str("TEMPI_OUTDOOR_STATION", None),
            outdoor_latitude=_env_optional_float("TEMPI_OUTDOOR_LAT"),
            outdoor_longitude=_env_optional_float("TEMPI_OUTDOOR_LON"),
            # La clé reste hors de la ligne de commande : les arguments d'un
            # processus sont lisibles par tous dans /proc.
            outdoor_token=_env_str("TEMPI_OUTDOOR_TOKEN", None),
            outdoor_label=_env_str("TEMPI_OUTDOOR_LABEL", "Extérieur"),
            outdoor_interval=_env_float("TEMPI_OUTDOOR_INTERVAL", 600.0),
            outdoor_timeout=_env_float("TEMPI_OUTDOOR_TIMEOUT", 10.0),
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

        provider = (self.outdoor_provider or "").strip().lower()
        if provider and provider != "none":
            if provider not in OUTDOOR_PROVIDERS:
                raise ValueError(
                    f"fournisseur extérieur inconnu : {self.outdoor_provider!r} "
                    f"(attendu : {', '.join(OUTDOOR_PROVIDERS)})"
                )
            if self.outdoor_interval < 60:
                # Aucune station ne publie plus vite, et marteler une API
                # publique gratuite est le meilleur moyen de s'en faire bannir.
                raise ValueError("l'intervalle extérieur doit valoir au moins 60 secondes")
            if self.outdoor_timeout <= 0:
                raise ValueError("le délai d'attente extérieur doit être positif")
