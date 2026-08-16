"""Température extérieure relevée sur une API publique, vue comme un capteur.

Le DS18B20 mesure une pièce ; comparer sa courbe à la température extérieure
demande une seconde source, forcément distante. tempi la traite exactement
comme un capteur du bus : une adresse, un libellé, des mesures dans la même
table. L'interface web, l'export CSV et les statistiques en héritent sans
qu'aucun d'eux ait à connaître l'existence du réseau.

L'adresse du pseudo-capteur a la forme ``outdoor-<fournisseur>-<station>`` ;
elle ne peut donc jamais entrer en collision avec une adresse 1-Wire, qui
commence toujours par un code de famille hexadécimal.

Trois fournisseurs, du plus fiable au plus proche :

``metar``
    Observations aéronautiques diffusées par la NOAA. Mesure réelle, sous abri
    normalisé, aucune clé d'API. Les aérodromes sont souvent excentrés
    (``LFLY`` Lyon-Bron est à ~8 km de la Presqu'île).

``infoclimat``
    Réseau StatIC de l'association Infoclimat : des stations bien plus denses
    en ville, avec des exigences d'exposition. Demande une clé, délivrée
    gratuitement, **liée à l'adresse IP appelante** — elle cesse de fonctionner
    quand l'IP change.

``open-meteo``
    Aucune clé, couverture mondiale, mais ce n'est **pas une mesure** : c'est
    la sortie d'un modèle interpolée sur une grille de quelques kilomètres. À
    réserver aux endroits sans station exploitable.

Deux garde-fous s'appliquent à tous :

* on enregistre l'horodatage **de l'observation**, pas celui de la requête,
  sinon la courbe extérieure serait décalée du délai de diffusion ;
* une observation déjà enregistrée est ignorée. Les stations ne publient que
  toutes les 6 à 60 minutes : sans ce filtre, chaque interrogation dupliquerait
  la même valeur sous un horodatage différent.
"""

from __future__ import annotations

import json
import logging
import re
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from . import __version__
from .config import OUTDOOR_PROVIDERS as PROVIDERS
from .sensor import Reading

log = logging.getLogger(__name__)

#: Intervalle minimal entre deux interrogations. Les stations ne publient pas
#: plus vite, et marteler une API publique gratuite est le meilleur moyen de
#: se faire bloquer.
MIN_INTERVAL = 60.0

#: Plage de validité d'une température extérieure. Un relevé hors de ces bornes
#: traduit une erreur de format (millidegrés, degrés Fahrenheit) plutôt qu'une
#: canicule.
MIN_CELSIUS = -90.0
MAX_CELSIUS = 60.0

#: Caractères autorisés dans une adresse de capteur par la route de renommage
#: (``/api/sensors/<adresse>/label``).
_UNSAFE_IN_ADDRESS = re.compile(r"[^0-9A-Za-z._-]+")

_USER_AGENT = f"tempi/{__version__} (+https://github.com/Orkad/tempi)"


class OutdoorError(Exception):
    """La température extérieure n'a pas pu être obtenue."""


@dataclass(frozen=True)
class Observation:
    """Une température extérieure, telle que publiée par la station."""

    celsius: float
    #: Instant de la mesure (epoch UTC), et non celui de la requête.
    ts: int
    #: Station ou point de grille d'origine, pour les journaux.
    station: str


def _slug(value: str) -> str:
    """Rend une chaîne utilisable dans une adresse de pseudo-capteur."""
    return _UNSAFE_IN_ADDRESS.sub("-", value.strip()).strip("-") or "inconnu"


def _iso_to_epoch(value: str, offset_seconds: int = 0) -> int:
    """Convertit une date ISO 8601 (ou ``YYYY-MM-DD HH:MM:SS``) en epoch UTC."""
    text = value.strip().replace("Z", "+00:00")
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as exc:
        raise OutdoorError(f"date illisible : {value!r}") from exc
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
        return int(parsed.timestamp()) - offset_seconds
    return int(parsed.timestamp())


def _as_celsius(value, field: str) -> float:
    """Valide une température brute issue d'une réponse JSON."""
    if value is None or value == "":
        raise OutdoorError(f"température absente de la réponse ({field})")
    try:
        celsius = float(value)
    except (TypeError, ValueError) as exc:
        raise OutdoorError(f"température illisible ({field}) : {value!r}") from exc
    if not MIN_CELSIUS <= celsius <= MAX_CELSIUS:
        raise OutdoorError(f"{celsius} °C hors de la plage plausible ({field})")
    return celsius


def http_get(url: str, timeout: float) -> bytes:
    """Récupère une URL. Isolé pour être remplacé dans les tests."""
    request = urllib.request.Request(url, headers={"User-Agent": _USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        # Le corps d'une erreur porte souvent le motif exact (clé invalide,
        # quota dépassé) : le perdre rendrait le diagnostic impossible.
        detail = ""
        try:
            detail = exc.read(500).decode("utf-8", "replace").strip()
        except Exception:  # pragma: no cover - dépend du serveur distant
            pass
        raise OutdoorError(f"HTTP {exc.code} sur {url}{' — ' + detail if detail else ''}") from exc
    except urllib.error.URLError as exc:
        raise OutdoorError(f"{url} injoignable : {exc.reason}") from exc
    except OSError as exc:  # délai dépassé, connexion coupée
        raise OutdoorError(f"{url} injoignable : {exc}") from exc


def _load_json(payload: bytes):
    try:
        return json.loads(payload.decode("utf-8", "replace"))
    except json.JSONDecodeError as exc:
        raise OutdoorError(f"réponse JSON invalide : {exc}") from exc


# -- fournisseurs -----------------------------------------------------------


class MetarSource:
    """Observations METAR diffusées par aviationweather.gov (NOAA).

    Les stations sont désignées par leur code OACI à quatre lettres :
    ``LFLY`` (Lyon-Bron), ``LFLL`` (Lyon-Saint-Exupéry), ``LFPG``…
    """

    name = "metar"
    #: Groupe température/point de rosée d'un METAR brut, en secours si le
    #: champ numérique manque. ``M`` préfixe les valeurs négatives.
    _RAW_TEMP = re.compile(r"\s(M?\d{2})/(M?\d{2})\s")

    def __init__(self, station: str) -> None:
        self.station = station.strip().upper()
        if not self.station:
            raise OutdoorError("le fournisseur metar demande un code OACI (TEMPI_OUTDOOR_STATION)")

    @property
    def slug(self) -> str:
        return _slug(self.station)

    def describe(self) -> str:
        return f"METAR {self.station}"

    def url(self) -> str:
        query = urllib.parse.urlencode({"ids": self.station, "format": "json"})
        return f"https://aviationweather.gov/api/data/metar?{query}"

    def parse(self, payload: bytes) -> Observation:
        data = _load_json(payload)
        if isinstance(data, dict):
            data = data.get("data") or data.get("features") or []
        if not isinstance(data, list) or not data:
            raise OutdoorError(f"aucune observation pour la station {self.station}")

        # L'API renvoie les rapports du plus récent au plus ancien, mais rien ne
        # le garantit : on trie explicitement.
        report = max(
            (item for item in data if isinstance(item, dict)),
            key=self._sort_key,
            default=None,
        )
        if report is None:
            raise OutdoorError(f"aucune observation pour la station {self.station}")

        raw_ts = report.get("obsTime")
        if raw_ts in (None, ""):
            raise OutdoorError("horodatage d'observation absent")
        ts = int(raw_ts)

        temp = report.get("temp")
        if temp in (None, ""):
            temp = self._from_raw(report.get("rawOb") or "")
        celsius = _as_celsius(temp, "temp")
        station = str(report.get("icaoId") or self.station)
        return Observation(celsius=celsius, ts=ts, station=station)

    @staticmethod
    def _sort_key(report: dict) -> float:
        """Classe les rapports par ancienneté, sans se fier au type de ``obsTime``."""
        try:
            return float(report.get("obsTime") or 0)
        except (TypeError, ValueError):
            return 0.0

    def _from_raw(self, raw: str):
        match = self._RAW_TEMP.search(f" {raw} ")
        if not match:
            return None
        group = match.group(1)
        if group.startswith("M"):  # M pour « minus »
            return -int(group[1:])
        return int(group)


class InfoclimatSource:
    """Réseau StatIC d'Infoclimat, via son API open data.

    La clé est délivrée sur https://www.infoclimat.fr/opendata/ après création
    d'un compte et déclaration d'un usage commercial ou non. Les données sont
    sous Licence Ouverte ou Creative Commons selon la station : citer
    « Infoclimat (StatIC) » lors de toute rediffusion.
    """

    name = "infoclimat"

    def __init__(self, station: str, token: str) -> None:
        self.station = station.strip()
        self.token = token.strip()
        if not self.station:
            raise OutdoorError(
                "le fournisseur infoclimat demande un identifiant de station "
                "(TEMPI_OUTDOOR_STATION)"
            )
        if not self.token:
            raise OutdoorError("le fournisseur infoclimat demande une clé (TEMPI_OUTDOOR_TOKEN)")

    @property
    def slug(self) -> str:
        return _slug(self.station)

    def describe(self) -> str:
        return f"Infoclimat StatIC {self.station}"

    def url(self) -> str:
        # L'API impose une plage explicite. Demander la veille et le jour même
        # suffit à englober la dernière observation, y compris juste après
        # minuit UTC ou au retour d'une coupure réseau.
        today = datetime.now(timezone.utc).date()
        query = urllib.parse.urlencode(
            {
                "method": "get",
                "format": "json",
                "stations[]": self.station,
                "start": (today - timedelta(days=1)).isoformat(),
                "end": today.isoformat(),
                "token": self.token,
            }
        )
        return f"https://www.infoclimat.fr/opendata/?{query}"

    def parse(self, payload: bytes) -> Observation:
        data = _load_json(payload)
        if not isinstance(data, dict):
            raise OutdoorError("réponse inattendue de l'API Infoclimat")

        message = data.get("message") or data.get("err") or data.get("error")
        if message and not data.get("hourly"):
            raise OutdoorError(f"Infoclimat a refusé la requête : {message}")

        records = self._records(data)
        if not records:
            raise OutdoorError(f"aucune mesure pour la station {self.station}")

        latest = max(records, key=lambda item: str(item.get("dh_utc") or ""))
        stamp = latest.get("dh_utc")
        if not stamp:
            raise OutdoorError("horodatage 'dh_utc' absent de la réponse Infoclimat")
        celsius = _as_celsius(latest.get("temperature"), "temperature")
        return Observation(celsius=celsius, ts=_iso_to_epoch(str(stamp)), station=self.station)

    def _records(self, data: dict) -> list:
        """Extrait la liste de relevés de la station demandée.

        Les mesures sont regroupées par station sous ``hourly``. On accepte que
        la clé diffère de l'identifiant demandé (casse, préfixe) tant qu'une
        seule station a été demandée.
        """
        hourly = data.get("hourly")
        if isinstance(hourly, list):
            return [item for item in hourly if isinstance(item, dict)]
        if not isinstance(hourly, dict):
            return []

        for key in (self.station, self.station.upper(), self.station.lower()):
            entries = hourly.get(key)
            if isinstance(entries, list):
                return [item for item in entries if isinstance(item, dict)]

        merged: list = []
        for entries in hourly.values():
            if isinstance(entries, list):
                merged.extend(item for item in entries if isinstance(item, dict))
        return merged


class OpenMeteoSource:
    """Grille Open-Meteo : sortie de modèle, pas une mesure.

    Aucune clé, aucune inscription, couverture mondiale — mais la valeur est
    interpolée sur une maille de quelques kilomètres. Le libellé par défaut le
    rappelle pour éviter de la confondre avec un relevé de station.
    """

    name = "open-meteo"

    def __init__(self, latitude: float, longitude: float) -> None:
        if not -90.0 <= latitude <= 90.0:
            raise OutdoorError(f"latitude hors bornes : {latitude}")
        if not -180.0 <= longitude <= 180.0:
            raise OutdoorError(f"longitude hors bornes : {longitude}")
        self.latitude = latitude
        self.longitude = longitude

    @property
    def slug(self) -> str:
        return _slug(f"{self.latitude:.4f}_{self.longitude:.4f}")

    def describe(self) -> str:
        return f"Open-Meteo {self.latitude:.4f},{self.longitude:.4f}"

    def url(self) -> str:
        query = urllib.parse.urlencode(
            {
                "latitude": f"{self.latitude:.4f}",
                "longitude": f"{self.longitude:.4f}",
                "current": "temperature_2m",
            }
        )
        return f"https://api.open-meteo.com/v1/forecast?{query}"

    def parse(self, payload: bytes) -> Observation:
        data = _load_json(payload)
        if not isinstance(data, dict):
            raise OutdoorError("réponse inattendue de l'API Open-Meteo")
        if data.get("error"):
            raise OutdoorError(f"Open-Meteo a refusé la requête : {data.get('reason', '?')}")

        current = data.get("current")
        if not isinstance(current, dict):
            raise OutdoorError("bloc 'current' absent de la réponse Open-Meteo")

        celsius = _as_celsius(current.get("temperature_2m"), "temperature_2m")
        stamp = current.get("time")
        if not stamp:
            raise OutdoorError("horodatage 'time' absent de la réponse Open-Meteo")
        # Sans paramètre ``timezone`` la réponse est en UTC ; le décalage est
        # retranché au cas où une version future en ajouterait un.
        offset = int(data.get("utc_offset_seconds") or 0)
        return Observation(
            celsius=celsius,
            ts=_iso_to_epoch(str(stamp), offset),
            station=self.describe(),
        )


def make_source(config):
    """Construit le fournisseur décrit par la configuration, ou ``None``."""
    provider = (config.outdoor_provider or "").strip().lower()
    if not provider or provider == "none":
        return None
    if provider == "metar":
        return MetarSource(config.outdoor_station or "")
    if provider == "infoclimat":
        return InfoclimatSource(config.outdoor_station or "", config.outdoor_token or "")
    if provider == "open-meteo":
        if config.outdoor_latitude is None or config.outdoor_longitude is None:
            raise OutdoorError(
                "le fournisseur open-meteo demande des coordonnées "
                "(TEMPI_OUTDOOR_LAT / TEMPI_OUTDOOR_LON)"
            )
        return OpenMeteoSource(config.outdoor_latitude, config.outdoor_longitude)
    raise OutdoorError(
        f"fournisseur inconnu : {provider!r} (attendu : {', '.join(PROVIDERS)})"
    )


def address_for(source) -> str:
    """Adresse du pseudo-capteur correspondant à un fournisseur."""
    return f"outdoor-{source.name}-{source.slug}"


# -- collecte ---------------------------------------------------------------


class OutdoorPoller:
    """Interroge la source distante et enregistre le résultat comme une mesure."""

    def __init__(self, config, storage, source=None, fetch=None) -> None:
        self.config = config
        self.storage = storage
        self.source = source if source is not None else make_source(config)
        if self.source is None:
            raise OutdoorError("aucune source extérieure configurée")
        # Résolu à l'appel et non dans la signature, sinon ``http_get`` serait
        # figé à l'import et ne pourrait plus être remplacé.
        self.fetch = fetch if fetch is not None else http_get
        self.address = address_for(self.source)
        #: Compteurs exposés par ``/api/health``.
        self.polls = 0
        self.stored = 0
        self.errors = 0
        self.last_ok_ts: int | None = None
        self.last_error: str | None = None
        self._last_ts: int | None = None
        self._silenced: str | None = None

    # -- une interrogation --------------------------------------------------

    def observe(self) -> Observation:
        """Récupère la dernière observation publiée, sans rien enregistrer."""
        payload = self.fetch(self.source.url(), self.config.outdoor_timeout)
        return self.source.parse(payload)

    def poll_once(self) -> Reading | None:
        """Interroge la source et enregistre la mesure si elle est nouvelle.

        Retourne la mesure enregistrée, ou ``None`` si l'observation était déjà
        connue. Les erreurs réseau sont journalisées, jamais propagées : une API
        publique indisponible ne doit pas interrompre la collecte du DS18B20.
        """
        self.polls += 1
        try:
            observation = self.observe()
        except OutdoorError as exc:
            self.errors += 1
            self.last_error = str(exc)
            # Une panne durable (clé révoquée, IP changée) répéterait le même
            # message à chaque cycle : on ne le journalise qu'au changement.
            if self._silenced == str(exc):
                log.debug("température extérieure indisponible : %s", exc)
            else:
                log.warning("température extérieure indisponible : %s", exc)
                self._silenced = str(exc)
            return None

        self._silenced = None
        self.last_error = None
        self.last_ok_ts = int(time.time())

        if self._last_ts is None:
            self._last_ts = self._stored_ts()
        if self._last_ts is not None and observation.ts <= self._last_ts:
            log.debug(
                "observation %s déjà enregistrée (%d)", self.source.describe(), observation.ts
            )
            return None

        reading = Reading(self.address, observation.celsius, observation.ts)
        self.storage.ensure_sensor(self.address, self.config.outdoor_label)
        self.storage.record([reading])
        self._last_ts = observation.ts
        self.stored += 1
        log.info(
            "extérieur (%s) = %.1f °C, observé à %s",
            self.source.describe(),
            observation.celsius,
            datetime.fromtimestamp(observation.ts, timezone.utc).isoformat(),
        )
        return reading

    def _stored_ts(self) -> int | None:
        """Dernier horodatage déjà en base, pour survivre à un redémarrage."""
        for sensor in self.storage.latest():
            if sensor["address"] == self.address:
                return sensor["ts"]
        return None


class OutdoorThread(threading.Thread):
    """Interroge la source extérieure en tâche de fond.

    Un thread séparé, et non un ajout au cycle du collecteur : un appel réseau
    peut bloquer plusieurs secondes, ce qui décalerait les relevés du DS18B20,
    et la cadence utile n'est pas la même — une station publie toutes les 6 à
    60 minutes là où le capteur est lu chaque minute.
    """

    def __init__(self, config, storage, poller=None) -> None:
        super().__init__(name="tempi-outdoor", daemon=True)
        self.poller = poller if poller is not None else OutdoorPoller(config, storage)
        self.interval = max(MIN_INTERVAL, config.outdoor_interval)
        # ``_stop`` masquerait une méthode interne de ``threading.Thread``,
        # employée par ``join()`` : le thread deviendrait injoignable.
        self._stop_event = threading.Event()

    def stop(self) -> None:
        self._stop_event.set()

    def run(self) -> None:
        log.info(
            "température extérieure : %s toutes les %.0f s, capteur %s",
            self.poller.source.describe(),
            self.interval,
            self.poller.address,
        )
        while not self._stop_event.is_set():
            try:
                self.poller.poll_once()
            except Exception:  # pragma: no cover - filet de sécurité
                log.exception("relevé extérieur en échec")
            self._stop_event.wait(self.interval)


def make_thread(config, storage) -> OutdoorThread | None:
    """Construit le thread extérieur si la configuration en décrit un.

    Une configuration invalide est signalée sans empêcher le démarrage : mieux
    vaut enregistrer les DS18B20 sans la courbe extérieure que pas du tout.
    """
    try:
        if make_source(config) is None:
            return None
        return OutdoorThread(config, storage)
    except OutdoorError as exc:
        log.warning("source extérieure ignorée : %s", exc)
        return None
