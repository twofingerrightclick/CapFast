# Swahili manual test pack

Twelve additional human-read Google FLEURS Swahili recordings with official parallel English references. This pack excludes the original difficult 21-second fixture.

For a quick manual test, play `playlist.wav` into your selected audio input. It lasts 157.16 seconds (2:37), with exactly one second of inserted silence between clips. This is an artificial playlist, not natural continuous conversation. Individual numbered WAVs are unchanged original recordings, lasting 6.24–22.20 seconds.

Each numbered `*-reference.json` contains the Swahili transcript, English reference, sentence ID, original filename, duration, SHA-256, and source links. `manifest.json` also gives each clip's position in the playlist. `playlist-reference.json` contains the combined text.

References were verified by joining the official Swahili filename and sentence ID to the same ID in official English metadata, requiring a unique English text. WAV sample counts were checked against metadata. These are parallel reference sentences, not word-timed English annotations.

Selection used the first 24 eligible distinct sentence IDs in a bounded archive sample, then selected by duration and reported gender where available. It was not random and did not use model results. Reported gender does not establish distinct speaker identities. This pack adds topic and duration variety but is not a representative benchmark.

Source: [Google FLEURS](https://huggingface.co/datasets/google/fleurs), Swahili Kenya dev split. Credit: Google FLEURS dataset contributors. Licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Original clip audio is unmodified; the playlist concatenates it with added digital silence. Exact source URLs and metadata hashes are in the manifest.

The pack's audio structure, hashes, and reference joins were checked. App translation quality on these new recordings has not yet been evaluated.
