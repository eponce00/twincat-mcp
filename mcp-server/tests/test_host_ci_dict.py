import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from twincat_mcp.host import _ci_wrap


class CaseInsensitiveDictTests(unittest.TestCase):
    def test_getitem_contains_and_get_are_case_insensitive(self):
        result = _ci_wrap(
            {
                "errorMessage": "real host error",
                "plcProjects": [{"name": "Coating_PLC"}],
            }
        )

        self.assertEqual(result.get("ErrorMessage"), "real host error")
        self.assertEqual(result["ErrorMessage"], "real host error")
        self.assertIn("ErrorMessage", result)
        self.assertEqual(result.get("PlcProjects")[0]["Name"], "Coating_PLC")


if __name__ == "__main__":
    unittest.main()
