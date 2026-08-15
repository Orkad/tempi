"""Analyse de l'état du bus 1-Wire.

Ce module ne fait aucun accès système : tout lui est passé en argument. La
collecte est le travail de la commande ``tempi doctor`` (voir ``cli.py``), ce
qui rend l'ensemble des diagnostics vérifiable par des tests, sans Raspberry Pi
ni capteur.

Le cœur du sujet est le périphérique fantôme. Quand le maître 1-Wire interroge
un bus en défaut, il enregistre malgré tout une ROM — de famille ``00``, qui
n'existe chez aucun composant réel. La forme de ces ROM désigne la panne :

``00-800000000000`` constant
    La ligne est tenue à la masse : le maître ne lit que des zéros.

ROM différentes d'un balayage à l'autre
    La ligne est flottante et capte du bruit : la résistance de tirage
    n'établit pas le lien entre la donnée et le 3,3 V.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Sequence

from .sensor import TEMPERATURE_FAMILIES

#: ROM renvoyée lorsque le maître ne lit que des zéros sur la ligne.
STUCK_LOW_ROM = "00-800000000000"

#: Famille des périphériques qui n'existent pas physiquement.
PHANTOM_FAMILY = "00"

#: GPIO utilisé par l'overlay w1-gpio en l'absence de paramètre ``gpiopin``.
DEFAULT_W1_GPIO = 4


@dataclass
class Check:
    """Résultat d'une vérification.

    ``ok`` vaut ``None`` quand la vérification n'a pas pu être menée — outil
    absent, droits insuffisants. Un indéterminé n'est pas un échec : sur une
    machine de développement, la plupart des vérifications matérielles le sont.
    """

    name: str
    ok: bool | None
    detail: str
    remedy: str = ""
    critical: bool = False

    @property
    def symbol(self) -> str:
        return {True: "✓", False: "✗", None: "?"}[self.ok]

    def as_dict(self) -> dict:
        return {
            "name": self.name,
            "ok": self.ok,
            "detail": self.detail,
            "remedy": self.remedy or None,
            "critical": self.critical,
        }


@dataclass
class BusInventory:
    """Contenu de ``/sys/bus/w1/devices``, trié par nature."""

    sensors: list[str] = field(default_factory=list)
    phantoms: list[str] = field(default_factory=list)
    masters: list[str] = field(default_factory=list)


# -- analyse de la configuration --------------------------------------------


def parse_overlay(config_text: str) -> tuple[bool, int]:
    """Cherche ``dtoverlay=w1-gpio`` dans un ``config.txt``.

    Retourne (activé, numéro de GPIO). Les lignes commentées sont ignorées, et
    le paramètre ``gpiopin`` est pris en compte : un montage sur une autre
    broche que le GPIO 4 est parfaitement valide, et diagnostiquer la mauvaise
    broche enverrait sur une fausse piste.
    """
    for raw in config_text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        # « w1-gpio-pullup » est une variante légitime, qui active aussi le bus.
        # Tout autre suffixe désigne un overlay différent : \b ne suffit pas ici,
        # un tiret étant une frontière de mot.
        if not re.match(r"^dtoverlay\s*=\s*w1-gpio(-pullup)?\s*(,|$)", line):
            continue
        match = re.search(r"gpiopin\s*=\s*(\d+)", line)
        return True, int(match.group(1)) if match else DEFAULT_W1_GPIO
    return False, DEFAULT_W1_GPIO


def parse_modules(proc_modules: str) -> set[str]:
    """Extrait les noms de modules chargés du contenu de ``/proc/modules``."""
    names = set()
    for line in proc_modules.splitlines():
        parts = line.split()
        if parts:
            names.add(parts[0])
    return names


def parse_pinctrl(output: str) -> tuple[str, str] | None:
    """Extrait (fonction, niveau) de la sortie de ``pinctrl`` ou ``raspi-gpio``.

    Deux formats coexistent selon la version de Raspberry Pi OS :

        4: ip    pu | lo // GPIO4 = input
        GPIO 4: level=0 func=OUTPUT pull=NONE
    """
    modern = re.search(r"^\s*\d+:\s*(\S+)\s+(\S+)\s*\|\s*(hi|lo)", output, re.MULTILINE)
    if modern:
        return f"{modern.group(1)} {modern.group(2)}", modern.group(3)

    legacy = re.search(r"level=([01]).*?func=(\S+)", output)
    if legacy:
        return legacy.group(2).lower(), "hi" if legacy.group(1) == "1" else "lo"

    return None


# -- analyse du bus ----------------------------------------------------------


def classify_devices(names: Sequence[str]) -> BusInventory:
    """Range le contenu de ``/sys/bus/w1/devices`` par nature.

    Contrairement à ``W1Bus.discover()``, qui ne retient que les capteurs
    utilisables, on conserve ici les fantômes : ce sont eux qui portent le
    diagnostic.
    """
    inventory = BusInventory()
    for name in sorted(names):
        if name.startswith("w1_bus_master"):
            inventory.masters.append(name)
            continue
        family = name.split("-", 1)[0].lower()
        if family in TEMPERATURE_FAMILIES:
            inventory.sensors.append(name)
        elif family == PHANTOM_FAMILY:
            inventory.phantoms.append(name)
    return inventory


def holds_low(gpio_level: str | None, gpio_function: str | None) -> bool:
    """Vrai si la ligne reste basse alors que le tirage interne est actif.

    C'est la preuve la plus solide dont on dispose : le tirage interne du SoC
    vaut une cinquantaine de kilo-ohms, assez pour ramener au niveau haut une
    ligne simplement flottante. S'il n'y parvient pas, un chemin conducteur tire
    vers la masse. Cette observation prime sur la lecture des ROM parasites, qui
    n'est qu'un symptôme indirect.
    """
    if gpio_level != "lo" or not gpio_function:
        return False
    function = gpio_function.lower()
    return "pu" in function.split() or "pull=up" in function


def diagnose_bus(
    inventory: BusInventory,
    second_scan: BusInventory | None = None,
    gpio_level: str | None = None,
    gpio_function: str | None = None,
) -> Check:
    """Nomme l'état du bus à partir de l'inventaire et, si possible, d'un second
    balayage et du niveau électrique de la ligne.

    ``second_scan`` permet de distinguer des fantômes stables — ligne tenue à la
    masse — de fantômes changeants, signature d'une ligne flottante.
    """
    if inventory.sensors:
        detail = f"{len(inventory.sensors)} capteur(s) : {', '.join(inventory.sensors)}"
        if inventory.phantoms:
            return Check(
                "État du bus",
                True,
                detail + f", plus {len(inventory.phantoms)} ROM parasite(s)",
                "Le capteur répond mais le bus est bruité : raccourcissez le câble, "
                "ou descendez la résistance de tirage à 2,2 kΩ.",
            )
        return Check("État du bus", True, detail)

    if not inventory.masters:
        return Check(
            "État du bus",
            False,
            "aucun maître 1-Wire",
            "Le bus n'est pas monté : vérifiez l'overlay puis redémarrez.",
            critical=True,
        )

    if not inventory.phantoms:
        return Check(
            "État du bus",
            False,
            "bus actif, aucun périphérique",
            "Le bus fonctionne mais ne voit rien : le fil de données n'atteint pas "
            "le capteur, ou le capteur n'est pas alimenté.",
            critical=True,
        )

    # Des fantômes, et aucun capteur : la ligne est en défaut. Reste à dire lequel.
    roms = ", ".join(inventory.phantoms)
    stable = second_scan is not None and set(second_scan.phantoms) == set(inventory.phantoms)
    changing = second_scan is not None and not stable
    all_stuck = bool(inventory.phantoms) and all(
        rom == STUCK_LOW_ROM for rom in inventory.phantoms
    )
    grounded = holds_low(gpio_level, gpio_function)

    # Le niveau électrique prime : des ROM changeantes évoquent une ligne
    # flottante, mais si le tirage interne ne parvient pas à la remonter, c'est
    # qu'elle est bel et bien reliée à la masse.
    if grounded or all_stuck or (stable and gpio_level == "lo"):
        if grounded and changing:
            detail = (
                f"ROM parasite(s) changeantes ({roms}), mais la ligne reste basse "
                "malgré le tirage interne"
            )
        elif all_stuck or stable:
            detail = f"ROM parasite(s) constante(s) : {roms}"
        else:
            detail = f"ROM parasite(s) : {roms}"
        return Check(
            "État du bus",
            False,
            detail,
            "Ligne de données reliée à la masse. Le fil de données et la masse "
            "partagent une rangée, la résistance de tirage part sur la masse au lieu "
            "du 3,3 V, ou le capteur est monté à l'envers. Débranchez le fil de "
            "données côté platine : si la ligne reste basse, le défaut est côté "
            "Raspberry Pi.",
            critical=True,
        )

    if changing:
        return Check(
            "État du bus",
            False,
            f"ROM parasite(s) changeantes : {roms}",
            "Ligne de données flottante : elle capte du bruit. La résistance de "
            "4,7 kΩ ne relie pas la donnée au 3,3 V — pattes dans la mauvaise rangée, "
            "ou valeur trop élevée (anneaux jaune, violet, rouge).",
            critical=True,
        )

    return Check(
        "État du bus",
        False,
        f"ROM parasite(s) : {roms}",
        "Aucun capteur ne répond, le bus lit du bruit. Vérifiez la résistance de "
        "tirage entre la donnée et le 3,3 V, puis l'orientation du capteur.",
        critical=True,
    )


def diagnose_gpio(level: str | None, function: str | None) -> Check:
    """Interprète le niveau électrique de la ligne de données au repos.

    Au repos, la résistance de tirage doit maintenir la ligne au niveau haut.
    Un niveau bas persistant signale un chemin conducteur vers la masse.
    """
    if level is None:
        return Check(
            "Niveau de la ligne",
            None,
            "pinctrl et raspi-gpio absents",
            "Installez raspi-utils pour cette vérification.",
        )

    if level == "hi":
        return Check("Niveau de la ligne", True, f"niveau haut au repos ({function})")

    if function and "output" in function.lower():
        return Check(
            "Niveau de la ligne",
            None,
            f"niveau bas, mais la ligne est pilotée ({function})",
            "Le pilote 1-Wire était en train d'émettre : relancez pour obtenir "
            "l'état au repos.",
        )

    return Check(
        "Niveau de la ligne",
        False,
        f"niveau bas au repos ({function})",
        "La résistance de tirage ne fait pas son travail, ou la ligne touche la masse.",
        critical=True,
    )


def summarise(checks: Sequence[Check]) -> tuple[bool, str]:
    """Retourne (tout va bien, message de synthèse).

    Le message reprend le premier échec critique : c'est celui qui explique tous
    les suivants, et enchaîner les remèdes ferait perdre le fil.
    """
    failures = [c for c in checks if c.ok is False]
    if not failures:
        undetermined = [c for c in checks if c.ok is None]
        if undetermined:
            return True, f"Aucun problème détecté ({len(undetermined)} vérification(s) non concluante(s))."
        return True, "Tout est en ordre."

    first = next((c for c in failures if c.critical), failures[0])
    return False, f"{first.name} : {first.detail}\n{first.remedy}".rstrip()
