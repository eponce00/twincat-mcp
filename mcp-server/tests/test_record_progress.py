"""Recording pipe/Job progress tests using a Python child, never ADS or a native worker."""

import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp import cli
from twincat_mcp.backend import Backend
from twincat_mcp.engine import Engine

READY = "RECORD_READY channels=2 timestamp=2020-01-01T00:00:00.0000000+00:00"
FAKE_RECORDER = """
import json, sys, time
from pathlib import Path
# More than a pipe buffer on both streams: neither may wait for process exit.
print(json.dumps(dict(Success=True, OutputPath='fake.csv', SamplesCollected=2,
                     padding='x' * 150000)), flush=True)
print('diagnostic ' + 'x' * 150000, file=sys.stderr, flush=True)
print(sys.argv[2], file=sys.stderr, flush=True)
release = Path(sys.argv[1])
deadline = time.monotonic() + 8
while not release.exists() and time.monotonic() < deadline:
    time.sleep(0.005)
print('final stderr diagnostic', file=sys.stderr, flush=True)
"""


class RecordProgressTests(unittest.TestCase):
    def terminal(self, engine, receipt):
        deadline = time.monotonic() + 4
        while time.monotonic() < deadline:
            current = engine.execute("job.get", dict(jobHandle=receipt["jobHandle"]))
            if current["state"] not in {"queued", "running"}:
                return current
            time.sleep(0.005)
        self.fail("Fake job did not finish")

    def test_ready_is_visible_in_running_job_and_reads_still_finish(self):
        real_popen = subprocess.Popen
        children = []
        with (
            tempfile.TemporaryDirectory() as folder,
            patch.dict(os.environ, TWINCAT_DISABLE_HOST="1"),
        ):
            release = Path(folder) / "release"

            def fake_popen(command, **kwargs):
                self.assertEqual("ads-record", command[1])
                child = real_popen(
                    [
                        sys.executable,
                        "-I",
                        "-B",
                        "-c",
                        FAKE_RECORDER,
                        str(release),
                        "[PROGRESS] " + READY,
                    ],
                    **kwargs,
                )
                children.append(child)
                return child

            backend = Backend()
            # No close_context/host creation is needed for recording or fake reads.
            backend.close = Mock()
            engine = Engine(Path(folder) / "state", backend)
            try:
                grant_job = engine.execute(
                    "safety.grant",
                    dict(operations=["runtime.record"], amsNetId="1.2.3.4.1.1", reason="fake test"),
                    "record-grant",
                )
                grant = self.terminal(engine, grant_job)["result"]["grantHandle"]
                with (
                    patch(
                        "twincat_mcp.cli.find_tc_automation_exe", return_value=Path(sys.executable)
                    ),
                    patch("twincat_mcp.cli.subprocess.Popen", side_effect=fake_popen),
                    patch(
                        "twincat_mcp.backend.run_shell_step",
                        return_value=({"Success": True, "Value": "42"}, []),
                    ),
                ):
                    record = engine.execute(
                        "runtime.record",
                        dict(amsNetId="1.2.3.4.1.1", variables=["A", "B"], grantHandle=grant),
                        "record-progress",
                    )
                    deadline = time.monotonic() + 4
                    while True:
                        current = engine.execute("job.get", dict(jobHandle=record["jobHandle"]))
                        if READY in current["progress"]:
                            break
                        self.assertLess(time.monotonic(), deadline, current)
                        time.sleep(0.005)
                    self.assertEqual("running", current["state"])
                    self.assertFalse(current["resultAvailable"])
                    self.assertIsNone(children[0].poll())
                    read = engine.execute(
                        "runtime.read_var",
                        dict(amsNetId="1.2.3.4.1.1", symbol="A"),
                        "parallel-read",
                    )
                    self.assertEqual("succeeded", self.terminal(engine, read)["state"])
                    self.assertIsNone(children[0].poll())
                    release.touch()
                    final = self.terminal(engine, record)
                    self.assertEqual("succeeded", final["state"])
                    self.assertEqual(1, final["progress"].count(READY))
                    text, offset = "", 0
                    while True:
                        part = engine.jobs.result(record["jobHandle"], offset)
                        text += part["text"]
                        if part["nextOffset"] is None:
                            break
                        offset = part["nextOffset"]
                    result = json.loads(text)
                    self.assertEqual(2, result["samplesCollected"])
                    self.assertEqual("x" * 150000, result["padding"])
                    self.assertEqual("fake.csv", result["outputPath"])
            finally:
                release.touch()
                for child in children:
                    child.wait(timeout=4)
                engine.close()

    def test_record_drains_stderr_including_nonprogress_lines(self):
        real_popen = subprocess.Popen
        with tempfile.TemporaryDirectory() as folder:
            release = Path(folder) / "release"
            release.touch()

            def fake_popen(command, **kwargs):
                return real_popen(
                    [
                        sys.executable,
                        "-I",
                        "-B",
                        "-c",
                        FAKE_RECORDER,
                        str(release),
                        "ordinary RECORD_READY text",
                    ],
                    **kwargs,
                )

            callback = Mock()
            with (
                patch("twincat_mcp.cli.find_tc_automation_exe", return_value=Path(sys.executable)),
                patch("twincat_mcp.cli.subprocess.Popen", side_effect=fake_popen),
            ):
                result, messages = cli._run("ads-record", [], 4, callback)
            self.assertTrue(result["Success"])
            self.assertIn("final stderr diagnostic", messages)
            self.assertIn("ordinary RECORD_READY text", messages)
            callback.assert_not_called()

    def test_record_timeout_is_unknown_and_pipes_are_drained(self):
        real_popen = subprocess.Popen
        children = []

        def fake_popen(command, **kwargs):
            child = real_popen(
                [
                    sys.executable,
                    "-I",
                    "-B",
                    "-c",
                    "import time; print('not JSON', flush=True); time.sleep(8)",
                ],
                **kwargs,
            )
            children.append(child)
            return child

        with (
            patch("twincat_mcp.cli.find_tc_automation_exe", return_value=Path(sys.executable)),
            patch("twincat_mcp.cli.subprocess.Popen", side_effect=fake_popen),
        ):
            result, _ = cli._run("ads-record", [], 0.2)
        self.assertTrue(result["outcomeUnknown"])
        self.assertFalse(result["success"])
        self.assertIsNotNone(children[0].poll())
        self.assertTrue(children[0].stdout.closed)
        self.assertTrue(children[0].stderr.closed)

    def test_other_cli_commands_keep_communicate(self):
        child = Mock(returncode=0)
        child.communicate.return_value = ('{"success":true}', "[PROGRESS] old message\n")
        callback = Mock()
        with (
            patch("twincat_mcp.cli.find_tc_automation_exe", return_value=Path(sys.executable)),
            patch("twincat_mcp.cli.subprocess.Popen", return_value=child),
        ):
            result, messages = cli._run("scope-export", [], 4, callback)
        self.assertTrue(result["success"])
        self.assertEqual(["old message"], messages)
        child.communicate.assert_called_once_with(timeout=4)
        callback.assert_not_called()


if __name__ == "__main__":
    unittest.main()
