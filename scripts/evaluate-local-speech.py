#!/usr/bin/env python3
"""Replay documented local fixtures through production Silero + speech gate. No cloud calls."""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path, help='New directory for PCM derivatives and reports')
    parser.add_argument('--full-talks', action='store_true', help='Also replay both complete supplied MP4 talks')
    parser.add_argument('--ffmpeg', help='ffmpeg binary; defaults to PATH or repository imageio-ffmpeg')
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        raise SystemExit('Output already exists; choose a new directory to preserve previous results.')
    ffmpeg = args.ffmpeg or shutil.which('ffmpeg')
    if not ffmpeg:
        sys.path.insert(0, str(ROOT / 'artifacts/pythonmedia'))
        import imageio_ffmpeg
        ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    fixtures = []
    def add(path, group, sha=None, normalize=False):
        path = ROOT / path
        if not path.is_file():
            raise SystemExit(f'Missing documented fixture: {path}')
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if sha and actual != sha:
            raise SystemExit(f'Source checksum mismatch: {path}')
        fixtures.append(dict(source=str(path.relative_to(ROOT)), group=group, sha256=actual, normalize=normalize))

    pack = json.loads((ROOT / 'swahili-pack/manifest.json').read_text())
    for clip in pack['clips']:
        for normalized in [False, True]:
            add('swahili-pack/' + clip['localAudio'], 'FLEURS normalized diagnostic' if normalized else 'FLEURS original',
                clip['audioSHA256'], normalized)
    add('swahili-pack/playlist.wav', 'FLEURS playlist')
    alternating = json.loads((ROOT / 'sampleTalk/alternating-snippets/manifest.json').read_text())
    for clip in alternating['clips']:
        add('sampleTalk/alternating-snippets/' + clip['filename'], 'Talk excerpt', clip['sha256'])
    for filename in ['alternating-eng-swa-77s.wav', 'alternating-eng-swa-long-pause-83s.wav']:
        add('sampleTalk/alternating-snippets/' + filename, 'Alternating playlist')
    for filename in ['early-30s.wav', 'middle-30s.wav', 'late-30s.wav']:
        add('sampleTalk/swa-extracted/' + filename, 'Swahili 30-second excerpt')
    for filename in ['silence', 'noise', 'tone', 'instrumental-trumpet', 'vocal-song', 'speech-music-equal', 'speech-music-quarter']:
        add(f'artifacts/speech-gate/{filename}.wav', 'Non-speech control' if filename in ['silence', 'noise', 'tone'] else 'Music observation')
    if args.full_talks:
        for language in ['eng', 'swa']:
            add(f'sampleTalk/2026-04-1050-clark-g-gilbert-360p-{language}.mp4', 'Full talk')
    output.mkdir(parents=True)
    pcm_dir = output / 'pcm'
    pcm_dir.mkdir()
    subprocess.run(['dotnet', 'build', str(ROOT / 'tests/CapFast.Checks'), '--no-restore'], check=True)
    for index, fixture in enumerate(fixtures):
        source = ROOT / fixture['source']
        command = [ffmpeg, '-hide_banner', '-loglevel', 'error', '-i', str(source), '-map', '0:a:0',
                   '-vn', '-ac', '1', '-ar', '16000', '-f', 'f32le', '-']
        data = subprocess.check_output(command)
        samples = array.array('f', data)
        if sys.byteorder != 'little': samples.byteswap()
        if not samples or not all(math.isfinite(x) for x in samples):
            raise SystemExit(f'Invalid audio: {source}')
        peak = max(abs(x) for x in samples)
        rms = math.sqrt(sum(x*x for x in samples) / len(samples))
        gain = 0.8 / peak if fixture['normalize'] and peak else 1.0
        pcm = array.array('h', (int(max(-1, min(1, x * gain)) * 32767) for x in samples))
        if sys.byteorder != 'little': pcm.byteswap()
        target = pcm_dir / f'{index+1:02}-{source.stem}{"-normalized" if fixture["normalize"] else ""}.pcm'
        target.write_bytes(pcm.tobytes())
        fixture.update(pcm=str(target), durationSeconds=len(samples)/16000, peak=peak,
                       rmsDbFS=20*math.log10(rms) if rms else None, gain=gain,
                       pcmSHA256=hashlib.sha256(pcm.tobytes()).hexdigest())
        print(f'Prepared {index+1}/{len(fixtures)}: {target.name}', flush=True)
    manifest = dict(ffmpegVersion=subprocess.check_output([ffmpeg, '-version'], text=True).splitlines()[0],
                    modelSHA256=hashlib.sha256((ROOT/'third_party/silero-vad/silero_vad.onnx').read_bytes()).hexdigest(),
                    conversion='Decode to float32 mono 16 kHz; optional peak gain; clamp and truncate sample*32767 to PCM16 (same quantizer as Mac helper).', fixtures=fixtures)
    (output/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
    subprocess.run(['dotnet', str(ROOT/'tests/CapFast.Checks/bin/Debug/net10.0/CapFast.Checks.dll'),
                    '--local-speech-report', str(output/'measurements.json')] + [f['pcm'] for f in fixtures], check=True)
    results = json.loads((output/'measurements.json').read_text())
    rows = ['# Local Silero and speech-gate replay', '',
            'No cloud requests or microphone capture. Uploaded means approved by the gate, not actually sent.',
            'Durations are not accuracy scores: no word-timed speech labels are supplied. Normalized rows are diagnostics only.', '',
            '| Group | Fixture | Input s | Source RMS dBFS | Speech s | Approved s | Detected speech withheld s |',
            '| --- | --- | ---: | ---: | ---: | ---: | ---: |']
    for fixture, result in zip(fixtures, results):
        level = f'{fixture["rmsDbFS"]:.1f}' if fixture['rmsDbFS'] is not None else 'silence'
        rows.append(f'| {fixture["group"]} | {Path(result["file"]).stem} | {result["durationSeconds"]:.2f} | {level} | {result["speechSeconds"]:.1f} | {result["uploadedSeconds"]:.1f} | {result["speechWithheldSeconds"]:.1f} |')
    (output/'report.md').write_text('\n'.join(rows)+'\n')
    failures = [Path(r['file']).name for f,r in zip(fixtures,results) if f['group']=='Non-speech control' and r['uploadedSeconds'] > 0]
    print(f'Report: {output / "report.md"}')
    if failures:
        raise SystemExit('Non-speech controls passed audio through the gate: '+', '.join(failures))


if __name__ == '__main__':
    main()
