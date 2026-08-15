"""Boucle de collecte : lit les capteurs à intervalle régulier et enregistre.

Le rythme est calé sur une horloge absolue plutôt que sur ``sleep(interval)``
après chaque cycle : la lecture d'un DS18B20 prend jusqu'à 750 ms, et cumuler
ce délai ferait dériver les horodatages.
"""

from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass

from .config import Config
from .sensor import Reading, make_bus
from .storage import Storage

log = logging.getLogger(__name__)


@dataclass
class _LastStored:
    ts: int
    celsius: float


class Collector:
    """Interroge les capteurs et écrit les mesures en base."""

    def __init__(self, config: Config, storage: Storage, bus=None) -> None:
        self.config = config
        self.storage = storage
        self.bus = bus if bus is not None else make_bus(config)
        self._stop = threading.Event()
        self._last: dict[str, _LastStored] = {}
        self._known_addresses: set[str] = set()
        #: Compteurs exposés par ``/api/health``.
        self.cycles = 0
        self.stored = 0
        self.errors = 0
        self.last_cycle_ts: int | None = None

    # -- filtrage -----------------------------------------------------------

    def _should_store(self, reading: Reading) -> bool:
        """Applique la bande morte : évite d'écrire des mesures identiques.

        Sur une carte SD, réduire les écritures allonge nettement la durée de
        vie. On conserve toutefois un point au moins toutes les
        ``max_interval`` secondes pour que les courbes ne comportent pas de
        trous, et pour prouver que le capteur répond toujours.
        """
        if self.config.min_delta <= 0:
            return True

        previous = self._last.get(reading.address)
        if previous is None:
            return True
        if abs(reading.celsius - previous.celsius) >= self.config.min_delta:
            return True
        if self.config.max_interval > 0 and reading.ts - previous.ts >= self.config.max_interval:
            return True
        return False

    # -- cycle --------------------------------------------------------------

    def poll_once(self) -> list[Reading]:
        """Effectue un cycle de lecture et retourne les mesures enregistrées."""
        readings, failures = self.bus.read_all()

        for address, error in failures:
            self.errors += 1
            log.warning("capteur %s : %s", address, error)

        for reading in readings:
            if reading.address not in self._known_addresses:
                self._known_addresses.add(reading.address)
                log.info("capteur détecté : %s", reading.address)

        if not readings and not failures:
            log.warning(
                "aucun capteur détecté dans %s — le bus 1-Wire est-il activé ?",
                getattr(self.bus, "w1_dir", "?"),
            )

        to_store = [reading for reading in readings if self._should_store(reading)]
        if to_store:
            self.storage.record(to_store)
            self.stored += len(to_store)
            for reading in to_store:
                self._last[reading.address] = _LastStored(reading.ts, reading.celsius)

        self.cycles += 1
        self.last_cycle_ts = int(time.time())

        for reading in readings:
            log.debug("%s = %.3f °C", reading.address, reading.celsius)

        return to_store

    # -- boucle -------------------------------------------------------------

    def stop(self) -> None:
        self._stop.set()

    def run(self, max_cycles: int | None = None) -> None:
        """Boucle jusqu'à ``stop()`` (ou ``max_cycles`` cycles, pour les tests)."""
        interval = self.config.interval
        log.info(
            "collecte démarrée : intervalle %.1f s, base %s",
            interval,
            self.storage.path,
        )

        next_run = time.monotonic()
        cycles = 0
        while not self._stop.is_set():
            try:
                self.poll_once()
            except Exception:  # une erreur ponctuelle ne doit pas tuer le service
                self.errors += 1
                log.exception("cycle de collecte en échec")

            cycles += 1
            if max_cycles is not None and cycles >= max_cycles:
                break

            # Cadence absolue : on rattrape le retard sans accumuler la dérive.
            next_run += interval
            delay = next_run - time.monotonic()
            if delay < 0:
                missed = int(-delay // interval) + 1
                log.debug("collecte en retard de %.1f s, %d cycle(s) sautés", -delay, missed)
                next_run += missed * interval
                delay = max(0.0, next_run - time.monotonic())
            self._stop.wait(delay)

        log.info(
            "collecte arrêtée après %d cycle(s), %d mesure(s) enregistrée(s), %d erreur(s)",
            self.cycles,
            self.stored,
            self.errors,
        )


def apply_retention(config: Config, storage: Storage) -> int:
    """Supprime les mesures au-delà de la durée de rétention configurée."""
    if config.retention_days <= 0:
        return 0
    cutoff = int(time.time()) - config.retention_days * 86400
    removed = storage.prune(cutoff)
    if removed:
        log.info("rétention : %d mesure(s) de plus de %d jour(s) supprimée(s)",
                 removed, config.retention_days)
    return removed


class RetentionThread(threading.Thread):
    """Applique la rétention une fois par jour, en tâche de fond."""

    def __init__(self, config: Config, storage: Storage, period: float = 86400.0) -> None:
        super().__init__(name="tempi-retention", daemon=True)
        self.config = config
        self.storage = storage
        self.period = period
        self._stop = threading.Event()

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        while not self._stop.is_set():
            try:
                apply_retention(self.config, self.storage)
            except Exception:
                log.exception("application de la rétention en échec")
            self._stop.wait(self.period)
