#!/usr/bin/env python3
"""Verify a Jellyfin plugin repository manifest: every plugin has a non-empty versions array and each version has
version, changelog, targetAbi, sourceUrl, checksum (32-char MD5) and timestamp. Optionally downloads the zip of the
newest version and checks its MD5 (--download). An empty versions array makes the plugin invisible in the catalogue."""
import hashlib
import json
import re
import sys
import urllib.request

path = sys.argv[1] if len(sys.argv) > 1 else "manifest.json"
download = "--download" in sys.argv
manifest = json.load(open(path, encoding="utf-8"))
errors = []
if not isinstance(manifest, list) or not manifest:
    errors.append("manifest must be a non-empty JSON array")
for p in manifest if isinstance(manifest, list) else []:
    for k in ("guid", "name", "overview", "description", "owner", "category"):
        if not p.get(k):
            errors.append(f"plugin is missing {k}")
    versions = p.get("versions") or []
    if not versions:
        errors.append(f"{p.get('name')}: versions array is empty (the plugin would be invisible in the catalogue)")
    for v in versions:
        for k in ("version", "changelog", "targetAbi", "sourceUrl", "checksum", "timestamp"):
            if not v.get(k):
                errors.append(f"{p.get('name')} {v.get('version')}: missing {k}")
        if v.get("checksum") and not re.fullmatch(r"[0-9a-f]{32}", v["checksum"]):
            errors.append(f"{v.get('version')}: checksum is not an MD5 hex string")
    if download and versions:
        v = versions[0]
        data = urllib.request.urlopen(v["sourceUrl"], timeout=60).read()
        md5 = hashlib.md5(data).hexdigest()
        if md5 != v["checksum"]:
            errors.append(f"{v['version']}: downloaded zip MD5 {md5} != manifest checksum {v['checksum']}")
        else:
            print(f"OK: {v['sourceUrl']} md5 {md5}")
if errors:
    print("Manifest problems:\n  - " + "\n  - ".join(errors))
    sys.exit(1)
print(f"OK: {path} is a valid plugin repository manifest")
