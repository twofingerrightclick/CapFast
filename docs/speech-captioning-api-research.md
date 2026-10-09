# Live Swahili → English captioning: Azure Speech feasibility

Research date: October 7, 2026. Documentation review only; no Azure subscription, live audio, translation quality, or latency testing was performed.

## Finding

**The service supports the required language pair and a streaming API suitable for a captioning prototype.** Microsoft's speech-translation language documentation permits supported speech-recognition locales as source languages (with two unrelated exceptions), including Kiswahili `sw-KE` and `sw-TZ`, and English text target `en`. Select the source locale that matches the speaker. The table's ❌ for `sw-TZ` is in the fast-transcription column; it must not be read as a blanket exclusion from real-time speech recognition. [Language support](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support)

Use standard Azure Speech real-time translation through the C# Speech SDK. This is the Speech capability surfaced in Foundry; it does not require an LLM, spoken English output, or a separate app call to Translator for the basic workflow. The service accepts streaming audio and returns translated text. [Speech translation overview](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-translation)

## Documented API behavior

Configure `SpeechTranslationConfig.SpeechRecognitionLanguage` to `sw-KE` or `sw-TZ`, call `AddTargetLanguage("en")`, construct a `TranslationRecognizer` with microphone/audio-stream configuration, and start continuous recognition. The C# how-to exposes intermediate translations through `Recognizing`, and translated strings are available in the result's `Translations` dictionary. Its specific exception is **multi-lingual translation without source language candidates**, which lacks intermediate translation results. A fixed Swahili source avoids that particular mode. [C# translation guide](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/how-to-translate-speech?pivots=programming-language-csharp)

`Recognizing` signals intermediate recognition; `Recognized` signals a final result. Handle `Canceled` and `SessionStopped` as lifecycle events. [TranslationRecognizer .NET reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.cognitiveservices.speech.translation.translationrecognizer?view=azure-dotnet)

There are **two different stability controls**:

- `SpeechServiceResponse_TranslationRequestStablePartialResult` is a boolean requesting more stable **translation** partials by withholding words at the end.
- `SpeechServiceResponse_StablePartialResultThreshold` specifies how often a word must occur in partial results before it is returned. It is described in the speech-recognition captioning guide; do not assume this is an equivalent guarantee about translated English words.

The property reference documents neither an immutable English prefix nor a maximum number of words that may change. The translation-specific property is worth evaluating, but is not a promise that only the final word changes. [PropertyId .NET reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.cognitiveservices.speech.propertyid?view=azure-dotnet)

Microsoft's captioning guide explicitly describes provisional speech-recognition hypotheses being reevaluated as context arrives and a latency/stability tradeoff when displaying partials. It also recommends microphone or stream input for live captioning. These are speech-recognition concepts, not a guarantee that translation revisions are confined to a suffix. [Captioning concepts](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/captioning-concepts)

## Recommended caption behavior

Keep committed final captions separate from the current provisional utterance. Replace the provisional text with each new English hypothesis; commit it once on the final event. Update existing visual elements without clearing the window. Use a fixed caption area, stable line wrapping, and minimal scrolling; retain the unchanged text prefix when practical. An update may revise earlier words within the current utterance, so an implementation must allow more than a one-word tail correction. These are application design recommendations, not automatic Azure UI features.

No flashing from repainting is achievable in the app. Zero provisional wording changes is a different requirement: waiting for final results provides greater stability with additional delay. Any buffering or word-freezing heuristic requires testing against the actual English hypotheses.

## Seminar operations and cost

Use continuous recognition rather than the single-utterance API. The current quota table combines speech-to-text and translation concurrency: F0 permits one request; S0 defaults to 100. It does not state a maximum standard speech-translation session duration. Limits listed for Voice Live or diarization should not be applied to this API. Absence of a listed cap is not an uninterrupted-service guarantee: implement reconnect/error handling and verify a full-length seminar. [Quotas and limits](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-services-quotas-and-limits)

Standard real-time speech translation is charged by audio duration, billed in one-second increments, and includes up to two text translation targets. F0 lists five translation audio hours per month. The pricing page returned dynamic `$-` placeholders during this review, so no verified dollar rate is quoted; obtain the selected region/currency/offer price in Azure's calculator. Estimate a seminar from streamed audio hours, including operational testing. [Azure Speech pricing](https://azure.microsoft.com/en-us/pricing/details/speech/)

## Cross-platform desktop recommendation

Assume desktop Windows, macOS, and Linux for this assessment. The C# Speech SDK supports all three, with native runtime dependencies. Current setup documentation specifies Windows 11 or later with a 64-bit target and Visual C++ runtime, macOS 10.14 or later, and a defined set of Linux distributions with OpenSSL and ALSA dependencies. UI framework requirements may raise these minimums. Validate the chosen SDK package on each intended CPU/OS combination before promising deployment support. [Speech SDK setup](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/quickstarts/setup-platform?pivots=programming-language-csharp)

Recommend C# with Avalonia for this small desktop app. Avalonia supports Windows, macOS (Intel and Apple Silicon), and desktop Linux; a black fullscreen window with a bottom caption region is a straightforward application design. This recommendation is based on platform fit, not a tested integration. [Avalonia platform documentation](https://docs.avaloniaui.net/docs/welcome)

.NET MAUI is another option for Windows and macOS through Mac Catalyst. Microsoft's additional Linux GTK4 backend is currently experimental and not officially supported, so Avalonia is the preferred starting point if desktop Linux is required. [MAUI supported platforms](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms?view=net-maui-10.0), [experimental backends](https://learn.microsoft.com/en-us/dotnet/maui/developer-tools/platform-backends/?view=net-maui-10.0)

Proposed flow: microphone or mixer feed → Speech SDK translation → caption state (final history + provisional phrase) → existing text elements on a black presentation window. Keep audio selection, start/stop, font size, display selection, and connection status in a separate operator window. For a single operator machine, a direct SDK connection is sufficient for the prototype; keep credentials outside source code.

Choose an Azure resource region with real-time translation availability and measure performance from the seminar venue. For example, the region table lists `southafricanorth`, `westeurope`, and `eastus`; geographic proximity alone does not establish latency. [Speech regions](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/regions)

## Proof of concept before committing to production

The following are proposed validation steps, not completed tests:

1. Stream representative Swahili seminar audio at real-time pace, preferably through the actual mixer/USB input. Test the appropriate source locale and assess names, technical terms, accents, and English code-switching with a bilingual reviewer.
2. Compare translation stabilization enabled and disabled. Log provisional and final English output with receipt times; measure time to first useful caption, time to final caption, and how far back wording changes. Do not substitute recognition latency alone for the audience's end-to-end delay.
3. Verify that repeated hypotheses do not duplicate text, finalization causes no blank frame, long phrases wrap legibly, and provisional corrections do not disturb already finalized captions.
4. Run a full seminar-length session and simulate network loss, microphone loss, and restart. Verify recovery behavior and clearly indicate when captions are stale to the operator.
5. Publish and test on each intended OS and CPU, including microphone permissions and projector fullscreen behavior. Agree acceptable delay and translation quality before calling the app production ready.

Decision: proceed to a small live proof of concept. Documentation establishes API and language feasibility; real audio must establish audience suitability.
