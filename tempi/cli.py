"""Interface en ligne de commande de tempi."""

from __future__ import annotations

import argparse
import csv
import json
import logging
import os
import shutil
import signal
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

from . import __version__
from .collector import Collector, RetentionThread, apply_retention
from .config import Config
from .diagnostics import (
    DEFAULT_W1_GPIO,
    BusInventory,
    Check,
    classify_devices,
    diagnose_bus,
    diagnose_gpio,
    parse_modules,
    parse_overlay,
    parse_pinctrl,
    summarise,
)
from .sensor import (
    CrcError,
    OutOfRangeError,
    ResetValueError,
    SensorError,
    W1Bus,
    make_bus,
)
from .storage import Storage
from .web import parse_duration, parse_timestamp, serve

log = logging.getLogger("tempi")


def _setup_logging(verbose: bool) -> None:
    logging.basicConfig(
        level=logging.DEBUG if verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%Y-%m-%dT%H:%M:%S",
        stream=sys.stderr,
    )


def _fmt_ts(ts: int | None) -> str:
    if ts is None:
        return "—"
    return datetime.fromtimestamp(ts, timezone.utc).astimezone().strftime("%Y-%m-%d %H:%M:%S")


def _fmt_size(num_bytes: int) -> str:
    value = float(num_bytes)
    for unit in ("o", "Kio", "Mio", "Gio"):
        if value < 1024 or unit == "Gio":
            return f"{value:.1f} {unit}" if unit != "o" else f"{int(value)} {unit}"
        value /= 1024
    return f"{value:.1f} Gio"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="tempi",
        description="Enregistre et consulte l'évolution de la température d'un capteur DS18B20.",
    )
    parser.add_argument("--version", action="version", version=f"tempi {__version__}")
    parser.add_argument("--db", type=Path, help="chemin de la base SQLite (défaut : $TEMPI_DB)")
    parser.add_argument("--w1-dir", type=Path, help="répertoire des périphériques 1-Wire")
    parser.add_argument(
        "--simulate",
        action="store_true",
        help="utilise un capteur simulé (développement sans Raspberry Pi)",
    )
    parser.add_argument("-v", "--verbose", action="store_true", help="journalisation détaillée")

    subparsers = parser.add_subparsers(dest="command", required=True)

    sub = subparsers.add_parser("sensors", help="liste les capteurs détectés et connus")
    sub.set_defaults(func=cmd_sensors)

    sub = subparsers.add_parser("read", help="effectue une lecture immédiate sans rien enregistrer")
    sub.set_defaults(func=cmd_read)

    sub = subparsers.add_parser("collect", help="lance la boucle de collecte")
    sub.add_argument("-i", "--interval", type=float, help="intervalle entre deux lectures, en secondes")
    sub.add_argument("--min-delta", type=float,
                     help="n'enregistre que si l'écart avec la dernière mesure atteint cette valeur (°C)")
    sub.add_argument("--max-interval", type=float,
                     help="durée maximale sans enregistrement malgré --min-delta, en secondes")
    sub.add_argument("--retention-days", type=int, help="supprime les mesures plus anciennes que N jours")
    sub.add_argument("-n", "--cycles", type=int, help="s'arrête après N cycles (utile pour tester)")
    sub.set_defaults(func=cmd_collect)

    sub = subparsers.add_parser("serve", help="lance l'interface web et l'API")
    sub.add_argument("--host", help="adresse d'écoute (défaut : 127.0.0.1)")
    sub.add_argument("-p", "--port", type=int, help="port d'écoute (défaut : 8080)")
    sub.set_defaults(func=cmd_serve)

    sub = subparsers.add_parser("run", help="lance la collecte et l'interface web dans un seul processus")
    sub.add_argument("-i", "--interval", type=float, help="intervalle entre deux lectures, en secondes")
    sub.add_argument("--min-delta", type=float, help="bande morte en °C")
    sub.add_argument("--max-interval", type=float, help="durée maximale sans enregistrement, en secondes")
    sub.add_argument("--retention-days", type=int, help="supprime les mesures plus anciennes que N jours")
    sub.add_argument("--host", help="adresse d'écoute (défaut : 127.0.0.1)")
    sub.add_argument("-p", "--port", type=int, help="port d'écoute (défaut : 8080)")
    sub.set_defaults(func=cmd_run)

    sub = subparsers.add_parser("export", help="exporte les mesures au format CSV")
    sub.add_argument("--from", dest="start", help="début (epoch ou date ISO 8601)")
    sub.add_argument("--to", dest="end", help="fin (epoch ou date ISO 8601)")
    sub.add_argument("--range", help="fenêtre relative à maintenant, par exemple 7d")
    sub.add_argument("--sensor", action="append", help="limite à ce capteur (répétable)")
    sub.add_argument("-o", "--output", type=Path, help="fichier de sortie (défaut : sortie standard)")
    sub.set_defaults(func=cmd_export)

    sub = subparsers.add_parser("prune", help="supprime les mesures anciennes")
    sub.add_argument("--retention-days", type=int, help="conserve les N derniers jours")
    sub.add_argument("--vacuum", action="store_true", help="compacte la base après la purge")
    sub.set_defaults(func=cmd_prune)

    sub = subparsers.add_parser("label", help="donne un nom lisible à un capteur")
    sub.add_argument("address", help="adresse 1-Wire, par exemple 28-000005e2fdc3")
    sub.add_argument("name", nargs="?", help="nom à attribuer (omis : efface le nom)")
    sub.set_defaults(func=cmd_label)

    sub = subparsers.add_parser("stats", help="affiche l'état de la base")
    sub.set_defaults(func=cmd_stats)

    sub = subparsers.add_parser(
        "doctor", help="diagnostique le bus 1-Wire et nomme la panne"
    )
    sub.add_argument("--json", action="store_true", help="sortie exploitable par un script")
    sub.set_defaults(func=cmd_doctor)

    return parser


def _config_from_args(args: argparse.Namespace) -> Config:
    config = Config.from_env()
    for attr in ("interval", "min_delta", "max_interval", "retention_days", "host", "port", "w1_dir"):
        value = getattr(args, attr, None)
        if value is not None:
            setattr(config, attr, value)
    if getattr(args, "db", None):
        config.db_path = args.db
    if getattr(args, "simulate", False):
        config.simulate = True
    config.validate()
    return config


# -- commandes --------------------------------------------------------------


def cmd_sensors(args: argparse.Namespace, config: Config) -> int:
    bus = make_bus(config)
    detected = bus.discover()

    if not detected:
        print("Aucun capteur détecté.", file=sys.stderr)
        if not config.simulate:
            print(
                f"Vérifiez que le bus 1-Wire est activé et que {config.w1_dir} existe "
                "(voir la section « Câblage » du README).",
                file=sys.stderr,
            )
    else:
        print(f"{len(detected)} capteur(s) détecté(s) sur le bus :")
        for address in detected:
            try:
                celsius = bus.read(address)
                value = f"{celsius:6.1f} °C"
            except SensorError as exc:
                value = f"erreur : {exc}"
            print(f"  {address}  {value}")

    with Storage(config.db_path) as storage:
        known = storage.sensors()
        if known:
            print(f"\nCapteurs enregistrés dans {config.db_path} :")
            for sensor in known:
                label = f" « {sensor['label']} »" if sensor["label"] else ""
                print(
                    f"  {sensor['address']}{label}  {sensor['count']} mesure(s), "
                    f"dernière vue {_fmt_ts(sensor['last_seen'])}"
                )
    return 0


def cmd_read(args: argparse.Namespace, config: Config) -> int:
    bus = make_bus(config)
    readings, failures = bus.read_all()
    for reading in readings:
        print(f"{reading.address}  {reading.celsius:6.1f} °C")
    for address, error in failures:
        print(f"{address}  erreur : {error}", file=sys.stderr)
    if not readings and not failures:
        print("Aucun capteur détecté.", file=sys.stderr)
        return 1
    return 1 if failures and not readings else 0


def _install_signal_handlers(*stoppables) -> None:
    def handler(signum, _frame):
        log.info("signal %s reçu, arrêt en cours…", signal.Signals(signum).name)
        for stoppable in stoppables:
            stoppable.stop()

    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, handler)


def cmd_collect(args: argparse.Namespace, config: Config) -> int:
    with Storage(config.db_path) as storage:
        apply_retention(config, storage)
        collector = Collector(config, storage)
        retention = RetentionThread(config, storage)
        retention.start()
        _install_signal_handlers(collector, retention)
        collector.run(max_cycles=args.cycles)
        retention.stop()
    return 0


def cmd_serve(args: argparse.Namespace, config: Config) -> int:
    with Storage(config.db_path) as storage:
        try:
            serve(config, storage)
        except KeyboardInterrupt:
            log.info("arrêt du serveur")
    return 0


def cmd_run(args: argparse.Namespace, config: Config) -> int:
    with Storage(config.db_path) as storage:
        apply_retention(config, storage)
        collector = Collector(config, storage)
        retention = RetentionThread(config, storage)

        collector_thread = threading.Thread(
            target=collector.run, name="tempi-collector", daemon=True
        )
        collector_thread.start()
        retention.start()

        from .web import make_server

        server = make_server(config, storage, collector)
        host = config.host if config.host not in ("0.0.0.0", "::") else "<toutes interfaces>"
        log.info("interface web disponible sur http://%s:%d/", host, server.server_address[1])

        def shutdown():
            collector.stop()
            retention.stop()
            threading.Thread(target=server.shutdown, daemon=True).start()

        class _Stoppable:
            stop = staticmethod(shutdown)

        _install_signal_handlers(_Stoppable())
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            shutdown()
        finally:
            server.server_close()
            collector.stop()
            retention.stop()
            collector_thread.join(timeout=5)
    return 0


def cmd_export(args: argparse.Namespace, config: Config) -> int:
    now = int(time.time())
    with Storage(config.db_path) as storage:
        if args.start:
            start = parse_timestamp(args.start)
        elif args.range:
            start = now - parse_duration(args.range)
        else:
            first, _ = storage.time_range()
            start = first if first is not None else now
        end = parse_timestamp(args.end) if args.end else now

        stream = args.output.open("w", newline="", encoding="utf-8") if args.output else sys.stdout
        try:
            writer = csv.writer(stream)
            writer.writerow(["timestamp_utc", "epoch", "address", "label", "celsius"])
            rows = 0
            for row in storage.iter_rows(start, end, args.sensor):
                iso = datetime.fromtimestamp(row["ts"], timezone.utc).isoformat()
                writer.writerow([iso, row["ts"], row["address"], row["label"] or "", row["celsius"]])
                rows += 1
        finally:
            if args.output:
                stream.close()

    if args.output:
        print(f"{rows} mesure(s) exportée(s) vers {args.output}", file=sys.stderr)
    return 0


def cmd_prune(args: argparse.Namespace, config: Config) -> int:
    if config.retention_days <= 0:
        print(
            "Aucune rétention configurée : précisez --retention-days ou TEMPI_RETENTION_DAYS.",
            file=sys.stderr,
        )
        return 2
    with Storage(config.db_path) as storage:
        removed = apply_retention(config, storage)
        if args.vacuum:
            storage.vacuum()
    print(f"{removed} mesure(s) supprimée(s).")
    return 0


def cmd_label(args: argparse.Namespace, config: Config) -> int:
    with Storage(config.db_path) as storage:
        if not storage.set_label(args.address, args.name):
            print(f"Capteur inconnu : {args.address}", file=sys.stderr)
            return 1
    if args.name:
        print(f"{args.address} → « {args.name} »")
    else:
        print(f"Nom effacé pour {args.address}")
    return 0


def cmd_stats(args: argparse.Namespace, config: Config) -> int:
    with Storage(config.db_path) as storage:
        stats = storage.stats()
        print(f"Base       : {stats['db_path']} ({_fmt_size(stats['db_bytes'])})")
        print(f"Capteurs   : {stats['sensors']}")
        print(f"Mesures    : {stats['readings']}")
        print(f"Première   : {_fmt_ts(stats['first_ts'])}")
        print(f"Dernière   : {_fmt_ts(stats['last_ts'])}")
        for sensor in storage.sensors():
            label = f" « {sensor['label']} »" if sensor["label"] else ""
            print(f"  {sensor['address']}{label} : {sensor['count']} mesure(s)")
    return 0


# -- diagnostic -------------------------------------------------------------

#: Même ordre que scripts/install.sh : Bookworm d'abord, versions antérieures ensuite.
BOOT_CONFIGS = (Path("/boot/firmware/config.txt"), Path("/boot/config.txt"))


def _read_text(path: Path) -> str | None:
    try:
        return path.read_text()
    except OSError:
        return None


def _check_overlay() -> tuple[Check, int]:
    """Vérifie l'activation du 1-Wire et retourne le GPIO effectivement utilisé."""
    for path in BOOT_CONFIGS:
        text = _read_text(path)
        if text is None:
            continue
        enabled, gpio = parse_overlay(text)
        if enabled:
            return Check("Overlay 1-Wire", True, f"GPIO {gpio}, déclaré dans {path}"), gpio
        return (
            Check(
                "Overlay 1-Wire",
                False,
                f"absent de {path}",
                "Lancez « sudo raspi-config » (Interface Options > 1-Wire), ou ajoutez "
                "« dtoverlay=w1-gpio », puis redémarrez.",
                critical=True,
            ),
            gpio,
        )
    return (
        Check("Overlay 1-Wire", None, "aucun config.txt lisible sur cette machine"),
        DEFAULT_W1_GPIO,
    )


def _check_modules() -> Check:
    text = _read_text(Path("/proc/modules"))
    if text is None:
        return Check("Modules noyau", None, "/proc/modules illisible")
    loaded = parse_modules(text)
    missing = [name for name in ("w1_gpio", "w1_therm") if name not in loaded]
    if missing:
        return Check(
            "Modules noyau",
            False,
            f"absent(s) : {', '.join(missing)}",
            "« sudo modprobe w1-gpio w1-therm ». S'ils refusent de se charger, "
            "l'overlay n'est pas actif : il faut redémarrer.",
            critical=True,
        )
    return Check("Modules noyau", True, "w1_gpio et w1_therm chargés")


def _list_devices(w1_dir: Path) -> list[str]:
    try:
        return [entry.name for entry in w1_dir.iterdir()]
    except OSError:
        return []


def _rescan(w1_dir: Path) -> bool:
    """Force un nouveau balayage du bus. Retourne False si les droits manquent."""
    masters = sorted(w1_dir.glob("w1_bus_master*"))
    if not masters:
        return False
    for master in masters:
        try:
            (master / "w1_master_search").write_text("1\n")
        except OSError:
            return False
    return True


def _read_gpio(gpio: int) -> tuple[str | None, str | None]:
    """Retourne (niveau, fonction) de la ligne, via pinctrl ou raspi-gpio."""
    for tool in ("pinctrl", "raspi-gpio"):
        binary = shutil.which(tool)
        if binary is None:
            continue
        try:
            result = subprocess.run(
                [binary, "get", str(gpio)], capture_output=True, text=True, timeout=5
            )
        except (OSError, subprocess.SubprocessError):
            continue
        parsed = parse_pinctrl(result.stdout)
        if parsed is not None:
            function, level = parsed
            return level, function
    return None, None


def _check_sensor_reads(bus: W1Bus, addresses: list[str]) -> list[Check]:
    """Lit chaque capteur détecté et traduit l'échec éventuel en geste correctif."""
    checks: list[Check] = []
    for address in addresses:
        name = f"Lecture {address}"
        try:
            celsius = bus.read(address)
        except CrcError:
            checks.append(
                Check(
                    name,
                    False,
                    "CRC invalide à chaque tentative",
                    "Contact incertain ou câble trop long : raccourcissez la liaison, "
                    "ou descendez la résistance de tirage à 2,2 kΩ.",
                    critical=True,
                )
            )
        except ResetValueError:
            checks.append(
                Check(
                    name,
                    False,
                    "valeur de reset 85 °C",
                    "Alimentation insuffisante : alimentez le capteur en 3,3 V plutôt "
                    "qu'en parasite.",
                    critical=True,
                )
            )
        except OutOfRangeError as exc:
            checks.append(
                Check(
                    name,
                    False,
                    str(exc),
                    "Valeur hors de la plage du capteur : la ligne de données est perturbée.",
                    critical=True,
                )
            )
        except SensorError as exc:
            checks.append(Check(name, False, str(exc), critical=True))
        else:
            # Trois décimales ici, contrairement au reste de l'application : le
            # diagnostic est le seul endroit où la quantification du capteur, par
            # pas de 0,0625 °C, porte une information — une valeur parfaitement
            # figée d'une lecture à l'autre trahit un capteur bloqué.
            checks.append(Check(name, True, f"{celsius:.3f} °C"))
    return checks


def _check_storage(config: Config) -> Check:
    if config.db_path.exists():
        if os.access(config.db_path, os.W_OK):
            return Check("Stockage", True, f"{config.db_path} accessible en écriture")
        return Check(
            "Stockage",
            False,
            f"{config.db_path} non inscriptible",
            "Vérifiez le propriétaire du fichier.",
            critical=True,
        )
    parent = config.db_path.parent
    if parent.is_dir() and os.access(parent, os.W_OK):
        return Check("Stockage", True, f"{parent} accessible, base à créer")
    return Check(
        "Stockage",
        False,
        f"{parent} non inscriptible",
        "Créez ce répertoire ou corrigez ses droits.",
        critical=True,
    )


def _check_service() -> Check:
    binary = shutil.which("systemctl")
    if binary is None:
        return Check("Service", None, "systemctl absent")
    try:
        result = subprocess.run(
            [binary, "is-active", "tempi"], capture_output=True, text=True, timeout=5
        )
    except (OSError, subprocess.SubprocessError):
        return Check("Service", None, "état indéterminable")
    state = result.stdout.strip() or "inconnu"
    if state == "active":
        return Check("Service", True, "tempi.service actif")
    # Non critique : « tempi doctor » sert justement à préparer le terrain avant
    # de lancer le service.
    return Check(
        "Service",
        None,
        f"tempi.service : {state}",
        "« sudo systemctl start tempi » si vous l'attendiez en marche.",
    )


def cmd_doctor(args: argparse.Namespace, config: Config) -> int:
    if config.simulate:
        print("Mode simulé : les vérifications matérielles sont sans objet.\n", file=sys.stderr)

    checks: list[Check] = []

    overlay_check, gpio = _check_overlay()
    checks.append(overlay_check)
    checks.append(_check_modules())

    bus = W1Bus(
        w1_dir=config.w1_dir,
        retries=config.read_retries,
        allow_reset_value=config.allow_reset_value,
    )
    if bus.available():
        checks.append(Check("Répertoire 1-Wire", True, str(config.w1_dir)))
    else:
        checks.append(
            Check(
                "Répertoire 1-Wire",
                False,
                f"{config.w1_dir} absent",
                "Le bus n'est pas monté : activez l'overlay puis redémarrez.",
                critical=True,
            )
        )

    inventory = classify_devices(_list_devices(config.w1_dir))

    # Un second balayage ne sert que si le bus ne renvoie que des fantômes :
    # c'est leur stabilité qui distingue une ligne à la masse d'une ligne flottante.
    second: BusInventory | None = None
    if inventory.phantoms and not inventory.sensors:
        if _rescan(config.w1_dir):
            time.sleep(1.5)
            second = classify_devices(_list_devices(config.w1_dir))
        else:
            checks.append(
                Check(
                    "Second balayage",
                    None,
                    "droits insuffisants pour relancer une recherche",
                    "Relancez avec sudo pour distinguer une ligne à la masse d'une "
                    "ligne flottante.",
                )
            )

    level, function = _read_gpio(gpio)
    checks.append(diagnose_bus(inventory, second, level, function))
    checks.append(diagnose_gpio(level, function))
    checks.extend(_check_sensor_reads(bus, inventory.sensors))
    checks.append(_check_storage(config))
    checks.append(_check_service())

    ok, message = summarise(checks)

    if args.json:
        print(
            json.dumps(
                {"ok": ok, "summary": message, "checks": [c.as_dict() for c in checks]},
                ensure_ascii=False,
                indent=2,
            )
        )
        return 0 if ok else 1

    width = max(len(check.name) for check in checks)
    for check in checks:
        print(f"  {check.symbol}  {check.name.ljust(width)}  {check.detail}")
    print()
    if ok:
        print(message)
    else:
        print("Cause la plus probable")
        print("──────────────────────")
        print(message)
    return 0 if ok else 1


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    _setup_logging(args.verbose)
    try:
        config = _config_from_args(args)
    except ValueError as exc:
        parser.error(str(exc))
    try:
        return args.func(args, config)
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
