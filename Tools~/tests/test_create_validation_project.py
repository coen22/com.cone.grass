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
        obsolete_metadata = self.output / "Assets/MicroVerseMaskBridge/Bridge.cs.meta"
        obsolete_metadata.write_text("guid: obsolete-sample\n", encoding="utf-8")
        (self.sample / "Bridge.cs").unlink()
        (self.sample / "Replacement.cs").write_text("// Replacement\n", encoding="utf-8")
        result = self.generate()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((self.output / "Assets/MicroVerseMaskBridge/Bridge.cs").exists())
        self.assertFalse(obsolete_metadata.exists())
        self.assertTrue((self.output / "Assets/MicroVerseMaskBridge/Replacement.cs").is_file())

    def test_regeneration_removes_obsolete_harness_sources(self):
        self.assertEqual(self.generate().returncode, 0)
        editor_metadata = self.output / "Assets/Editor/Bootstrap.cs.meta"
        runtime_metadata = self.output / "Assets/ValidationRuntime/Probe.cs.meta"
        editor_metadata.write_text("guid: obsolete-editor\n", encoding="utf-8")
        runtime_metadata.write_text("guid: obsolete-runtime\n", encoding="utf-8")
        (self.bootstrap / "Bootstrap.cs").unlink()
        (self.runtime / "Probe.cs").unlink()
        result = self.generate()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((self.output / "Assets/Editor/Bootstrap.cs").exists())
        self.assertFalse((self.output / "Assets/ValidationRuntime/Probe.cs").exists())
        self.assertFalse(editor_metadata.exists())
        self.assertFalse(runtime_metadata.exists())

    def test_regeneration_preserves_retained_script_and_folder_metadata_with_saved_references(self):
        nested = self.runtime / "Nested"
        nested.mkdir()
        (nested / "Helper.cs").write_text("// Helper\n", encoding="utf-8")
        self.assertEqual(self.generate().returncode, 0)
        guid = "1234567890abcdef1234567890abcdef"
        metadata_paths = [
            self.output / "Assets/ValidationRuntime/Probe.cs.meta",
            self.output / "Assets/ValidationRuntime/Nested.meta",
            self.output / "Assets/ValidationRuntime/Nested/Helper.cs.meta",
            self.output / "Assets/Editor/Bootstrap.cs.meta",
        ]
        imported_metadata = {}
        for index, path in enumerate(metadata_paths):
            asset_guid = f"{int(guid, 16) + index:032x}"
            importer = "folderAsset: yes\n" if path.name == "Nested.meta" else "MonoImporter:\n  executionOrder: 10\n"
            imported_metadata[path] = "fileFormatVersion: 2\nguid: " + asset_guid + "\n" + importer
            path.write_text(imported_metadata[path], encoding="utf-8")
        settings = self.output / "Assets/ValidationSettings"
        settings.mkdir()
        scene = settings / "GrassValidation.unity"
        reference = "m_Script: {fileID: 11500000, guid: " + guid + ", type: 3}\n"
        scene.write_text(reference, encoding="utf-8")
        (self.runtime / "Probe.cs").write_text("// Updated runtime probe\n", encoding="utf-8")
        (self.bootstrap / "Bootstrap.cs").write_text("// Updated bootstrap\n", encoding="utf-8")

        result = self.generate()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.output / "Assets/ValidationRuntime/Probe.cs").read_text(), "// Updated runtime probe\n")
        self.assertEqual((self.output / "Assets/Editor/Bootstrap.cs").read_text(), "// Updated bootstrap\n")
        for path in metadata_paths:
            self.assertEqual(path.read_text(encoding="utf-8"), imported_metadata[path])
        self.assertEqual(scene.read_text(encoding="utf-8"), reference)

    def test_source_metadata_is_authoritative_and_obsolete_nested_folder_is_removed(self):
        nested = self.runtime / "Obsolete"
        nested.mkdir()
        (nested / "Old.cs").write_text("// Removed later\n", encoding="utf-8")
        self.assertEqual(self.generate().returncode, 0)
        destination = self.output / "Assets/ValidationRuntime"
        (destination / "Probe.cs.meta").write_text("guid: previous\n", encoding="utf-8")
        (destination / "Obsolete.meta").write_text("guid: removed-folder\n", encoding="utf-8")
        (destination / "Obsolete/Old.cs.meta").write_text("guid: removed-script\n", encoding="utf-8")
        source_metadata = "fileFormatVersion: 2\nguid: fedcba0987654321fedcba0987654321\n"
        (self.runtime / "Probe.cs.meta").write_text(source_metadata, encoding="utf-8")
        (nested / "Old.cs").unlink()
        nested.rmdir()

        result = self.generate()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((destination / "Probe.cs.meta").read_text(encoding="utf-8"), source_metadata)
        self.assertFalse((destination / "Obsolete").exists())
        self.assertFalse((destination / "Obsolete.meta").exists())

    def test_destination_inside_copy_source_is_rejected_before_creating_files(self):
        # Otherwise copytree can recursively copy its own generated destination.
        for source in (self.sample, self.bootstrap, self.runtime, self.runtime.parent):
            with self.subTest(source=source):
                destination = source / "Nested project"
                result = self.generate(destination)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("copied source directory", result.stderr)
                self.assertFalse(destination.exists())

    def test_missing_required_sources_preserve_existing_assets_and_do_not_create_a_project(self):
        self.assertEqual(self.generate().returncode, 0)
        guid = "1234567890abcdef1234567890abcdef"
        for index, relative in enumerate(("Editor/Bootstrap.cs", "ValidationRuntime/Probe.cs", "MicroVerseMaskBridge/Bridge.cs")):
            metadata = "guid: " + f"{int(guid, 16) + index:032x}" + "\n"
            (self.output / ("Assets/" + relative + ".meta")).write_text(metadata, encoding="utf-8")
        settings = self.output / "Assets/ValidationSettings"
        settings.mkdir()
        (settings / "GrassValidation.unity").write_text(
            "m_Script: {fileID: 11500000, guid: " + guid + ", type: 3}\n", encoding="utf-8")
        original = {str(path.relative_to(self.output)): path.read_bytes() if path.is_file() else None
                    for path in self.output.rglob("*")}
        for source in (self.sample, self.bootstrap, self.runtime):
            with self.subTest(source=source):
                unavailable = source.with_name(source.name + ".unavailable")
                source.rename(unavailable)
                try:
                    result = self.generate()
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("required validation template directory", result.stderr)
                    self.assertEqual({str(path.relative_to(self.output)): path.read_bytes() if path.is_file() else None
                                      for path in self.output.rglob("*")}, original)
                    fresh = self.folder / "Fresh project"
                    result = self.generate(fresh)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertFalse(fresh.exists())
                finally:
                    unavailable.rename(source)

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
