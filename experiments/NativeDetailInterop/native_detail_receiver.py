#!/usr/bin/env python3
"""NDL1 experimental receiver parity probe. Stdlib only; not a production endpoint."""
import base64
import hashlib
import hmac
import json
from pathlib import Path
import struct
import sys
import zlib


def checked(condition):
    if not condition:
        raise ValueError("Invalid NDL1 data")


def context(value):
    epoch, request, width, height = value
    checked(0 < epoch < 2**63 and 0 < request < 2**63 and 0 < width <= 8192 and 0 < height <= 8192)
    checked(width * height <= 16_777_216 and tiles(value) <= 2048)
    return tuple(value)


def tiles(ctx):
    return ((ctx[2] + 127) // 128) * ((ctx[3] + 127) // 128)


def tile_rect(ctx, tile):
    checked(0 <= tile < tiles(ctx))
    x, y = tile % ((ctx[2] + 127) // 128) * 128, tile // ((ctx[2] + 127) // 128) * 128
    return x, y, min(128, ctx[2] - x), min(128, ctx[3] - y)


def header(data, kind, minimum):
    checked(len(data) >= minimum and data[:4] == b"NDL1" and data[4:8] == bytes((kind, 0, 0, 0)))
    ctx = context(struct.unpack_from("<qqii", data, 8))
    seq, = struct.unpack_from("<q", data, 32)
    checked(seq > 0)
    return ctx, seq


def manifest(data):
    ctx, seq = header(data, 1, 44)
    runs, = struct.unpack_from("<i", data, 40)
    checked(0 < runs <= tiles(ctx) and len(data) == 44 + 10 * runs)
    versions = []
    for index in range(runs):
        count, version = struct.unpack_from("<Hq", data, 44 + 10 * index)
        checked(0 < count <= tiles(ctx) - len(versions) and 0 < version <= seq)
        versions.extend([version] * count)
    checked(len(versions) == tiles(ctx))
    return ctx, seq, tuple(versions)


def chunk(data):
    checked(len(data) >= 96 and data[4] in (2, 3))
    chunk_bytes = 4096 if data[4] == 2 else 512
    ctx, seq = header(data, data[4], 96)
    tile, version, total, offset, size = struct.unpack_from("<iqiii", data, 40)
    tile_rect(ctx, tile)
    checked(0 < version <= seq and 0 < total <= 96 * 1024 and 0 <= offset < total and offset % chunk_bytes == 0)
    checked(size == min(chunk_bytes, total - offset) and len(data) == 96 + size)
    return ctx, seq, tile, version, total, offset, data[64:96], data[96:], chunk_bytes


class Cache:
    def __init__(self):
        self.ctx = None
        self.enabled = False
        self.displayed = None
        self.viewport = None
        self.minimum = 0
        self.now = 0
        self.clear()

    def clear(self):
        self.assemblies = {}
        self.patches = {}  # Insertion order is the bounded FIFO eviction order.

    def reset(self, values):
        ctx = context(values[:4])
        x, y, w, h = viewport = tuple(values[4:])
        checked(x >= 0 and y >= 0 and w > 0 and h > 0 and x + w <= ctx[2] and y + h <= ctx[3])
        checked(self.ctx is None or ctx[0] > self.ctx[0] or (ctx[0] == self.ctx[0] and ctx[1] > self.ctx[1]))
        self.clear()
        self.ctx, self.viewport, self.enabled, self.displayed, self.minimum = ctx, viewport, True, None, 0
        return "reset"

    def present(self, data):
        item = manifest(data)
        if not self.enabled or item[0] != self.ctx or (self.displayed and item[1] <= self.displayed[1]):
            return "rejected"
        if self.displayed and any(new < old for new, old in zip(item[2], self.displayed[2])):
            return "rejected"
        self.patches = {tile: value for tile, value in self.patches.items() if value[0] == item[2][tile]}
        self.assemblies = {tile: value for tile, value in self.assemblies.items() if value[0][3] == item[2][tile]}
        self.displayed = item
        return "presented"

    def off(self):
        self.clear()
        self.enabled, self.displayed = False, None
        return "ok"

    def interaction(self):
        if self.displayed and self.displayed[1] == 2**63 - 1:
            return self.off()
        self.clear()
        self.minimum = self.displayed[1] + 1 if self.displayed else 1
        return "ok"

    def receive(self, data, now):
        checked(now >= self.now)
        self.now = now
        self.assemblies = {tile: value for tile, value in self.assemblies.items() if now - value[1] <= 1500}
        if not self.enabled or not self.displayed:
            return "Inactive"
        try:
            item = chunk(data)
        except (ValueError, struct.error):
            return "Invalid"
        ctx, seq, tile, version, total, offset, digest, content, chunk_bytes = item
        if ctx != self.ctx or seq < self.minimum:
            return "Stale"
        if seq > self.displayed[1]:
            return "Future"
        if version != self.displayed[2][tile]:
            return "Stale"
        x, y, w, h = tile_rect(ctx, tile)
        vx, vy, vw, vh = self.viewport
        if not (x < vx + vw and vx < x + w and y < vy + vh and vy < y + h):
            return "OutsideViewport"
        if tile in self.patches:
            return "Duplicate"
        if tile not in self.assemblies:
            if len(self.assemblies) >= 2:
                return "Limited"
            self.assemblies[tile] = [item, now, bytearray(total), set()]
        entry = self.assemblies[tile]
        previous = entry[0]
        if (previous[1], previous[3], previous[4], previous[6], previous[8]) != (seq, version, total, digest, chunk_bytes):
            if seq < previous[1]:
                return "Stale"
            if seq > previous[1] and offset == 0:
                entry = [item, now, bytearray(total), set()]
                self.assemblies[tile] = entry
            else:
                return "Invalid"
        if offset in entry[3]:
            if entry[2][offset:offset + len(content)] == content:
                return "Duplicate"
            del self.assemblies[tile]
            return "Invalid"
        entry[2][offset:offset + len(content)] = content
        entry[3].add(offset)
        if len(entry[3]) != (total + chunk_bytes - 1) // chunk_bytes:
            return "Partial"
        del self.assemblies[tile]
        if not hmac.compare_digest(hashlib.sha256(entry[2]).digest(), digest):
            return "Invalid"
        try:
            decoder = zlib.decompressobj()
            rgba = decoder.decompress(entry[2], w * h * 4 + 1)
            checked(len(rgba) == w * h * 4 and decoder.eof and not decoder.unconsumed_tail and not decoder.unused_data)
        except (ValueError, zlib.error):
            return "Invalid"
        while len(self.patches) >= 64:
            del self.patches[next(iter(self.patches))]
        self.patches[tile] = version, rgba
        return "Applied"

    def fingerprint(self):
        text = "\n".join(f"{tile}|{version}|{hashlib.sha256(rgba).hexdigest().upper()}"
                         for tile, (version, rgba) in sorted(self.patches.items()))
        return hashlib.sha256(text.encode("ascii")).hexdigest().upper()

    def pending_bytes(self):
        return sum(len(entry[2]) for entry in self.assemblies.values())


def run(path):
    cache = Cache()
    count = 0
    for line in Path(path).read_text(encoding="utf-8-sig").splitlines():
        operation, now, data, expected, fingerprint, pending = line.split("\t")
        try:
            if operation == "reset":
                result = cache.reset(tuple(map(int, data.split(","))))
            elif operation == "frame":
                result = cache.present(base64.b64decode(data, validate=True))
            elif operation == "chunk":
                result = cache.receive(base64.b64decode(data, validate=True), int(now))
            elif operation == "input":
                result = cache.interaction()
            elif operation == "off":
                result = cache.off()
            else:
                raise RuntimeError("Unknown test operation")
        except (ValueError, struct.error):
            result = "invalid"
        count += 1
        if result != expected or cache.fingerprint() != fingerprint or cache.pending_bytes() != int(pending):
            raise AssertionError(f"event {count} {operation}: {result} != {expected}, cache/pending parity={cache.fingerprint() == fingerprint}/{cache.pending_bytes() == int(pending)}")
    checked(count > 50)
    print(json.dumps(dict(passed=True, events=count, runtime="Python", scope="NDL1 wire/state/RGBA parity; not a production Linux viewer or GUI test")))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: native_detail_receiver.py <interop-vectors.txt>")
    run(sys.argv[1])
