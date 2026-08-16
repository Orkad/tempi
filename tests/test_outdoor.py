"""Tests de la source de température extérieure.

Aucun test ne sort sur le réseau : les réponses des trois API sont rejouées
telles qu'elles sont documentées, ce qui permet de lancer la suite sur un
Raspberry Pi hors ligne comme en intégration continue.
"""

import json
import logging
import time
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from tempi.config import Config
from tempi.outdoor import (
    InfoclimatSource,
    MetarSource,
    OpenMeteoSource,
    OutdoorError,
    OutdoorPoller,
    OutdoorThread,
    address_for,
    make_source,
    make_thread,
)
from tempi.storage import Storage

# Plusieurs tests provoquent volontairement des pannes de la source distante.
logging.getLogger("tempi").setLevel(logging.CRITICAL)


def make_config(**overrides) -> Config:
    base = dict(
        db_path=Path(":memory:"),
        w1_dir=Path("/nonexistent"),
        simulate=True,
        read_retries=1,
        allow_reset_value=False,
        interval=0.01,
        min_delta=0.0,
        max_interval=0.0,
        retention_days=0,
        host="127.0.0.1",
        port=8080,
    )
    base.update(overrides)
    return Config(**base)


def fetcher(*payloads):
    """Faux client HTTP : chaque appel consomme la réponse suivante.

    Une entrée ``Exception`` est levée, ce qui simule une panne réseau.
    """
    remaining = list(payloads)
    calls = []

    def fetch(url, timeout):
        calls.append(url)
        payload = remaining.pop(0) if remaining else payloads[-1]
        if isinstance(payload, Exception):
            raise payload
        if isinstance(payload, (dict, list)):
            return json.dumps(payload).encode("utf-8")
        return payload if isinstance(payload, bytes) else payload.encode("utf-8")

    fetch.calls = calls
    return fetch


METAR_PAYLOAD = [
    {
        "icaoId": "LFLY",
        "obsTime": 1_700_000_000,
        "temp": 12.4,
        "dewp": 8.0,
        "rawOb": "LFLY 141200Z 27008KT 9999 FEW030 12/08 Q1018",
    }
]

INFOCLIMAT_PAYLOAD = {
    "hourly": {
        "000JT": [
            {"dh_utc": "2024-01-14 11:00:00", "temperature": "4.8"},
            {"dh_utc": "2024-01-14 12:00:00", "temperature": "5.6"},
        ]
    }
}

OPEN_METEO_PAYLOAD = {
    "latitude": 45.75,
    "longitude": 4.85,
    "utc_offset_seconds": 0,
    "current_units": {"temperature_2m": "°C"},
    "current": {"time": "2024-01-14T12:00", "interval": 900, "temperature_2m": 5.9},
}


class MetarTests(unittest.TestCase):
    def test_parses_temperature_and_observation_time(self):
        source = MetarSource("lfly")
        observation = source.parse(json.dumps(METAR_PAYLOAD).encode())
        self.assertAlmostEqual(observation.celsius, 12.4)
        self.assertEqual(observation.ts, 1_700_000_000)
        self.assertEqual(observation.station, "LFLY")

    def test_station_code_is_normalised_in_url_and_address(self):
        source = MetarSource(" lfly ")
        self.assertIn("ids=LFLY", source.url())
        self.assertEqual(address_for(source), "outdoor-metar-LFLY")

    def test_keeps_the_most_recent_report(self):
        payload = [
            {"icaoId": "LFLY", "obsTime": 1_700_000_000, "temp": 12.4},
            {"icaoId": "LFLY", "obsTime": 1_700_003_600, "temp": 13.1},
        ]
        observation = MetarSource("LFLY").parse(json.dumps(payload).encode())
        self.assertAlmostEqual(observation.celsius, 13.1)

    def test_falls_back_to_the_raw_metar_when_the_field_is_missing(self):
        payload = [
            {
                "icaoId": "LFLY",
                "obsTime": 1_700_000_000,
                "temp": None,
                "rawOb": "LFLY 141200Z 27008KT 9999 FEW030 M03/M07 Q1018",
            }
        ]
        observation = MetarSource("LFLY").parse(json.dumps(payload).encode())
        self.assertAlmostEqual(observation.celsius, -3.0)

    def test_empty_response_is_an_error(self):
        with self.assertRaises(OutdoorError):
            MetarSource("LFLY").parse(b"[]")

    def test_station_is_required(self):
        with self.assertRaises(OutdoorError):
            MetarSource("")


class InfoclimatTests(unittest.TestCase):
    def test_keeps_the_most_recent_record(self):
        source = InfoclimatSource("000JT", "cle")
        observation = source.parse(json.dumps(INFOCLIMAT_PAYLOAD).encode())
        self.assertAlmostEqual(observation.celsius, 5.6)
        # 2024-01-14 12:00:00 UTC
        self.assertEqual(observation.ts, 1_705_233_600)

    def test_url_carries_the_token_and_an_explicit_range(self):
        url = InfoclimatSource("000JT", "cle").url()
        self.assertIn("token=cle", url)
        self.assertIn("stations%5B%5D=000JT", url)
        self.assertIn("start=", url)
        self.assertIn("end=", url)

    def test_error_message_is_reported(self):
        payload = {"message": "Token invalide"}
        with self.assertRaises(OutdoorError) as caught:
            InfoclimatSource("000JT", "cle").parse(json.dumps(payload).encode())
        self.assertIn("Token invalide", str(caught.exception))

    def test_token_is_required(self):
        with self.assertRaises(OutdoorError):
            InfoclimatSource("000JT", "")


class OpenMeteoTests(unittest.TestCase):
    def test_parses_the_current_block(self):
        source = OpenMeteoSource(45.75, 4.85)
        observation = source.parse(json.dumps(OPEN_METEO_PAYLOAD).encode())
        self.assertAlmostEqual(observation.celsius, 5.9)
        self.assertEqual(observation.ts, 1_705_233_600)

    def test_local_time_is_converted_back_to_utc(self):
        payload = dict(OPEN_METEO_PAYLOAD)
        payload["utc_offset_seconds"] = 3600
        payload["current"] = {"time": "2024-01-14T13:00", "temperature_2m": 5.9}
        observation = OpenMeteoSource(45.75, 4.85).parse(json.dumps(payload).encode())
        self.assertEqual(observation.ts, 1_705_233_600)

    def test_address_encodes_the_coordinates(self):
        self.assertEqual(
            address_for(OpenMeteoSource(45.75, 4.85)), "outdoor-open-meteo-45.7500_4.8500"
        )

    def test_coordinates_are_checked(self):
        with self.assertRaises(OutdoorError):
            OpenMeteoSource(91.0, 4.85)


class ValueValidationTests(unittest.TestCase):
    def test_implausible_temperature_is_rejected(self):
        # Une API renvoyant des millidegrés passerait sinon inaperçue.
        payload = [{"icaoId": "LFLY", "obsTime": 1_700_000_000, "temp": 12400}]
        with self.assertRaises(OutdoorError):
            MetarSource("LFLY").parse(json.dumps(payload).encode())

    def test_invalid_json_is_reported(self):
        with self.assertRaises(OutdoorError):
            MetarSource("LFLY").parse(b"<html>503</html>")


class MakeSourceTests(unittest.TestCase):
    def test_no_provider_means_no_source(self):
        self.assertIsNone(make_source(make_config()))
        self.assertIsNone(make_source(make_config(outdoor_provider="none")))

    def test_each_provider_is_built(self):
        metar = make_source(make_config(outdoor_provider="metar", outdoor_station="LFLY"))
        self.assertIsInstance(metar, MetarSource)
        infoclimat = make_source(
            make_config(
                outdoor_provider="infoclimat", outdoor_station="000JT", outdoor_token="cle"
            )
        )
        self.assertIsInstance(infoclimat, InfoclimatSource)
        open_meteo = make_source(
            make_config(
                outdoor_provider="open-meteo", outdoor_latitude=45.75, outdoor_longitude=4.85
            )
        )
        self.assertIsInstance(open_meteo, OpenMeteoSource)

    def test_unknown_provider_is_rejected(self):
        with self.assertRaises(OutdoorError):
            make_source(make_config(outdoor_provider="meteo-france"))

    def test_open_meteo_requires_coordinates(self):
        with self.assertRaises(OutdoorError):
            make_source(make_config(outdoor_provider="open-meteo"))

    def test_configuration_error_does_not_prevent_startup(self):
        # Sans capteur extérieur exploitable, la collecte des DS18B20 doit
        # continuer : make_thread signale et rend None plutôt que de lever.
        with Storage(Path(":memory:")) as storage:
            self.assertIsNone(make_thread(make_config(outdoor_provider="metar"), storage))


class PollerTests(unittest.TestCase):
    def setUp(self):
        self._tmp = TemporaryDirectory()
        self.storage = Storage(Path(self._tmp.name) / "tempi.db")
        self.addCleanup(self._tmp.cleanup)
        self.addCleanup(self.storage.close)
        self.config = make_config(outdoor_provider="metar", outdoor_station="LFLY")

    def poller(self, *payloads) -> OutdoorPoller:
        return OutdoorPoller(self.config, self.storage, fetch=fetcher(*payloads))

    def test_stores_the_reading_as_a_sensor(self):
        poller = self.poller(METAR_PAYLOAD)
        reading = poller.poll_once()

        self.assertIsNotNone(reading)
        self.assertEqual(reading.address, "outdoor-metar-LFLY")
        # L'horodatage est celui de l'observation, pas celui de la requête.
        self.assertEqual(reading.ts, 1_700_000_000)

        sensors = self.storage.sensors()
        self.assertEqual([s["address"] for s in sensors], ["outdoor-metar-LFLY"])
        self.assertEqual(sensors[0]["label"], "Extérieur")
        self.assertEqual(sensors[0]["count"], 1)

    def test_same_observation_is_not_stored_twice(self):
        poller = self.poller(METAR_PAYLOAD, METAR_PAYLOAD)
        self.assertIsNotNone(poller.poll_once())
        self.assertIsNone(poller.poll_once())
        self.assertEqual(poller.stored, 1)
        self.assertEqual(poller.polls, 2)

    def test_new_observation_is_stored(self):
        newer = [dict(METAR_PAYLOAD[0], obsTime=1_700_003_600, temp=13.1)]
        poller = self.poller(METAR_PAYLOAD, newer)
        poller.poll_once()
        reading = poller.poll_once()
        self.assertIsNotNone(reading)
        self.assertAlmostEqual(reading.celsius, 13.1)
        self.assertEqual(poller.stored, 2)

    def test_a_restart_does_not_replay_a_known_observation(self):
        self.poller(METAR_PAYLOAD).poll_once()
        # Un nouveau poller ignore ce que la base contient déjà.
        second = self.poller(METAR_PAYLOAD)
        self.assertIsNone(second.poll_once())
        self.assertEqual(second.stored, 0)

    def test_network_failure_is_swallowed(self):
        poller = self.poller(OutdoorError("réseau injoignable"))
        self.assertIsNone(poller.poll_once())
        self.assertEqual(poller.errors, 1)
        self.assertIn("réseau", poller.last_error)
        # La panne ne doit rien écrire ni empêcher un succès ultérieur.
        self.assertEqual(self.storage.sensors(), [])

    def test_recovery_after_a_failure(self):
        poller = self.poller(OutdoorError("panne"), METAR_PAYLOAD)
        poller.poll_once()
        self.assertIsNotNone(poller.poll_once())
        self.assertIsNone(poller.last_error)
        self.assertEqual(poller.stored, 1)

    def test_manual_label_survives_a_restart(self):
        poller = self.poller(METAR_PAYLOAD)
        poller.poll_once()
        self.storage.set_label("outdoor-metar-LFLY", "Jardin")

        newer = [dict(METAR_PAYLOAD[0], obsTime=1_700_003_600)]
        self.poller(newer).poll_once()

        sensors = {s["address"]: s["label"] for s in self.storage.sensors()}
        self.assertEqual(sensors["outdoor-metar-LFLY"], "Jardin")

    def test_series_mixes_outdoor_and_bus_sensors(self):
        # Le pseudo-capteur doit ressortir des requêtes ordinaires, sans quoi
        # l'interface web et l'export CSV l'ignoreraient.
        self.poller(METAR_PAYLOAD).poll_once()
        series = self.storage.series(1_699_999_000, 1_700_001_000)
        self.assertIn("outdoor-metar-LFLY", series)
        self.assertEqual(len(series["outdoor-metar-LFLY"]), 1)


class ThreadTests(unittest.TestCase):
    def test_interval_never_falls_below_the_floor(self):
        # Une valeur trop basse martèlerait une API publique gratuite.
        with Storage(Path(":memory:")) as storage:
            config = make_config(
                outdoor_provider="metar", outdoor_station="LFLY", outdoor_interval=1.0
            )
            poller = OutdoorPoller(config, storage, fetch=fetcher(METAR_PAYLOAD))
            thread = OutdoorThread(config, storage, poller)
            self.assertGreaterEqual(thread.interval, 60.0)

    def test_thread_polls_then_stops(self):
        with Storage(Path(":memory:")) as storage:
            config = make_config(
                outdoor_provider="metar", outdoor_station="LFLY", outdoor_interval=60.0
            )
            poller = OutdoorPoller(config, storage, fetch=fetcher(METAR_PAYLOAD))
            thread = OutdoorThread(config, storage, poller)
            thread.start()
            deadline = time.monotonic() + 5
            while poller.polls == 0 and time.monotonic() < deadline:
                time.sleep(0.01)
            thread.stop()
            thread.join(timeout=5)
            self.assertFalse(thread.is_alive())
            self.assertEqual(poller.stored, 1)


class ConfigValidationTests(unittest.TestCase):
    """``validate()`` porte sur la configuration complète : l'intervalle de
    collecte doit donc être réaliste, contrairement aux autres tests."""

    def config(self, **overrides) -> Config:
        return make_config(interval=60.0, **overrides)

    def test_known_provider_passes(self):
        self.config(outdoor_provider="metar", outdoor_station="LFLY").validate()

    def test_unknown_provider_fails_fast(self):
        # Une faute de frappe doit arrêter le démarrage, pas désactiver
        # silencieusement la source extérieure.
        with self.assertRaises(ValueError):
            self.config(outdoor_provider="metaar").validate()

    def test_interval_floor_is_enforced(self):
        with self.assertRaises(ValueError):
            self.config(
                outdoor_provider="metar", outdoor_station="LFLY", outdoor_interval=5.0
            ).validate()

    def test_no_provider_skips_the_outdoor_checks(self):
        self.config(outdoor_interval=1.0).validate()


if __name__ == "__main__":
    unittest.main()
