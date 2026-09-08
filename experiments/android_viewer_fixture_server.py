#!/usr/bin/env python3
"""Synthetic encrypted peer on localhost. Records protocol input, never injects OS input."""
import argparse
import io
import json
from pathlib import Path
import socket
import struct
import sys
import threading
import time
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire
import remotedesk_linux_host as host


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--seconds", type=int, default=900)
    parser.add_argument("--discovery-port", type=int, choices=(40566,), help="Optional loopback-only product UDP fixture, advertises this peer's actual TCP port")
    parser.add_argument("--machine-name", default="Synthetic test PC")
    parser.add_argument("--device-id", default="", help="Optional stable synthetic identity, negotiated after authentication")
    parser.add_argument("--allow-test-controls", action="store_true", help="Read a fixed local fixture-command.json for fault injection; never an OS command")
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    units = host.extract_h264_access_units(bytearray((Path(args.fixtures) / "static-current-gop1.h264").read_bytes()), flush=True)
    if len(units) != 180:
        raise RuntimeError("Need exactly 180 known GOP1 fixture access units")
    jpeg_images = {}
    for name, size in (("primary", (1920,1080)), ("secondary", (1280,720))):
        image = Image.new("RGB", size, "#dce7f4")
        draw = ImageDraw.Draw(image)
        draw.rectangle((40, 40, size[0]-40, size[1]-40), fill="white")
        draw.rectangle((40, 40, size[0]-40, 135), fill="#1e3a5f")
        font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 42)
        small = ImageFont.truetype("C:/Windows/Fonts/consola.ttf", 28)
        draw.text((70,60), "RemoteDesk mobile UI test / " + name, font=font, fill="white")
        for i, text in enumerate(("Synthetic desktop only - no access to user files", "Trackpad: move without dragging", "Touch: tap / drag; two fingers: scroll / zoom", "Keyboard: Chinese composition + shortcuts", "Input events are recorded, not injected into Windows")):
            draw.text((80,200+i*70), text, font=small, fill="#152030")
        draw.rectangle((80,size[1]-170,450,size[1]-80), fill="#2563eb")
        draw.text((105,size[1]-150), "CLICK TARGET", font=small, fill="white")
        buffer = io.BytesIO(); image.save(buffer, format="JPEG", quality=90)
        jpeg_images[name] = struct.pack("<iidd", *size, 0.0, 0.0) + buffer.getvalue()
    deadline = time.monotonic() + max(10, min(args.seconds, 1800))
    report = {"scope":"synthetic encrypted loopback peer; not actual Windows capture/input", "sessions":0, "events":[], "controls":[], "frames":0,
              "testControls":args.allow_test_controls, "commands":[], "sessionStarts":[]}
    last_command = 0
    accept_after = 0.0
    save_lock = threading.Lock()
    def save():
        with save_lock:
            temp = output / "server-state.tmp"
            temp.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            temp.replace(output / "server-state.json")
    listener = socket.socket(); listener.bind(("127.0.0.1",0)); listener.listen(2); listener.settimeout(1)
    report["port"] = listener.getsockname()[1]; save()
    discovery = None
    discovery_done = threading.Event()
    if args.discovery_port:
        # Never bind a LAN wildcard or replace the real host's discovery socket.
        discovery = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        discovery.bind(("127.0.0.1", args.discovery_port)); discovery.settimeout(.3)
        report["discoveryPort"] = args.discovery_port; report["discoveryRequests"] = 0; save()
        def discover():
            while not discovery_done.is_set():
                try:
                    request, endpoint = discovery.recvfrom(256)
                    if request != b"RemoteDesk.Discover.v1": continue
                    reply = {"Type":"RemoteDesk.Discover.Response.v1", "MachineName":args.machine_name,
                             "Platform":"Windows", "Port":report["port"], "IsHostRunning":True}
                    if args.device_id: reply["DeviceId"] = args.device_id
                    discovery.sendto(json.dumps(reply).encode("utf-8"), endpoint)
                    report["discoveryRequests"] += 1; save()
                except socket.timeout: continue
                except OSError:
                    if discovery_done.is_set(): break
                    raise
        discovery_thread = threading.Thread(target=discover, name="SyntheticDiscovery", daemon=True)
        discovery_thread.start()
    print(json.dumps({"port":report["port"],"scope":report["scope"]}), flush=True)
    try:
        while time.monotonic() < deadline:
            if time.monotonic() < accept_after:
                time.sleep(.05); continue
            try: client,_ = listener.accept()
            except socket.timeout: continue
            with client:
                client.settimeout(5); wire.configure_low_latency_socket(client)
                try:
                    session = wire.authenticate_server(client, "RemoteDesk-synthetic-ui-fixture")
                except (OSError, EOFError, ValueError, wire.ProtocolError):
                    report["authenticationFailures"] = report.get("authenticationFailures", 0) + 1
                    save()
                    continue
                client.settimeout(30)
                report["sessions"] += 1
                report["sessionStarts"].append({"session":report["sessions"],"eventOffset":len(report["events"])})
                lock=threading.Lock(); stopped=threading.Event()
                state={"h264":False,"target":"primary", "pausedUntil":0.0}
                def send(kind,payload):
                    with lock: wire.write_message(client,session,kind,payload)
                caps=wire.CAPABILITY_REMOTE_DESKTOP | wire.CAPABILITY_INPUT_CONTROL | wire.CAPABILITY_CAPTURE_TARGET_SELECTION
                if args.device_id: caps |= wire.CAPABILITY_DEVICE_IDENTITY
                send(wire.MESSAGE_CONTROL,wire.encode_device_info(args.machine_name,"Windows",caps))
                send(wire.MESSAGE_CONTROL,wire.encode_capture_target_list([("primary","测试屏幕 1 · 1080P"),("secondary","测试屏幕 2 · 720P")]))
                send(wire.MESSAGE_CONTROL,wire.encode_capture_target_changed("primary","测试屏幕 1 · 1080P"))
                def reader():
                    try:
                        while not stopped.is_set():
                            kind,payload=wire.read_message(client,session)
                            if kind==wire.MESSAGE_PING: send(wire.MESSAGE_PONG,payload)
                            elif kind==wire.MESSAGE_INPUT:
                                event=list(struct.unpack("<BBiii",payload)); report["events"].append(event); save()
                            elif kind==wire.MESSAGE_CONTROL:
                                control=wire.decode_control(payload); report["controls"].append(control)
                                if payload[0]==wire.CONTROL_VIEWER_INFO: state["h264"]=bool(struct.unpack_from("<i",payload,1)[0]&2)
                                elif payload[0]==wire.CONTROL_DEVICE_IDENTITY_REQUEST and args.device_id:
                                    send(wire.MESSAGE_CONTROL, wire.encode_device_identity(args.device_id))
                                elif payload[0]==wire.CONTROL_SELECT_CAPTURE_TARGET:
                                    selected=control["targetId"]
                                    if selected not in jpeg_images: raise ValueError("Unknown synthetic target")
                                    with lock:
                                        state["target"]=selected
                                        wire.write_message(client,session,wire.MESSAGE_CONTROL,wire.encode_capture_target_changed(selected,"测试屏幕 " + selected))
                                save()
                    except Exception as error:
                        report["lastReaderEnd"]=type(error).__name__
                    finally: stopped.set()
                thread=threading.Thread(target=reader,daemon=True); thread.start()
                index=0
                try:
                    while not stopped.is_set() and time.monotonic()<deadline:
                        if args.allow_test_controls:
                            try:
                                command=json.loads((output / "fixture-command.json").read_text(encoding="utf-8"))
                            except (FileNotFoundError, json.JSONDecodeError): command={}
                            sequence=command.get("sequence",0)
                            if isinstance(sequence,int) and sequence>last_command:
                                last_command=sequence; action=command.get("action")
                                if action in ("info_repeat","readonly","control"):
                                    if action=="readonly": caps &= ~wire.CAPABILITY_INPUT_CONTROL
                                    elif action=="control": caps |= wire.CAPABILITY_INPUT_CONTROL
                                    send(wire.MESSAGE_CONTROL,wire.encode_device_info(args.machine_name,"Windows",caps))
                                elif action=="hold_screen":
                                    state["target"]="secondary"; state["pausedUntil"]=time.monotonic()+10
                                    send(wire.MESSAGE_CONTROL,wire.encode_capture_target_changed("secondary","测试屏幕 secondary"))
                                elif action=="resume": state["pausedUntil"]=0.0
                                elif action=="drop":
                                    accept_after=time.monotonic()+2; stopped.set()
                                else: raise ValueError("Unsupported fixture action")
                                report["commands"].append({"sequence":sequence,"action":action,"eventOffset":len(report["events"]),"session":report["sessions"]}); save()
                        if stopped.is_set(): break
                        if time.monotonic()<state["pausedUntil"]:
                            time.sleep(.02); continue
                        if state["h264"]:
                            send(wire.MESSAGE_VIDEO_FRAME,wire.encode_video_frame(1920,1080,3,units[index%len(units)]))
                        else: send(wire.MESSAGE_FRAME,jpeg_images[state["target"]])
                        index+=1; report["frames"]+=1
                        if index%30==0: save()
                        time.sleep(1/30)
                except (OSError,TimeoutError): pass
                finally:
                    stopped.set()
                    try: client.shutdown(socket.SHUT_RDWR)
                    except OSError: pass
                    thread.join(timeout=2); save()
    finally:
        listener.close(); discovery_done.set()
        if discovery is not None:
            discovery.close(); discovery_thread.join(timeout=2)
        save()


if __name__ == "__main__": main()
