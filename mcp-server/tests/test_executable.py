"""Fork test cases that still apply to v3, rewritten for unittest with mocks only."""

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp import cli


class ExecutableTests(unittest.TestCase):
    def test_first_existing_default_path_wins(self):
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ):
            os.environ.pop("TWINCAT_AUTOMATION_EXE", None)
            a, b, c = (Path(folder) / n for n in ("a.exe", "b.exe", "c.exe"))
            b.touch()
            c.touch()
            with patch.object(cli, "TC_AUTOMATION_PATHS", [a, b, c]):
                self.assertEqual(b, cli.find_tc_automation_exe())
            with patch.object(cli, "TC_AUTOMATION_PATHS", [a]):
                with self.assertRaises(FileNotFoundError) as caught:
                    cli.find_tc_automation_exe()
            self.assertIn(str(a), str(caught.exception))


if __name__ == "__main__":
    unittest.main()
