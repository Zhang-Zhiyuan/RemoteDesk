"""Install one public key in the authenticated Linux/WSL account's standard SSH key file."""
import base64
import fcntl
import os
import pwd
import re
import stat
import sys


def authorize(home, key):
    algorithm, encoded = key.split()
    if not re.fullmatch(rb"(?:ssh-(?:ed25519|rsa)|ecdsa-sha2-nistp(?:256|384|521)|sk-(?:ssh-ed25519|ecdsa-sha2-nistp256)@openssh\.com)", algorithm):
        raise ValueError("Unsupported SSH public key type")
    blob = base64.b64decode(encoded, validate=True)
    if len(blob) > 16384 or not blob.startswith(len(algorithm).to_bytes(4, "big") + algorithm) or len(blob) <= 4 + len(algorithm):
        raise ValueError("Invalid SSH public key")
    uid = os.getuid()
    directory = os.path.join(home, ".ssh")
    try:
        os.mkdir(directory, 0o700)
    except FileExistsError:
        pass
    root = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        info = os.fstat(root)
        if info.st_uid != uid or info.st_mode & 0o022:
            raise PermissionError("Check ownership and write permissions of ~/.ssh")
        flags = os.O_RDWR | os.O_CREAT | os.O_APPEND | os.O_NOFOLLOW | os.O_NONBLOCK
        fd = os.open("authorized_keys", flags, 0o600, dir_fd=root)
        with os.fdopen(fd, "r+b", buffering=0) as keys:
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_uid != uid or info.st_mode & 0o022:
                raise PermissionError("Check ownership and write permissions of ~/.ssh/authorized_keys")
            fcntl.flock(fd, fcntl.LOCK_EX)
            original = keys.read(2 * 1024 * 1024 + 1)
            if len(original) > 2 * 1024 * 1024:
                raise ValueError("SSH key file too large")
            # A matching restricted entry must never acquire an unrestricted duplicate.
            for line in original.splitlines():
                if not line.lstrip().startswith(b"#") and encoded in line.split():
                    return
            entry = (b"\n" if original and not original.endswith(b"\n") else b"") + algorithm + b" " + encoded + b" RemoteDesk\n"
            if keys.write(entry) != len(entry):
                raise OSError("Incomplete SSH public key write")
            os.fsync(fd)
    finally:
        os.close(root)


if __name__ == "__main__":
    try:
        authorize(pwd.getpwuid(os.getuid()).pw_dir, sys.stdin.buffer.readline(24576).strip())
    except (OSError, ValueError) as error:
        sys.exit("SSH public key: " + str(error))
