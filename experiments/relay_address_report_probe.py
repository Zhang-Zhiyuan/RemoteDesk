"""Owned synthetic relay host; no desktop/input, stored secrets or NIC changes."""
import asyncio
from dataclasses import replace
import json
import socket
import uuid
from unittest import mock
import remotedesk_linux_relay as relay


async def run(options):
    options = replace(options, device_id=str(uuid.uuid4()))
    actual = relay.local_direct_addresses()
    if not actual:
        raise RuntimeError("No usable local IPv4 address")
    async def echo(reader, writer):
        try:
            while data := await reader.read(4096):
                writer.write(data)
                await writer.drain()
        finally:
            await relay.close_writer(writer)
    local = await asyncio.start_server(echo, "127.0.0.1", 0)
    port = local.sockets[0].getsockname()[1]
    host = relay.RelayHostConnector(options, port, "Owned Linux address probe")
    viewer = None
    async def wait_for(expected):
        for _ in range(30):
            devices = await relay.list_devices_async(options)
            owned = [device for device in devices if device["deviceId"] == options.device_id]
            if len(owned) == 1 and owned[0]["directAddresses"] == expected and owned[0]["directPort"] == (port if expected else 0):
                return
            await asyncio.sleep(.2)
        raise RuntimeError("Address report did not update on the same device ID")
    try:
        with mock.patch.object(relay, "local_direct_addresses", return_value=actual) as addresses:
            host.start()
            await wait_for(actual)
            viewer = await asyncio.to_thread(relay.connect_viewer, options)
            stages = []
            for values in ([], actual):
                addresses.return_value = values
                if not host.request_address_refresh():
                    raise RuntimeError("Registration is not active")
                await wait_for(values)
                def exchange():
                    value = "address-change-中文".encode()
                    viewer.sendall(value)
                    data = bytearray()
                    while len(data) < len(value):
                        block = viewer.recv(len(value) - len(data))
                        if not block: raise EOFError("Session interrupted by address update")
                        data.extend(block)
                    return bytes(data) == value
                if not await asyncio.to_thread(exchange):
                    raise RuntimeError("Active relay bytes changed")
                stages.append(dict(advertisedCount=len(values), sameSessionEcho=True))
            return dict(complete=True, platform="Linux", localAddresses=actual, customPort=port, stages=stages,
                        scope="Public relay; product registration/directory. Simulated removal/restoration, no NIC changes or OS input.")
    finally:
        if viewer is not None: viewer.close()
        await asyncio.to_thread(host.close)
        local.close()
        await local.wait_closed()
