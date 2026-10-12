# Historical Google captioning research

This dated research includes the retired direct Live Translation experiment. The current app supports only Transcribe Live followed by two-output Flash text translation; see the README for its workflow.

# Google alternatives for live Swahili → English captions

Research date: October 8, 2026. Primary documentation review only; no authenticated API requests, audio-quality evaluation, latency measurements, or macOS SDK build were performed. No application implementation was changed.

## Decision

Google offers two plausible prototypes: **Gemini Live Translation for direct streaming translation**, and **Cloud Speech-to-Text V2 Chirp 3 plus Cloud Translation Advanced** for explicit transcript/finalization control. Test the direct translation candidate first if simpler account setup and fewer application orchestration steps are the priorities. Keep the Cloud pipeline as the alternative when source transcripts and explicit recognition-final boundaries matter. Both relevant speech routes involve Preview support; neither is yet a verified seminar solution for this app.

## Direct translation candidate

Gemini Live Translation uses `gemini-3.5-live-translate-preview` over a continuous WebSocket session. Swahili `sw` and English `en` are supported. Configure English as the target and request output audio transcription to obtain caption text. The documented configuration uses AUDIO responses with accompanying text, so do not assume a text-only billing path; playback can be suppressed by the app. Input is mono 16 kHz PCM; the guide recommends roughly 100 ms audio chunks. [Live translation guide](https://ai.google.dev/gemini-api/docs/live-api/live-translate)

The model is explicitly Preview and supports audio plus text transcript output. [Model card](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-live-translate-preview) This path requires testing the exact transcript event boundaries and revisions, session rollover, error recovery, and available project limits. General Gemini Live examples for other models do not establish that this translation model supports the same resume/compression behavior. Generated translated speech may cost money even if we discard it; check model-specific input/output pricing. [Gemini pricing](https://ai.google.dev/gemini-api/docs/pricing)

**Integration inference:** A native Avalonia macOS app can use .NET WebSocket facilities for the documented WSS protocol. This is not a verified build or an assertion of an official C# Live Translation sample. Audio capture must produce PCM through the native capture helper. Use local operator credentials for a private prototype; a distributed client should use a backend-issued short-lived credential rather than embed a shared long-lived key.

## Cloud Speech-to-Text route: model choice matters

Chirp 3 (`chirp_3`) explicitly supports `StreamingRecognize` with Swahili `sw` and Kenyan Swahili `sw-KE`; **both language entries are Preview**. Its documented region table lists `us` and `eu` multi-regions. Use a matching recognizer location and endpoint, such as `us-speech.googleapis.com`, and query the locations API for current availability before deployment. The document does not list `sw-TZ`. [Chirp 3 model and streaming documentation](https://docs.cloud.google.com/speech-to-text/docs/models/chirp-3)

Chirp 2 is unsuitable as the assumed Swahili streaming choice: its method-specific streaming language list excludes Swahili even though general language/model tables include Swahili for other recognition methods. Its documented direct speech-translation pairs also do not establish Swahili support. [Chirp 2 methods and languages](https://docs.cloud.google.com/speech-to-text/docs/models/chirp-2)

For the Cloud pipeline, translate recognized Swahili using Cloud Translation Advanced with source `sw`, target `en`, and plain text. Both languages are supported by the NMT model. [Translation languages](https://docs.cloud.google.com/translate/docs/languages)

## Interim captions and translation revisions

Speech streaming results expose `IsFinal`, `Stability`, and `ResultEndOffset`. A final result will not receive further hypotheses for that audio portion. Stability estimates how likely an interim recognition is to remain unchanged; it is not correctness confidence. A zero value can mean the stability field was not set. These are **Swahili recognition** semantics, not an English translation guarantee. [StreamingRecognitionResult .NET reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Speech.V2/latest/Google.Cloud.Speech.V2.StreamingRecognitionResult)

Responses can include a newly finalized portion followed by provisional portions. Process the complete result collection rather than assuming every response contains one full utterance. [StreamingRecognizeResponse reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Speech.V2/latest/Google.Cloud.Speech.V2.StreamingRecognizeResponse)

Cloud Translation's `TranslateTextAsync` is a request/response RPC returning translated text, rather than an audio stream with translation revision events. [TranslationServiceClient reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Translate.V3/latest/Google.Cloud.Translate.V3.TranslationServiceClient)

**Recommended app logic:** Translate final source segments reliably, and optionally translate selected interim hypotheses for lower latency. Debounce repeated source updates; attach utterance/revision IDs to translation requests; discard stale responses so a slow earlier response cannot overwrite newer text. Replace the whole current provisional English segment when needed and commit only the final translation. English word order can change as more source context arrives; freezing a common prefix is a heuristic. Neither stable Swahili recognition nor synchronous translation promises that only the English tail will change. Keep the existing black caption surface and update visual elements without clearing them.

## .NET and macOS

Official packages are `Google.Cloud.Speech.V2` and `Google.Cloud.Translate.V3`. Speech exposes bidirectional streaming. [SpeechClient .NET reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Speech.V2/latest/Google.Cloud.Speech.V2.SpeechClient) Modern Google libraries use managed `Grpc.Net.Client` where available, rather than requiring the legacy native `Grpc.Core` transport. [Transport selection](https://docs.cloud.google.com/dotnet/docs/reference/help/transports)

Google supports modern .NET framework targets, but does not claim mobile MAUI/Xamarin support. [Supported platforms](https://docs.cloud.google.com/dotnet/docs/reference/help/platforms) **Inference:** a current native .NET Avalonia macOS desktop app is a reasonable fit, including Apple Silicon using managed networking; verify the actual target/publish configuration. HTTP/2 connectivity is required for bidirectional gRPC. Microphone permissions, device capture, channel conversion, and resampling remain application responsibilities.

## Authentication, session length, and costs

The Cloud pipeline requires a project with billing enabled and both APIs enabled. [Speech setup](https://docs.cloud.google.com/speech-to-text/docs/setup), [Translation setup](https://docs.cloud.google.com/translate/docs/setup) For local development Google recommends user ADC via `gcloud auth application-default login`; service-account impersonation is also supported in C#. A service-account JSON key is a fallback secret, not something to commit or bundle into the app. [Local ADC setup](https://docs.cloud.google.com/docs/authentication/set-up-adc-local-dev-environment)

Each Cloud speech stream lasts at most five minutes, individual audio requests are limited to 25 KB, and audio must arrive at approximately real-time speed. A seminar needs planned stream rotation plus replay/deduplication around boundaries; reconnect alone can lose words. [Speech quotas and limits](https://docs.cloud.google.com/speech-to-text/docs/quotas)

Cloud speech costs depend on processed audio duration, channel count, API/model, and usage tier. [Speech pricing](https://cloud.google.com/speech-to-text/pricing) NMT text translation is billed by submitted input characters per target language. Retranslating growing interim hypotheses repeatedly increases billable character volume beyond the final transcript length, so throttle and measure it. [Translation pricing](https://cloud.google.com/products/translate/pricing) Verify exact rates and Preview availability against the account used for the prototype.
