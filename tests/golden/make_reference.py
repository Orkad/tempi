#!/usr/bin/env python3
"""Fabrique ``tests/golden/reference.db``, la base de référence du golden master.

Cette base est **versionnée** et sert d'entrée fixe aux deux implémentations : on
compare ensuite leurs réponses octet à octet. Elle n'est donc pas régénérée à chaque
exécution — ce script existe pour documenter sa construction et pouvoir la refaire.

Les valeurs ne sortent pas de ``SimulatedBus`` : celui-ci s'appuie sur
``random.Random``, le Mersenne Twister de Python, qu'aucune autre plateforme ne
reproduit. On utilise un générateur congruentiel écrit ici, trivial à réimplémenter,
et surtout on fige le résultat dans le fichier.

    python3 tests/golden/make_reference.py
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tempi.sensor import Reading  # noqa: E402
from tempi.storage import Storage  # noqa: E402

#: 2026-01-01T00:00:00Z — début de la fenêtre de référence.
BASE = 1767225600
#: 48 heures : assez large pour que le sous-échantillonnage automatique choisisse
#: plusieurs paliers de bucket selon la plage demandée.
SPAN = 48 * 3600
END = BASE + SPAN

SALON = "28-000005e2fdc3"
CAVE = "28-000005e30a1b"
OUTDOOR = "outdoor-metar-LFLY"

#: Trou volontaire dans la série du salon : vérifie que l'agrégation et le tracé
#: gèrent une interruption de collecte.
GAP_START = BASE + 20 * 3600
GAP_END = BASE + 23 * 3600


def _lcg(seed: int):
    """Générateur congruentiel (Numerical Recipes) : déterministe et portable."""
    state = seed
    while True:
        state = (1664525 * state + 1013904223) % (2**32)
        yield state / 2**32 - 0.5


def _quantize(celsius: float) -> float:
    """Arrondit au pas réel d'un DS18B20 en 12 bits, soit 1/16 de degré."""
    return round(celsius * 16) / 16


def build(path: Path) -> None:
    if path.exists():
        path.unlink()
    for suffix in ("-wal", "-shm"):
        sidecar = path.with_name(path.name + suffix)
        if sidecar.exists():
            sidecar.unlink()

    noise = _lcg(20260101)
    readings: list[Reading] = []

    # Salon : un relevé par minute, avec une coupure de trois heures.
    for ts in range(BASE, END + 1, 60):
        if GAP_START <= ts < GAP_END:
            continue
        hour = (ts - BASE) / 3600.0
        celsius = 19.5 + 2.5 * math.sin(2 * math.pi * (hour - 6) / 24) + next(noise) * 0.2
        readings.append(Reading(SALON, _quantize(celsius), ts))

    # Cave : plus froide, plus stable, et une excursion sous zéro la deuxième nuit.
    for ts in range(BASE, END + 1, 60):
        hour = (ts - BASE) / 3600.0
        celsius = 6.0 + 1.2 * math.sin(2 * math.pi * (hour - 4) / 24) + next(noise) * 0.1
        if 30 <= hour <= 34:
            celsius -= 8.0
        readings.append(Reading(CAVE, _quantize(celsius), ts))

    # Extérieur : une observation toutes les demi-heures, comme une station METAR.
    for ts in range(BASE, END + 1, 1800):
        hour = (ts - BASE) / 3600.0
        celsius = 3.0 + 7.0 * math.sin(2 * math.pi * (hour - 9) / 24) + next(noise) * 0.4
        readings.append(Reading(OUTDOOR, round(celsius, 1), ts))

    with Storage(path) as storage:
        storage.record(readings)
        storage.set_label(SALON, "Salon")
        storage.ensure_sensor(OUTDOOR, "Extérieur")
        # Un capteur connu mais sans aucune mesure : /api/latest doit le rendre
        # avec ts et celsius à null (jointure externe).
        storage.sensor_id("28-0000ffffffff")

        # ``sensor_id()`` renseigne ``first_seen`` avec l'heure courante : sans ce
        # recalage, /api/sensors renverrait une valeur différente à chaque
        # génération et le golden master serait inutilisable.
        storage.conn.execute(
            """
            UPDATE sensors SET
                first_seen = COALESCE(
                    (SELECT MIN(ts) FROM readings WHERE sensor_id = sensors.id), ?),
                last_seen  = (SELECT MAX(ts) FROM readings WHERE sensor_id = sensors.id)
            """,
            (BASE,),
        )
        # VACUUM compacte et normalise l'agencement des pages : deux générations
        # successives produisent alors le même fichier, donc un diff lisible.
        storage.conn.execute("VACUUM")
        # Ramène le journal WAL dans le fichier principal : la base versionnée doit
        # se suffire à elle-même, sans fichier -wal à côté.
        storage.conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")

    for suffix in ("-wal", "-shm"):
        sidecar = path.with_name(path.name + suffix)
        if sidecar.exists():
            sidecar.unlink()

    print(f"{path} : {len(readings)} mesures, {path.stat().st_size} octets")
    print(f"fenêtre de référence : from={BASE} to={END}")


if __name__ == "__main__":
    build(Path(__file__).resolve().parent / "reference.db")
