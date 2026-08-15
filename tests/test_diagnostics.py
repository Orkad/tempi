"""Tests de l'analyse du bus 1-Wire.

Les cas de panne reproduisent des signatures relevées sur un montage réel.
"""

import unittest

from tempi.diagnostics import (
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


class OverlayTests(unittest.TestCase):
    def test_absent(self):
        self.assertEqual(parse_overlay("dtparam=audio=on\n"), (False, 4))

    def test_present_default_pin(self):
        self.assertEqual(parse_overlay("dtparam=audio=on\ndtoverlay=w1-gpio\n"), (True, 4))

    def test_custom_pin(self):
        self.assertEqual(parse_overlay("dtoverlay=w1-gpio,gpiopin=17\n"), (True, 17))

    def test_commented_line_is_ignored(self):
        self.assertEqual(parse_overlay("#dtoverlay=w1-gpio\n"), (False, 4))

    def test_tolerates_spacing(self):
        self.assertEqual(parse_overlay("  dtoverlay = w1-gpio,gpiopin = 22 \n"), (True, 22))

    def test_pullup_variant_also_enables_the_bus(self):
        self.assertEqual(parse_overlay("dtoverlay=w1-gpio-pullup\n"), (True, 4))
        self.assertEqual(parse_overlay("dtoverlay=w1-gpio-pullup,gpiopin=17\n"), (True, 17))

    def test_other_overlay_sharing_the_prefix_is_not_matched(self):
        self.assertEqual(parse_overlay("dtoverlay=w1-gpio-something-else\n"), (False, 4))

    def test_empty_file(self):
        self.assertEqual(parse_overlay(""), (False, 4))


class ModulesTests(unittest.TestCase):
    def test_extracts_names(self):
        proc = "w1_therm 32768 0 - Live 0x0\nw1_gpio 16384 0 - Live 0x0\ncfg80211 1044480 1\n"
        self.assertEqual(parse_modules(proc), {"w1_therm", "w1_gpio", "cfg80211"})

    def test_empty(self):
        self.assertEqual(parse_modules(""), set())


class PinctrlTests(unittest.TestCase):
    def test_modern_format(self):
        self.assertEqual(parse_pinctrl("4: ip    pu | lo // GPIO4 = input"), ("ip pu", "lo"))

    def test_modern_format_high(self):
        self.assertEqual(parse_pinctrl("4: ip    pn | hi // GPIO4 = input"), ("ip pn", "hi"))

    def test_legacy_format(self):
        self.assertEqual(
            parse_pinctrl("GPIO 4: level=0 func=OUTPUT pull=NONE"), ("output", "lo")
        )

    def test_legacy_format_high(self):
        self.assertEqual(
            parse_pinctrl("GPIO 4: level=1 func=INPUT pull=UP"), ("input", "hi")
        )

    def test_unparsable(self):
        self.assertIsNone(parse_pinctrl("commande introuvable"))


class ClassifyTests(unittest.TestCase):
    def test_sorts_by_nature(self):
        inventory = classify_devices(
            ["28-000005e2fdc3", "00-800000000000", "w1_bus_master1", "10-000801f2ab34"]
        )
        self.assertEqual(inventory.sensors, ["10-000801f2ab34", "28-000005e2fdc3"])
        self.assertEqual(inventory.phantoms, ["00-800000000000"])
        self.assertEqual(inventory.masters, ["w1_bus_master1"])

    def test_empty(self):
        inventory = classify_devices([])
        self.assertEqual((inventory.sensors, inventory.phantoms, inventory.masters), ([], [], []))

    def test_unknown_family_is_neither(self):
        inventory = classify_devices(["81-0000abcdef01"])
        self.assertEqual(inventory.sensors, [])
        self.assertEqual(inventory.phantoms, [])


class DiagnoseBusTests(unittest.TestCase):
    def test_healthy_bus(self):
        check = diagnose_bus(classify_devices(["28-000005e2fdc3", "w1_bus_master1"]))
        self.assertTrue(check.ok)
        self.assertIn("28-000005e2fdc3", check.detail)

    def test_sensor_present_but_bus_noisy(self):
        check = diagnose_bus(
            classify_devices(["28-000005e2fdc3", "00-1f8000000000", "w1_bus_master1"])
        )
        self.assertTrue(check.ok)
        self.assertIn("parasite", check.detail)
        self.assertIn("2,2 kΩ", check.remedy)

    def test_no_master_at_all(self):
        check = diagnose_bus(classify_devices([]))
        self.assertFalse(check.ok)
        self.assertTrue(check.critical)
        self.assertIn("overlay", check.remedy)

    def test_bus_up_but_empty(self):
        check = diagnose_bus(classify_devices(["w1_bus_master1"]))
        self.assertFalse(check.ok)
        self.assertIn("ne voit rien", check.remedy)

    def test_stuck_low_signature(self):
        # Signature relevée quand la ligne de données touche la masse.
        check = diagnose_bus(classify_devices(["00-800000000000", "w1_bus_master1"]))
        self.assertFalse(check.ok)
        self.assertTrue(check.critical)
        self.assertIn("masse", check.remedy)

    def test_floating_line_signature(self):
        # Deux balayages successifs donnant des ROM différentes : ligne flottante.
        first = classify_devices(["00-1f8000000000", "00-6f8000000000", "w1_bus_master1"])
        second = classify_devices(["00-ef8000000000", "00-3a8000000000", "w1_bus_master1"])
        check = diagnose_bus(first, second)
        self.assertFalse(check.ok)
        self.assertIn("flottante", check.remedy)
        self.assertIn("4,7 kΩ", check.remedy)

    def test_stable_phantoms_with_low_level_means_short(self):
        first = classify_devices(["00-1f8000000000", "w1_bus_master1"])
        check = diagnose_bus(first, first, gpio_level="lo")
        self.assertFalse(check.ok)
        self.assertIn("masse", check.remedy)

    def test_phantoms_without_second_scan_stays_generic(self):
        check = diagnose_bus(classify_devices(["00-1f8000000000", "w1_bus_master1"]))
        self.assertFalse(check.ok)
        self.assertIn("tirage", check.remedy)


class DiagnoseGpioTests(unittest.TestCase):
    def test_high_is_healthy(self):
        self.assertTrue(diagnose_gpio("hi", "ip pu").ok)

    def test_low_input_is_a_fault(self):
        check = diagnose_gpio("lo", "ip pu")
        self.assertFalse(check.ok)
        self.assertTrue(check.critical)

    def test_low_while_driven_is_inconclusive(self):
        # Le pilote 1-Wire tire la ligne pendant une transaction : ce n'est pas
        # l'état au repos, on ne peut rien en conclure.
        check = diagnose_gpio("lo", "OUTPUT")
        self.assertIsNone(check.ok)

    def test_missing_tool(self):
        self.assertIsNone(diagnose_gpio(None, None).ok)


class SummariseTests(unittest.TestCase):
    def test_all_good(self):
        ok, message = summarise([Check("a", True, "ok"), Check("b", True, "ok")])
        self.assertTrue(ok)
        self.assertEqual(message, "Tout est en ordre.")

    def test_undetermined_is_not_a_failure(self):
        ok, message = summarise([Check("a", True, "ok"), Check("b", None, "inconnu")])
        self.assertTrue(ok)
        self.assertIn("non concluante", message)

    def test_reports_first_critical_failure(self):
        checks = [
            Check("mineur", False, "détail mineur", "remède mineur"),
            Check("majeur", False, "détail majeur", "remède majeur", critical=True),
        ]
        ok, message = summarise(checks)
        self.assertFalse(ok)
        self.assertIn("détail majeur", message)
        self.assertNotIn("mineur", message)

    def test_falls_back_to_first_failure(self):
        ok, message = summarise([Check("seul", False, "détail", "remède")])
        self.assertFalse(ok)
        self.assertIn("détail", message)


if __name__ == "__main__":
    unittest.main()
