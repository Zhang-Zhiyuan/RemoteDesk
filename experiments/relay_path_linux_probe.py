#!/usr/bin/env python3
"""Read installed relay options; test candidate code as the ordinary Linux user.

No app/server update, screen/input, persistent credential copy, or route changes.
Only a valid pinned TLS connection is allowed to request the online directory.
"""
import asyncio
import hashlib
import json
import os
from pathlib import Path
import time
import sys
import remotedesk_linux_relay as relay


async def main():
    options = relay.RelayOptions.from_dict(json.load(sys.stdin)) if "--stdin" in sys.argv else relay.load_settings()
    if options is None or options.server_address != "8.138.5.232":
        raise RuntimeError("Installed relay is not the authorized test server")
    rows = []
    for repeat in range(3):
        began = time.monotonic()
        reader, writer = await relay.connect_tls(options)
        try:
            tls_ms = (time.monotonic() - began) * 1000
            directory = await relay.hello(reader, writer, options, "directory")
            rows.append(dict(repeat=repeat, tlsMs=tls_ms,
                directoryTotalMs=(time.monotonic() - began) * 1000,
                localAddress=writer.get_extra_info("sockname")[0], deviceCount=len(directory["devices"])))
        finally:
            await relay.close_writer(writer)
    interfaces = relay._local_relay_interfaces()
    bound = []
    for name, index, address, gateway in interfaces:
        started = time.monotonic()
        reader, writer = await relay._connect_tls_path(options,
            relay.RelayNetworkPath(name, index, address, options.server_address, gateway))
        try:
            bound.append(dict(interface=name, success=True, tlsMs=(time.monotonic() - started) * 1000,
                localAddress=writer.get_extra_info("sockname")[0]))
        finally:
            await relay.close_writer(writer)
    return dict(complete=True, uid=os.getuid(), measurements=rows, boundInterfaceProbes=bound,
        eligibleInterfaceCount=len(interfaces),
        productSha256=hashlib.sha256(Path(relay.__file__).read_bytes()).hexdigest(),
        scope="Real ordinary Linux user; product pinned TLS and directory. No video/input or multi-uplink speed claim.")


if __name__ == "__main__":
    try:
        print(json.dumps(asyncio.run(asyncio.wait_for(main(), 60))))
    except Exception as error:
        print(json.dumps(dict(complete=False, failure=type(error).__name__)))
        raise SystemExit(1)
