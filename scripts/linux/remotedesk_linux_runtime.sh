#!/usr/bin/env sh
# Sourced by launchers. Host/probe replace their process via exec; GUI/doctor
# invoke this function in a subshell to keep private paths away from OS dialogs.
remotedesk_python() {
    remotedesk_app_root="$1"
    shift
    unset PYTHONHOME PYTHONPATH LD_LIBRARY_PATH LD_PRELOAD
    remotedesk_private_python="$remotedesk_app_root/runtime/bin/python"
    remotedesk_private_paths="$remotedesk_app_root/runtime/lib/python3/dist-packages:$remotedesk_app_root/runtime/lib/python3.12/dist-packages:$remotedesk_app_root/app:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages"
    remotedesk_runtime_check='import ctypes, ssl, sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)'
    # Probe before exporting private paths. An Ubuntu 24.04 ELF cannot run on
    # Ubuntu 22.04/glibc 2.35; never inject its stdlib/extensions into Python 3.10.
    if [ -x "$remotedesk_private_python" ] &&
        env PYTHONHOME="$remotedesk_app_root/runtime" \
            PYTHONPATH="$remotedesk_private_paths" \
            LD_LIBRARY_PATH="$remotedesk_app_root/runtime/lib" \
            "$remotedesk_private_python" -s -c "$remotedesk_runtime_check" >/dev/null 2>&1; then
        export PYTHONHOME="$remotedesk_app_root/runtime"
        export PYTHONPATH="$remotedesk_private_paths"
        export LD_LIBRARY_PATH="$remotedesk_app_root/runtime/lib"
        exec "$remotedesk_private_python" -s "$@"
    fi
    # Prefer the distribution interpreter, not an unrelated Conda/venv on PATH.
    # Missing application modules still use the existing consent-based checker.
    if [ -x /usr/bin/python3 ] &&
        /usr/bin/python3 -s -c "$remotedesk_runtime_check" >/dev/null 2>&1; then
        if [ -e "$remotedesk_private_python" ]; then
            printf '%s\n' 'RemoteDesk: 包内运行时不适配当前系统，使用系统 Python；未修改系统组件。' >&2
        fi
        export PYTHONPATH="$remotedesk_app_root/app"
        exec /usr/bin/python3 -s "$@"
    fi
    printf '%s\n' 'RemoteDesk 需要可用的 Python 3.10 或更高版本。请使用适配当前系统的安装包；不要替换系统 glibc。' >&2
    exit 78
}
