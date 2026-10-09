#!/usr/bin/env python3
"""Extract supplied seminar audio locally; does not call a cloud service."""
import array
import json
import math
from pathlib import Path
import re
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "artifacts" / "pythonmedia"))
import imageio_ffmpeg


def levels(path):
    data = path.read_bytes()
    pos = 12
    samples = None
    while pos + 8 <= len(data):
        kind = data[pos:pos + 4]
        size = struct.unpack_from("<I", data, pos + 4)[0]
        pos += 8
        if pos + size > len(data):
            raise ValueError("Invalid extracted WAV chunk")
        if kind == b"data":
            samples = array.array("f")
            samples.frombytes(data[pos:pos + size])
            if sys.byteorder != "little":
                samples.byteswap()
        pos += size + (size & 1)
    if not samples or not all(math.isfinite(x) for x in samples):
        raise ValueError("Missing or non-finite extracted samples")
    peak = max(abs(x) for x in samples)
    rms = math.sqrt(sum(x * x for x in samples) / len(samples))
    return {"peak": peak, "rms": rms,
            "peakDbFS": 20 * math.log10(peak) if peak else None,
            "rmsDbFS": 20 * math.log10(rms) if rms else None,
            "sampleCount": len(samples)}


def main():
    source = ROOT / "sampleTalk" / "2026-04-1050-clark-g-gilbert-360p-swa.mp4"
    output = ROOT / "sampleTalk" / "extracted"
    if not source.is_file():
        raise SystemExit("Supplied talk MP4 is missing.")
    if output.exists() and any(output.iterdir()):
        raise SystemExit("Extraction output already contains files; preserve them or choose a new directory before rerunning.")
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    probe = subprocess.run([ffmpeg, "-hide_banner", "-i", str(source)],
                           capture_output=True, text=True)
    match = re.search(r"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)", probe.stderr)
    if not match:
        raise SystemExit("Could not determine supplied video duration.")
    hours, minutes, seconds = map(float, match.groups())
    duration = hours * 3600 + minutes * 60 + seconds
    if duration < 90:
        raise SystemExit("Talk must be at least 90 seconds for this three-clip extraction.")
    output.mkdir(parents=True, exist_ok=True)
    commands = []

    def run(arguments):
        command = [ffmpeg, "-hide_banner", "-loglevel", "error", "-n"] + arguments
        subprocess.run(command, check=True)
        commands.append(command)

    run(["-i", str(source), "-map", "0:a:0", "-vn", "-c:a", "libmp3lame",
         "-q:a", "2", str(output / "talk.mp3")])
    offsets = {"early": 30, "middle": math.floor(duration / 2),
               "late": max(0, math.floor(duration - 90))}
    clips = []
    for label, offset in offsets.items():
        filename = label + "-30s.wav"
        target = output / filename
        # Seek after opening input for accurately decoded offsets. No gain filter.
        run(["-i", str(source), "-ss", str(offset), "-t", "30", "-map", "0:a:0",
             "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_f32le", str(target)])
        clips.append({"id": label, "localAudio": filename, "audioSeconds": 30,
                      "startSeconds": offset, "sourceText": "", "referenceText": "",
                      "levels": levels(target)})
    manifest = {
        "sourceVideo": source.name, "sourceDurationSeconds": duration,
        "fullAudio": "talk.mp3", "clips": clips,
        "notes": ["Offsets selected before recognition: early at 30s, midpoint, and 90s before end.",
                  "WAV clips decode directly from original video; MP3 is for listening only.",
                  "Mono 16kHz float32 WAV, no gain normalization or noise processing.",
                  "Provided Swahili transcript is untimed and not aligned to clips.",
                  "No English gold reference was supplied; empty referenceText must not be scored as accuracy."],
        "ffmpegVersion": subprocess.check_output([ffmpeg, "-version"], text=True).splitlines()[0],
        "commands": commands,
    }
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps({"durationSeconds": duration,
                      "clips": [{"id": c["id"], "startSeconds": c["startSeconds"], "levels": c["levels"]} for c in clips],
                      "manifest": str(output / "manifest.json")}, indent=2))


if __name__ == "__main__":
    main()
