#!/usr/bin/env python3
"""Prepare unpublished basic/full/extended ZIPs from a sealed local publish.

Offline only. Does not build, run, install, merge data, commit or publish.
Component archives must match the fixed production download catalog.

Before building and sealing the runtime receipt, prepare its rebuild source:
    python scripts/prepare_release.py --prepare-source-archive <new-source.zip> --source-ref <commit>
Build from that source and retain the emitted commit and archive SHA256 in the
build receipt. Never rewrite an already sealed source archive.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile
from fnmatch import fnmatchcase
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
TIERS = {
    "basic": ("基础版", ()),
    "full": ("完整版", ("sing-box-statistics",)),
    "extended": ("扩展版", ("sing-box-statistics", "xray-probes")),
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def plain_path(path: Path) -> None:
    for parent in (path, *path.parents):
        require(not parent.is_symlink() and not parent.is_junction(), "Linked input or output path")


def sha256(value: Path | bytes) -> str:
    if isinstance(value, bytes):
        return hashlib.sha256(value).hexdigest()
    with value.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def json_bytes(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def safe_name(name: str) -> None:
    parts = PurePosixPath(name).parts
    require(bool(parts) and not PurePosixPath(name).is_absolute()
            and not any(p in {".", ".."} or ":" in p for p in parts)
            and "\\" not in name and str(PurePosixPath(name)) == name,
            "Unsafe payload name")


def private_name(name: str) -> bool:
    parts = PurePosixPath(name.casefold()).parts
    return any(p in {".private", ".ai-toolbox", ".git", ".codex", ".vscode", "logs",
                     "agents.md", "context.md", "status.md", "backlog.json",
                     "findings.md", "progress.md", "task_plan.md"}
               or p.startswith(".env") for p in parts) or name.lower().endswith((".log", ".tmp", ".user"))


def allowed_source_name(name: str) -> bool:
    root_files = {
        "Directory.Build.props", "global.json", "V2rayNMonitor.sln", "LICENSE", "README.md", "SECURITY.md",
        "THIRD-PARTY-NOTICES.md", "web/package.json", "web/package-lock.json",
        "web/tsconfig.json", "web/index.html", "web/playwright.config.mjs", "web/README.md",
        "web/public/icon.ico", "web/public/icon.svg", "web/public/icon-256.png",
        "tests/fixtures/README.md", "tests/fixtures/provider-synthetic.csv",
        "tests/test_prepare_release.py",
        "scripts/prepare_release.py", "scripts/prepare_core_component.py",
        "scripts/generate_icons.py", "scripts/smoke_demo.py",
        "docs/architecture.md", "docs/compatibility.md", "docs/QUICKSTART.md",
        "docs/release-v0.2.0.md", "docs/singbox-extension-scope.md",
        "docs/proposals/scope-ledger-v2.sql", "docs/proposals/scope-ledger-v3.sql",
    }
    source_patterns = ("src/*/*.cs", "src/*/*.csproj", "tests/*.Tests/*.cs", "tests/*.Tests/*.csproj",
                       "web/src/*.ts", "web/src/*.css", "web/tests/*.mjs",
                       "extensions/*/*.patch", "extensions/*/*.go", "extensions/*/build-ci.ps1",
                       "extensions/*/README.md", "extensions/*/LICENSE", "extensions/*/COPYING",
                       "licenses/*.txt")
    return not private_name(name) and (name in root_files or any(
        fnmatchcase(name, pattern) for pattern in source_patterns))


def read_source(path: Path) -> None:
    """Reject a dirty sealed source ZIP; never rewrite its build identity."""
    with zipfile.ZipFile(path) as archive:
        seen = set()
        files = set()
        for member in archive.infolist():
            name = member.filename.rstrip("/") if member.is_dir() else member.filename
            safe_name(name)
            require(name.casefold() not in seen and not private_name(name),
                    "Private, development or duplicate source entry")
            seen.add(name.casefold())
            require(not stat.S_ISLNK(member.external_attr >> 16) and not member.flag_bits & 1,
                    "Linked or encrypted source entry")
            if member.is_dir():
                continue
            require(allowed_source_name(name),
                    "Source entry is outside the rebuild file list")
            files.add(name)
        require({"Directory.Build.props", "global.json", "V2rayNMonitor.sln", "src/Host/Host.csproj", "LICENSE",
                 "web/package.json", "web/package-lock.json", "docs/proposals/scope-ledger-v2.sql",
                 "docs/proposals/scope-ledger-v3.sql"} <= files,
                "Incomplete rebuild source archive")
        require(archive.testzip() is None, "Invalid Monitor source archive")


def prepare_source_archive(output: Path, source_ref: str) -> dict:
    """Select rebuild inputs from one Git commit before building/sealing."""
    plain_path(output)
    require(not output.exists() and output.suffix.lower() == ".zip", "Source output must be a new ZIP")
    require(bool(source_ref) and not source_ref.startswith("-"), "Invalid source ref")
    def git(*args: str) -> bytes:
        return subprocess.check_output(["git", "-C", str(ROOT), "--literal-pathspecs", *args],
                                       stderr=subprocess.PIPE)
    commit = git("rev-parse", "--verify", source_ref + "^{commit}").decode().strip()
    require(re.fullmatch(r"[0-9a-f]{40}", commit) is not None, "Invalid source commit")
    tracked = git("ls-tree", "-r", "--name-only", "-z", commit).decode("utf-8").split("\0")
    files = [name for name in tracked if name and allowed_source_name(name)]
    require(bool(files), "No rebuild inputs at the source commit")
    for name in files:
        safe_name(name)
    with tempfile.TemporaryDirectory(prefix="monitor-source-") as staging:
        candidate = Path(staging) / "source.zip"
        candidate.write_bytes(git("archive", "--format=zip", commit, "--", *files))
        read_source(candidate)
        with output.open("xb") as target, candidate.open("rb") as source:
            shutil.copyfileobj(source, target)
    return {"sourceCommit": commit, "sourceArchiveSha256": sha256(output), "fileCount": len(files)}


def read_runtime(directory: Path, receipt: Path) -> tuple[dict, dict[str, Path]]:
    plain_path(directory)
    plain_path(receipt)
    info = json.loads(receipt.read_text(encoding="utf-8"))
    require(info.get("runtime") == "win-x64" and info.get("selfContained") is True,
            "A self-contained Windows x64 publish is required")
    require(re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", info.get("version", "")) is not None
            and re.fullmatch(r"[0-9a-f]{40}", info.get("sourceCommit", "")) is not None,
            "Invalid version or source identity")
    require(info.get("productVersion") == info["version"] + "+" + info["sourceCommit"],
            "Published product version differs from the build receipt")
    require(0 < len(info["files"]) <= 2000, "Invalid runtime inventory")
    files: dict[str, Path] = {}
    identities: set[str] = set()
    total = 0
    for item in info["files"]:
        name = item["name"]
        safe_name(name)
        require(not private_name(name), "Private or development runtime entry")
        key = name.casefold()
        require(key not in identities, "Duplicate runtime filename")
        require(PurePosixPath(name).suffix.lower() not in {".db", ".sqlite", ".sqlite3", ".csv", ".lnk", ".key", ".pem"}
                and not key.endswith(("-wal", "-shm"))
                and PurePosixPath(key).name not in {"settings.json", "config.json", "monitor-stats.json", "guinconfig.json"},
                "Runtime data or configuration must not be distributed")
        path = directory / name
        plain_path(path)
        require(path.is_file() and path.stat().st_size == item["bytes"]
                and sha256(path) == item["sha256"], "Runtime file differs from the sealed publish")
        files[name] = path
        identities.add(key)
        total += item["bytes"]
    actual = {p.relative_to(directory).as_posix() for p in directory.rglob("*") if p.is_file()}
    require(actual == set(files), "Unlisted or missing runtime files")
    require(0 < total <= 512 * 1024 * 1024, "Invalid runtime size")
    require({"V2rayN-Monitor.exe", "V2rayN-Monitor.dll", "V2rayN-Monitor.deps.json",
             "V2rayN-Monitor.runtimeconfig.json", "Core.dll", "Server.dll", "hostfxr.dll",
             "hostpolicy.dll", "coreclr.dll", "wwwroot/index.html"} <= files.keys(),
            "Incomplete self-contained application")
    return info, files


def component_catalog() -> dict[str, dict]:
    source = (ROOT / "src/Host/CoreComponentDownloads.cs").read_text(encoding="utf-8")
    pattern = (r'"(sing-box-statistics|xray-probes)" => new\(new\(ComponentRelease \+\s*'
               r'"([^"]+)"\),\s*(\d+), "([0-9a-f]{64})",\s*'
               r'Array\.AsReadOnly\(new CoreAssetFile\[\]\s*\{(.*?)\}\)\)')
    result = {}
    for component, name, size, digest, body in re.findall(pattern, source, re.S):
        files = re.findall(r'new\("([^"]+)", (\d+), "([0-9a-f]{64})"\)', body)
        require(bool(files), "Missing fixed component file catalog")
        result[component] = {
            "assetName": name, "bytes": int(size), "sha256": digest,
            "files": {n: (int(b), h) for n, b, h in files},
        }
    require(set(result) == {"sing-box-statistics", "xray-probes"}, "Fixed component catalog unavailable")
    return result


def read_component(path: Path, spec: dict, component: str) -> tuple[dict, dict[str, bytes]]:
    plain_path(path)
    require(path.stat().st_size == spec["bytes"] and sha256(path) == spec["sha256"],
            "Component archive identity mismatch")
    contents = {}
    with zipfile.ZipFile(path) as archive:
        members = archive.infolist()
        names = [m.filename for m in members]
        require(len(names) == len(set(n.casefold() for n in names))
                and set(names) == set(spec["files"]), "Unexpected component archive contents")
        for member in members:
            safe_name(member.filename)
            require(not member.is_dir() and not stat.S_ISLNK(member.external_attr >> 16)
                    and not member.flag_bits & 1, "Linked, encrypted or directory component entry")
            length, digest = spec["files"][member.filename]
            require(member.file_size == length and 0 < length <= 256 * 1024 * 1024,
                    "Component entry size mismatch")
            data = archive.read(member)
            require(sha256(data) == digest, "Component file identity mismatch")
            contents[member.filename] = data
    metadata = json.loads(contents["component.json"])
    require(metadata["componentId"] == component
            and metadata["binarySha256"] == sha256(contents[metadata["fileName"]]),
            "Component metadata identity mismatch")
    return metadata, contents


def write_zip(path: Path, payload: dict[str, Path | bytes]) -> None:
    with zipfile.ZipFile(path, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name, value in sorted(payload.items()):
            safe_name(name)
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            with archive.open(info, "w") as target:
                if isinstance(value, bytes):
                    target.write(value)
                else:
                    with value.open("rb") as source:
                        shutil.copyfileobj(source, target)


def prepare(publish: Path, receipt: Path, source_archive: Path,
            component_directory: Path, output: Path) -> dict:
    plain_path(output)
    require(not output.exists(), "Output directory must be new")
    info, runtime = read_runtime(publish, receipt)
    plain_path(source_archive)
    require(info.get("sourceArchiveSha256") == sha256(source_archive),
            "Monitor source archive differs from the sealed build receipt")
    read_source(source_archive)
    components = {}
    for component, spec in component_catalog().items():
        metadata, files = read_component(component_directory / spec["assetName"], spec, component)
        components[component] = (metadata, files, spec)
    common: dict[str, Path | bytes] = dict(runtime)
    for name in ("LICENSE", "SECURITY.md", "THIRD-PARTY-NOTICES.md", "docs/release-v0.2.0.md"):
        path = ROOT / name
        plain_path(path)
        common[name] = path
    for name in ("DOTNET-MIT.txt", "DOTNET-THIRD-PARTY.txt", "SQLITE-PUBLIC-DOMAIN.txt",
                 "SQLITEPCLRAW-APACHE-2.0.txt"):
        path = ROOT / "licenses" / name
        plain_path(path)
        require(path.is_file(), "Missing runtime license entry")
        common["licenses/" + path.name] = path
    common["source/monitor-source.zip"] = source_archive
    common_hashes = {n: sha256(p) for n, p in common.items()}
    output.mkdir()
    assets = []
    for tier, (label, included) in TIERS.items():
        payload = dict(common)
        notes = (
            f"# v2rayN Monitor {info['version']} — {label}\n\n"
            "本目录由离线发布准备脚本生成；正式发行状态以 Release 为准。"
            "不会自动安装核心或合并数据。\n\n"
            f"Monitor 来源：`{info['sourceCommit']}`；三档共用相同程序。\n\n"
            f"随包组件：{', '.join(included) if included else '无；保留原生 Xray'}。\n\n"
            "先读 [升级与支持说明](docs/release-v0.2.0.md)。已有用户必须保留原 "
            "`--data-dir` 和账本模式；v3 继续 `--probe-ledger`，不能按包名降级。\n\n"
            "全新基础模式可运行根目录 EXE。多范围 v2／v3 启动分别使用 "
            "`--scope-ledger`／`--probe-ledger` 并显式选择全新目录；已有 v1 "
            "不得直接按新模式打开，须先执行有备份的单步离线升级。\n\n"
            "配套核心位于 `components/`，含固定源码、补丁、许可与构建记录，"
            "只随包提供，不自动运行或覆盖客户端。\n"
        )
        payload["README.md"] = notes.encode("utf-8")
        core_manifest = {}
        for component in included:
            metadata, files, spec = components[component]
            payload.update({f"components/{component}/{n}": b for n, b in files.items()})
            core_manifest[component] = {
                **metadata, "originalAsset": spec["assetName"], "originalAssetSha256": spec["sha256"],
            }
        hashes = {n: common_hashes[n] if n in common_hashes else sha256(p) for n, p in payload.items()}
        manifest = {
            "schemaVersion": 1, "publicationStatusAtPreparation": "unpublished", "version": info["version"],
            "tier": tier, "monitorCommit": info["sourceCommit"], "productVersion": info["productVersion"],
            "buildOrigin": info["buildOrigin"], "runtime": "win-x64", "selfContained": True,
            "buildVersionOverride": info.get("buildVersionOverride"), "monitorCi": info.get("ci"),
            "packagerSha256": sha256(Path(__file__)),
            "monitorAssemblySha256": hashes["V2rayN-Monitor.dll"],
            "sourceArchiveSha256": hashes["source/monitor-source.zip"],
            "components": core_manifest, "files": hashes,
        }
        payload["RELEASE-MANIFEST.json"] = json_bytes(manifest)
        hashes["RELEASE-MANIFEST.json"] = sha256(payload["RELEASE-MANIFEST.json"])
        payload["SHA256SUMS.txt"] = "".join(f"{h}  {n}\n" for n, h in sorted(hashes.items())).encode("utf-8")
        path = output / f"v2rayn-monitor-{info['version']}-{tier}-windows-x64.zip"
        write_zip(path, payload)
        assets.append({"tier": tier, "name": path.name, "bytes": path.stat().st_size,
                       "sha256": sha256(path), "monitorAssemblySha256": manifest["monitorAssemblySha256"],
                       "fileCount": len(payload), "components": list(included)})
        print(json.dumps(assets[-1]), flush=True)
    require(len({a["monitorAssemblySha256"] for a in assets}) == 1, "Tier application identity differs")
    result = {
        "schemaVersion": 1, "publicationStatus": "unpublished", "version": info["version"],
        "sourceCommit": info["sourceCommit"], "buildOrigin": info["buildOrigin"], "assets": assets,
    }
    (output / "release-candidate.json").write_bytes(json_bytes(result))
    (output / "SHA256SUMS.txt").write_text(
        "".join(f"{a['sha256']}  {a['name']}\n" for a in assets), encoding="utf-8", newline="\n")
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-source-archive", type=Path, help="Prepare a new rebuild-source ZIP before building/sealing")
    parser.add_argument("--source-ref", help="Exact Git commit/ref for --prepare-source-archive")
    parser.add_argument("--publish-dir", type=Path)
    parser.add_argument("--runtime-input", type=Path, help="Sealed native build receipt with file identities")
    parser.add_argument("--source-archive", type=Path,
                        help="Sealed rebuild-source ZIP; omit private data and development records before building/sealing")
    parser.add_argument("--component-dir", type=Path, help="Existing fixed independent component ZIPs")
    parser.add_argument("--output", type=Path, help="New local candidate directory")
    args = parser.parse_args()
    release_inputs = (args.publish_dir, args.runtime_input, args.source_archive, args.component_dir, args.output)
    if args.prepare_source_archive:
        if not args.source_ref or any(release_inputs):
            parser.error("Source preparation requires --source-ref and cannot be mixed with release inputs")
    elif args.source_ref or not all(release_inputs):
        parser.error("Provide all five release inputs, or --prepare-source-archive with --source-ref")
    try:
        if args.prepare_source_archive:
            print(json.dumps(prepare_source_archive(args.prepare_source_archive, args.source_ref)))
        else:
            prepare(args.publish_dir, args.runtime_input, args.source_archive, args.component_dir, args.output)
    except (ValueError, OSError, subprocess.CalledProcessError, zipfile.BadZipFile, KeyError) as error:
        print(f"Release preparation failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error


if __name__ == "__main__":
    main()
