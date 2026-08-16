"""Chiffrement TLS de l'interface web.

tempi n'a aucune dépendance externe : le service TLS repose sur le module
``ssl`` de la bibliothèque standard, et la fabrication des certificats sur le
binaire ``openssl``, présent sur Raspberry Pi OS.

Le certificat du serveur n'est pas auto-signé : il est signé par une petite
autorité locale, créée une seule fois dans le même répertoire. C'est ce qui rend
l'installation supportable sur un téléphone — on installe l'autorité, pas le
certificat — et surtout durable : renouveler le certificat du serveur ne demande
alors plus rien à l'appareil, l'autorité restant valable dix ans.

Les contraintes imposées par Safari (iOS 13 et suivants, macOS 10.15 et
suivants) dictent la forme du certificat : ``subjectAltName`` obligatoire — le
``CN`` n'est plus regardé —, ``extendedKeyUsage`` limité à ``serverAuth``, et
validité inférieure à 398 jours. Un certificat qui les ignore est refusé sans
que le navigateur explique laquelle manque.
"""

from __future__ import annotations

import os
import shutil
import socket
import ssl
import subprocess
import tempfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Sequence

#: Emplacement des certificats quand tempi tourne en service système.
SYSTEM_TLS_DIR = Path("/etc/tempi/tls")

#: Noms des fichiers produits, relatifs au répertoire de travail.
CA_CERT_NAME = "ca.pem"
CA_KEY_NAME = "ca-key.pem"
CERT_NAME = "tempi-cert.pem"
KEY_NAME = "tempi-key.pem"

#: Nom de l'autorité, tel qu'il apparaîtra dans les réglages du téléphone.
CA_COMMON_NAME = "tempi local CA"

#: Validité du certificat du serveur. Safari refuse au-delà de 398 jours.
LEAF_DAYS = 397

#: Validité de l'autorité : c'est elle qu'on installe sur les appareils, autant
#: n'avoir à le refaire qu'une fois.
CA_DAYS = 3650

#: Seuil à partir duquel « tempi doctor » réclame un renouvellement.
RENEW_WARNING_DAYS = 30

#: Utilisateur système créé par scripts/install.sh.
SERVICE_USER = "tempi"


class TlsError(Exception):
    """Certificat inutilisable, ou fabrication impossible."""


@dataclass(frozen=True)
class Bundle:
    """Fichiers produits par :func:`generate`."""

    cert: Path
    key: Path
    ca_cert: Path
    ca_key: Path


@dataclass(frozen=True)
class CertificateInfo:
    """Ce qu'on peut lire d'un certificat sans outil externe."""

    not_after: datetime
    names: list[str]
    addresses: list[str]

    @property
    def days_left(self) -> int:
        delta = self.not_after - datetime.now(timezone.utc)
        return delta.days

    @property
    def expired(self) -> bool:
        return self.not_after <= datetime.now(timezone.utc)

    def subjects(self) -> list[str]:
        return self.names + self.addresses


# -- emplacements ------------------------------------------------------------


def default_tls_dir() -> Path:
    """Choisit où déposer les certificats.

    Même logique que ``default_db_path`` : l'emplacement système quand il est
    accessible en écriture — cas de ``sudo tempi cert`` —, sinon le répertoire
    de données de l'utilisateur.
    """
    parent = SYSTEM_TLS_DIR.parent
    if os.access(SYSTEM_TLS_DIR, os.W_OK) or os.access(parent, os.W_OK):
        return SYSTEM_TLS_DIR

    xdg = os.environ.get("XDG_DATA_HOME")
    base = Path(xdg) if xdg else Path.home() / ".local" / "share"
    return base / "tempi" / "tls"


# -- noms couverts par le certificat -----------------------------------------


def _unique(values: Iterable[str]) -> list[str]:
    """Déduplique en conservant l'ordre : le premier nom sert de sujet."""
    seen: set[str] = set()
    result = []
    for value in values:
        value = value.strip()
        if value and value not in seen:
            seen.add(value)
            result.append(value)
    return result


def default_names() -> list[str]:
    """Noms sous lesquels le Raspberry Pi est joignable sur le réseau local."""
    hostname = socket.gethostname().split(".")[0] or "raspberrypi"
    return _unique([f"{hostname}.local", hostname, "localhost"])


def default_addresses() -> list[str]:
    """Adresses IPv4 de la machine, celle de sortie en tête.

    Beaucoup de navigateurs mobiles ne résolvent pas les noms ``.local`` ; sans
    l'adresse dans le certificat, l'accès par ``https://192.168.…`` échouerait.
    """
    addresses = []

    # Aucun paquet n'est émis : la connexion UDP sert seulement à faire choisir
    # au noyau l'interface qu'il utiliserait pour sortir.
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("192.0.2.1", 53))  # TEST-NET-1, jamais routée
        addresses.append(probe.getsockname()[0])
    except OSError:
        pass
    finally:
        probe.close()

    try:
        for family, _, _, _, sockaddr in socket.getaddrinfo(socket.gethostname(), None):
            if family == socket.AF_INET:
                addresses.append(sockaddr[0])
    except OSError:
        pass

    addresses.append("127.0.0.1")
    return _unique(addresses)


def format_san(names: Sequence[str], addresses: Sequence[str]) -> str:
    """Construit la valeur ``subjectAltName`` attendue par openssl."""
    entries = [f"DNS:{name}" for name in names] + [f"IP:{address}" for address in addresses]
    if not entries:
        raise TlsError("aucun nom ni adresse à inscrire dans le certificat")
    return ",".join(entries)


# -- fabrication -------------------------------------------------------------


def _openssl() -> str:
    binary = shutil.which("openssl")
    if binary is None:
        raise TlsError(
            "openssl est introuvable : « sudo apt install openssl », ou copiez "
            "un certificat produit ailleurs."
        )
    return binary


def _run(args: list[str]) -> None:
    try:
        result = subprocess.run(args, capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.SubprocessError) as exc:
        raise TlsError(f"openssl n'a pas pu être exécuté : {exc}") from exc
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip().splitlines()
        message = detail[-1] if detail else f"code de retour {result.returncode}"
        raise TlsError(f"openssl a échoué : {message}")


def protect_key(path: Path, user: str = SERVICE_USER) -> bool:
    """Restreint la clé privée et tente de la donner au service.

    Retourne ``True`` si le service pourra la lire, ``False`` s'il reste un
    ``chown`` à faire à la main.
    """
    os.chmod(path, 0o640)
    if os.geteuid() != 0:
        return False
    try:
        shutil.chown(path, group=user)
    except (LookupError, PermissionError, OSError):
        return False
    return True


def _ensure_ca(directory: Path) -> tuple[Path, Path, bool]:
    """Crée l'autorité locale si elle n'existe pas déjà.

    Une autorité existante est systématiquement réutilisée : c'est elle qui est
    installée sur les appareils, la remplacer obligerait à refaire le tour des
    téléphones à chaque renouvellement.
    """
    ca_cert = directory / CA_CERT_NAME
    ca_key = directory / CA_KEY_NAME
    if ca_cert.exists() and ca_key.exists():
        return ca_cert, ca_key, False

    _run(
        [
            _openssl(), "req", "-x509", "-newkey", "rsa:2048", "-sha256", "-nodes",
            "-days", str(CA_DAYS),
            "-keyout", str(ca_key), "-out", str(ca_cert),
            "-subj", f"/CN={CA_COMMON_NAME}",
            "-addext", "basicConstraints=critical,CA:TRUE,pathlen:0",
            "-addext", "keyUsage=critical,keyCertSign,cRLSign",
        ]
    )
    os.chmod(ca_key, 0o600)
    os.chmod(ca_cert, 0o644)
    return ca_cert, ca_key, True


def generate(
    directory: Path,
    names: Sequence[str] | None = None,
    addresses: Sequence[str] | None = None,
    days: int = LEAF_DAYS,
    force: bool = False,
) -> tuple[Bundle, bool]:
    """Produit le certificat du serveur, et l'autorité qui le signe si besoin.

    Retourne le lot de fichiers et un booléen indiquant si l'autorité vient
    d'être créée — auquel cas il faut la réinstaller sur les appareils.
    """
    if days < 1 or days > 398:
        raise TlsError("la validité doit tenir entre 1 et 398 jours (limite de Safari)")

    names = _unique(names or default_names())
    addresses = _unique(addresses or default_addresses())
    san = format_san(names, addresses)

    directory.mkdir(parents=True, exist_ok=True)
    cert = directory / CERT_NAME
    key = directory / KEY_NAME
    if cert.exists() and not force:
        raise TlsError(f"{cert} existe déjà : relancez avec --force pour le remplacer")

    ca_cert, ca_key, ca_created = _ensure_ca(directory)

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        csr = tmp_path / "request.csr"
        extensions = tmp_path / "extensions.cnf"
        extensions.write_text(
            "basicConstraints=critical,CA:FALSE\n"
            "keyUsage=critical,digitalSignature,keyEncipherment\n"
            "extendedKeyUsage=serverAuth\n"
            f"subjectAltName={san}\n",
            encoding="utf-8",
        )

        _run(
            [
                _openssl(), "req", "-new", "-newkey", "rsa:2048", "-sha256", "-nodes",
                "-keyout", str(key), "-out", str(csr),
                "-subj", f"/CN={names[0]}",
            ]
        )
        _run(
            [
                _openssl(), "x509", "-req", "-in", str(csr), "-sha256",
                "-CA", str(ca_cert), "-CAkey", str(ca_key), "-CAcreateserial",
                "-days", str(days),
                "-extfile", str(extensions),
                "-out", str(cert),
            ]
        )

    os.chmod(cert, 0o644)
    protect_key(key)
    return Bundle(cert=cert, key=key, ca_cert=ca_cert, ca_key=ca_key), ca_created


# -- lecture -----------------------------------------------------------------


def parse_end_date(output: str) -> datetime:
    """Extrait ``notAfter=Sep 17 09:00:00 2027 GMT`` de la sortie d'openssl."""
    for line in output.splitlines():
        if line.startswith("notAfter="):
            try:
                return datetime.fromtimestamp(
                    ssl.cert_time_to_seconds(line.split("=", 1)[1].strip()), timezone.utc
                )
            except ValueError as exc:
                raise TlsError(f"date d'expiration illisible : {line}") from exc
    raise TlsError("openssl n'a pas renvoyé de date d'expiration")


def parse_san(output: str) -> tuple[list[str], list[str]]:
    """Sépare noms et adresses de la ligne ``DNS:…, IP Address:…`` d'openssl."""
    names, addresses = [], []
    for entry in output.replace("\n", ",").split(","):
        entry = entry.strip()
        if entry.startswith("DNS:"):
            names.append(entry[4:].strip())
        elif entry.startswith("IP Address:"):
            addresses.append(entry[11:].strip())
    return names, addresses


def describe(cert_path: Path) -> CertificateInfo:
    """Lit la date d'expiration et les noms couverts par un certificat.

    Le détour par openssl n'est pas un choix : ``ssl.get_ca_certs`` est le seul
    décodeur de la bibliothèque standard, et il écarte délibérément les
    certificats qui ne sont pas des autorités — donc précisément celui du
    serveur.
    """
    try:
        result = subprocess.run(
            [_openssl(), "x509", "-in", str(cert_path), "-noout", "-enddate",
             "-ext", "subjectAltName"],
            capture_output=True,
            text=True,
            timeout=30,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise TlsError(f"certificat non inspectable ({cert_path}) : {exc}") from exc
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip().splitlines()
        raise TlsError(f"certificat illisible ({cert_path}) : {detail[-1] if detail else ''}")

    names, addresses = parse_san(result.stdout)
    return CertificateInfo(
        not_after=parse_end_date(result.stdout), names=names, addresses=addresses
    )


def load_context(cert_path: Path, key_path: Path) -> ssl.SSLContext:
    """Prépare le contexte TLS du serveur."""
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    # TLS 1.0 et 1.1 sont refusés par les navigateurs récents : les proposer
    # n'apporterait rien qu'une surface d'attaque.
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    try:
        context.load_cert_chain(str(cert_path), str(key_path))
    except FileNotFoundError as exc:
        raise TlsError(f"certificat ou clé introuvable : {exc.filename}") from exc
    except PermissionError as exc:
        raise TlsError(
            f"{exc.filename} n'est pas lisible par l'utilisateur du service : "
            f"« sudo chgrp {SERVICE_USER} {exc.filename} && sudo chmod 640 {exc.filename} »"
        ) from exc
    except (OSError, ssl.SSLError) as exc:
        raise TlsError(f"certificat inutilisable ({cert_path}) : {exc}") from exc
    return context
