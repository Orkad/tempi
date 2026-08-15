"""Lecture des capteurs de température 1-Wire (DS18B20 et compatibles).

Le noyau Linux expose chaque capteur détecté sous ``/sys/bus/w1/devices/<adresse>``.
Deux fichiers y sont utilisables :

``w1_slave``
    Format historique, sur deux lignes. La première se termine par ``YES`` ou
    ``NO`` selon que le CRC de la trame est valide ; la seconde contient
    ``t=<millidegrés>``.

``temperature``
    Exposé par les noyaux récents, contient directement les millidegrés. La
    lecture échoue (``EIO``) quand le CRC est invalide, ce qui rend la
    vérification implicite.

On préfère ``w1_slave`` car il est disponible partout et permet de distinguer
une erreur de CRC d'une erreur d'entrée/sortie.
"""

from __future__ import annotations

import logging
import math
import random
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence

from .config import DEFAULT_W1_DIR

log = logging.getLogger(__name__)

#: Codes de famille 1-Wire correspondant à des capteurs de température.
TEMPERATURE_FAMILIES = ("28", "10", "22", "3b", "42")

#: Plage de mesure du DS18B20, d'après la fiche technique.
MIN_CELSIUS = -55.0
MAX_CELSIUS = 125.0

#: Valeur du registre de température après une mise sous tension : une lecture
#: à exactement 85 °C traduit presque toujours une conversion interrompue.
RESET_VALUE_MILLIDEGREES = 85000


class SensorError(Exception):
    """Erreur de lecture d'un capteur."""


class CrcError(SensorError):
    """La trame lue est corrompue (CRC invalide)."""


class ResetValueError(SensorError):
    """Le capteur a renvoyé sa valeur de reset (85 °C), typiquement une conversion ratée."""


class OutOfRangeError(SensorError):
    """La valeur lue sort de la plage physique du capteur."""


@dataclass(frozen=True)
class Reading:
    """Une mesure horodatée."""

    address: str
    celsius: float
    ts: int


def _parse_w1_slave(payload: str) -> float:
    """Extrait la température en degrés Celsius du contenu d'un fichier ``w1_slave``."""
    lines = [line for line in payload.splitlines() if line.strip()]
    if len(lines) < 2:
        raise SensorError(f"trame incomplète : {payload!r}")

    if not lines[0].rstrip().endswith("YES"):
        raise CrcError("CRC invalide")

    marker = lines[1].find("t=")
    if marker < 0:
        raise SensorError(f"champ 't=' absent : {lines[1]!r}")

    raw = lines[1][marker + 2 :].strip()
    try:
        millidegrees = int(raw)
    except ValueError as exc:
        raise SensorError(f"valeur de température illisible : {raw!r}") from exc

    return millidegrees / 1000.0


class W1Bus:
    """Accès aux capteurs de température branchés sur le bus 1-Wire."""

    def __init__(
        self,
        w1_dir: Path = DEFAULT_W1_DIR,
        retries: int = 3,
        allow_reset_value: bool = False,
        retry_delay: float = 0.2,
    ) -> None:
        self.w1_dir = Path(w1_dir)
        self.retries = max(1, retries)
        self.allow_reset_value = allow_reset_value
        self.retry_delay = retry_delay

    # -- découverte ---------------------------------------------------------

    def available(self) -> bool:
        """Indique si le bus 1-Wire est monté sur ce système."""
        return self.w1_dir.is_dir()

    def discover(self) -> list[str]:
        """Retourne les adresses des capteurs de température détectés, triées."""
        if not self.available():
            return []
        addresses = [
            entry.name
            for entry in self.w1_dir.iterdir()
            if entry.name.split("-", 1)[0].lower() in TEMPERATURE_FAMILIES
        ]
        return sorted(addresses)

    # -- lecture ------------------------------------------------------------

    def _read_once(self, address: str) -> float:
        path = self.w1_dir / address / "w1_slave"
        try:
            payload = path.read_text()
        except FileNotFoundError as exc:
            raise SensorError(f"capteur {address} introuvable ({path})") from exc
        except OSError as exc:
            # Le pilote renvoie EIO lorsque la conversion échoue.
            raise SensorError(f"lecture de {path} impossible : {exc}") from exc

        celsius = _parse_w1_slave(payload)

        if not self.allow_reset_value and round(celsius * 1000) == RESET_VALUE_MILLIDEGREES:
            raise ResetValueError(
                "valeur de reset 85 °C — vérifiez l'alimentation et la résistance de tirage"
            )
        if not MIN_CELSIUS <= celsius <= MAX_CELSIUS:
            raise OutOfRangeError(f"{celsius} °C hors de la plage du capteur")

        return celsius

    def read(self, address: str) -> float:
        """Lit un capteur, en réessayant sur erreur transitoire.

        Les erreurs de CRC et les valeurs de reset sont fréquentes sur un câblage
        long ; une nouvelle tentative suffit généralement.
        """
        last_error: Exception | None = None
        for attempt in range(1, self.retries + 1):
            try:
                return self._read_once(address)
            except SensorError as exc:
                last_error = exc
                log.debug(
                    "lecture de %s échouée (tentative %d/%d) : %s",
                    address,
                    attempt,
                    self.retries,
                    exc,
                )
                if attempt < self.retries:
                    time.sleep(self.retry_delay)
        assert last_error is not None
        raise last_error

    def read_all(
        self, addresses: Sequence[str] | None = None
    ) -> tuple[list[Reading], list[tuple[str, Exception]]]:
        """Lit plusieurs capteurs et sépare les succès des échecs.

        Un capteur défaillant ne doit jamais interrompre la collecte des autres.
        """
        targets: Iterable[str] = addresses if addresses is not None else self.discover()
        readings: list[Reading] = []
        failures: list[tuple[str, Exception]] = []
        for address in targets:
            try:
                celsius = self.read(address)
            except SensorError as exc:
                failures.append((address, exc))
            else:
                readings.append(Reading(address, celsius, int(time.time())))
        return readings, failures


class SimulatedBus:
    """Bus factice : permet de développer et de tester sans Raspberry Pi.

    Génère une température qui suit un cycle journalier auquel s'ajoute un bruit
    de mesure, ce qui donne des courbes réalistes dans l'interface web.
    """

    def __init__(self, addresses: Sequence[str] = ("28-000005e2fdc3", "28-000005e30a1b")) -> None:
        self._addresses = list(addresses)
        self._random = random.Random(1234)

    def available(self) -> bool:
        return True

    def discover(self) -> list[str]:
        return list(self._addresses)

    def read(self, address: str) -> float:
        try:
            offset = self._addresses.index(address)
        except ValueError as exc:
            raise SensorError(f"capteur simulé {address} inconnu") from exc

        seconds_of_day = time.time() % 86400
        daily = 6.0 * math.sin(2 * math.pi * (seconds_of_day - 6 * 3600) / 86400)
        noise = self._random.uniform(-0.15, 0.15)
        return round(19.0 + offset * 1.5 + daily + noise, 3)

    def read_all(
        self, addresses: Sequence[str] | None = None
    ) -> tuple[list[Reading], list[tuple[str, Exception]]]:
        targets = addresses if addresses is not None else self.discover()
        readings: list[Reading] = []
        failures: list[tuple[str, Exception]] = []
        for address in targets:
            try:
                readings.append(Reading(address, self.read(address), int(time.time())))
            except SensorError as exc:
                failures.append((address, exc))
        return readings, failures


def make_bus(config) -> W1Bus | SimulatedBus:
    """Construit le bus correspondant à la configuration."""
    if config.simulate:
        return SimulatedBus()
    return W1Bus(
        w1_dir=config.w1_dir,
        retries=config.read_retries,
        allow_reset_value=config.allow_reset_value,
    )
