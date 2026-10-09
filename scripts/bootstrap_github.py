#!/usr/bin/env python3
"""Publish the allowlisted planning files using an already-authenticated GitHub CLI.

No tokens are read or printed. An existing repository is never overwritten.
Application code, real traffic data, and CI verification are not created here.
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DESCRIPTION = (
    "Local-first per-node traffic history for v2rayN: Windows tray, "
    "HTML statistics, and optional provider CSV / TrafficMonitor integration."
)


def run(args: list[str], cwd: Path | None = None) -> str:
    result = subprocess.run(
        args, cwd=cwd, text=True, encoding="utf-8", errors="replace",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
    )
    if result.returncode:
        message = result.stderr.strip() or result.stdout.strip()
        raise RuntimeError(f"{args[0]} {args[1]} failed ({result.returncode}): {message}")
    return result.stdout.strip()


def api(endpoint: str, method: str = "GET", payload: dict[str, Any] | None = None) -> Any:
    command = ["gh", "api", "--hostname", "github.com", "--method", method, endpoint]
    if payload is None:
        return json.loads(run(command))
    # A file avoids platform-specific shell quoting, especially for Chinese issue bodies.
    with tempfile.TemporaryDirectory(prefix="v2nm-api-") as temporary:
        path = Path(temporary) / "body.json"
        path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        return json.loads(run(command + ["--input", str(path)]))


def files_to_publish() -> list[str]:
    items = json.loads((ROOT / "publish-manifest.json").read_text(encoding="utf-8"))["files"]
    if len(items) != len(set(items)):
        raise ValueError("Duplicate entries in publish-manifest.json")
    for item in items:
        path = Path(item)
        if path.is_absolute() or ".." in path.parts or not (ROOT / path).is_file():
            raise ValueError(f"Invalid publish path: {item}")
    return items


def validate_target(owner: str, name: str) -> None:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]*", owner):
        raise ValueError("Invalid GitHub owner")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", name):
        raise ValueError("Invalid repository name; use v2rayN-Monitor, without spaces")


def verify_public_repository(full_name: str) -> dict[str, Any]:
    repo = api(f"repos/{full_name}")
    if repo["full_name"].lower() != full_name.lower() or repo["private"]:
        raise RuntimeError("The requested public repository was not verified")
    return repo


def publish_backlog(full_name: str, plan: dict[str, Any]) -> dict[str, Any]:
    # This initializer has eight milestones and 33 issues; pagination covers later reruns too.
    def all_pages(endpoint: str) -> list[dict[str, Any]]:
        pages = json.loads(run([
            "gh", "api", "--hostname", "github.com", endpoint,
            "--paginate", "--slurp",
        ]))
        return [item for page in pages for item in page]

    milestones = all_pages(f"repos/{full_name}/milestones?state=all&per_page=100")
    by_title = {m["title"]: m for m in milestones}
    stage_numbers: dict[str, int] = {}
    milestone_receipts = []
    for stage in plan["milestones"]:
        obj = by_title.get(stage["title"])
        if obj is None:
            obj = api(f"repos/{full_name}/milestones", "POST", {
                "title": stage["title"],
                "description": f"{stage['description']}\n交付：{stage['release']}",
            })
        stage_numbers[stage["id"]] = obj["number"]
        milestone_receipts.append({"stage": stage["id"], "url": obj["html_url"]})

    existing = all_pages(f"repos/{full_name}/issues?state=all&per_page=100")
    issue_by_title = {i["title"]: i for i in existing if "pull_request" not in i}
    issue_receipts = []
    for task in plan["tasks"]:
        title = f"[{task['id']}] {task['title']}"
        obj = issue_by_title.get(title)
        if obj is None:
            body = (
                f"## 任务节点\n\n`{task['id']}` · 初始状态：planned\n\n"
                f"## 依赖\n\n{', '.join(task['depends_on']) or '无'}\n\n"
                f"## 实施\n\n{task['work']}\n\n"
                f"## 产物\n\n" + "\n".join(f"- {x}" for x in task["outputs"]) +
                f"\n\n## 验收\n\n{task['acceptance']}\n\n"
                f"## 收口\n\n{task['stop_rule']}\n\n"
                f"总计划：https://github.com/{full_name}/blob/main/docs/development-plan.md\n\n"
                "完成时附对应提交、执行命令与结果；不要把源码核读当作 Windows 实机通过。"
            )
            obj = api(f"repos/{full_name}/issues", "POST", {
                "title": title, "body": body, "milestone": stage_numbers[task["phase"]],
            })
        issue_receipts.append({"task": task["id"], "url": obj["html_url"]})
        print(f"Backlog: {task['id']}", flush=True)
    return {"milestones": milestone_receipts, "issues": issue_receipts}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--owner", default="lengmh")
    parser.add_argument("--repo", default="v2rayN-Monitor")
    parser.add_argument("--with-backlog", action="store_true", help="Also create milestones and task issues")
    parser.add_argument("--backlog-only", action="store_true", help="Resume only backlog creation in the existing public repository")
    parser.add_argument("--dry-run", action="store_true", help="Validate and list actions without any network operation")
    args = parser.parse_args()
    validate_target(args.owner, args.repo)
    files = files_to_publish()
    plan = json.loads((ROOT / "docs/backlog.json").read_text(encoding="utf-8"))
    full_name = f"{args.owner}/{args.repo}"
    print(f"Target: {full_name} (PUBLIC)")
    print(f"Allowlisted files: {len(files)}; stages: {len(plan['milestones'])}; tasks: {len(plan['tasks'])}")
    if args.dry_run:
        print("DRY RUN: no network calls, no repository created, no files pushed.")
        for item in files:
            print(f"  {item}")
        return 0
    for executable in ("gh", "git"):
        if not shutil.which(executable):
            raise RuntimeError(f"Install {executable} first. No remote changes have been made.")
    run(["gh", "auth", "status", "--hostname", "github.com"])
    user = json.loads(run(["gh", "api", "--hostname", "github.com", "user", "--jq", "{login: .login, id: .id}"]))
    if user["login"].lower() != args.owner.lower():
        raise RuntimeError(f"Authenticated as {user['login']}, not {args.owner}; select the intended account first")

    receipt: dict[str, Any]
    if args.backlog_only:
        repo = verify_public_repository(full_name)
        receipt = {"repository_url": repo["html_url"], "visibility": "public", "operation": "backlog-only"}
    else:
        # Copy only the declared public files to a clean temporary repository. This avoids
        # accidentally pushing the caller's working tree, old commits, CSVs or databases.
        with tempfile.TemporaryDirectory(prefix="v2rayn-monitor-init-") as temporary:
            work = Path(temporary)
            for item in files:
                destination = work / item
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(ROOT / item, destination)
            run(["git", "init", "--initial-branch=main"], work)
            run(["git", "config", "user.name", user["login"]], work)
            run(["git", "config", "user.email", f"{user['id']}+{user['login']}@users.noreply.github.com"], work)
            run(["git", "add", "--all"], work)
            run(["git", "commit", "-m", "docs: initialize v2rayN Monitor phased development plan"], work)
            commit = run(["git", "rev-parse", "HEAD"], work)
            # gh repo create rejects an existing name; no force-push or deletion is used.
            run(["gh", "repo", "create", full_name, "--public", "--source", str(work),
                 "--remote", "origin", "--push", "--description", DESCRIPTION], work)
            repo = verify_public_repository(full_name)
            remote_commit = api(f"repos/{full_name}/git/ref/heads/main")["object"]["sha"]
            if remote_commit != commit:
                raise RuntimeError("Repository exists, but main does not match the planned initial commit")
            receipt = {"repository_url": repo["html_url"], "visibility": "public", "commit_sha": commit}
        print(f"Verified repository: {receipt['repository_url']}", flush=True)
        (ROOT / "bootstrap-result.json").write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")

    if args.with_backlog or args.backlog_only:
        receipt.update(publish_backlog(full_name, plan))
    (ROOT / "bootstrap-result.json").write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(receipt, ensure_ascii=False, indent=2))
    print("Update docs/STATUS.md with this verified URL/commit; application implementation remains planned.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, ValueError, OSError, KeyError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        print("If creation had started, inspect GitHub before rerunning; no existing repository is overwritten. "
              "Use --backlog-only to resume milestone/issue creation after a verified repository creation.", file=sys.stderr)
        raise SystemExit(1)
