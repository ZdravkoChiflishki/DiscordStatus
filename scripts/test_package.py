"""Packaging contracts; no network or real DLLs required."""
import io
from pathlib import Path
import tempfile
import unittest
import zipfile

from package import PLUGIN_ROOT, checked_upstream, make_zip, package_plugin


class PackagingTests(unittest.TestCase):
    def test_rejects_unpinned_upstream_archive(self):
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            checked_upstream(b"not the checksum-pinned release")

    def test_layout_dependencies_license_and_provenance(self):
        original = {
            PLUGIN_ROOT + "DiscordStatus.dll": b"old plugin",
            PLUGIN_ROOT + "DiscordStatus.pdb": b"old symbols",
            PLUGIN_ROOT + "DiscordStatus.deps.json": b"original dependency metadata",
            PLUGIN_ROOT + "Discord.Net.Rest.dll": b"pinned Discord.Net runtime",
            "release/desktop.ini": b"not a runtime file",
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "DiscordStatus.dll").write_bytes(b"fixed plugin")
            (root / "DiscordStatus.pdb").write_bytes(b"fixed symbols")
            (root / "CounterStrikeSharp.API.dll").write_bytes(b"must not ship")
            result = package_plugin(make_zip(original), root, {"source_commit": "test"})
        with zipfile.ZipFile(io.BytesIO(result)) as archive:
            self.assertEqual(archive.read(PLUGIN_ROOT + "DiscordStatus.dll"), b"fixed plugin")
            for name in ("Discord.Net.Rest.dll", "DiscordStatus.deps.json"):
                self.assertEqual(archive.read(PLUGIN_ROOT + name), original[PLUGIN_ROOT + name])
            self.assertIn("GNU GENERAL PUBLIC LICENSE", archive.read(PLUGIN_ROOT + "LICENSE").decode())
            self.assertIn(b"test", archive.read(PLUGIN_ROOT + "BUILD.json"))
            self.assertTrue(all(name.startswith(PLUGIN_ROOT) for name in archive.namelist()))
            self.assertFalse(any("CounterStrikeSharp.API" in name for name in archive.namelist()))

    def test_missing_upstream_plugin_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "Missing expected upstream file"):
            package_plugin(make_zip({}), Path("unused"), {})

    def test_zip_is_repeatable_and_sorted(self):
        self.assertEqual(make_zip({"b": b"2", "a": b"1"}), make_zip({"a": b"1", "b": b"2"}))


if __name__ == "__main__":
    unittest.main()
