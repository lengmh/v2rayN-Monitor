#!/usr/bin/env python3
"""Synthetic production-asset/API smoke test; never connects to a real v2rayN instance."""
from __future__ import annotations
from datetime import datetime, timedelta, timezone
import http.cookiejar
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    with tempfile.TemporaryDirectory(prefix='v2rayn-demo-smoke-') as directory:
        process = subprocess.Popen(
            [os.environ.get('DOTNET_COMMAND', 'dotnet'),
             str(ROOT / 'src/Demo/bin/Release/net10.0/Demo.dll'), directory],
            stdout=subprocess.PIPE, text=True)
        try:
            assert process.stdout is not None
            process.stdout.readline()
            url = process.stdout.readline().strip()
            base, token = url.split('/#')
            assert base.startswith('http://127.0.0.1:')
            opener = urllib.request.build_opener(
                urllib.request.ProxyHandler({}),
                urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

            def request(path: str, body: object | None = None):
                return opener.open(urllib.request.Request(
                    base + path, data=json.dumps(body).encode() if body is not None else None,
                    headers={'X-Monitor-Request': '1', 'Content-Type': 'application/json'}), timeout=10)

            with request('/api/session', {'token': token}) as response:
                assert response.status == 200
            with request('/') as response:
                html = response.read().decode()
            assets = re.findall(r'(?:src|href)="(/assets/[^\"]+)"', html)
            assert assets, 'Production assets were not packaged'
            for asset in assets:
                with request(asset) as response:
                    assert response.status == 200
            for name, mime in [('icon.svg', 'image/svg+xml'), ('icon.ico', 'image/x-icon'), ('icon-256.png', 'image/png')]:
                with request('/' + name) as response:
                    assert response.status == 200
                    assert response.headers.get_content_type() == mime
                    assert response.read() == (ROOT / 'web/public' / name).read_bytes()
            now = datetime.now(timezone.utc)
            start = (now - timedelta(days=365)).date().isoformat()
            end = (now + timedelta(days=1)).date().isoformat()
            with request(f'/api/history?start={start}T00:00:00Z&end={end}T00:00:00Z&timeZoneId=UTC&bucket=month') as response:
                history = json.load(response)
            assert len(history['nodes']) == 3 and int(history['download']) > 0
            print('PASS: synthetic production assets, session exchange, annual history and node totals')
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
                raise AssertionError('Demo did not shut down after termination signal') from None
            if process.stdout is not None:
                process.stdout.close()
        print('PASS: demo process exits after shutdown signal')


if __name__ == '__main__':
    main()
