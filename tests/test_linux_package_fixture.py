import io
import os
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "experiments"))
from wsl_package_fixture import extract_package


class LinuxPackageFixtureExtractionTests(unittest.TestCase):
    def archive(self, root, members):
        archive = root / "package.tar.gz"
        with tarfile.open(archive, "w:gz") as package:
            for member, data in members:
                package.addfile(member, io.BytesIO(data) if data is not None else None)
        return archive

    def regular(self, name, data=b"owned runtime"):
        item = tarfile.TarInfo(name)
        item.size = len(data)
        return item, data

    def link(self, name, target, hard=False):
        item = tarfile.TarInfo(name)
        item.type = tarfile.LNKTYPE if hard else tarfile.SYMTYPE
        item.linkname = target
        return item, None

    @unittest.skipIf(os.name == "nt", "Linux package links require a native Linux filesystem")
    def test_internal_runtime_symlink_and_hardlink_are_supported(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = self.archive(root, [self.regular("runtime/lib/library.so.1"),
                self.link("runtime/lib/library.so", "library.so.1"),
                self.link("runtime/lib/library-copy.so", "runtime/lib/library.so.1", hard=True)])
            target = root / "extracted"
            extract_package(archive, target)
            self.assertTrue((target / "runtime/lib/library.so").is_symlink())
            for name in ("library.so", "library-copy.so"):
                self.assertEqual(b"owned runtime", (target / "runtime/lib" / name).read_bytes())

    def test_traversal_and_absolute_member_names_are_rejected(self):
        for name in ("../outside.txt", "/tmp/outside.txt", "app/../../outside.txt"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                with self.assertRaises(ValueError):
                    extract_package(self.archive(root, [self.regular(name)]), root / "extracted")

    def test_links_cannot_escape_the_owned_directory(self):
        for hard in (False, True):
            for destination in ("/tmp/outside.txt", "../../../../outside.txt"):
                with self.subTest(hard=hard, destination=destination), tempfile.TemporaryDirectory() as temp:
                    root = Path(temp)
                    outside = root / "outside.txt"
                    outside.write_bytes(b"do not change")
                    with self.assertRaises((ValueError, tarfile.FilterError)):
                        extract_package(self.archive(root, [self.link("runtime/lib/link", destination, hard)]), root / "extracted")
                    self.assertEqual(b"do not change", outside.read_bytes())

    def test_device_nodes_remain_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            item = tarfile.TarInfo("device")
            item.type = tarfile.CHRTYPE
            with self.assertRaises(ValueError):
                extract_package(self.archive(root, [(item, None)]), root / "extracted")

    @unittest.skipIf(os.name == "nt", "Requires native Linux symlink behavior")
    def test_existing_destination_symlink_cannot_redirect_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            outside = root / "outside"
            outside.mkdir()
            sentinel = outside / "module.py"
            sentinel.write_bytes(b"preserve outside the extraction root")
            target = root / "extracted"
            target.mkdir()
            (target / "app").symlink_to(outside, target_is_directory=True)
            archive = self.archive(root, [self.regular("app/module.py", b"must not overwrite")])
            with self.assertRaises(tarfile.FilterError):
                extract_package(archive, target)
            self.assertEqual(b"preserve outside the extraction root", sentinel.read_bytes())


if __name__ == "__main__":
    unittest.main()
