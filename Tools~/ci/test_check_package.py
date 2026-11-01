"""Regression tests for check_package.py's readonly-serialized-field scan (stdlib only).

    python3 -m unittest discover -s "Tools~/ci" -p "test_check_package.py"
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


class ReadonlySerializedFieldTests(unittest.TestCase):
    def run_checker(self, files: dict) -> str:
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for rel, text in files.items():
                (root / rel).parent.mkdir(parents=True, exist_ok=True)
                (root / rel).write_text(text)
            subprocess.run(["git", "init", "-q"], cwd=root, check=True)
            subprocess.run(["git", "add", "-A"], cwd=root, check=True)
            return subprocess.run([sys.executable, str(Path(__file__).with_name("check_package.py")), str(root)],
                                  capture_output=True, text=True).stdout

    def scan(self, source: str) -> list:
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "Runtime").mkdir()
            (root / "package.json").write_text('{"name": "fixture", "version": "1.0.0"}')
            (root / "Runtime" / "Fixture.cs").write_text(source)
            subprocess.run(["git", "init", "-q"], cwd=root, check=True)
            subprocess.run(["git", "add", "-A"], cwd=root, check=True)
            out = subprocess.run([sys.executable, str(Path(__file__).with_name("check_package.py")), str(root)],
                                 capture_output=True, text=True).stdout
        return [line.split(":")[1] for line in out.splitlines() if "readonly field" in line]

    def test_flags_readonly_serialized_fields(self):
        source = (
            "class A {\n"
            "  [SerializeField] private readonly int _a = 1;\n"           # 2: same line
            "  [SerializeField, Range(0, 1)]\n"
            "  private readonly float _b;\n"                               # 4: attribute on the line above
            "  [Header(\"x\")]\n"
            "  [SerializeReference] readonly object _c = null;\n"         # 6: second attribute line
            "  [SerializeField][HideInInspector] private readonly int _d;\n"  # 7: two lists, one line
            "}\n")
        self.assertEqual(self.scan(source), ["2", "4", "6", "7"])

    def test_ignores_writable_static_and_unattributed_fields(self):
        source = (
            "class A {\n"
            "  [SerializeField] private int _ok;\n"
            "  private readonly int _plain = 2;\n"
            "  [SerializeField][HideInInspector] private int _hidden;\n"
            "  private static readonly int s_later = 3;\n"                 # MF: no carry-over from the line above
            "  [SerializeField] private static readonly int s_static = 4;\n"  # statics are never serialized
            "  // [SerializeField] private readonly int _commented;\n"
            "}\n")
        self.assertEqual(self.scan(source), [])



class MetaAndPathTests(unittest.TestCase):
    run_checker = ReadonlySerializedFieldTests.run_checker

    def test_root_files_and_folders_need_metas_but_hidden_tilde_and_bundle_contents_do_not(self):
        out = self.run_checker({
            "package.json": '{"name": "fixture", "version": "1.0.0"}',
            "README.md": "x", "Runtime/A.cs": "class A { }", "Runtime/A.cs.meta": "m",
            "Documentation~/index.md": "x", ".github/ci.yml": "x",
            "Runtime/Plugins/X.androidlib.meta": "m", "Runtime/Plugins/X.androidlib/src/Main.java": "x",
            "Runtime/Plugins.meta": "m",
        })
        missing = sorted(line.split(":")[0] for line in out.splitlines() if "no .meta" in line)
        self.assertEqual(missing, ["README.md", "Runtime", "package.json"])

    def test_developer_paths_are_found_in_json_and_urls_are_not(self):
        out = self.run_checker({
            "package.json": r'{"name": "fixture", "version": "1.0.0", "x": "C:\\Users\\bob\\pkg"}',
            "Tools~/ci/test-project.json": '{"dependencies": {"a": "file:/home/me/a"}}',
            "README.md": "See https://example.com/home/docs/ and https://a.b/Users/x/.",
        })
        flagged = sorted(line.split(":")[0] for line in out.splitlines() if "absolute path" in line)
        self.assertEqual(flagged, ["Tools~/ci/test-project.json", "package.json"])


if __name__ == "__main__":
    unittest.main()
