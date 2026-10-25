# Silero VAD model

Source: https://github.com/snakers4/silero-vad
Pinned revision: `066409f3d9db45a30d21dfcbf9147826ffdd7052`
Model: `src/silero_vad/data/silero_vad.onnx`
SHA-256: `1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3`
License: MIT (included in LICENSE and published with the application).

The model is bundled, not downloaded at runtime. The C# adapter follows the
pinned upstream `utils_vad.py` streaming contract: 512 samples at 16 kHz,
64 prior context samples, recurrent state shaped [2, 1, 128], scalar int64
sample rate, output probability and next recurrent state. One detector is
created per capture session. No model weights were modified.
