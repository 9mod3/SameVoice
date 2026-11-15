"""Minimal SameVoice source probe for on-device testing.

Connects to the Android output engine over TCP, performs the NTP-style clock
exchange, then streams silent 48 kHz/16-bit packets whose timestamp clock runs
at a deliberately chosen rate offset. The phone's UI/logcat shows the resulting
buffer and clock trim, which is exactly what we need to validate the mixer.

    python phone_source_probe.py --host 192.168.3.208 --eps -0.01 --seconds 60

No audio is produced (payload is zeros), so the test is silent.
"""

from __future__ import annotations

import argparse
import json
import socket
import struct
import sys
import threading
import time

HEADER = struct.Struct("<4sBBBBiqiqHH")
TYPE_AUDIO = 1
TYPE_PING = 2
TYPE_PONG = 3
SAMPLE_RATE = 48_000
PACKET_FRAMES = 240
PACKET_US = 5_000


def signed64(value: int) -> int:
    value &= 0xFFFFFFFFFFFFFFFF
    return value - (1 << 64) if value >= (1 << 63) else value


def encode(ptype: int, session: int, token: int, sequence: int, capture_us: int,
           payload: bytes = b"", flags: int = 0) -> bytes:
    return HEADER.pack(b"SVA1", 1, ptype, flags, 0, session, signed64(token),
                       sequence & 0xFFFFFFFF, capture_us, len(payload), 0) + payload


def now_us() -> int:
    return int(time.perf_counter() * 1_000_000)


class Probe:
    def __init__(self, host: str, port: int, eps: float, name: str) -> None:
        self.host = host
        self.port = port
        self.eps = eps
        self.name = name
        self.offset = 0
        self.offset_ready = threading.Event()
        self.running = False
        self.pong_thread: threading.Thread | None = None
        self.udp: socket.socket | None = None
        self.tcp: socket.socket | None = None
        self.session = 0
        self.token = 0
        self.best_rtt = 1 << 60

    # ---------------------------------------------------------------- control
    def connect(self) -> None:
        self.tcp = socket.create_connection((self.host, self.port), timeout=5)
        self.tcp.settimeout(6)
        hello = json.dumps({"type": "hello", "proto": 1, "name": self.name,
                            "platform": "windows", "pin": "", "bits": 16}) + "\n"
        self.tcp.sendall(hello.encode())
        buf = b""
        while b"\n" not in buf:
            chunk = self.tcp.recv(4096)
            if not chunk:
                raise RuntimeError("host closed during handshake")
            buf += chunk
        line, _, _rest = buf.partition(b"\n")
        welcome = json.loads(line.decode())
        if welcome.get("type") != "welcome":
            raise RuntimeError(f"unexpected handshake: {welcome}")
        self.session = int(welcome["session"])
        self.token = int(welcome["token"], 16)
        audio_port = int(welcome["audioPort"])
        bits = int(welcome.get("bits", 16))
        print(f"welcome session={self.session} audio={audio_port} bits={bits} "
              f"mode={welcome.get('mode')} host={welcome.get('name')}", flush=True)
        if bits != 16:
            raise RuntimeError(f"probe only implements 16-bit (host chose {bits})")

        self.udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp.bind(("0.0.0.0", 0))
        self.udp.settimeout(0.0)
        self.audio_addr = (self.host, audio_port)

    def send_control(self, obj: dict) -> None:
        assert self.tcp is not None
        self.tcp.sendall((json.dumps(obj) + "\n").encode())

    # ------------------------------------------------------------------ clock
    def ping_loop(self) -> None:
        assert self.udp is not None
        seq = 0
        last = 0.0
        while self.running:
            t = time.perf_counter()
            if t - last >= 2.0:
                last = t
                seq += 1
                t1 = now_us()
                self.udp.sendto(encode(TYPE_PING, self.session, self.token, seq, t1), self.audio_addr)
            try:
                data, _ = self.udp.recvfrom(2048)
            except BlockingIOError:
                time.sleep(0.001)
                continue
            if len(data) < HEADER.size:
                continue
            _magic, _ver, ptype, _flags, _r, session, token, _seq, t3, plen, _r2 = \
                HEADER.unpack_from(data, 0)
            if ptype != TYPE_PONG or session != self.session or \
                    (token & 0xFFFFFFFFFFFFFFFF) != self.token:
                continue
            if plen < 16:
                continue
            t1, t2 = struct.unpack_from("<qq", data, HEADER.size)
            t4 = now_us()
            rtt = (t4 - t1) - (t3 - t2)
            if rtt < 0:
                continue
            sample = ((t2 - t1) + (t3 - t4)) / 2.0
            if not self.offset_ready.is_set():
                self.offset = int(sample)
                self.best_rtt = rtt
                self.offset_ready.set()
            else:
                # 2 ms/s slew, like ClockSynchronizer on the Windows side.
                elapsed = max(1, t4 - self.last_adjust) if hasattr(self, "last_adjust") else 1_000_000
                max_step = max(200.0, elapsed * 2.0 / 1000.0)
                delta = max(-max_step, min(max_step, sample - self.offset))
                self.offset = int(self.offset + delta)
                self.last_adjust = t4

    # ------------------------------------------------------------------ stream
    def stream(self, seconds: float) -> None:
        assert self.udp is not None
        payload = b"\x00" * (PACKET_FRAMES * 2 * 2)
        self.running = True
        self.pong_thread = threading.Thread(target=self.ping_loop, daemon=True)
        self.pong_thread.start()

        for _ in range(5):
            self.udp.sendto(encode(TYPE_PING, self.session, self.token, 1000, now_us()), self.audio_addr)
            time.sleep(0.08)
        if not self.offset_ready.wait(3.0):
            raise RuntimeError("clock sync failed")
        print(f"clock offset={self.offset}us best_rtt={self.best_rtt}us", flush=True)

        self.send_control({"type": "start"})
        start = time.perf_counter()
        capture_base = now_us()          # PC clock at the first packet
        n = 0
        sent = 0
        next_heartbeat = start + 3.0
        while True:
            elapsed = time.perf_counter() - start
            if elapsed >= seconds:
                break
            if time.perf_counter() >= next_heartbeat:
                next_heartbeat += 3.0
                try:
                    self.send_control({"type": "heartbeat"})
                except OSError:
                    break
            target = start + n * (PACKET_US / 1e6) / (1.0 + self.eps)
            now = time.perf_counter()
            if now < target:
                if target - now > 0.0015:
                    time.sleep(target - now - 0.001)
                while time.perf_counter() < target:
                    pass
            capture = int(round(capture_base + n * PACKET_US + self.offset))
            self.udp.sendto(encode(TYPE_AUDIO, self.session, self.token, n, capture, payload),
                            self.audio_addr)
            sent += 1
            n += 1
            if sent % 200 == 0:
                real = time.perf_counter() - start
                print(f"t={real:6.1f}s packets={sent} rate={sent / real:7.2f}/s "
                      f"offset={self.offset}", flush=True)
        self.running = False
        try:
            self.send_control({"type": "stop"})
            self.send_control({"type": "bye"})
        except OSError:
            pass
        print(f"done packets={sent}", flush=True)

    def close(self) -> None:
        self.running = False
        try:
            if self.udp:
                self.udp.close()
        except OSError:
            pass
        try:
            if self.tcp:
                self.tcp.close()
        except OSError:
            pass


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="192.168.3.208")
    ap.add_argument("--port", type=int, default=47820)
    ap.add_argument("--eps", type=float, default=0.0, help="timestamp clock rate error, e.g. -0.01")
    ap.add_argument("--seconds", type=float, default=60.0)
    ap.add_argument("--name", default="PyProbe")
    args = ap.parse_args()

    probe = Probe(args.host, args.port, args.eps, args.name)
    try:
        probe.connect()
        probe.stream(args.seconds)
        return 0
    except Exception as exc:  # noqa: BLE001
        print(f"FAILED: {exc}", flush=True)
        return 1
    finally:
        probe.close()


if __name__ == "__main__":
    sys.exit(main())
