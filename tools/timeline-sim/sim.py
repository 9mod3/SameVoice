"""Timeline / jitter-buffer control-loop simulation for the SameVoice Android sink.

The script ports the Android sink logic (HostEngine.kt: MixEngine.push/render,
SourceTimeline.push/mixInto/bufferAheadMs) to Python so the buffer behaviour can
be studied without a device.  The source side models the Windows sender
(SourceClient.cs): packets are built from a synthetic 5 ms sample clock
(240 frames per packet) and stamped with a clock offset that is re-estimated
every 4 s and slewed at most 2 ms/s (ClockSynchronizer.cs).

Run:  python sim.py
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass, field

SR = 48_000
CH = 2
PACKET_FRAMES = 240          # 5 ms, 16-bit transport
PACKET_US = 5_000
BLOCK = 480                  # mixer.render(buffer, 480), 10 ms


# --------------------------------------------------------------------------- mix engine


@dataclass
class Chunk:
    start: int
    frames: int


@dataclass
class Timeline:
    chunks: list[Chunk] = field(default_factory=list)
    dropped: int = 0

    def clear(self) -> None:
        self.chunks.clear()

    def push(self, start: int, frames: int) -> None:
        if frames <= 0:
            return
        skip = 0
        if self.chunks:
            tail = self.chunks[-1]
            tail_start = tail.start
            tail_end = tail.start + tail.frames
            if start < tail_start:
                self.dropped += 1
                return
            if start < tail_end:
                if start > tail_start:
                    new_frames = start - tail_start
                    if new_frames <= 0:
                        self.chunks.pop()
                    else:
                        tail.frames = new_frames
                else:
                    skip = tail_end - start
                    start = tail_end
                    if skip >= frames:
                        self.dropped += 1
                        return
            while len(self.chunks) > 1:
                prev = self.chunks[-2]
                if prev.start + prev.frames > start:
                    self.chunks.pop(-2)
                else:
                    break
        final = frames - skip
        if final <= 0:
            self.dropped += 1
            return
        self.chunks.append(Chunk(start, final))
        if len(self.chunks) > 800:
            remove = len(self.chunks) - 800
            del self.chunks[:remove]
            self.dropped += remove

    def mix_into(self, block_start: int, block_frames: int) -> bool:
        block_end = block_start + block_frames
        keep_from = 0
        while keep_from < len(self.chunks) and self.chunks[keep_from].start + self.chunks[keep_from].frames <= block_start:
            keep_from += 1
        if keep_from:
            del self.chunks[:keep_from]
        contributed = False
        for chunk in self.chunks:
            if chunk.start >= block_end:
                break
            if min(chunk.start + chunk.frames, block_end) <= max(chunk.start, block_start):
                continue
            contributed = True
        return contributed

    def buffer_ahead_ms(self, block_start: int) -> float:
        if not self.chunks:
            return 0.0
        newest_end = self.chunks[-1].start + self.chunks[-1].frames
        return max(0, newest_end - block_start) * 1000.0 / SR

    def has_data_after(self, frame: int) -> bool:
        return bool(self.chunks) and self.chunks[-1].start + self.chunks[-1].frames > frame


@dataclass
class SinkConfig:
    target_ms: int = 240
    track_capacity_frames: int = 4800      # AudioTrack buffer ~100 ms
    drift_authority: int = 2               # +/-2 frames per block
    rate_error: float = 0.0                # sink DAC / resampler rate error
    start_anchor: str = "write"            # "write" = current 1.1.7, "play" = playhead
    control: str = "integer"               # "integer" (1.1.6/1.1.7) or "fractional"
    resync: bool = False                   # recover when the buffer underruns
    reanchor: bool = True                  # 300 ms cursor-lag re-anchor (1.1.6/1.1.7)
    deadband_low: int = 20
    deadband_high: int = 40
    trim_gain: float = 0.0004              # fractional rate per ms of buffer error
    trim_limit: float = 0.03               # +/-3 %
    error_smoothing: float = 0.08          # per block (~120 ms)
    trim_smoothing: float = 0.05           # per block (~200 ms)


class Sink:
    def __init__(self, cfg: SinkConfig) -> None:
        self.cfg = cfg
        self.timeline = Timeline()
        self.start_us = 0.0
        self.rendered = 0
        self.running = False
        self.written = 0
        self.playhead = 0.0
        self.bandwidth_us = 0.0
        self.last_reanchor_us = -1e18
        self.last_resync_us = -1e18
        self.reanchors = 0
        self.late_drops = 0
        self.future_drops = 0
        self.underruns = 0
        self.packets_seen = 0
        self.last_control_ms = 0.0
        self.trim = 0.0
        self.smooth_error = 0.0
        self.carry = 0.0
        self.resyncs = 0

    # AudioTrack: capacity in frames, consumed at SR*(1+rate_error)
    def tick_track(self, now_us: float) -> None:
        played = (now_us - self.t0) * 1e-6 * SR * (1 + self.cfg.rate_error)
        self.playhead = played

    def track_has_room(self) -> bool:
        return self.written - self.playhead <= self.cfg.track_capacity_frames - BLOCK

    def start(self, now_us: float) -> None:
        self.t0 = now_us
        if self.cfg.start_anchor == "play":
            self.start_us = now_us
        else:
            self.start_us = now_us - self.cfg.track_capacity_frames * 1e6 / SR
        self.rendered = 0
        self.running = True

    def push(self, packet_us: float, capture_host_us: float, now_us: float) -> None:
        if not self.running:
            return
        self.packets_seen += 1
        if self.cfg.reanchor:
            rendered_time_us = self.start_us + self.rendered * 1e6 / SR
            lag = now_us - rendered_time_us
            if lag > 300_000 and (now_us - self.last_reanchor_us) > 800_000:
                self.last_reanchor_us = now_us
                self.start_us += lag
                self.reanchors += 1
                self.timeline.clear()

        target = self.cfg.target_ms
        start_frame = round((capture_host_us + target * 1000.0 - self.start_us) * SR / 1e6)
        frames = PACKET_FRAMES
        if self.cfg.resync:
            too_late = start_frame + frames <= self.rendered
            too_early = start_frame > self.rendered + (target + 1000) * SR // 1000
            if too_late or too_early:
                if (now_us - self.last_resync_us) >= 500_000:
                    self.last_resync_us = now_us
                    self.start_us = capture_host_us + target * 1000.0 - self.rendered * 1e6 / SR
                    start_frame = self.rendered + round(target * SR / 1000.0)
                    self.timeline.clear()
                    self.resyncs += 1
                else:
                    self.late_drops += 1
                    return
        if start_frame + frames <= self.rendered - SR // 4:
            self.late_drops += 1
            return
        if start_frame > self.rendered + SR * 10:
            self.future_drops += 1
            return
        self.timeline.push(start_frame, frames)

    def render(self, now_us: float) -> bool:
        """Called whenever AudioTrack has room for another 480-frame block."""
        self.tick_track(now_us)
        active = self.timeline.has_data_after(self.rendered)
        if not active and self.packets_seen:
            self.underruns += 1

        if self.cfg.control == "integer":
            advance = BLOCK
            if active:
                error = self.timeline.buffer_ahead_ms(self.rendered) - self.cfg.target_ms
                n = self.cfg.drift_authority
                if error < -80:
                    advance -= n
                elif error < -self.cfg.deadband_low:
                    advance -= 1
                elif error > 100:
                    advance += n
                elif error > self.cfg.deadband_high:
                    advance += 1
            advance = max(1, advance)
        else:
            # Fractional sample-rate trim: the mix cursor can run at 1 +/- 3 %
            # of the AudioTrack rate, so any realistic clock difference is
            # absorbed instead of draining the queue to zero.
            error = (self.timeline.buffer_ahead_ms(self.rendered) - self.cfg.target_ms) if active \
                else -float(self.cfg.target_ms)
            self.smooth_error += (error - self.smooth_error) * self.cfg.error_smoothing
            wanted = max(-self.cfg.trim_limit, min(self.cfg.trim_limit, self.smooth_error * self.cfg.trim_gain))
            self.trim += (wanted - self.trim) * self.cfg.trim_smoothing
            self.carry += (1.0 + self.trim) * BLOCK
            advance = int(self.carry)
            self.carry -= advance
            advance = max(1, advance)

        self.timeline.mix_into(self.rendered, BLOCK)
        self.rendered += advance
        self.written += BLOCK
        return True

    def buffer_ms(self, now_us: float) -> float:
        self.tick_track(now_us)
        return self.timeline.buffer_ahead_ms(self.rendered)


# --------------------------------------------------------------------------- windows source


class WindowsSource:
    """Sample-count timestamps + NTP offset slewing, as in SourceClient.cs."""

    def __init__(self, rate_error: float, target: Sink, t0: float, slew_ms_per_s: float = 2.0,
                 sync_period_s: float = 4.0) -> None:
        self.rate_error = rate_error
        self.sink = target
        self.t_start = t0
        self.packet_index = 0
        # Connection setup finishes the initial clock sync before sharing starts,
        # so the simulation begins with a valid offset (as ConnectAsync does).
        self.offset = t0 - 0.0       # host - local
        self.last_adjust = t0
        self.next_sync = t0 + sync_period_s * 1e6
        # ClockSynchronizer: max 2 ms of slew per second of elapsed time.
        self.slew = slew_ms_per_s / 1000.0   # us of offset per us of elapsed time
        self.sync_period = sync_period_s * 1e6

    def local_time(self, now_us: float) -> float:
        """Source-local sample clock: 48 kHz nominal, actual rate SR*(1+rate_error)."""
        return (now_us - self.t_start) * (1 + self.rate_error)

    def next_packet_time(self, now_us: float) -> float:
        return self.t_start + self.packet_index * PACKET_FRAMES / (SR * (1 + self.rate_error)) * 1e6

    def maybe_sync(self, now_us: float) -> None:
        if now_us < self.next_sync:
            return
        self.next_sync += self.sync_period
        # perfect symmetric measurement: offset = host - local
        sample = now_us - self.local_time(now_us)
        max_step = max(200.0, (now_us - self.last_adjust) * self.slew)
        delta = sample - self.offset
        if delta > max_step:
            delta = max_step
        elif delta < -max_step:
            delta = -max_step
        self.offset += delta
        self.last_adjust = now_us

    def emit_due(self, now_us: float) -> None:
        while self.next_packet_time(now_us) <= now_us:
            local_us = self.packet_index * PACKET_US
            capture_host = local_us + self.offset
            self.sink.push(local_us, capture_host, now_us)
            self.packet_index += 1


# --------------------------------------------------------------------------- scenario


def run(eps_src: float, eps_sink: float = 0.0, seconds: int = 120, cfg: SinkConfig | None = None,
        verbose: bool = False, report_every: float = 2.0, stall_at: float = -1.0,
        stall_ms: float = 0.0):
    cfg = cfg or SinkConfig()
    sink = Sink(cfg)
    t0 = 1_000_000.0                     # arbitrary monotonic start
    sink.start(t0)
    source = WindowsSource(eps_src, sink, t0)

    step = 500.0                          # 0.5 ms simulation step
    end = t0 + seconds * 1e6
    samples: list[tuple[float, float, int]] = []
    next_report = t0 + report_every * 1e6
    t = t0
    while t < end:
        sink.tick_track(t)
        source.maybe_sync(t)
        in_stall = stall_at >= 0 and stall_at * 1e6 <= (t - t0) < (stall_at + stall_ms / 1000.0) * 1e6
        if not in_stall:
            source.emit_due(t)
        # writes are paced by the AudioTrack's free space, exactly like the blocking write loop
        while sink.track_has_room():
            sink.render(t)
        t += step
        if t >= next_report:
            buf = sink.buffer_ms(t)
            samples.append(((t - t0) / 1e6, buf, sink.reanchors))
            if verbose:
                print(f"t={samples[-1][0]:6.1f}s buffer={buf:7.1f}ms trim={sink.trim * 100:+6.2f}% "
                      f"rendered={sink.rendered / SR:8.3f}s written={sink.written / SR:8.3f}s "
                      f"reanchors={sink.reanchors} resyncs={sink.resyncs} "
                      f"late={sink.late_drops} drops={sink.timeline.dropped}")
            next_report += report_every * 1e6
    return samples, sink


def summary(sink: Sink, samples) -> str:
    low = min(s[1] for s in samples)
    high = max(s[1] for s in samples)
    return (f"final={samples[-1][1]:6.1f}ms range={low:6.1f}-{high:6.1f}ms "
            f"trim={sink.trim * 100:+6.2f}% reanchors={sink.reanchors} resyncs={sink.resyncs} "
            f"late={sink.late_drops} underruns={sink.underruns}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--eps-src", type=float, default=0.0)
    ap.add_argument("--eps-sink", type=float, default=0.0)
    ap.add_argument("--seconds", type=int, default=120)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--control", default="integer", choices=["integer", "fractional"])
    ap.add_argument("--resync", action="store_true")
    ap.add_argument("--no-reanchor", action="store_true")
    ap.add_argument("--compare", action="store_true")
    ap.add_argument("--stall-at", type=float, default=-1.0)
    ap.add_argument("--stall-ms", type=float, default=0.0)
    args = ap.parse_args()

    if args.compare:
        cases = [0.0, -0.001, -0.003, -0.005, -0.01, -0.02, 0.01]
        print(f"{'eps_src':>8} | {'current 1.1.7':<78} | {'fractional + resync':<78}")
        for eps in cases:
            old_cfg = SinkConfig(control="integer")
            new_cfg = SinkConfig(control="fractional", resync=True, reanchor=False)
            old_s, old_k = run(eps, seconds=args.seconds, cfg=old_cfg)
            new_s, new_k = run(eps, seconds=args.seconds, cfg=new_cfg)
            print(f"{eps * 100:+7.2f}% | {summary(old_k, old_s):<78} | {summary(new_k, new_s):<78}")
    else:
        cfg = SinkConfig(control=args.control, resync=args.resync, reanchor=not args.no_reanchor)
        samples, sink = run(args.eps_src, args.eps_sink, args.seconds, cfg, verbose=args.verbose,
                            stall_at=args.stall_at, stall_ms=args.stall_ms)
        print(summary(sink, samples))

