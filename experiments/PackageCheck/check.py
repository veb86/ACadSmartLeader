"""Validate the distributable without executing AutoCAD reference assemblies."""
import argparse
import json
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET
import zipfile

TARGETS = {
    "AutoCAD2020": ("R23.1", "R23.1"),
    "AutoCAD2021-2024": ("R24.0", "R24.3"),
    "AutoCAD2025": ("R25.0", None),
}
COMMANDS = {
    "SL_CREATE", "SL_CREATE_GROUP", "SL_UPDATE", "SL_EDIT", "SL_RULES", "SL_RULES_IMPORT",
    "SL_RULES_EXPORT", "SL_SETTINGS", "SL_SELECT_PROBLEMS",
}


def project_version():
    props = Path(__file__).resolve().parents[2] / "Directory.Build.props"
    return ET.parse(props).findtext("PropertyGroup/Version")


def check_manifest(manifest, version):
    assert manifest.tag == "ApplicationPackage"
    assert manifest.attrib["Name"] == "ACadSmartLeader"
    assert manifest.attrib["AppVersion"] == version
    components = manifest.findall("Components")
    assert len(components) == len(TARGETS), "Expected separate 2020, 2021–2024 and 2025+ components"
    for component, (target, releases) in zip(components, TARGETS.items()):
        runtime = component.find("RuntimeRequirements")
        assert runtime is not None
        assert runtime.attrib["OS"] == "Win64"
        assert runtime.attrib["Platform"] == "AutoCAD*"
        assert (runtime.get("SeriesMin"), runtime.get("SeriesMax")) == releases, target
        entries = component.findall("ComponentEntry")
        assert len(entries) == 1, f"{target}: expected only the plugin DLL"
        (dll,) = entries
        assert dll.attrib["ModuleName"] == f"./Contents/{target}/ACadSmartLeader.dll"
        assert dll.attrib["AppType"] == ".Net"
        assert dll.attrib["LoadOnAutoCADStartup"].lower() == "true"
        assert dll.attrib["LoadOnCommandInvocation"].lower() == "true"
        commands = dll.findall("Commands/Command")
        assert len(commands) == len(COMMANDS)
        assert {c.attrib["Global"] for c in commands} == COMMANDS
        assert all(c.attrib["Local"] == c.attrib["Global"] for c in commands)


def check_package(path, version):
    with zipfile.ZipFile(path) as package:
        names = set(package.namelist())
        assert len(names) == len(package.namelist()), "Duplicate archive entries"
        assert all("\\" not in n and not n.startswith("/") and ".." not in PurePosixPath(n).parts for n in names)
        root = "ACadSmartLeader.bundle/"
        manifest = ET.fromstring(package.read(root + "PackageContents.xml"))
        check_manifest(manifest, version)
        dlls = {n for n in names if n.lower().endswith(".dll")}
        assert dlls == {root + "Contents/" + target + "/ACadSmartLeader.dll" for target in TARGETS}, dlls
        assert root + "THIRD-PARTY-NOTICES.txt" in names
        instructions = package.read("INSTALL.md").decode("utf-8-sig")
        assert "%APPDATA%\\Autodesk\\ApplicationPlugins\\ACadSmartLeader.bundle" in instructions
        expected_files = {"INSTALL.md", root + "PackageContents.xml", root + "THIRD-PARTY-NOTICES.txt"}
        for entry in manifest.iter("ComponentEntry"):
            name = root + entry.attrib["ModuleName"].removeprefix("./")
            assert name in names, name
            expected_files.add(name)
            assert package.read(name).startswith(b"MZ"), name
            folder = str(PurePosixPath(name).parent) + "/"
            for data in ("label-rules.json", "demo-schema.json"):
                expected_files.add(folder + data)
                json.loads(package.read(folder + data))
        assert {n for n in names if not n.endswith("/")} == expected_files, "Unexpected or missing payload files"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", nargs="?")
    parser.add_argument("--manifest")
    parser.add_argument("--version", default=project_version())
    args = parser.parse_args()
    if args.manifest:
        check_manifest(ET.parse(args.manifest).getroot(), args.version)
    elif args.package:
        check_package(args.package, args.version)
    else:
        parser.error("Supply an archive or --manifest")
    print("PACKAGE_CHECK_OK")
