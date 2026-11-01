"""Grass-specific hook for the shared Package CI: shader include contracts, Unity GUIDs and JSON metadata."""
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[2]
sys.exit(subprocess.call([sys.executable, str(root / "Tools~" / "validate_source.py"), str(root)]))
