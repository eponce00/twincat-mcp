"""Qualify the real stdio/native HMI path on an explicitly selected engineering project/server."""
import argparse
import json
import os
import subprocess
import sys
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--solution', required=True)
    parser.add_argument('--project', required=True)
    parser.add_argument('--target-url', required=True)
    parser.add_argument('--profile', required=True)
    parser.add_argument('--publish', action='store_true', help='Explicitly authorize updating this HMI server')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError('Use a new output file; existing qualification receipts are preserved')
    state = output.parent / ('hmi-qualification-' + uuid.uuid4().hex)
    proc = subprocess.Popen([sys.executable, str(root / 'mcp-server/server.py')], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8',
                            env={**os.environ, 'TWINCAT_MCP_STATE_DIR': str(state)})
    number = 0
    records = []

    def tool(name, data):
        nonlocal number
        number += 1
        proc.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': number, 'method': 'tools/call',
            'params': {'name': name, 'arguments': data, '_meta': {
                'io.modelcontextprotocol/protocolVersion': '2026-07-28',
                'io.modelcontextprotocol/clientCapabilities': {}}}}) + '\n')
        proc.stdin.flush()
        reply = json.loads(proc.stdout.readline())
        if 'error' in reply:
            raise RuntimeError(reply['error'])
        return reply['result'].get('structuredContent', {})

    def execute(op, values, key=None):
        receipt = tool('twincat_execute', {'operation': op, 'arguments': values,
                       'requestKey': key or uuid.uuid4().hex, 'waitSeconds': 1})
        while receipt.get('state') in ('queued', 'running'):
            print(op, receipt['state'], flush=True)
            receipt = tool('twincat_execute', {'operation': 'job.wait',
                        'arguments': {'jobHandle': receipt['jobHandle'], 'waitSeconds': 20}})
        data = receipt.get('result')
        if data is None:
            chunks = []
            offset = 0
            while True:
                page = tool('twincat_execute', {'operation': 'job.result',
                    'arguments': {'jobHandle': receipt['jobHandle'], 'offset': offset, 'limit': 16000}})
                chunks.append(page['text'])
                if page['nextOffset'] is None:
                    break
                offset = page['nextOffset']
            data = json.loads(''.join(chunks))
        records.append({'operation': op, 'state': receipt['state'], 'result': data})
        print(op, receipt['state'], json.dumps(data), flush=True)
        return receipt, data

    ctx = None
    try:
        status = tool('twincat_execute', {'operation': 'system.status', 'arguments': {}})
        assert status['version'] == '3.1.0', status
        _, opened = execute('context.open_hmi', {'solutionPath': str(Path(args.solution).resolve()),
            'projectPath': str(Path(args.project).resolve()), 'targetUrl': args.target_url})
        ctx = opened['contextHandle']
        assert execute('hmi.inspect', {'contextHandle': ctx})[1]['success']
        assert execute('hmi.status', {'contextHandle': ctx})[1]['success']
        # The fixture's remote profile must not be selected by a local context.
        if args.profile == 'LocalHil':
            wrong = execute('hmi.preflight', {'contextHandle': ctx, 'profileName': 'Administrator'})[1]
            assert wrong.get('code') == 'hmi_target_mismatch', wrong
        grant = execute('safety.grant', {'operations': ['hmi.build', 'hmi.publish'],
            'contextHandle': ctx, 'reason': 'Explicit HMI qualification on the selected project and server', 'ttlSeconds': 1200})[1]['grantHandle']
        assert execute('hmi.build', {'contextHandle': ctx, 'grantHandle': grant, 'confirm': 'CONFIRM'})[1]['success']
        preflight = execute('hmi.preflight', {'contextHandle': ctx, 'profileName': args.profile})[1]
        assert preflight['success'], preflight
        if args.publish:
            values = {'contextHandle': ctx, 'grantHandle': grant, 'confirm': 'CONFIRM',
                      'profileName': args.profile, 'expectedFingerprint': '0' * 64}
            refused = execute('hmi.publish', values)[1]
            assert refused.get('code') == 'hmi_preflight_changed', refused
            values['expectedFingerprint'] = preflight['fingerprint']
            key = uuid.uuid4().hex
            first, result = execute('hmi.publish', values, key)
            assert result['success'], result
            second, repeated = execute('hmi.publish', values, key)
            assert first['jobHandle'] == second['jobHandle'], 'Duplicate request was not deduplicated'
            assert repeated['submissionId'] == result['submissionId']
            final = execute('hmi.status', {'contextHandle': ctx})[1]
            assert not final['isPublishing'] and final['lastResult'] is True, final
        execute('context.close', {'contextHandle': ctx})
        ctx = None
        with output.open('x', encoding='utf-8') as file:
            json.dump({'passed': True, 'version': status['version'], 'published': args.publish,
                       'records': records}, file, indent=2)
        print('PASS: native HMI qualification; receipt:', output)
    finally:
        if ctx:
            try:
                execute('context.close', {'contextHandle': ctx})
            except (RuntimeError, OSError, ValueError, KeyError, TypeError) as exc:
                print('Context cleanup failed: ' + type(exc).__name__, file=sys.stderr)
        proc.stdin.close()
        proc.wait(timeout=30)


if __name__ == '__main__':
    main()
