"""Interface en ligne de commande de tempi."""

from __future__ import annotations

import argparse
import csv
import logging
import signal
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

from . import __version__
from .collector import Collector, RetentionThread, apply_retention
from .config import Config
from .sensor import SensorError, make_bus
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
                value = f"{celsius:7.3f} °C"
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
        print(f"{reading.address}  {reading.celsius:7.3f} °C")
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
