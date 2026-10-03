import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


GENERATOR = Path(__file__).resolve().parents[1] / "create_validation_project.py"


class ValidationProjectGeneratorTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="grass-project-", dir=Path.cwd())
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.repository = self.folder / "Package source"
        self.repository.mkdir()
        (self.repository / "package.json").write_text('{"name":"com.cone.grass"}', encoding="utf-8")
        self.sample = self.repository / "Integrations~/MicroVerse"
        self.sample.mkdir(parents=True)
        (self.sample / "Bridge.cs").write_text("// Synthetic bridge\n", encoding="utf-8")
        self.bootstrap = self.repository / "Tools~/UnityProject/Editor"
        self.bootstrap.mkdir(parents=True)
        (self.bootstrap / "Bootstrap.cs").write_text("// Synthetic bootstrap\n", encoding="utf-8")
        self.runtime = self.repository / "Tools~/UnityProject/Runtime"
        self.runtime.mkdir()
        (self.runtime / "Probe.cs").write_text("// Synthetic runtime probe\n", encoding="utf-8")
        self.output = self.folder / "Validation project"

    def generate(self, output=None, *extra):
        return subprocess.run(
            [sys.executable, str(GENERATOR), "--repo", str(self.repository),
             "--output", str(output or self.output), *extra],
            capture_output=True, text=True, check=False, timeout=10)

    def test_fresh_project_has_relocatable_package_and_required_engine_modules(self):
        result = self.generate()
        self.assertEqual(result.returncode, 0, result.stderr)
        packages = self.output / "Packages"
        manifest = json.loads((packages / "manifest.json").read_text(encoding="utf-8"))
        dependency = manifest["dependencies"]["com.cone.grass"]
        self.assertTrue(dependency.startswith("file:"))
        self.assertFalse(Path(dependency[5:]).is_absolute())
        self.assertEqual((packages / dependency[5:]).resolve(), self.repository.resolve())
        self.assertEqual(manifest["testables"], ["com.cone.grass"])
        for module in ("imgui", "physics", "terrain", "terrainphysics", "jsonserialize", "screencapture", "imageconversion"):
            self.assertEqual(manifest["dependencies"]["com.unity.modules." + module], "1.0.0")
        self.assertEqual((self.output / "Assets/MicroVerseMaskBridge/Bridge.cs").read_text(), "// Synthetic bridge\n")
        self.assertEqual((self.output / "Assets/Editor/Bootstrap.cs").read_text(), "// Synthetic bootstrap\n")
        self.assertEqual((self.output / "Assets/ValidationRuntime/Probe.cs").read_text(), "// Synthetic runtime probe\n")

    def test_regeneration_replaces_removed_sample_files(self):
        self.assertEqual(self.generate().returncode, 0)
        (self.sample / "Bridge.cs").unlink()
        (self.sample / "Replacement.cs").write_text("// Replacement\n", encoding="utf-8")
        result = self.generate()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((self.output / "Assets/MicroVerseMaskBridge/Bridge.cs").exists())
        self.assertTrue((self.output / "Assets/MicroVerseMaskBridge/Replacement.cs").is_file())

    def test_regeneration_removes_obsolete_harness_sources(self):
        self.assertEqual(self.generate().returncode, 0)
        (self.bootstrap / "Bootstrap.cs").unlink()
        (self.runtime / "Probe.cs").unlink()
        result = self.generate()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((self.output / "Assets/Editor/Bootstrap.cs").exists())
        self.assertFalse((self.output / "Assets/ValidationRuntime/Probe.cs").exists())

    def test_destination_inside_copy_source_is_rejected_before_creating_files(self):
        # Otherwise copytree can recursively copy its own generated destination.
        for source in (self.sample, self.bootstrap, self.runtime, self.runtime.parent):
            with self.subTest(source=source):
                destination = source / "Nested project"
                result = self.generate(destination)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("copied source directory", result.stderr)
                self.assertFalse(destination.exists())

    def test_unrelated_output_is_preserved(self):
        self.output.mkdir()
        unrelated = self.output / "User document.txt"
        unrelated.write_text("Keep this file", encoding="utf-8")
        result = self.generate()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("unrelated files", result.stderr)
        self.assertEqual(unrelated.read_text(), "Keep this file")
        self.assertEqual(list(self.output.iterdir()), [unrelated])

    def test_unsupported_editor_version_does_not_create_project(self):
        result = self.generate(None, "--unity", "6000.0.0f1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unity 6.6-or-newer", result.stderr)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
