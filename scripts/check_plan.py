#!/usr/bin/env python3
"""Offline consistency checks for the planning package, not application tests."""
from __future__ import annotations
import ast
import csv
import json
import re
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    plan = json.loads((ROOT / 'docs/backlog.json').read_text(encoding='utf-8'))
    tasks = plan['tasks']
    ids = [t['id'] for t in tasks]
    assert len(ids) == len(set(ids)) == 38, 'Expected 38 unique task nodes'
    phases = {m['id'] for m in plan['milestones']}
    assert len(phases) == 8
    seen: set[str] = set()
    for task in tasks:
        assert task['phase'] in phases
        assert set(task['depends_on']) <= seen, f"Missing/cyclic/out-of-order dependency: {task['id']}"
        assert task['status'] in {'planned', 'in_progress', 'blocked', 'done', 'deferred'}, 'Unknown task status'
        for key in ('work', 'outputs', 'acceptance', 'stop_rule'):
            assert task[key], f"Missing {key}: {task['id']}"
        seen.add(task['id'])
    text = (ROOT / 'docs/development-plan.md').read_text(encoding='utf-8')
    headings = re.findall(r'^#### (S\d+\.\d+) ·', text, re.M)
    assert Counter(headings) == Counter(ids), 'Plan headings and backlog tasks differ'
    for script in (ROOT / 'scripts').glob('*.py'):
        ast.parse(script.read_text(encoding='utf-8'), filename=str(script))
    fixture = ROOT / 'tests/fixtures/provider-synthetic.csv'
    with fixture.open(encoding='utf-8', newline='') as stream:
        reader = csv.DictReader(stream)
        assert reader.fieldnames == ['node_name', 'rate', 'origin_traffic', 'origin_x_rate', 'traffic', 'log_time']
        rows = list(reader)
    assert len(rows) == 5 and all(row['node_name'].startswith('Synthetic-') for row in rows)
    assert rows[-1] == rows[-2], 'The fixture must preserve a legitimate duplicate example'
    manifest = json.loads((ROOT / 'publish-manifest.json').read_text(encoding='utf-8'))['files']
    assert len(manifest) == len(set(manifest))
    for relative in manifest:
        path = Path(relative)
        assert not path.is_absolute() and '..' not in path.parts
        assert (ROOT / path).is_file(), f'Missing publish file: {relative}'
        assert path.suffix not in {'.db', '.sqlite', '.sqlite3', '.log', '.key', '.pem'}
        assert not relative.endswith('.csv') or relative == 'tests/fixtures/provider-synthetic.csv'
    for document in ROOT.rglob('*.md'):
        if any(part in {'node_modules', 'bin', 'obj', 'dist', '.git'} for part in document.relative_to(ROOT).parts):
            continue
        body = document.read_text(encoding='utf-8')
        for link in re.findall(r'\]\(([^)]+)\)', body):
            if '://' in link or link.startswith(('#', 'mailto:')):
                continue
            assert (document.parent / link.split('#')[0]).exists(), f'Broken local link: {document}: {link}'
    print('PASS: 8 phases / 38 unique tasks / ordered acyclic dependencies')
    print('PASS: one matching plan heading per task; required task fields present')
    print('PASS: Python syntax / internal Markdown links / public file allowlist')
    print('PASS: synthetic-only CSV fixture including a legal duplicate')
    print('NOTE: this check validates plan/fixtures only; application/Windows/CI evidence is recorded separately')


if __name__ == '__main__':
    main()
