"""Small regressions for release routing and incomplete distribution payloads."""
import copy
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

from check import check_manifest, check_package, project_version

ROOT = Path(__file__).resolve().parents[2]


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.manifest = ET.parse(ROOT / "installer/ACadSmartLeader.bundle/PackageContents.xml").getroot()

    def test_supported_hosts_and_startup_commands(self):
        check_manifest(self.manifest, project_version())

    def test_missing_autocad_2020_is_rejected(self):
        self.manifest.remove(self.manifest.findall("Components")[0])
        with self.assertRaises(AssertionError):
            check_manifest(self.manifest, project_version())

    def test_wrong_release_or_dll_routing_is_rejected(self):
        cases = [
            ("RuntimeRequirements", "SeriesMin", "R24.0"),
            ("RuntimeRequirements", "SeriesMax", "R24.3"),
            ("ComponentEntry", "ModuleName", "./Contents/AutoCAD2021-2024/ACadSmartLeader.dll"),
            ("ComponentEntry", "LoadOnAutoCADStartup", "False"),
            ("ComponentEntry/Commands/Command", "Global", "SL_MISSING"),
        ]
        for element, attribute, value in cases:
            with self.subTest(attribute=attribute):
                manifest = copy.deepcopy(self.manifest)
                manifest.find("Components/" + element).set(attribute, value)
                with self.assertRaises(AssertionError):
                    check_manifest(manifest, project_version())

    def test_stale_version_is_rejected(self):
        with self.assertRaises(AssertionError):
            check_manifest(self.manifest, "0.0.0")

    def test_legacy_menu_component_is_rejected(self):
        component = self.manifest.find("Components")
        ET.SubElement(component, "ComponentEntry", ModuleName="./Contents/AutoCAD2020/ACadSmartLeader.cuix")
        with self.assertRaisesRegex(AssertionError, "expected only the plugin DLL"):
            check_manifest(self.manifest, project_version())

    def test_missing_payload_and_unsafe_paths_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "incomplete.zip"
            for extra in (None, "../escape.dll", "C:\\wrong.dll"):
                with self.subTest(extra=extra):
                    with zipfile.ZipFile(path, "w") as archive:
                        archive.writestr("ACadSmartLeader.bundle/PackageContents.xml", ET.tostring(self.manifest))
                        if extra:
                            archive.writestr(extra, b"MZ")
                    with self.assertRaises(AssertionError):
                        check_package(path, project_version())


if __name__ == "__main__":
    unittest.main()
