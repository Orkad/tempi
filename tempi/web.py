"""Serveur HTTP : API JSON et interface de consultation.

Basé sur ``http.server`` de la bibliothèque standard, pour qu'une installation
sur Raspberry Pi ne demande aucune dépendance externe.

Le serveur n'a pas d'authentification : il écoute par défaut sur 127.0.0.1.
Pour l'exposer sur le réseau local, voir la section correspondante du README.
"""

from __future__ import annotations

import csv
import io
import json
import logging
import re
import time
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from . import __version__
from .config import Config
from .storage import Storage, choose_bucket

log = logging.getLogger(__name__)

STATIC_DIR = Path(__file__).parent / "static"

#: Raccourcis de plage acceptés par ``?range=``.
_DURATION_RE = re.compile(r"^(\d+(?:\.\d+)?)\s*([smhdw])$", re.IGNORECASE)
_DURATION_UNITS = {"s": 1, "m": 60, "h": 3600, "d": 86400, "w": 604800}


class BadRequest(Exception):
    """Paramètre de requête invalide."""


def parse_duration(value: str) -> int:
    """Convertit ``90m``, ``24h``, ``7d``… en secondes."""
    match = _DURATION_RE.match(value.strip())
    if not match:
        raise BadRequest(f"durée invalide : {value!r} (exemples : 30m, 24h, 7d)")
    return int(float(match.group(1)) * _DURATION_UNITS[match.group(2).lower()])


def parse_timestamp(value: str) -> int:
    """Accepte un epoch en secondes ou une date ISO 8601."""
    value = value.strip()
    try:
        return int(float(value))
    except ValueError:
        pass
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise BadRequest(f"date invalide : {value!r}") from exc
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return int(parsed.timestamp())


def resolve_window(params: dict[str, list[str]], storage: Storage) -> tuple[int, int]:
    """Détermine la fenêtre temporelle demandée.

    Priorité : ``from``/``to`` explicites, puis ``range`` relatif à maintenant,
    et par défaut les 24 dernières heures.
    """
    now = int(time.time())

    if "range" in params and params["range"][0] == "all":
        first, last = storage.time_range()
        if first is None:
            return now - 86400, now
        return first, last or now

    end = parse_timestamp(params["to"][0]) if "to" in params else now
    if "from" in params:
        start = parse_timestamp(params["from"][0])
    elif "range" in params:
        start = end - parse_duration(params["range"][0])
    else:
        start = end - 86400

    if start > end:
        raise BadRequest("'from' est postérieur à 'to'")
    return start, end


class Handler(BaseHTTPRequestHandler):
    """Routeur HTTP. ``storage``/``config`` sont injectés par ``make_server``."""

    server_version = f"tempi/{__version__}"
    protocol_version = "HTTP/1.1"

    storage: Storage
    config: Config
    collector = None

    # -- utilitaires de réponse --------------------------------------------

    def _send(self, status: HTTPStatus, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _send_json(self, payload, status: HTTPStatus = HTTPStatus.OK) -> None:
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self._send(status, body, "application/json; charset=utf-8")

    def _send_error_json(self, status: HTTPStatus, message: str) -> None:
        self._send_json({"error": message}, status)

    def log_message(self, fmt: str, *args) -> None:  # noqa: A003 - signature imposée
        log.debug("%s - %s", self.address_string(), fmt % args)

    # -- routage ------------------------------------------------------------

    def do_GET(self) -> None:  # noqa: N802 - signature imposée
        url = urlparse(self.path)
        params = parse_qs(url.query)
        route = url.path.rstrip("/") or "/"

        try:
            if route == "/":
                self._serve_index()
            elif route == "/api/health":
                self._api_health()
            elif route == "/api/sensors":
                self._api_sensors()
            elif route == "/api/latest":
                self._api_latest()
            elif route == "/api/series":
                self._api_series(params)
            elif route == "/api/summary":
                self._api_summary(params)
            elif route in ("/api/export.csv", "/api/export"):
                self._api_export(params)
            else:
                self._send_error_json(HTTPStatus.NOT_FOUND, f"route inconnue : {url.path}")
        except BadRequest as exc:
            self._send_error_json(HTTPStatus.BAD_REQUEST, str(exc))
        except BrokenPipeError:
            pass  # le navigateur a fermé l'onglet en cours de transfert
        except Exception as exc:  # pragma: no cover - filet de sécurité
            log.exception("erreur lors du traitement de %s", self.path)
            self._send_error_json(HTTPStatus.INTERNAL_SERVER_ERROR, str(exc))

    do_HEAD = do_GET

    def do_POST(self) -> None:  # noqa: N802 - signature imposée
        url = urlparse(self.path)
        route = url.path.rstrip("/")
        try:
            match = re.fullmatch(r"/api/sensors/([0-9a-zA-Z._-]+)/label", route)
            if match:
                self._api_set_label(match.group(1))
            else:
                self._send_error_json(HTTPStatus.NOT_FOUND, f"route inconnue : {url.path}")
        except BadRequest as exc:
            self._send_error_json(HTTPStatus.BAD_REQUEST, str(exc))
        except Exception as exc:  # pragma: no cover - filet de sécurité
            log.exception("erreur lors du traitement de %s", self.path)
            self._send_error_json(HTTPStatus.INTERNAL_SERVER_ERROR, str(exc))

    # -- points d'entrée ----------------------------------------------------

    def _serve_index(self) -> None:
        index = STATIC_DIR / "index.html"
        try:
            body = index.read_bytes()
        except OSError:
            self._send_error_json(HTTPStatus.NOT_FOUND, "interface web introuvable")
            return
        self._send(HTTPStatus.OK, body, "text/html; charset=utf-8")

    def _api_health(self) -> None:
        stats = self.storage.stats()
        payload = {
            "status": "ok",
            "version": __version__,
            "now": int(time.time()),
            "storage": stats,
        }
        if self.collector is not None:
            payload["collector"] = {
                "cycles": self.collector.cycles,
                "stored": self.collector.stored,
                "errors": self.collector.errors,
                "last_cycle_ts": self.collector.last_cycle_ts,
                "interval": self.config.interval,
            }
        self._send_json(payload)

    def _api_sensors(self) -> None:
        self._send_json({"sensors": self.storage.sensors()})

    def _api_latest(self) -> None:
        self._send_json({"now": int(time.time()), "sensors": self.storage.latest()})

    def _api_series(self, params: dict[str, list[str]]) -> None:
        start, end = resolve_window(params, self.storage)
        addresses = params.get("sensor") or None

        if "bucket" in params:
            raw = params["bucket"][0]
            if raw in ("auto", ""):
                bucket = choose_bucket(end - start)
            elif raw == "raw":
                bucket = 0
            else:
                try:
                    bucket = max(0, int(raw))
                except ValueError:
                    bucket = parse_duration(raw)
        else:
            bucket = choose_bucket(end - start)

        series = self.storage.series(start, end, addresses, bucket)
        labels = {s["address"]: s["label"] for s in self.storage.sensors()}
        self._send_json(
            {
                "from": start,
                "to": end,
                "bucket": bucket,
                "series": [
                    {"address": address, "label": labels.get(address), "points": points}
                    for address, points in sorted(series.items())
                ],
            }
        )

    def _api_summary(self, params: dict[str, list[str]]) -> None:
        start, end = resolve_window(params, self.storage)
        addresses = params.get("sensor") or None
        self._send_json(
            {"from": start, "to": end, "summary": self.storage.summary(start, end, addresses)}
        )

    def _api_export(self, params: dict[str, list[str]]) -> None:
        start, end = resolve_window(params, self.storage)
        addresses = params.get("sensor") or None

        buffer = io.StringIO()
        writer = csv.writer(buffer)
        writer.writerow(["timestamp_utc", "epoch", "address", "label", "celsius"])
        for row in self.storage.iter_rows(start, end, addresses):
            iso = datetime.fromtimestamp(row["ts"], timezone.utc).isoformat()
            writer.writerow([iso, row["ts"], row["address"], row["label"] or "", row["celsius"]])

        body = buffer.getvalue().encode("utf-8")
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/csv; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Content-Disposition", 'attachment; filename="tempi-export.csv"')
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _api_set_label(self, address: str) -> None:
        length = int(self.headers.get("Content-Length") or 0)
        if length > 64 * 1024:
            raise BadRequest("corps de requête trop volumineux")
        raw = self.rfile.read(length) if length else b"{}"
        try:
            payload = json.loads(raw or b"{}")
        except json.JSONDecodeError as exc:
            raise BadRequest(f"JSON invalide : {exc}") from exc
        if not isinstance(payload, dict):
            raise BadRequest("un objet JSON est attendu")

        label = payload.get("label")
        if label is not None:
            if not isinstance(label, str):
                raise BadRequest("'label' doit être une chaîne")
            label = label.strip()[:80] or None

        if not self.storage.set_label(address, label):
            self._send_error_json(HTTPStatus.NOT_FOUND, f"capteur inconnu : {address}")
            return
        self._send_json({"address": address, "label": label})


def make_server(config: Config, storage: Storage, collector=None) -> ThreadingHTTPServer:
    """Construit le serveur HTTP sans le démarrer."""
    handler = type(
        "BoundHandler",
        (Handler,),
        {"storage": storage, "config": config, "collector": collector},
    )
    server = ThreadingHTTPServer((config.host, config.port), handler)
    server.daemon_threads = True
    return server


def serve(config: Config, storage: Storage, collector=None) -> None:
    """Démarre le serveur et bloque jusqu'à l'interruption."""
    server = make_server(config, storage, collector)
    host = config.host if config.host not in ("0.0.0.0", "::") else "<toutes interfaces>"
    log.info("interface web disponible sur http://%s:%d/", host, server.server_address[1])
    try:
        server.serve_forever()
    finally:
        server.server_close()
