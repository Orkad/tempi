"""Tests du chiffrement TLS : fabrication du certificat et service HTTPS."""

import shutil
import ssl
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path
from tempfile import TemporaryDirectory

from tempi import tls
from tempi.config import Config
from tempi.storage import Storage
from tempi.web import make_server, server_url

from test_web import make_config

HAS_OPENSSL = shutil.which("openssl") is not None
needs_openssl = unittest.skipUnless(HAS_OPENSSL, "openssl absent de cette machine")


class ParsingTests(unittest.TestCase):
    """Analyse de la sortie d'openssl, sans openssl."""

    SAMPLE = (
        "notAfter=Sep 17 07:54:46 2027 GMT\n"
        "X509v3 Subject Alternative Name: \n"
        "    DNS:r4.local, DNS:r4, IP Address:192.168.1.42, IP Address:127.0.0.1\n"
    )

    def test_end_date(self):
        parsed = tls.parse_end_date(self.SAMPLE)
        self.assertEqual(parsed.year, 2027)
        self.assertEqual(parsed.month, 9)
        self.assertEqual(parsed.day, 17)

    def test_end_date_missing(self):
        with self.assertRaises(tls.TlsError):
            tls.parse_end_date("X509v3 Subject Alternative Name:\n    DNS:r4\n")

    def test_san(self):
        names, addresses = tls.parse_san(self.SAMPLE)
        self.assertEqual(names, ["r4.local", "r4"])
        self.assertEqual(addresses, ["192.168.1.42", "127.0.0.1"])

    def test_format_san(self):
        self.assertEqual(
            tls.format_san(["r4.local"], ["192.168.1.42"]),
            "DNS:r4.local,IP:192.168.1.42",
        )

    def test_format_san_needs_a_subject(self):
        with self.assertRaises(tls.TlsError):
            tls.format_san([], [])

    def test_defaults_are_deduplicated(self):
        for values in (tls.default_names(), tls.default_addresses()):
            self.assertEqual(len(values), len(set(values)))
            self.assertTrue(all(values))

    def test_local_addresses_include_loopback(self):
        self.assertIn("127.0.0.1", tls.default_addresses())


@needs_openssl
class GenerationTests(unittest.TestCase):
    def setUp(self):
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.directory = Path(self._tmp.name) / "tls"

    def test_generates_a_usable_certificate(self):
        bundle, ca_created = tls.generate(
            self.directory, names=["r4.local"], addresses=["127.0.0.1"]
        )
        self.assertTrue(ca_created)
        for path in (bundle.cert, bundle.key, bundle.ca_cert, bundle.ca_key):
            self.assertTrue(path.is_file(), path)

        info = tls.describe(bundle.cert)
        self.assertEqual(info.names, ["r4.local"])
        self.assertEqual(info.addresses, ["127.0.0.1"])
        self.assertFalse(info.expired)
        # Safari refuse au-delà de 398 jours : la marge doit rester sous la limite.
        self.assertLessEqual(info.days_left, 398)

        tls.load_context(bundle.cert, bundle.key)

    def test_private_key_is_not_world_readable(self):
        bundle, _ = tls.generate(self.directory, names=["r4.local"], addresses=["127.0.0.1"])
        self.assertEqual(bundle.key.stat().st_mode & 0o007, 0)
        self.assertEqual(bundle.ca_key.stat().st_mode & 0o077, 0)

    def test_existing_certificate_is_preserved(self):
        tls.generate(self.directory, names=["r4.local"], addresses=["127.0.0.1"])
        with self.assertRaises(tls.TlsError):
            tls.generate(self.directory, names=["r4.local"], addresses=["127.0.0.1"])

    def test_renewal_keeps_the_same_authority(self):
        """Le point du montage : renouveler ne doit rien redemander aux appareils."""
        first, _ = tls.generate(self.directory, names=["r4.local"], addresses=["127.0.0.1"])
        authority = first.ca_cert.read_bytes()
        certificate = first.cert.read_bytes()

        second, ca_created = tls.generate(
            self.directory, names=["r4.local"], addresses=["127.0.0.1"], force=True
        )
        self.assertFalse(ca_created)
        self.assertEqual(second.ca_cert.read_bytes(), authority)
        self.assertNotEqual(second.cert.read_bytes(), certificate)

    def test_validity_beyond_safari_limit_is_refused(self):
        with self.assertRaises(tls.TlsError):
            tls.generate(self.directory, names=["r4.local"], days=800)

    def test_unreadable_certificate_is_reported(self):
        missing = self.directory / "absent.pem"
        with self.assertRaises(tls.TlsError):
            tls.load_context(missing, missing)


class ConfigTests(unittest.TestCase):
    def test_certificate_without_key_is_refused(self):
        config = make_config(tls_cert=Path("/etc/tempi/tls/cert.pem"))
        with self.assertRaises(ValueError):
            config.validate()

    def test_missing_file_is_refused(self):
        config = make_config(
            tls_cert=Path("/nonexistent/cert.pem"), tls_key=Path("/nonexistent/key.pem")
        )
        with self.assertRaises(ValueError):
            config.validate()

    def test_scheme_follows_the_certificate(self):
        self.assertEqual(make_config().scheme, "http")
        self.assertEqual(
            make_config(tls_cert=Path("a.pem"), tls_key=Path("b.pem")).scheme, "https"
        )


@needs_openssl
class HttpsServerTests(unittest.TestCase):
    """Le serveur répond-il vraiment en TLS, et survit-il aux connexions ratées ?"""

    @classmethod
    def setUpClass(cls):
        cls._tmp = TemporaryDirectory()
        directory = Path(cls._tmp.name) / "tls"
        cls.bundle, _ = tls.generate(directory, names=["localhost"], addresses=["127.0.0.1"])

        cls.storage = Storage(":memory:")
        config = make_config(port=0, tls_cert=cls.bundle.cert, tls_key=cls.bundle.key)
        cls.config = config
        cls.server = make_server(config, cls.storage)
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

        cls.trust = ssl.create_default_context(cafile=str(cls.bundle.ca_cert))

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=5)
        cls.storage.close()
        cls._tmp.cleanup()

    def get(self, path="/api/health"):
        url = f"https://localhost:{self.port}{path}"
        with urllib.request.urlopen(url, timeout=10, context=self.trust) as response:
            return response.status, response.read()

    def test_serves_over_tls(self):
        status, body = self.get()
        self.assertEqual(status, 200)
        self.assertIn(b'"status":"ok"', body)

    def test_certificate_is_trusted_by_its_authority(self):
        """Sans l'autorité, la connexion doit être refusée : c'est tout l'intérêt."""
        with self.assertRaises(urllib.error.URLError) as caught:
            urllib.request.urlopen(f"https://localhost:{self.port}/", timeout=10)
        self.assertIsInstance(caught.exception.reason, ssl.SSLError)

    def test_plaintext_request_does_not_bring_the_server_down(self):
        """Une adresse http:// tapée sur le port TLS ne doit pas figer le service."""
        with self.assertRaises(Exception):
            urllib.request.urlopen(f"http://127.0.0.1:{self.port}/", timeout=10)
        self.assertEqual(self.get()[0], 200)

    def test_url_announced_in_the_log(self):
        self.assertTrue(server_url(self.config, self.server).startswith("https://127.0.0.1:"))


class HttpServerUrlTests(unittest.TestCase):
    def test_plain_server_announces_http(self):
        storage = Storage(":memory:")
        self.addCleanup(storage.close)
        config = make_config(port=0)
        server = make_server(config, storage)
        self.addCleanup(server.server_close)
        self.assertTrue(server_url(config, server).startswith("http://127.0.0.1:"))


if __name__ == "__main__":
    unittest.main()
