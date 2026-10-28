import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.lifecycle import WindowsProcess


@unittest.skipUnless(os.name == "nt", "Windows redirector and inherited-handle test")
class ParentExitTests(unittest.TestCase):
    def test_eof_cancels_a_long_wait_without_waiting_for_its_deadline(self):
        root = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as folder:
            server = subprocess.Popen(
                [sys.executable, str(root / "server.py")],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                env={**os.environ, "TWINCAT_DISABLE_HOST": "1", "TWINCAT_MCP_STATE_DIR": folder},
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            pinned = None
            number = 0

            def call(operation, arguments, **options):
                nonlocal number
                number += 1
                payload = dict(operation=operation, arguments=arguments, **options)
                server.stdin.write(
                    json.dumps(
                        dict(
                            jsonrpc="2.0",
                            id=number,
                            method="tools/call",
                            params={
                                "name": "twincat_execute",
                                "arguments": payload,
                                "_meta": {
                                    "io.modelcontextprotocol/protocolVersion": "2026-07-28",
                                    "io.modelcontextprotocol/clientCapabilities": {},
                                },
                            },
                        )
                    )
                    + "\n"
                )
                server.stdin.flush()

            try:
                call("system.status", {})
                status = json.loads(server.stdout.readline())["result"]["structuredContent"]
                pinned = WindowsProcess(status["lifecycle"]["processId"])
                call(
                    "workflow.wait_state",
                    dict(amsNetId="1.2.3.4.1.1", expectedState="Run", timeoutSeconds=30),
                    requestKey="eof-long-running-job",
                    waitSeconds=0,
                )
                job = json.loads(server.stdout.readline())["result"]["structuredContent"]
                call("job.wait", dict(jobHandle=job["jobHandle"], waitSeconds=120))
                server.stdin.close()
                server.wait(timeout=6)
                self.assertEqual(0, server.returncode, server.stderr.read())
            finally:
                if pinned:
                    if pinned.alive():
                        os.kill(pinned.pid, signal.SIGTERM)
                    pinned.close()
                server.wait(timeout=5)
                for stream in (server.stdin, server.stdout, server.stderr):
                    if stream and not stream.closed:
                        stream.close()

    def test_client_death_exits_server_even_when_another_process_holds_stdin_open(self):
        root = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as folder:
            helper = Path(folder) / "client.py"
            helper.write_text(
                """import json,os,subprocess,sys,threading
from pathlib import Path
root=Path(__ROOT__)
state=Path(__STATE__)
guard=threading.Timer(12,lambda:os._exit(88));guard.daemon=True;guard.start()
server=subprocess.Popen([sys.executable,str(root/'server.py')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=open(state/'server.err','w'),text=True,encoding='utf-8',env={**os.environ,'TWINCAT_DISABLE_HOST':'1','TWINCAT_MCP_STATE_DIR':str(state),'TWINCAT_MCP_SHUTDOWN_GRACE_SECONDS':'1'})
body={'jsonrpc':'2.0','id':1,'method':'tools/call','params':{'name':'twincat_execute','arguments':{'operation':'system.status','arguments':{}},'_meta':{'io.modelcontextprotocol/protocolVersion':'2026-07-28','io.modelcontextprotocol/clientCapabilities':{},'io.modelcontextprotocol/clientInfo':{'name':'lifecycle-test','version':'1'}}}}
server.stdin.write(json.dumps(body)+'\\n');server.stdin.flush()
response=json.loads(server.stdout.readline())
status=response['result']['structuredContent']['lifecycle']
holder=subprocess.Popen([sys._base_executable,'-c','import time;time.sleep(30)'],stdin=server.stdin,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
print(json.dumps({'server':status['processId'],'client':os.getpid(),'monitored':status['monitoredClientPid'],'holder':holder.pid}),flush=True)
sys.stdin.readline()
os._exit(0)
""".replace("__ROOT__", repr(str(root))).replace("__STATE__", repr(folder)),
                encoding="utf-8",
            )
            client = subprocess.Popen(
                [sys.executable, str(helper)],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            owned = []
            try:
                line = client.stdout.readline()
                if not line:
                    self.fail(client.stderr.read())
                actors = json.loads(line)
                owned = [WindowsProcess(actors[key]) for key in ("server", "holder", "client")]
                self.assertEqual(
                    actors["client"],
                    actors["monitored"],
                    "Watch the client beyond the venv redirector",
                )
                client.communicate("exit\n", timeout=8)
                deadline = time.monotonic() + 8
                while owned[0].alive() and time.monotonic() < deadline:
                    time.sleep(0.1)
                self.assertFalse(
                    owned[0].alive(), (Path(folder) / "server.err").read_text(encoding="utf-8")
                )
                self.assertTrue(
                    owned[1].alive(), "The server must exit itself, not kill the stdin holder"
                )
            finally:
                for process in owned:
                    try:
                        # Retained handles pin these test-owned PIDs against reuse.
                        if process.alive():
                            os.kill(process.pid, signal.SIGTERM)
                    finally:
                        process.close()
                if client.poll() is None:
                    client.communicate("exit\n", timeout=5)
                for stream in (client.stdin, client.stdout, client.stderr):
                    if stream and not stream.closed:
                        stream.close()
