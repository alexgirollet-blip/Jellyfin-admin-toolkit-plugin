#!/usr/bin/env python3
"""Add (or replace) a version entry in a Jellyfin plugin repository manifest.json.

Usage:
  update_manifest.py --manifest manifest.json --build-yaml build.yaml --version 1.0.0.0 \
      --source-url https://github.com/<owner>/<repo>/releases/download/v1.0.0.0/admin-toolkit_1.0.0.0.zip \
      --zip path/to/admin-toolkit_1.0.0.0.zip --changelog "text"

The checksum is the MD5 of the zip (what Jellyfin verifies when installing). Plugin metadata
(guid, name, overview, description, owner, category, targetAbi) comes from build.yaml.
Uses only the Python standard library (simple parser for the flat build.yaml format).
"""
import argparse
import datetime
import hashlib
import json
import os
import re


def read_build_yaml(path):
    """Minimal reader for the flat key: value / key: > block format used by build.yaml."""
    data = {}
    key = None
    block = []
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            m = re.match(r"^([A-Za-z][A-Za-z0-9_]*):\s*(.*)$", line)
            if m and not line.startswith(" "):
                if key is not None and block:
                    data[key] = " ".join(block).strip()
                key, value = m.group(1), m.group(2).strip()
                block = []
                if value in (">", "|", ""):
                    continue
                data[key] = value.strip('"').strip("'")
                key = None
            elif key is not None and line.startswith(" "):
                stripped = line.strip()
                if not stripped.startswith("- "):
                    block.append(stripped)
    if key is not None and block:
        data[key] = " ".join(block).strip()
    return data


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def version_key(v):
    return tuple(int(p) if p.isdigit() else 0 for p in v.split("."))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--build-yaml", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--source-url", required=True)
    ap.add_argument("--zip", required=True)
    ap.add_argument("--changelog", default="")
    ap.add_argument("--timestamp", default=None)
    args = ap.parse_args()

    meta = read_build_yaml(args.build_yaml)
    manifest = []
    if os.path.exists(args.manifest):
        with open(args.manifest, encoding="utf-8") as f:
            text = f.read().strip()
            manifest = json.loads(text) if text else []

    entry = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
    if entry is None:
        entry = {"guid": meta["guid"], "versions": []}
        manifest.append(entry)
    for field in ("name", "overview", "description", "owner", "category"):
        if meta.get(field):
            entry[field] = meta[field]

    timestamp = args.timestamp or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    version = {
        "version": args.version,
        "changelog": args.changelog.strip() or meta.get("changelog", ""),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": args.source_url,
        "checksum": md5(args.zip),
        "timestamp": timestamp,
    }
    versions = [v for v in entry.get("versions", []) if v.get("version") != args.version]
    versions.append(version)
    versions.sort(key=lambda v: version_key(v["version"]), reverse=True)
    entry["versions"] = versions

    ordered = {k: entry[k] for k in ("guid", "name", "overview", "description", "owner", "category") if k in entry}
    ordered["versions"] = entry["versions"]
    manifest = [ordered if p is entry else p for p in manifest]

    with open(args.manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"manifest updated: {args.version} md5={version['checksum']}")


if __name__ == "__main__":
    main()
