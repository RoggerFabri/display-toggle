"""Tests for make-manifest.py: python3 -m unittest discover -s .github/scripts"""

import hashlib
import importlib.util
import json
import os
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "make_manifest", os.path.join(os.path.dirname(os.path.abspath(__file__)), "make-manifest.py"))
mm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mm)

WIN = "display-toggle-windows-x64.zip"
MAC = "display-toggle-macos.tar.gz"
PUBLISHED = "2026-01-01T00:00:00Z"


class MakeManifestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.win = self._write(WIN, b"windows zip bytes")
        self.mac = self._write(MAC, b"mac tarball")

    def _write(self, name, data, subdir=""):
        os.makedirs(os.path.join(self.tmp.name, subdir), exist_ok=True)
        path = os.path.join(self.tmp.name, subdir, name)
        with open(path, "wb") as f:
            f.write(data)
        return path

    def test_exact_bytes(self):
        data = mm.make_manifest("1.2.3", "v1.2.3", PUBLISHED, [self.win, self.mac])
        expected = (
            '{"version":"1.2.3","tag":"v1.2.3","published":"2026-01-01T00:00:00Z",'
            '"notes_url":"https://github.com/RoggerFabri/display-toggle/releases/tag/v1.2.3",'
            '"assets":{'
            '"windows-x64":{"name":"display-toggle-windows-x64.zip","sha256":"%s","size":17},'
            '"macos":{"name":"display-toggle-macos.tar.gz","sha256":"%s","size":11}}}'
        ) % (hashlib.sha256(b"windows zip bytes").hexdigest(), hashlib.sha256(b"mac tarball").hexdigest())
        self.assertEqual(data, expected.encode("utf-8"))
        self.assertFalse(data.endswith(b"\n"))

    def test_asset_order_does_not_matter(self):
        a = mm.make_manifest("1.0.0", "v1.0.0", PUBLISHED, [self.win, self.mac])
        b = mm.make_manifest("1.0.0", "v1.0.0", PUBLISHED, [self.mac, self.win])
        self.assertEqual(a, b)

    def test_parses_as_json(self):
        doc = json.loads(mm.make_manifest("1.0.0", "v1.0.0", PUBLISHED, [self.mac]))
        self.assertEqual(list(doc["assets"]), ["macos"])
        self.assertRegex(doc["assets"]["macos"]["sha256"], r"^[0-9a-f]{64}$")

    def test_rejects_bad_input(self):
        bad = [
            ("1.2", "v1.2", PUBLISHED, [self.win]),            # not X.Y.Z
            ("01.2.3", "v01.2.3", PUBLISHED, [self.win]),      # leading zero
            ("1.2.3-dev", "v1.2.3-dev", PUBLISHED, [self.win]),  # dev builds are never released
            ("1.2.3", "v1.2.4", PUBLISHED, [self.win]),        # tag mismatch
            ("1.2.3", "v1.2.3", "2026-01-01 00:00:00", [self.win]),
            ("1.2.3", "v1.2.3", PUBLISHED, []),
            ("1.2.3", "v1.2.3", PUBLISHED, [self.win, self.win]),
            ("1.2.3", "v1.2.3", PUBLISHED, [self._write("other.zip", b"x")]),
            ("1.2.3", "v1.2.3", PUBLISHED, [self._write(WIN, b"", "empty")]),
        ]
        for args in bad:
            with self.subTest(args=args[:3]):
                with self.assertRaises(ValueError):
                    mm.make_manifest(*args)

    def test_main_writes_file(self):
        out = os.path.join(self.tmp.name, "manifest.json")
        rc = mm.main(["--version", "2.0.0", "--tag", "v2.0.0", "--published", PUBLISHED,
                      "--out", out, self.win, self.mac])
        self.assertEqual(rc, 0)
        with open(out, "rb") as f:
            self.assertEqual(f.read(), mm.make_manifest("2.0.0", "v2.0.0", PUBLISHED, [self.win, self.mac]))
        self.assertEqual(mm.main(["--version", "2.0.0", "--tag", "v2", "--out", out, self.win]), 1)


if __name__ == "__main__":
    unittest.main()
