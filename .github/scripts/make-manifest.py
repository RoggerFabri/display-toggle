#!/usr/bin/env python3
"""Write the release manifest.json that the apps' update check verifies (see the release workflow).

    make-manifest.py --version 1.2.3 --tag v1.2.3 [--published 2026-01-01T00:00:00Z] \
                     [--out manifest.json] ASSET...

Each ASSET is a release file named after a known platform (ASSET_KEYS). The output is compact UTF-8
JSON with a fixed key order and no trailing newline: these exact bytes are what CI signs, so the
same inputs always give the same bytes. Standard library only.
"""

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone

REPO_URL = "https://github.com/RoggerFabri/display-toggle"

# Release asset file name -> manifest key. The apps look an asset up by key, then download it by name.
ASSET_KEYS = {
    "display-toggle-windows-x64.zip": "windows-x64",
    "display-toggle-macos.tar.gz": "macos",
}

VERSION_RE = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
PUBLISHED_RE = re.compile(r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")


def describe_asset(path):
    """(key, entry) for one asset file: its manifest key and {name, sha256, size}."""
    name = os.path.basename(path)
    if name not in ASSET_KEYS:
        raise ValueError(f"unknown release asset {name!r} (expected one of {sorted(ASSET_KEYS)})")
    digest = hashlib.sha256()
    size = 0
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
            size += len(chunk)
    if size == 0:
        raise ValueError(f"release asset {name!r} is empty")
    return ASSET_KEYS[name], {"name": name, "sha256": digest.hexdigest(), "size": size}


def make_manifest(version, tag, published, asset_paths):
    """The manifest bytes: compact JSON, keys in a fixed order, no trailing newline."""
    if not VERSION_RE.match(version):
        raise ValueError(f"version {version!r} is not X.Y.Z")
    if tag != "v" + version:
        raise ValueError(f"tag {tag!r} does not match version {version!r}")
    if not PUBLISHED_RE.match(published):
        raise ValueError(f"published {published!r} is not YYYY-MM-DDTHH:MM:SSZ")
    if not asset_paths:
        raise ValueError("no release assets given")

    assets = {}
    for path in asset_paths:
        key, entry = describe_asset(path)
        if key in assets:
            raise ValueError(f"release asset {entry['name']!r} given twice")
        assets[key] = entry

    manifest = {
        "version": version,
        "tag": tag,
        "published": published,
        "notes_url": f"{REPO_URL}/releases/tag/{tag}",
        # In ASSET_KEYS order, so the bytes don't depend on the order the assets were passed in.
        "assets": {key: assets[key] for key in ASSET_KEYS.values() if key in assets},
    }
    return json.dumps(manifest, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Write the signed-release manifest.json.")
    parser.add_argument("--version", required=True, help="release version, X.Y.Z")
    parser.add_argument("--tag", required=True, help="release tag, vX.Y.Z")
    parser.add_argument("--published", help="UTC time, YYYY-MM-DDTHH:MM:SSZ (default: now)")
    parser.add_argument("--out", help="output file (default: stdout)")
    parser.add_argument("assets", nargs="+", help="release asset files")
    args = parser.parse_args(argv)

    published = args.published or datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    try:
        data = make_manifest(args.version, args.tag, published, args.assets)
    except (ValueError, OSError) as e:
        print(f"make-manifest: {e}", file=sys.stderr)
        return 1

    if args.out:
        with open(args.out, "wb") as f:
            f.write(data)
    else:
        sys.stdout.buffer.write(data)
    return 0


if __name__ == "__main__":
    sys.exit(main())
