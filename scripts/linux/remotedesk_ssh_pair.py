"""One-time OpenSSH public-key setup, run in the controller's own terminal."""
import base64
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile

import remotedesk_linux_ssh as ssh


def pair(route, label, expected_host=None):
    binary = shutil.which("ssh")
    if not binary:
        raise ValueError("请安装 OpenSSH 客户端。")
    print(f"正在检查 {label} 的 SSH 公钥登录…", flush=True)
    config = subprocess.run([binary, "-G", "-T", *route], check=True, stdout=subprocess.PIPE, text=True, timeout=15).stdout.splitlines()
    if expected_host and "hostname " + expected_host not in config:
        raise ValueError("SSH 别名的目标已变化，请重新配置。")
    base = [binary, "-T", "-o", "ControlPath=none", "-o", "ControlMaster=no", "-o", "ForwardAgent=no",
            "-o", "ClearAllForwardings=yes", "-o", "ConnectTimeout=20", "-o", "RemoteCommand=none"]
    def works():
        probe = subprocess.run([*base, "-n", "-o", "BatchMode=yes", "-o", "PreferredAuthentications=publickey",
                                "-o", "StrictHostKeyChecking=yes", *route, "echo RemoteDesk-publickey-ok"],
                               stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=45)
        return probe.returncode == 0 and b"RemoteDesk-publickey-ok" in probe.stdout.splitlines()
    if works():
        print("此电脑已经可以免密登录。终端和 VS Code 均可直接连接。")
        return
    filenames = [Path(line[len("identityfile "):]).expanduser() for line in config if line.startswith("identityfile ")]
    identity = next((path for path in filenames if path.is_absolute() and path.is_file()), None)
    if identity is None:
        identity = Path.home() / ".ssh/id_ed25519"
        if identity not in filenames or os.path.lexists(str(identity) + ".pub"):
            raise ValueError("请在 SSH 登录设置中选择已有私钥，或先用 ssh-keygen 建立密钥对。")
        identity.parent.mkdir(mode=0o700, exist_ok=True)
        print("正在当前账户创建 Ed25519 登录密钥。", flush=True)
        with tempfile.TemporaryDirectory(dir=identity.parent) as staging:
            new = Path(staging) / "identity"
            subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", "RemoteDesk", "-f", str(new)], check=True)
            os.link(new, identity)
            os.link(str(new) + ".pub", str(identity) + ".pub")
    pub = identity if identity.suffix == ".pub" else Path(str(identity) + ".pub")
    if pub.is_file():
        with pub.open("rb") as stream:
            line = stream.read(24577)
    else:
        line = subprocess.run(["ssh-keygen", "-y", "-f", str(identity)], stdout=subprocess.PIPE, check=True).stdout
    fields = line.split()
    types = {b"ssh-ed25519", b"ssh-rsa", b"ecdsa-sha2-nistp256", b"ecdsa-sha2-nistp384", b"ecdsa-sha2-nistp521",
             b"sk-ssh-ed25519@openssh.com", b"sk-ecdsa-sha2-nistp256@openssh.com"}
    if len(line) > 24576 or len(fields) < 2 or fields[0] not in types:
        raise ValueError("所选文件没有有效的 OpenSSH 公钥。")
    blob = base64.b64decode(fields[1], validate=True)
    if not 4 + len(fields[0]) < len(blob) <= 16384 or not blob.startswith(len(fields[0]).to_bytes(4, "big") + fields[0]):
        raise ValueError("所选文件没有有效的 OpenSSH 公钥。")
    remote = "python3 -c " + shlex.quote(Path(__file__).with_name("remotedesk_authorize_key.py").read_text(encoding="utf-8"))
    print(f"请按终端提示确认主机并登录 {label}。公钥将登记到这个 Linux / WSL 账号。", flush=True)
    if subprocess.run([*base, "-o", "StrictHostKeyChecking=ask", *route, remote], input=b" ".join(fields[:2]) + b"\n").returncode:
        raise ValueError("登记未完成，请查看 SSH 提示，并确认目标账号可运行 Python 3。")
    if not works():
        raise ValueError("已提交公钥，但独立的公钥登录未通过。若私钥有口令，请先用 ssh-add 解锁；也可检查目标 sshd 的授权文件设置。")
    print(f"已验证 {label} 的免密登录。终端和 VS Code 现在都可以使用。")


def main(args):
    if len(args) == 2 and args[0] == "--pair":
        alias = ssh.validate_alias(args[1])
        directory, root = ssh._alias_paths()
        profile = next((p for p in ssh._alias_profiles(directory) if p["alias"] == alias), None)
        if profile is None:
            raise ValueError("请先配置所选设备的 SSH 别名。")
        pair(["-F", str(root / "remotedesk_aliases.conf"), "--", alias], profile["user"] + "@" + alias,
             profile["id"] + ".remotedesk.invalid")
    elif len(args) == 4 and args[0] == "--pair-direct":
        target = ssh.DirectTarget.parse(*args[1:])
        pair(target.terminal_arguments()[1:], (target.user + "@" if target.user else "") + target.host)
    else:
        raise ValueError("请从目标设备的 SSH 选项打开免密配对。")


if __name__ == "__main__":
    status = 0
    try:
        main(sys.argv[1:])
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(error, file=sys.stderr)
        status = 1
    if sys.stdin.isatty() and sys.stdout.isatty():
        input("按回车关闭。")
    sys.exit(status)
