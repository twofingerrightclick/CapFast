#!/usr/bin/env python3
"""Sparse BlackHole playback; observe CapFast separately to assess session health."""
import argparse
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import time
from datetime import datetime, timezone


def wav_seconds(path):
    with path.open('rb') as audio:
        if audio.read(4) != b'RIFF':
            raise ValueError('Expected RIFF WAV input')
        audio.read(4)
        if audio.read(4) != b'WAVE':
            raise ValueError('Expected WAV input')
        byte_rate = data_size = None
        while header := audio.read(8):
            if len(header) != 8:
                raise ValueError('Truncated WAV header')
            kind, size = struct.unpack('<4sI', header)
            if kind == b'fmt ':
                fmt = audio.read(size)
                if len(fmt) < 16:
                    raise ValueError('Invalid WAV format')
                byte_rate = struct.unpack_from('<I', fmt, 8)[0]
            elif kind == b'data':
                data_size = size
                audio.seek(size, 1)
            else:
                audio.seek(size, 1)
            if size % 2:
                audio.seek(1, 1)
        if not byte_rate or not data_size:
            raise ValueError('Missing WAV audio')
        return data_size / byte_rate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--clip', type=Path, default=Path('sampleTalk/extracted/middle-30s.wav'))
    parser.add_argument('--player', type=Path, default=Path('artifacts/PlayToDevice'))
    parser.add_argument('--minutes', type=float, default=65)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--run', action='store_true', help='Actually play into BlackHole; default only validates the schedule')
    args = parser.parse_args()
    seconds = wav_seconds(args.clip)
    if not 0 < seconds <= 30.01 or not 60 < args.minutes <= 120:
        parser.error('Use a clip of at most 30 seconds and a duration over 60 and at most 120 minutes')
    if args.output.exists():
        parser.error('Choose a new report path')
    bursts = math.ceil(args.minutes / 5)
    report = dict(startedUtc=datetime.now(timezone.utc).isoformat(), mode='run' if args.run else 'dry-run',
                  durationSeconds=args.minutes * 60, intervalSeconds=300, clipSeconds=seconds,
                  plannedBursts=bursts, maximumAudioSeconds=bursts * seconds, device='BlackHole2ch_UID',
                  methodology='Playback schedule only. CapFast UI/session health must be observed separately. No keys or transcripts recorded.',
                  bursts=[], completed=False)
    def save():
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + '\n')
    save()
    print(f'{bursts} bursts, {seconds:.1f}s each, every 300s; at most {bursts * seconds:.1f}s audio.', flush=True)
    if not args.run:
        return
    started = time.monotonic()
    next_burst = 0
    player = None
    try:
        while (elapsed := time.monotonic() - started) < args.minutes * 60:
            if next_burst < bursts and elapsed >= next_burst * 300:
                print(f'Burst {next_burst + 1}/{bursts} at {elapsed:.1f}s', flush=True)
                player = subprocess.Popen([str(args.player.resolve()), '--play', str(args.clip.resolve()), 'BlackHole2ch_UID'],
                                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                try:
                    result = player.wait(timeout=seconds + 10)
                except subprocess.TimeoutExpired:
                    player.kill(); player.wait()
                    raise RuntimeError('Playback exceeded its bounded deadline')
                report['bursts'].append(dict(index=next_burst + 1, startedSeconds=elapsed, exitCode=result))
                save()
                if result != 0:
                    raise RuntimeError('Playback helper failed')
                next_burst += 1
            time.sleep(min(1, max(0, args.minutes * 60 - (time.monotonic() - started))))
        report['completed'] = True
    except KeyboardInterrupt:
        report['stopped'] = 'operator cancellation'
    except RuntimeError as error:
        report['stopped'] = str(error)
    finally:
        if player is not None and player.poll() is None:
            player.kill(); player.wait()
        report['elapsedSeconds'] = time.monotonic() - started
        save()
    print('Schedule complete.' if report['completed'] else 'Schedule stopped; see report.', flush=True)
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    sys.exit(main())
