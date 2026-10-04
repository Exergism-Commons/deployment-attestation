import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tarfile
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("self_update", ROOT / "install/self-update-agent.py")
updater = importlib.util.module_from_spec(spec)
spec.loader.exec_module(updater)


class SelfUpdateTests(unittest.TestCase):
    def setUp(self):
        base = os.environ.get("EC_SELF_UPDATE_TEST_ROOT", "/root")
        self.directory = tempfile.TemporaryDirectory(prefix="ec-self-update-tests.", dir=base)
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.root.chmod(0o700)
        self.tag = "v0.1.10"
        self.commit = "a" * 40
        self.payloads = {
            "ec-deployment-agent-linux-x64": b"#!/bin/sh\necho 0.1.10\n",
            "install-id-exergism.sh": b"#!/bin/sh\nexit 0\n",
            "ec-deployment-agent-self-update.py": b"# new helper\n",
        }
        self.manifest = {"schema_version": "0.1", "repository": updater.REPOSITORY,
                         "release_tag": self.tag, "source_commit": self.commit,
                         "assets": {key: {"name": name, "sha256": hashlib.sha256(self.payloads[name]).hexdigest()}
                                    for key, name in updater.ASSETS.items()}}
        self.manifest_bytes = json.dumps(self.manifest).encode()
        self.payloads["DEPLOYMENT_MANIFEST.json"] = self.manifest_bytes
        self.hashes = {key: hashlib.sha256(value).hexdigest() for key, value in self.payloads.items()}
        self.release = {"tag_name": self.tag, "html_url": "https://github.com/" + updater.REPOSITORY + "/releases/tag/" + self.tag,
                        "draft": False, "prerelease": False,
                        "assets": [{"name": key, "digest": "sha256:" + self.hashes[key], "size": len(value),
                                    "browser_download_url": "https://github.com/" + updater.REPOSITORY +
                                    "/releases/download/" + self.tag + "/" + key}
                                   for key, value in self.payloads.items()]}

    def policy(self, value):
        path = self.root / "policy.json"
        path.write_text(json.dumps(value))
        path.chmod(0o600)
        return path

    def archive(self, members=None):
        path = self.root / "source.tar.gz"
        root = "deployment-attestation-" + self.commit
        members = members or [(root + "/install/install-id-exergism.sh", self.payloads["install-id-exergism.sh"]),
                              (root + "/install/self-update-agent.py", self.payloads["ec-deployment-agent-self-update.py"])]
        with tarfile.open(path, "w:gz") as archive:
            for name, data in members:
                info = tarfile.TarInfo(name)
                if isinstance(data, tarfile.TarInfo):
                    info = data
                else:
                    info.size = len(data)
                archive.addfile(info, io.BytesIO(data) if isinstance(data, bytes) else None)
        return path

    def fake_fetch(self, url, destination=None, limit=updater.MAX_DOWNLOAD):
        if "codeload" in url:
            raw = self.archive().read_bytes()
        else:
            raw = self.payloads[url.rsplit("/", 1)[1]]
        if destination is None:
            return raw
        Path(destination).write_bytes(raw)

    def test_missing_and_false_policy_opt_out(self):
        self.assertFalse(updater.read_policy(self.root / "missing"))
        self.assertFalse(updater.read_policy(self.policy({"enabled": False})))
        self.assertTrue(updater.read_policy(self.policy({"enabled": True})))

    def test_policy_requires_boolean_and_no_unknown_keys(self):
        for value in ({"enabled": "true"}, {"enabled": 1}, {"enabled": True, "url": "other"}, {}):
            with self.subTest(value=value), self.assertRaises(updater.UpdateError):
                updater.read_policy(self.policy(value))

    def test_policy_rejects_writable_file_and_symlinks(self):
        path = self.policy({"enabled": True})
        path.chmod(0o666)
        with self.assertRaises(updater.UpdateError):
            updater.read_policy(path)
        path.chmod(0o600)
        link = self.root / "link"
        link.symlink_to(path)
        with self.assertRaises(OSError):
            updater.read_policy(link)

    def test_policy_rejects_linked_parent(self):
        parent = self.root / "directory"
        parent.mkdir()
        link = self.root / "linked-directory"
        link.symlink_to(parent, target_is_directory=True)
        with self.assertRaises(updater.UpdateError):
            updater.read_policy(link / "policy")

    def test_disabled_main_has_no_network_or_subprocess(self):
        with mock.patch.object(updater, "read_policy", return_value=False), \
             mock.patch.object(updater, "fetch") as fetch, mock.patch.object(updater.subprocess, "check_output") as run:
            self.assertEqual(0, updater.main())
            fetch.assert_not_called()
            run.assert_not_called()

    def test_opt_out_during_download_prevents_installer(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(updater, "fetch", side_effect=self.fake_fetch), \
             mock.patch.object(updater, "read_policy", return_value=False), \
             mock.patch.object(updater.subprocess, "run") as run:
            updater.install_snapshot(stage, self.tag, self.hashes)
            run.assert_not_called()

    def test_opt_out_after_candidate_preflight_prevents_installer(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(updater, "fetch", side_effect=self.fake_fetch), \
             mock.patch.object(updater, "read_policy", side_effect=[True, False]), \
             mock.patch.object(updater, "no_pending_transactions"), \
             mock.patch.object(updater.subprocess, "check_output", return_value=b"0.1.10"), \
             mock.patch.object(updater.subprocess, "run") as run:
            updater.install_snapshot(stage, self.tag, self.hashes)
            run.assert_not_called()

    def test_valid_snapshot_binds_all_assets(self):
        tag, hashes = updater.release_snapshot(self.release, "0.1.9")
        self.assertEqual(self.tag, tag)
        self.assertEqual(self.hashes, hashes)
        self.assertEqual(self.commit, updater.manifest_snapshot(self.manifest_bytes, tag, hashes))

    def test_no_downgrade_and_no_same_version_reinstall(self):
        self.assertIsNone(updater.release_snapshot(self.release, "0.1.10"))
        self.assertIsNone(updater.release_snapshot(self.release, "0.1.11"))

    def test_prerelease_draft_foreign_repository_and_invalid_version_rejected(self):
        for key, value in (("prerelease", True), ("draft", True), ("html_url", "https://github.com/other/repo"),
                           ("tag_name", "v0.1.10-rc1"), ("tag_name", "v0.01.10")):
            bad = copy.deepcopy(self.release)
            bad[key] = value
            with self.subTest(key=key), self.assertRaises(updater.UpdateError):
                updater.release_snapshot(bad, "0.1.9")

    def test_asset_digest_and_url_must_be_present_and_bound(self):
        for key, value in (("digest", None), ("digest", "sha256:" + "A" * 64),
                           ("browser_download_url", "https://example.org/root-code"), ("size", 0)):
            bad = copy.deepcopy(self.release)
            bad["assets"][0][key] = value
            with self.subTest(key=key), self.assertRaises(updater.UpdateError):
                updater.release_snapshot(bad, "0.1.9")

    def test_duplicate_asset_rejected(self):
        bad = copy.deepcopy(self.release)
        bad["assets"].append(copy.deepcopy(bad["assets"][0]))
        with self.assertRaises(updater.UpdateError):
            updater.release_snapshot(bad, "0.1.9")

    def test_changed_manifest_is_rejected(self):
        with self.assertRaises(updater.UpdateError):
            updater.manifest_snapshot(self.manifest_bytes + b" ", self.tag, self.hashes)

    def test_mixed_generation_helper_is_rejected(self):
        bad = copy.deepcopy(self.manifest)
        bad["assets"]["self_update_helper"]["sha256"] = "b" * 64
        raw = json.dumps(bad).encode()
        hashes = dict(self.hashes, **{"DEPLOYMENT_MANIFEST.json": hashlib.sha256(raw).hexdigest()})
        with self.assertRaises(updater.UpdateError):
            updater.manifest_snapshot(raw, self.tag, hashes)

    def test_duplicate_json_keys_rejected(self):
        with self.assertRaises(updater.UpdateError):
            updater.strict_json('{"enabled":false,"enabled":true}')

    def test_restrict_https_and_redirect_destinations(self):
        for url in ("http://github.com/file", "https://github.com.evil/file", "https://localhost/file",
                    "https://user@github.com/file", "https://github.com:444/file"):
            with self.subTest(url=url), self.assertRaises(updater.UpdateError):
                updater.allowed_url(url)

    def test_pending_or_symlinked_journal_blocks_update(self):
        path = self.root / "transaction.json"
        updater.no_pending_transactions([path])
        path.write_text("{}")
        with self.assertRaises(updater.UpdateError):
            updater.no_pending_transactions([path])
        path.unlink()
        path.symlink_to(self.root / "absent")
        with self.assertRaises(updater.UpdateError):
            updater.no_pending_transactions([path])

    def test_archive_cannot_escape_or_publish_links(self):
        prefix = "deployment-attestation-" + self.commit
        link = tarfile.TarInfo(prefix + "/link")
        link.type = tarfile.SYMTYPE
        link.linkname = "/etc"
        for member in ((prefix + "/../escape", b"x"), ("/absolute", b"x"), (prefix + "/link", link)):
            with self.subTest(member=member[0]), self.assertRaises(updater.UpdateError):
                updater.unpack_source(self.archive([member]), self.root / ("out-" + str(len(list(self.root.iterdir())))),
                                      self.commit)
        self.assertFalse((self.root / "escape").exists())

    def test_archive_duplicates_and_wrong_commit_rejected(self):
        prefix = "deployment-attestation-" + self.commit
        for members in ([(prefix + "/file", b"1"), (prefix + "/file", b"2")],
                        [("deployment-attestation-" + "b"*40 + "/file", b"1")]):
            with self.assertRaises(updater.UpdateError):
                updater.unpack_source(self.archive(members), self.root / ("out-" + str(len(list(self.root.iterdir())))),
                                      self.commit)

    def test_full_staging_invokes_installer_with_private_environment(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(updater, "fetch", side_effect=self.fake_fetch), \
             mock.patch.object(updater, "read_policy", return_value=True), \
             mock.patch.object(updater, "no_pending_transactions"), \
             mock.patch.object(updater.subprocess, "check_output", return_value=b"0.1.10\n"), \
             mock.patch.object(updater.subprocess, "run") as run:
            updater.install_snapshot(stage, self.tag, self.hashes)
        environment = run.call_args.kwargs["env"]
        self.assertEqual(str(stage), environment["EC_INSTALLER_TRUSTED_STAGE"])
        self.assertEqual(self.hashes["install-id-exergism.sh"], environment["EC_INSTALLER_SHA256"])
        self.assertEqual(self.hashes["ec-deployment-agent-linux-x64"], environment["EC_NATIVE_AGENT_SHA256"])
        self.assertNotIn("EC_AGENT_COORDINATION_LOCK_HELD", environment)
        self.assertNotIn("HOME", environment)
        self.assertTrue(run.call_args.kwargs["check"])
        self.assertEqual(0o500, stat.S_IMODE((stage/"install-id-exergism.sh").stat().st_mode))

    def test_candidate_version_mismatch_never_invokes_installer(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(updater, "fetch", side_effect=self.fake_fetch), \
             mock.patch.object(updater, "read_policy", return_value=True), \
             mock.patch.object(updater, "no_pending_transactions"), \
             mock.patch.object(updater.subprocess, "check_output", return_value=b"0.1.9\n"), \
             mock.patch.object(updater.subprocess, "run") as run, self.assertRaises(updater.UpdateError):
            updater.install_snapshot(stage, self.tag, self.hashes)
        run.assert_not_called()

    def test_failed_installer_is_not_reported_as_success(self):
        stage = self.root / "stage"
        stage.mkdir()
        with mock.patch.object(updater, "fetch", side_effect=self.fake_fetch), \
             mock.patch.object(updater, "read_policy", return_value=True), \
             mock.patch.object(updater, "no_pending_transactions"), \
             mock.patch.object(updater.subprocess, "check_output", return_value=b"0.1.10\n"), \
             mock.patch.object(updater.subprocess, "run", side_effect=subprocess.CalledProcessError(7, "installer")), \
             self.assertRaises(subprocess.CalledProcessError):
            updater.install_snapshot(stage, self.tag, self.hashes)

    def test_real_recovery_restores_package_and_preserves_revoked_policy(self):
        text = (ROOT/"install/recover-id-exergism-install.sh").read_text()
        def function(name):
            return re.search(r"^" + name + r"\(\) \{.*?^\}", text, re.M | re.S)[0]
        mapping = {"AGENT": "agent", "SMOKE": "smoke", "AGENT_SERVICE_UNIT": "service_unit",
                   "AGENT_TIMER_UNIT": "timer_unit", "ENV_FILE": "env", "FENCE_DROPIN": "fence",
                   "SELF_UPDATE_HELPER": "self_update_helper", "SELF_UPDATE_SERVICE_UNIT": "self_update_service",
                   "SELF_UPDATE_TIMER_UNIT": "self_update_timer"}
        journal = self.root / "journal"
        (journal/"backups").mkdir(parents=True)
        lines = ["set -euo pipefail", "TXN_DIR=" + str(journal)]
        policy = self.policy({"enabled": False})
        (journal/"backups"/"self_update_policy").write_text('{"enabled": true}')
        (journal/"self_update_policy_present").write_text("1")
        keys = []
        for variable, key in mapping.items():
            path = self.root / key
            path.write_text("new")
            lines.append(variable + "=" + str(path))
            (journal/(key+"_present")).write_text("1")
            (journal/"backups"/key).write_text("old-"+key)
            keys.append(key)
        lines += ['read_value() { cat "$TXN_DIR/$1"; }', function("path_exists_any"),
                  function("artifact_path"), function("restore_artifact"),
                  "for key in " + " ".join(keys) + '; do restore_artifact "$key"; done']
        subprocess.run(["bash", "-c", "\n".join(lines)], check=True)
        for key in keys:
            self.assertEqual("old-"+key, (self.root/key).read_text())
        self.assertFalse(updater.read_policy(policy))
        self.assertNotIn("self_update_policy)", text)
        self.assertIn("for key in self_update_helper self_update_service self_update_timer; do", text)

    def test_installer_policy_is_preserved_and_timer_is_never_enabled(self):
        text = (ROOT/"install/install-id-exergism.sh").read_text()
        self.assertIn('if [[ ! -e "$SELF_UPDATE_POLICY" && ! -L "$SELF_UPDATE_POLICY" ]]; then', text)
        self.assertNotIn('systemctl enable ec-deployment-agent-self-update.timer', text)
        self.assertFalse(json.loads((ROOT/"packaging/self-update.json").read_text())["enabled"])
        self.assertIn('ExecStart=/usr/local/libexec/ec-deployment-agent self-update',
                      (ROOT/"packaging/ec-deployment-agent-self-update.service").read_text())
        begin = text.index("\ncreate_install_transaction\n")
        published = text.index('atomic_install_root_file "$REPO_INPUT_STAGE/install/self-update-agent.py"', begin)
        validated = text.index("\nmark_generation_validated\n", published)
        self.assertLess(begin, published)
        self.assertLess(published, validated)

    def test_admission_under_installer_lock_rejects_completed_concurrent_upgrade(self):
        with mock.patch.object(updater, "read_policy", return_value=True), \
             mock.patch.object(updater, "no_pending_transactions"), \
             mock.patch.object(updater.subprocess, "check_output", return_value=b"0.1.11"):
            self.assertEqual(2, updater.admit_candidate("v0.1.10"))

    def test_admission_rechecks_opt_in_before_version_or_mutation(self):
        with mock.patch.object(updater, "read_policy", return_value=False), \
             mock.patch.object(updater.subprocess, "check_output") as run:
            self.assertEqual(2, updater.admit_candidate("v0.1.10"))
            run.assert_not_called()

    def test_admission_allows_only_a_newer_authorized_candidate(self):
        with mock.patch.object(updater, "read_policy", return_value=True), \
             mock.patch.object(updater, "no_pending_transactions"), \
             mock.patch.object(updater.subprocess, "check_output", return_value=b"0.1.9"):
            self.assertEqual(0, updater.admit_candidate("v0.1.10"))
        text = (ROOT/"install/install-id-exergism.sh").read_text()
        self.assertLess(text.index("flock -n 9"), text.index('"$AGENT" self-update --admit'))
        self.assertLess(text.index('"$AGENT" self-update --admit'),
                        text.index('atomic_install_root_file "$REPO_INPUT_STAGE/install/validate-id-exergism-generation.sh"'))

    def test_legacy_and_new_journal_schemas_supported(self):
        for filename in ("recover-id-exergism-install.sh", "finalize-id-exergism-recovery.sh"):
            text = (ROOT/"install"/filename).read_text()
            self.assertIn('[[ "$schema_version" == 2 || "$schema_version" == 3 ]]', text)


if __name__ == "__main__":
    unittest.main(verbosity=2)

