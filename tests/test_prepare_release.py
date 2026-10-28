"""Source archive regressions; standard library only, no product execution."""
import importlib.util
import posixpath
import tempfile
import unittest
import zipfile
from pathlib import Path, PurePosixPath
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("prepare_release", ROOT / "scripts/prepare_release.py")
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)


class SourceArchiveTests(unittest.TestCase):
    @unittest.skipUnless((ROOT / ".git").exists(), "Source export requires a Git checkout")
    def test_export_retains_explicit_project_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / "source.zip"
            release.prepare_source_archive(archive, "HEAD")
            with zipfile.ZipFile(archive) as source:
                names = set(source.namelist())
                for name in names:
                    if not name.endswith(".csproj"):
                        continue
                    project = ElementTree.fromstring(source.read(name))
                    for item in project.iter():
                        if item.tag not in {"EmbeddedResource", "Compile", "ProjectReference"}:
                            continue
                        include = item.get("Include")
                        if not include or any(c in include for c in "*$?"):
                            continue
                        target = posixpath.normpath(str(PurePosixPath(name).parent / include))
                        self.assertIn(target, names, f"{name} requires {target}")

    def test_source_validation_requires_each_embedded_schema(self):
        required = {"Directory.Build.props", "V2rayNMonitor.sln", "src/Host/Host.csproj", "LICENSE",
                    "web/package.json", "web/package-lock.json", "docs/proposals/scope-ledger-v2.sql",
                    "docs/proposals/scope-ledger-v3.sql"}
        with tempfile.TemporaryDirectory() as directory:
            for schema in ("scope-ledger-v2.sql", "scope-ledger-v3.sql"):
                with self.subTest(schema=schema):
                    archive = Path(directory) / "incomplete.zip"
                    with zipfile.ZipFile(archive, "w") as source:
                        for name in required - {"docs/proposals/" + schema}:
                            source.writestr(name, b"synthetic source")
                    with self.assertRaisesRegex(ValueError, "Incomplete rebuild source archive"):
                        release.read_source(archive)


if __name__ == "__main__":
    unittest.main()
