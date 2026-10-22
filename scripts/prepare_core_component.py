#!/usr/bin/env python3
"""Prepare an unpublished, flat component ZIP from an existing approved build.

Offline only: no build, EXE execution, download, installation or publication.
The emitted candidate record is review material, never a runtime trust source.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
import tarfile
import zipfile
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
CLIENT = "7.25.1+95f68d9955c2324518c5e1dc9485d73412102a95"
COMPONENTS = {
    "sing-box-statistics": {
        "extension": "sing-box",
        "source_project": "SagerNet/sing-box",
        "version": "1.14.1-monitor-test.5",
        "binary": "sing-box-client.exe",
        "binary_constant": "ApprovedProbeSingboxSha256",
        "revision": "1ac1a339cb1223e9c70eae14c44411c75033c02d",
        "archive": "artifacts/sing-box-fixed-source.tar.gz",
        "archive_sha256": "8420c7723828a8d9d062c3fafee28c7b7e0d20a4c904fa7ff283f6e884edd537",
        "license": "GPL-3.0-or-later",
        "provenance": "build-provenance.json",
        "patches": ["monitor.patch", "member-stats.patch", "member-sidecar.patch", "probe-stats.patch"],
        "required_tests": ["TestMonitorStatsEndpoint", "TestMonitorMemberBinding",
                           "TestMonitorMemberConcurrentIndependentConnections", "TestMonitorMembersDisabledPreservesOriginalCounters",
                           "TestMonitorMemberSidecar", "TestMonitorURLTestSeparateCounters", "TestMonitorURLTestDisabledAndConcurrent"],
    },
    "xray-probes": {
        "extension": "xray",
        "source_project": "XTLS/Xray-core",
        "version": "26.7.28-monitor-test.1",
        "binary": "xray.exe",
        "binary_constant": "ApprovedProbeXraySha256",
        "revision": "5ca6f4b7d4dc20a881d4330e498892697627ec0c",
        "archive": "xray-fixed-source.tar.gz",
        "archive_sha256": "45de3ead5186fea442b04c662c554a1ffd1d7bd9093a83410f2edabe74fb766e",
        "license": "MPL-2.0",
        "provenance": "provenance.json",
        "patches": ["probe-stats.patch"],
        "required_tests": ["TestMonitorProbeCountersKeepConnectionTagAndPartialErrors",
                           "TestMonitorProbeExcludedUnknownConcurrentAndDisabled"],
    },
}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_file(path: Path) -> bytes:
    for parent in (path, *path.parents):
        if parent.is_symlink() or parent.is_junction():
            raise ValueError(f"Linked input or output path: {parent.name}")
    if not path.is_file() or not 0 < path.stat().st_size <= 256 * 1024 * 1024:
        raise ValueError(f"Missing or oversized input: {path.name}")
    return path.read_bytes()


def require_hash(data: bytes, expected: str, label: str) -> None:
    if sha256(data) != expected.lower():
        raise ValueError(f"Identity mismatch: {label}")


def source_file(archive: tarfile.TarFile, name: str) -> bytes:
    member = archive.getmember(name)
    if not member.isfile() or not 0 < member.size <= 1024 * 1024:
        raise ValueError(f"Invalid upstream file: {name}")
    stream = archive.extractfile(member)
    if stream is None:
        raise ValueError(f"Missing upstream file: {name}")
    return stream.read()


def prepare(component_id: str, build_root: Path, output: Path) -> dict:
    spec = COMPONENTS[component_id]
    extension = ROOT / "extensions" / spec["extension"]
    discovery = (ROOT / "src/Host/WindowsDiscovery.cs").read_text(encoding="utf-8")
    binary_hash = re.search(spec["binary_constant"] + r' = "([A-F0-9]{64})"', discovery)
    if binary_hash is None:
        raise ValueError("Approved binary identity unavailable")
    binary = read_file(build_root / "artifacts" / spec["binary"])
    require_hash(binary, binary_hash[1], spec["binary"])
    source = read_file(build_root / spec["archive"])
    require_hash(source, spec["archive_sha256"], spec["archive"])
    provenance_bytes = read_file(build_root / "artifacts" / spec["provenance"])
    provenance = json.loads(provenance_bytes)
    for key, expected in (
        ("sourceRevision", spec["revision"]),
        ("sourceUrl", f"https://codeload.github.com/{spec['source_project']}/tar.gz/{spec['revision']}"),
        ("sourceArchiveSha256", spec["archive_sha256"]),
        ("coreSha256", sha256(binary)),
        ("goVersion", "go version go1.26.8 windows/amd64"),
        ("requiredTests", spec["required_tests"]),
        ("testsPassed", True),
        ("dependencyManifestsUnchanged", True),
    ):
        value = provenance.get(key)
        if isinstance(value, str) and key.endswith("Sha256"):
            value = value.lower()
        if value != expected or (isinstance(expected, bool) and type(value) is not bool):
            raise ValueError(f"Provenance mismatch: {key}")
    if spec["extension"] == "sing-box":
        if provenance.get("version") != spec["version"]:
            raise ValueError("Provenance version mismatch")
    elif not re.match(r"^Xray 26\.7\.28 .*monitor-test\.1 ", provenance.get("version", "")):
        raise ValueError("Provenance version mismatch")

    # Build parameters come from the digest-verified fixed source, not from arbitrary provenance.
    with tarfile.open(fileobj=io.BytesIO(source), mode="r:gz") as archive:
        prefix = ("sing-box-" if spec["extension"] == "sing-box" else "Xray-core-") + spec["revision"] + "/"
        license_bytes = source_file(archive, prefix + "LICENSE")
        if spec["extension"] == "sing-box":
            tags = source_file(archive, prefix + "release/DEFAULT_BUILD_TAGS_WINDOWS").decode().strip() + ",with_v2ray_api"
            flags = source_file(archive, prefix + "release/LDFLAGS").decode().strip()
            flags += f" -s -w -X github.com/sagernet/sing-box/constant.Version={spec['version']}"
            if provenance.get("tags") != tags:
                raise ValueError("Provenance tags mismatch")
            if not re.fullmatch(r"[a-fA-F0-9]{40}", str(provenance.get("monitorCommit", ""))):
                raise ValueError("Missing or invalid Monitor build commit")
        else:
            flags = "-s -w -X github.com/xtls/xray-core/core.build=monitor-test.1"
            if provenance.get("sourceIdentityMatchesInstalledCore") is not False:
                raise ValueError("Xray source identity boundary missing")
        if provenance.get("buildFlags") != flags:
            raise ValueError("Provenance build flags mismatch")
    # Local builds have both CI fields explicitly null; CI builds must identify both commit and run.
    if "ciHeadCommit" not in provenance or "ciRunId" not in provenance:
        raise ValueError("Missing build origin fields")
    if (provenance["ciHeadCommit"] is None) != (provenance["ciRunId"] is None):
        raise ValueError("Incomplete CI build identity")

    files = {spec["binary"]: binary, Path(spec["archive"]).name: source}
    if spec["extension"] == "sing-box":
        files["NOTICE.txt"] = read_file(extension / "LICENSE")
    for name in spec["patches"]:
        patch = read_file(extension / name)
        expected = provenance["patchSha256"]
        require_hash(patch, expected[name] if isinstance(expected, dict) else expected, name)
        files[name] = patch
    if spec["extension"] == "xray":
        for name, key in (("probe-stats.go", "probeSourceSha256"), ("probe-stats_test.go", "probeRegressionSha256")):
            path = extension / name if name == "probe-stats.go" else build_root / "artifacts" / name
            files[name] = read_file(path)
            require_hash(files[name], provenance[key], name)
    files["LICENSE"] = license_bytes
    for name in ("README.md", "build-ci.ps1"):
        files[name] = read_file(extension / name)
    # Keep build facts, not arbitrary extra fields from a local provenance file.
    keys = ("sourceRevision", "sourceUrl", "sourceArchiveSha256", "patchSha256", "goVersion", "tags",
            "buildFlags", "version", "coreSha256", "dependencyManifestsUnchanged", "testsPassed",
            "requiredTests", "probeSourceSha256", "probeRegressionSha256",
            "monitorCommit", "ciHeadCommit", "ciRunId")
    for key in ("monitorCommit", "ciHeadCommit", "ciRunId"):
        value = provenance.get(key)
        if value is not None and not re.fullmatch(r"[0-9]+" if key == "ciRunId" else r"[a-fA-F0-9]{40}", str(value)):
            raise ValueError(f"Invalid build identity: {key}")
    files[spec["provenance"]] = (json.dumps({k: provenance[k] for k in keys if k in provenance},
                                          indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    monitor_version = ElementTree.parse(ROOT / "src/Host/Host.csproj").findtext(".//Version")
    metadata = {
        "componentId": component_id, "componentVersion": spec["version"], "monitorVersion": monitor_version,
        "clientVersion": CLIENT, "runtime": "win-x64", "fileName": spec["binary"],
        "binaryBytes": len(binary), "binarySha256": sha256(binary),
        "upstreamRevision": spec["revision"], "license": spec["license"],
        "sourceIdentityMatchesInstalledCore": False,
    }
    files["component.json"] = (json.dumps(metadata, indent=2) + "\n").encode("utf-8")
    if sum(map(len, files.values())) > 512 * 1024 * 1024:
        raise ValueError("Expanded package too large")
    for parent in (output, *output.parents):
        if parent.is_symlink() or parent.is_junction():
            raise ValueError("Linked output path")
    output.mkdir()  # New directory only; never overwrite an existing artifact.
    name = f"v2rayn-monitor-component-{component_id}-{spec['version']}-windows-x64.zip"
    archive_path = output / name
    with zipfile.ZipFile(archive_path, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for filename, content in sorted(files.items()):
            info = zipfile.ZipInfo(filename, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, content)
    archive_bytes = read_file(archive_path)
    candidate = {
        **metadata, "publicationStatus": "unpublished", "assetName": name,
        "assetBytes": len(archive_bytes), "assetSha256": sha256(archive_bytes),
        "files": [{"name": n, "bytes": len(b), "sha256": sha256(b)} for n, b in sorted(files.items())],
    }
    (output / "component-candidate.json").write_text(json.dumps(candidate, indent=2) + "\n", encoding="utf-8")
    return candidate


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--component", required=True, choices=COMPONENTS)
    parser.add_argument("--build-root", required=True, type=Path, help="Existing fixed extension build OutputRoot")
    parser.add_argument("--output", required=True, type=Path, help="New local preparation directory")
    args = parser.parse_args()
    result = prepare(args.component, args.build_root, args.output)
    print(json.dumps({"publicationStatus": result["publicationStatus"], "assetName": result["assetName"],
                      "assetBytes": result["assetBytes"], "assetSha256": result["assetSha256"]}))


if __name__ == "__main__":
    main()
