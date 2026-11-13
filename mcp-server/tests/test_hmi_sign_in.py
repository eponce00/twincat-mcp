"""HMI server sign-in: the engineer saves the password once in XAE; the automation never takes, sets or writes
one. A profile without a saved sign-in is refused with hmi_profile_sign_in_missing in plain words."""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.catalog import Catalog

ROOT = Path(__file__).resolve().parents[2]


def source(*parts):
    return (ROOT.joinpath(*parts)).read_text(encoding="utf-8-sig").replace("\r\n", "\n")


class SignInTests(unittest.TestCase):
    def test_no_operation_takes_a_password(self):
        for id, entry in Catalog().entries.items():
            names = {n.lower() for n in entry.get("inputSchema", {}).get("properties", {})}
            self.assertFalse(names & {"password", "secret", "credentialtarget"}, id)
        self.assertIn("hmi_profile_sign_in_missing", Catalog().get("hmi.preflight")["description"])

    def test_missing_sign_in_is_refused_in_plain_words(self):
        command = source("TcAutomation", "Commands", "HmiCommand.cs")
        self.assertIn(
            "return HmiPolicy.HasSavedSignIn(directory, name) ? "
            'Failure("hmi_profile_invalid") : SignInMissing(name);',
            command,
        )
        message = command[command.index("private static object SignInMissing") :]
        message = message[: message.index("};")]
        self.assertIn('code = "hmi_profile_sign_in_missing"', message)
        self.assertIn("publish dialog", message)
        self.assertIn("never asks for or receives the password", message)

    def test_the_profile_file_is_only_read_and_no_password_is_set(self):
        policy = source("TcAutomation", "Core", "HmiPolicy.cs")
        sign_in = policy[policy.index("public static bool HasSavedSignIn") :]
        sign_in = sign_in[: sign_in.index("public static string[] Profiles")]
        self.assertIn("File.ReadAllBytes(path)", sign_in)
        self.assertNotRegex(sign_in, r"File\.(Write|Append|Create|Move|Copy|Delete)")
        command = source("TcAutomation", "Commands", "HmiCommand.cs")
        # Beckhoff's profile setters write through to the profile file: never set a sign-in.
        self.assertNotRegex(command, r"ServerPassword|ServerUsername|SetValue\(")


if __name__ == "__main__":
    unittest.main()
