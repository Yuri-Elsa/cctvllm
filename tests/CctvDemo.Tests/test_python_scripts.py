"""Runs the script-style Python test suites under `python -m unittest`.

Most Python tests in this repository are standalone scripts (`python eval/tests/test_x.py`) that print
PASS/FAIL and exit non-zero on failure. This module runs each one in its own process and turns the exit
code into a unittest result, so one command covers them. Files that already define unittest.TestCase
classes (tracking/test_live_logic.py) are left to unittest's own discovery.

    python -m unittest discover -s tests -p "test_python_scripts.py" -v
    python -m unittest discover -s tracking -p "test_live_logic.py" -v
"""
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = sorted([*(ROOT / "eval" / "tests").glob("test_*.py"), *(ROOT / "tracking").glob("test_*.py")])


class ScriptSuites(unittest.TestCase):
    pass


def _case(path):
    def run(self):
        done = subprocess.run([sys.executable, str(path)], cwd=ROOT, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=1200)
        self.assertEqual(done.returncode, 0, msg=(done.stdout + done.stderr)[-4000:])
    return run


for _path in SCRIPTS:
    if "unittest.TestCase" in _path.read_text(encoding="utf-8"):
        continue
    setattr(ScriptSuites, f"test_{_path.parent.name}_{_path.stem}", _case(_path))


if __name__ == "__main__":
    unittest.main()