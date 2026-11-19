import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "Clear-RemoteDeskReleaseCache.ps1"


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell 7 is required")
class ReleaseCacheCleanupTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.cache = self.root / "artifacts"
        self.cache.mkdir()
        self.script = self.root / "scripts" / SCRIPT.name
        self.script.parent.mkdir()
        shutil.copyfile(SCRIPT, self.script)

    def release(self, name, stamp):
        directory = self.cache / name
        version = name.split("-")[1]
        entries = []
        for relative in ("RemoteDesk-win-x64/RemoteDesk.exe", "RemoteDesk-android-release.apk",
                         f"remotedesk-linux-host_{version}_amd64.deb"):
            file = directory / relative
            file.parent.mkdir(parents=True, exist_ok=True)
            payload = f"{name}/{relative}".encode()
            file.write_bytes(payload)
            entries.append(dict(name=file.name, path=file.relative_to(self.root).as_posix(),
                                length=len(payload), sha256=hashlib.sha256(payload).hexdigest()))
        manifest = directory / "RemoteDesk-release-manifest.json"
        manifest.write_text(json.dumps(dict(artifacts=entries, buildStamp=stamp, sourceDirty=False,
                                      internalCandidate=False, androidReleaseRequested=True,
                                      scope="Windows host/viewer; Android; Linux host/viewer package")))
        return directory

    def promote(self, directory):
        shutil.copyfile(directory / "RemoteDesk-release-manifest.json", self.cache / "RemoteDesk-release-manifest.json")

    def run_cleanup(self, *args, check=True):
        return subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-File", str(self.script), *args],
                              capture_output=True, encoding="utf-8", check=check)

    def test_old_packages_and_finished_probes_preserve_evidence_and_active_builds(self):
        old = self.release("release-1.0.9", "20260101000000")
        pinned = self.release("release-1.0.10", "20260110000000")
        current = self.release("release-1.0.10-r2", "20260111000000")
        newer = self.release("release-1.0.11", "20260201000000")
        active = self.release("release-1.0.8", "20251201000000")
        (active / ".publish-backup-ongoing").mkdir()
        (old / "RemoteDesk-win-x64" / "result.png").touch()
        (old / "notes.json").touch()
        self.promote(pinned)
        apk = "RemoteDesk-android-release.apk"
        qa = self.cache / "rollout-1.0.10"
        probe = qa / "physical-probe-r2"
        probe.mkdir(parents=True)
        (probe / "RemoteDesk.InteropProbe.deps.json").touch()
        (probe / "RemoteDesk.InteropProbe.dll").touch()
        (probe / "screenshot.png").touch()
        (qa / "RemoteDesk-QA-fixture.zip").touch()
        (qa / "user.zip").touch()

        self.run_cleanup("-ReleaseName", current.name, "-WhatIf")
        self.assertTrue((old / apk).exists())
        self.run_cleanup("-ReleaseName", current.name)
        self.assertFalse((old / apk).exists())
        self.assertFalse((old / "RemoteDesk-win-x64" / "RemoteDesk.exe").exists())
        for item in (pinned / apk, current / apk, newer / apk, active / apk,
                     old / "notes.json", old / "RemoteDesk-win-x64" / "result.png", qa / "RemoteDesk-QA-fixture.zip"):
            self.assertTrue(item.exists(), item)
        self.promote(current)
        self.run_cleanup("-CompletedRollout")
        self.run_cleanup("-CompletedRollout")
        self.assertFalse((pinned / apk).exists())
        self.assertFalse((qa / "RemoteDesk-QA-fixture.zip").exists())
        self.assertFalse((probe / "RemoteDesk.InteropProbe.dll").exists())
        for item in (probe / "screenshot.png", qa / "user.zip", current / apk, newer / apk):
            self.assertTrue(item.exists(), item)

    def test_invalid_current_package_cannot_trigger_cleanup(self):
        old = self.release("release-1.0.1", "20260101000000")
        current = self.release("release-1.0.2", "20260102000000")
        self.promote(current)
        (current / "RemoteDesk-android-release.apk").write_bytes(b"damaged")
        self.assertNotEqual(0, self.run_cleanup(check=False).returncode)
        self.assertTrue((old / "RemoteDesk-android-release.apk").exists())

    @unittest.skipUnless(os.name == "nt", "Windows file sharing and directory junctions")
    def test_locked_file_retries_and_junction_cannot_affect_external_directory(self):
        old = self.release("release-1.0.1", "20260101000000")
        current = self.release("release-1.0.2", "20260102000000")
        self.promote(current)
        outside = self.root / "outside"
        (old / "RemoteDesk-win-x64").rename(outside)
        junction = old / "RemoteDesk-win-x64"
        env = dict(os.environ, RD_FIXTURE_LINK=str(junction), RD_FIXTURE_OUTSIDE=str(outside),
                   RD_FIXTURE_APK=str(old / "RemoteDesk-android-release.apk"), RD_FIXTURE_SCRIPT=str(self.script))
        subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:RD_FIXTURE_LINK "
                        "-Target $env:RD_FIXTURE_OUTSIDE | Out-Null; "
                        "$stream=[IO.File]::OpenRead($env:RD_FIXTURE_APK); try { "
                        "$r=& $env:RD_FIXTURE_SCRIPT; if ($r.PendingFiles -ne 1) { throw 'expected one locked file' } "
                        "} finally { $stream.Dispose() }"], capture_output=True, encoding="utf-8", env=env, check=True)
        self.assertTrue((old / "RemoteDesk-android-release.apk").exists())
        self.assertFalse((old / "remotedesk-linux-host_1.0.1_amd64.deb").exists())
        self.run_cleanup()
        self.assertFalse((old / "RemoteDesk-android-release.apk").exists())
        self.assertTrue((outside / "RemoteDesk.exe").exists())
