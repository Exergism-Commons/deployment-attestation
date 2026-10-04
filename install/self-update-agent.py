#!/usr/bin/python3
"""Opt-in package updater. The installed generation completes each transaction."""
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = "Exergism-Commons/deployment-attestation"
API = "https://api.github.com/repos/" + REPOSITORY
POLICY = Path("/etc/ec-deployment-attestation/self-update.json")
AGENT = "/usr/local/libexec/ec-deployment-agent"
BOOTSTRAP = Path("/var/lib/ec-deployment-attestation/bootstrap")
LOCK = "/run/lock/ec-deployment-agent-self-update.lock"
ENVIRONMENT = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin"}
ASSETS = {"amd64": "ec-deployment-agent-linux-x64",
          "installer": "install-id-exergism.sh",
          "self_update_helper": "ec-deployment-agent-self-update.py"}
MAX_DOWNLOAD = 64 * 1024 * 1024


class UpdateError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise UpdateError(message)


def version(tag):
    require(isinstance(tag, str) and re.fullmatch(r"v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", tag),
            "Expected a stable x.y.z package version")
    return tuple(int(part) for part in tag.lstrip("v").split("."))


def strict_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON key: " + key)
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs,
                      parse_constant=lambda value: (_ for _ in ()).throw(UpdateError("Invalid JSON constant")))


def trusted_parents(path):
    for parent in reversed(Path(path).absolute().parents):
        metadata = parent.lstat()
        require(stat.S_ISDIR(metadata.st_mode) and not stat.S_ISLNK(metadata.st_mode)
                and metadata.st_uid == 0 and not metadata.st_mode & 0o022,
                "Unsafe parent directory: " + str(parent))


def trusted_read(path, limit=4096):
    trusted_parents(path)
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode) and before.st_uid == 0 and not before.st_mode & 0o022,
                "Expected a root-owned, non-writable-by-others regular file")
        require(before.st_size <= limit, "Trusted file exceeds limit")
        with os.fdopen(os.dup(descriptor), "rb") as reader:
            raw = reader.read(limit + 1)
        after = os.fstat(descriptor)
        require(len(raw) <= limit and
                (before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns) ==
                (after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns), "Trusted file changed")
        return raw
    finally:
        os.close(descriptor)


def read_policy(path=POLICY):
    try:
        document = strict_json(trusted_read(path))
    except FileNotFoundError:
        return False
    require(isinstance(document, dict) and set(document) == {"enabled"}
            and type(document["enabled"]) is bool, "Policy must contain only a boolean enabled")
    return document["enabled"]


def no_pending_transactions(paths=None):
    if paths is None:
        paths = [Path("/var/lib/ec-deployment-attestation/install/id.exergism.org." + phase)
                 for phase in ("pending", "validated", "recovering", "recovered")]
        paths.append(Path("/var/lib/ec-deployment-attestation/id.exergism.org/transaction.json"))
    for path in paths:
        require(not os.path.lexists(path), "Pending recovery/deployment transaction: " + str(path))


def allowed_url(url):
    parsed = urllib.parse.urlsplit(url)
    require(parsed.scheme == "https" and parsed.port in (None, 443) and not parsed.username
            and not parsed.password and parsed.hostname in
            {"api.github.com", "github.com", "codeload.github.com",
             "release-assets.githubusercontent.com", "objects.githubusercontent.com"},
            "Untrusted download URL")
    return url


class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        allowed_url(url)
        return super().redirect_request(request, response, code, message, headers, url)


def fetch(url, destination=None, limit=MAX_DOWNLOAD):
    allowed_url(url)
    request = urllib.request.Request(url, headers={"User-Agent": "ec-deployment-agent-self-update",
                                                  "Accept": "application/vnd.github+json"})
    opener = urllib.request.build_opener(HttpsRedirect())
    started = time.monotonic()
    chunks = []
    size = 0
    writer = open(destination, "xb") if destination is not None else None
    try:
        with opener.open(request, timeout=30) as response:
            allowed_url(response.url)
            while True:
                require(time.monotonic() - started < 120, "Download deadline exceeded")
                chunk = response.read(64 * 1024)
                if not chunk:
                    break
                size += len(chunk)
                require(size <= limit, "Download size limit exceeded")
                if writer is not None:
                    writer.write(chunk)
                else:
                    chunks.append(chunk)
        if writer is not None:
            writer.flush()
            os.fsync(writer.fileno())
            return None
        return b"".join(chunks)
    finally:
        if writer is not None:
            writer.close()


def digest(path):
    with open(path, "rb") as reader:
        return hashlib.sha256(reader.read(MAX_DOWNLOAD + 1)).hexdigest()


def release_snapshot(document, installed):
    require(isinstance(document, dict) and document.get("draft") is False
            and document.get("prerelease") is False, "Release is not published and stable")
    tag = document.get("tag_name")
    require(isinstance(tag, str) and tag.startswith("v"), "Release tag must start with v")
    candidate = version(tag)
    require(document.get("html_url") == "https://github.com/" + REPOSITORY + "/releases/tag/" + tag,
            "Release repository mismatch")
    if candidate <= version(installed):
        return None
    assets = document.get("assets")
    require(isinstance(assets, list), "Missing release assets")
    by_name = {}
    for asset in assets:
        require(isinstance(asset, dict) and isinstance(asset.get("name"), str), "Malformed asset")
        require(asset["name"] not in by_name, "Duplicate release asset")
        by_name[asset["name"]] = asset
    hashes = {}
    for name in list(ASSETS.values()) + ["DEPLOYMENT_MANIFEST.json"]:
        asset = by_name.get(name, {})
        value = asset.get("digest", "")
        require(isinstance(value, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", value), "Missing asset digest: " + name)
        require(asset.get("browser_download_url") ==
                "https://github.com/" + REPOSITORY + "/releases/download/" + tag + "/" + name,
                "Asset URL mismatch: " + name)
        require(type(asset.get("size")) is int and 0 < asset["size"] <= MAX_DOWNLOAD,
                "Invalid asset size")
        hashes[name] = value[7:]
    return tag, hashes


def manifest_snapshot(raw, tag, hashes):
    require(hashlib.sha256(raw).hexdigest() == hashes["DEPLOYMENT_MANIFEST.json"], "Manifest digest mismatch")
    document = strict_json(raw)
    require(document.get("schema_version") == "0.1" and document.get("repository") == REPOSITORY
            and document.get("release_tag") == tag, "Manifest identity mismatch")
    commit = document.get("source_commit", "")
    require(isinstance(commit, str) and re.fullmatch(r"[0-9a-f]{40}", commit), "Invalid source commit")
    for key, name in ASSETS.items():
        asset = document.get("assets", {}).get(key, {})
        require(asset.get("name") == name and asset.get("sha256") == hashes[name], "Mixed release generation: " + key)
    return commit


def unpack_source(archive_path, target, commit):
    root_name = "deployment-attestation-" + commit
    target.mkdir(mode=0o700)
    seen = set()
    total = 0
    # Stream so a compressed archive cannot force an unbounded getmembers list.
    with tarfile.open(archive_path, "r|gz") as archive:
        for member in archive:
            raw = member.name.rstrip("/")
            parts = PurePosixPath(raw).parts
            require(parts and parts[0] == root_name and not raw.startswith("/")
                    and ".." not in parts and "\\" not in raw
                    and (member.isdir() or member.isfile()), "Unsafe source archive entry")
            normalized = "/".join(parts)
            require(normalized not in seen, "Duplicate source archive entry")
            seen.add(normalized)
            require(len(seen) <= 10000, "Too many source entries")
            total += member.size
            require(0 <= member.size <= MAX_DOWNLOAD and total <= 128 * 1024 * 1024,
                    "Source archive exceeds limit")
            relative = parts[1:]
            if not relative:
                require(member.isdir(), "Archive root is not a directory")
                continue
            path = target.joinpath(*relative)
            if member.isdir():
                path.mkdir(mode=0o700, parents=True, exist_ok=True)
            else:
                path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                reader = archive.extractfile(member)
                require(reader is not None, "Missing source member")
                with reader, open(path, "xb") as writer:
                    shutil.copyfileobj(reader, writer)


def install_snapshot(stage, tag, hashes):
    base = "https://github.com/" + REPOSITORY + "/releases/download/" + tag + "/"
    raw = fetch(base + "DEPLOYMENT_MANIFEST.json", limit=1024 * 1024)
    commit = manifest_snapshot(raw, tag, hashes)
    for name in ASSETS.values():
        fetch(base + name, stage / name)
        require(digest(stage / name) == hashes[name], "Asset digest mismatch: " + name)
    source_tar = stage / "source.tar.gz"
    fetch("https://codeload.github.com/" + REPOSITORY + "/tar.gz/" + commit, source_tar)
    source = stage / "source"
    unpack_source(source_tar, source, commit)
    require(digest(source / "install/install-id-exergism.sh") == hashes[ASSETS["installer"]],
            "Installer does not match source commit")
    require(digest(source / "install/self-update-agent.py") == hashes[ASSETS["self_update_helper"]],
            "Helper does not match source commit")
    # A revoked opt-in or an in-progress deployment never gets bypassed.
    if not read_policy():
        print("Self-update opt-in was withdrawn; staged package discarded.")
        return
    no_pending_transactions()
    installer = stage / ASSETS["installer"]
    agent = stage / ASSETS["amd64"]
    installer.chmod(0o500)
    agent.chmod(0o500)
    candidate_version = subprocess.check_output(
        [str(agent), "package-version"], env=ENVIRONMENT, timeout=30).decode().strip()
    require(version(candidate_version) == version(tag), "Candidate package version mismatch")
    environment = dict(ENVIRONMENT, EC_INSTALLER_TRUSTED_STAGE=str(stage),
                       EC_INSTALLER_SOURCE_ROOT=str(source),
                       EC_INSTALLER_SHA256=hashes[ASSETS["installer"]],
                       EC_NATIVE_AGENT_BINARY=str(agent),
                       EC_NATIVE_AGENT_SHA256=hashes[ASSETS["amd64"]])
    # Recheck after candidate execution too: preflight can take up to 30 seconds.
    if not read_policy():
        print("Self-update opt-in was withdrawn during preflight; package discarded.")
        return
    no_pending_transactions()
    # This is the EXISTING installer transaction, including rollback and boot recovery.
    subprocess.run([str(installer)], env=environment, check=True)
    installed = subprocess.check_output([AGENT, "package-version"], env=ENVIRONMENT, timeout=30).decode().strip()
    require(version(installed) == version(tag), "Installed package version mismatch")
    print("Updated deployment-attestation package to " + tag)


def main():
    require(os.geteuid() == 0, "Self-update must run as root")
    os.umask(0o077)
    if not read_policy():
        print("Agent self-updates are disabled (explicit opt-in required).")
        return 0
    require(os.uname().machine == "x86_64", "Self-update supports linux-x64 only")
    descriptor = os.open(LOCK, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        metadata = os.fstat(descriptor)
        require(stat.S_ISREG(metadata.st_mode) and metadata.st_uid == 0 and not metadata.st_mode & 0o022,
                "Unsafe self-update lock")
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print("Another self-update is running; skipped.")
            return 0
        no_pending_transactions()
        installed = subprocess.check_output([AGENT, "package-version"], env=ENVIRONMENT, timeout=30).decode().strip()
        document = strict_json(fetch(API + "/releases/latest", limit=1024 * 1024))
        snapshot = release_snapshot(document, installed)
        if snapshot is None:
            print("Already at the latest stable package version; no downgrade.")
            return 0
        trusted_parents(BOOTSTRAP / "placeholder")
        metadata = BOOTSTRAP.lstat()
        require(stat.S_ISDIR(metadata.st_mode) and metadata.st_uid == 0
                and stat.S_IMODE(metadata.st_mode) == 0o700, "Unsafe installer bootstrap directory")
        stage = Path(tempfile.mkdtemp(prefix="installer.", dir=BOOTSTRAP))
        try:
            install_snapshot(stage, *snapshot)
        finally:
            if stage.exists():
                shutil.rmtree(stage)
        return 0
    finally:
        os.close(descriptor)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("ERROR: self-update: " + str(error), file=sys.stderr)
        sys.exit(1)

