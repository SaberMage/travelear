#!/usr/bin/env python
"""T4a reference-capture analysis (M3-PLAN): compares Local Voice with what a real listener rendered.

Inputs (one capture directory written by the mod with Calibration.Capture = true):
  input.wav        the decoded outbound voice before the mod's chain (48 kHz mono, time-continuous)
  local-voice.wav  the mod's output as sent to the Helper, sample-aligned with input.wav
  peer.wav         the other machine's audio output, recorded on this PC (any rate/channels);
                   pass --peer if it lives elsewhere (an OBS recording works too)
  frames.csv       per-frame gate disposition (pass / tail / silence) and wall clock
  floats.csv       every mixer float the game wrote, with elapsed ms

What it measures, per phrase (a run of gate-passed frames) and per labelled segment:
  level    RMS of peer vs local over the phrase, in dB (after a global normalisation on the
           segment you name --reference, ideally outdoors where the game adds nothing)
  tail     energy in the windows 0.10-0.35 s, 0.35-0.70 s and 0.70-1.20 s after the phrase ends,
           relative to the phrase RMS, for peer and local: the wet level and decay
  tilt     1/3-octave band levels of peer minus local over the phrase, in dB (spectral shape)

Alignment: peer is resampled to 48 kHz, downmixed, and its lag against local-voice.wav is found
by cross-correlating the envelopes over the whole file (the click train makes this sharp).

Usage:
  python tools/calibrate.py <capture-dir> [--peer FILE] [--segments FILE] [--reference LABEL]
  --segments: CSV of "start_s,end_s,label" in input.wav time (frames.csv's wallclock column maps
              your notes to seconds: elapsed_ms / 1000).
Requires numpy, scipy, soundfile.
"""
from __future__ import annotations

import argparse
import csv
import math
import os
import sys

import numpy as np
import soundfile as sf
from scipy import signal

RATE = 48_000


def load_mono(path: str, rate: int = RATE) -> np.ndarray:
    data, sr = sf.read(path, dtype="float32", always_2d=True)
    mono = data.mean(axis=1)
    if sr != rate:
        g = math.gcd(sr, rate)
        mono = signal.resample_poly(mono, rate // g, sr // g).astype(np.float32)
    return mono


def envelope(x: np.ndarray, hop: int = 480) -> np.ndarray:
    n = len(x) // hop
    return np.sqrt(np.mean(x[: n * hop].reshape(n, hop) ** 2, axis=1) + 1e-12)


def align(reference: np.ndarray, other: np.ndarray, hop: int = 480, max_lag_s: float = 30.0) -> int:
    """Lag (in samples) that shifts `other` onto `reference`; positive = other starts later."""
    a = envelope(reference, hop)
    b = envelope(other, hop)
    a = (a - a.mean()) / (a.std() + 1e-9)
    b = (b - b.mean()) / (b.std() + 1e-9)
    corr = signal.correlate(a, b, mode="full", method="fft")
    lags = signal.correlation_lags(len(a), len(b), mode="full")
    max_lag = int(max_lag_s * RATE / hop)
    mask = np.abs(lags) <= max_lag
    best = lags[mask][np.argmax(corr[mask])]
    return int(-best * hop)


def db(x: float) -> float:
    return 10 * math.log10(max(x, 1e-12))


def rms(x: np.ndarray) -> float:
    return float(np.sqrt(np.mean(x.astype(np.float64) ** 2) + 1e-18))


def third_octave_levels(x: np.ndarray) -> dict[float, float]:
    centers = [125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000]
    f, p = signal.welch(x, fs=RATE, nperseg=4096)
    out = {}
    for c in centers:
        lo, hi = c / 2 ** (1 / 6), c * 2 ** (1 / 6)
        band = p[(f >= lo) & (f < hi)]
        out[c] = db(float(band.mean())) if len(band) else float("nan")
    return out


def phrases(frames_csv: str, min_len_s: float = 0.6, merge_gap_s: float = 0.4):
    """Runs of gate-passed frames as (start_sample, end_sample), merged across short gaps."""
    runs = []
    with open(frames_csv, newline="") as fh:
        rows = list(csv.DictReader(fh))
    current = None
    for row in rows:
        start = int(row["input_sample"])
        passed = row["disposition"] == "pass"
        if passed:
            if current is None:
                current = [start, start]
            current[1] = start
        elif current is not None:
            runs.append(current)
            current = None
    if current is not None:
        runs.append(current)
    frame = 2880
    merged = []
    for s, e in runs:
        e += frame
        if merged and s - merged[-1][1] < merge_gap_s * RATE:
            merged[-1][1] = e
        else:
            merged.append([s, e])
    return [(s, e) for s, e in merged if e - s >= min_len_s * RATE]


def tail_windows(x: np.ndarray, end: int, ref_rms: float):
    out = []
    for a, b in ((0.10, 0.35), (0.35, 0.70), (0.70, 1.20)):
        seg = x[end + int(a * RATE): end + int(b * RATE)]
        out.append(db(rms(seg) ** 2 / (ref_rms ** 2)) if len(seg) else float("nan"))
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("capture")
    ap.add_argument("--peer")
    ap.add_argument("--segments")
    ap.add_argument("--reference", help="segment label used to normalise the peer level onto local (outdoors, say)")
    args = ap.parse_args()

    d = args.capture
    local = load_mono(os.path.join(d, "local-voice.wav"))
    peer_path = args.peer or os.path.join(d, "peer.wav")
    if not os.path.exists(peer_path):
        print(f"no peer recording at {peer_path}; pass --peer", file=sys.stderr)
        return 2
    peer = load_mono(peer_path)

    lag = align(local, peer)
    print(f"peer lag vs local-voice: {lag} samples ({lag / RATE:+.3f} s)")
    if lag >= 0:
        peer = np.concatenate([np.zeros(lag, np.float32), peer])
    else:
        peer = peer[-lag:]
    n = min(len(local), len(peer))
    local, peer = local[:n], peer[:n]

    segments = []
    if args.segments:
        with open(args.segments, newline="") as fh:
            for row in csv.reader(fh):
                if len(row) >= 3 and row[0].strip() and not row[0].startswith("#"):
                    segments.append((float(row[0]), float(row[1]), row[2].strip()))
    if not segments:
        segments = [(0.0, n / RATE, "all")]

    runs = phrases(os.path.join(d, "frames.csv"))
    print(f"{len(runs)} phrases found from frames.csv")

    # Global normalisation: make the peer's level match local on the reference segment.
    gain_db = 0.0
    if args.reference:
        ref = [s for s in segments if s[2] == args.reference]
        if ref:
            a, b = int(ref[0][0] * RATE), int(ref[0][1] * RATE)
            in_ref = [(s, e) for s, e in runs if s >= a and e <= b]
            if in_ref:
                lp = np.concatenate([local[s:e] for s, e in in_ref])
                pp = np.concatenate([peer[s:e] for s, e in in_ref])
                gain_db = 20 * math.log10(rms(lp) / rms(pp))
                print(f"reference '{args.reference}': peer normalised by {gain_db:+.2f} dB onto local")
    peer = peer * 10 ** (gain_db / 20)

    print()
    print(f"{'segment':<14}{'phrase':>7}{'start':>8}{'len':>6}{'level':>8}  {'peer tail (dB re phrase)':>26}  {'local tail (dB re phrase)':>27}")
    for a, b, label in segments:
        sa, sb = int(a * RATE), int(b * RATE)
        idx = [i for i, (s, e) in enumerate(runs) if s >= sa and e <= sb]
        tilt_acc = []
        for i in idx:
            s, e = runs[i]
            lp, pp = local[s:e], peer[s:e]
            level = 20 * math.log10(rms(pp) / rms(lp))
            pt = tail_windows(peer, e, rms(pp))
            lt = tail_windows(local, e, rms(lp))
            print(f"{label:<14}{i:>7}{s / RATE:>8.1f}{(e - s) / RATE:>6.1f}{level:>+8.2f}  "
                  f"{pt[0]:>8.1f}{pt[1]:>9.1f}{pt[2]:>9.1f}  {lt[0]:>9.1f}{lt[1]:>9.1f}{lt[2]:>9.1f}")
            tp, tl = third_octave_levels(pp), third_octave_levels(lp)
            tilt_acc.append({c: tp[c] - tl[c] for c in tp})
        if tilt_acc:
            print(f"  {label}: peer minus local, 1/3-octave dB:")
            keys = list(tilt_acc[0].keys())
            print("   " + " ".join(f"{k:>6}" for k in keys))
            print("   " + " ".join(f"{np.nanmean([t[k] for t in tilt_acc]):>+6.1f}" for k in keys))
    print()
    print("Reading: level > 0 means the listener hears the voice louder than Local Voice renders it;")
    print("tail columns are the energy after the phrase relative to the phrase, so peer < local means")
    print("Local Voice is wetter than the listener; the decay across the three windows is the RT.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
