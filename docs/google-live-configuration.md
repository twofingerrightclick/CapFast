# Google Gemini Live configuration for CapFast

Researched 8 October 2026 against Google's current documentation and SDK source. No API key was read, and no account settings or billing were changed.

## Does the existing image-editor key work?

**It can, if it is a Gemini Developer API key with suitable project access and restrictions.** There is no separate translation key documented. A key working with an image model establishes neither Live Translate quota nor the success of a Live session. CapFast uses the Developer API (`generativelanguage.googleapis.com`), not Vertex AI. Google's SDK distinguishes these endpoints and authentication paths. [Google SDK client](https://github.com/googleapis/python-genai/blob/main/google/genai/client.py)

Google now rejects unrestricted standard keys; explicitly restricted standard keys still work. New AI Studio keys have used service-account-bound authorization keys since 28 May 2026 and default to Gemini API restrictions. [Official key guide](https://ai.google.dev/gemini-api/docs/api-key)

We recommend a separate CapFast key in the existing project to preserve other apps. Import the project into AI Studio if it is missing. [Key/project setup](https://ai.google.dev/gemini-api/docs/api-key)

## Practical setup and verification

1. Open [Google AI Studio API Keys](https://aistudio.google.com/api-keys), identify the project used by the existing key, and create a dedicated CapFast key in that project. Check for Blocked or Unrestricted labels. Keep the existing shared key intact. [Key guide](https://ai.google.dev/gemini-api/docs/api-key)
2. Confirm that project's **Generative Language API** is enabled in Cloud Console → APIs & Services. AI Studio checks this, plus accepted terms, region eligibility and project security standing. For a managed project, ask the administrator about access errors. [AI Studio access troubleshooting](https://ai.google.dev/gemini-api/docs/troubleshoot-ai-studio)
3. In AI Studio, select the same project and inspect its **Usage / Rate limits** for `gemini-3.5-live-translate-preview`. Limits depend on model and project; preview models can be more restricted. A second key in the same project does not create separate quota. Google now directs users to their actual dashboard instead of publishing a universal Live concurrency allowance. [Rate-limit guide](https://ai.google.dev/gemini-api/docs/rate-limits)
4. Use the model page's **Try in Google AI Studio** link to try Live Translate with English as target, using that same project/key when the playground offers key selection. A successful playground test is useful evidence; only a successful CapFast WebSocket setup proves the desktop integration works. [Live Translate model](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-live-translate-preview)
5. Put the new key in CapFast's local configuration, relaunch, and press Space. On failure, examine the updated error viewer for the connection/setup stage and specific category. Do not paste the secret into a support conversation.

## Billing, language and availability

Live Translate currently has both **Free and Paid tiers**. Billing is therefore not a universal prerequisite for a first test. The published paid rate is about **$0.0368/minute of combined audio consumption**; the model generates translated audio even when CapFast only displays its transcript. Free-tier data can be used to improve Google's products; paid-tier data is listed differently. [Current pricing](https://ai.google.dev/gemini-api/docs/pricing)

For higher quota, choose Set up billing on the project's AI Studio row. Follow its actual prompts: accounts may require Prepay credits (minimum $5) or have Postpay. Inspect Billing Tier/Status for Set up Prepay or No credits. A paid Prepay account with zero balance stops serving API calls rather than falling back to Free. Do not change a shared billing account merely to debug an unexplained connection error. [Billing setup and status](https://ai.google.dev/gemini-api/docs/billing)

Swahili (`sw`) and English (`en`) are explicitly supported. Translation streams audio continuously over WebSockets; configure target English and enable output transcription for caption text. This is a Preview model, not a text-only transcription request endpoint. [Translation guide](https://ai.google.dev/gemini-api/docs/live-api/live-translate) The US is supported, as are Kenya and Tanzania; deployment location should be checked against the current list. [Available regions](https://ai.google.dev/gemini-api/docs/available-regions)

## Authentication and setup details verified

The official Python SDK puts ordinary Developer API keys in the `x-goog-api-key` header and passes those headers to its Live WebSocket connection. It selects `Authorization: Token` and a constrained endpoint only for ephemeral `auth_tokens/…` credentials. New authorization API keys are distinct from these ephemeral tokens. CapFast's header-based ordinary-key authentication matches that SDK path; no separate OAuth flow or special Live IAM grant is documented for this path. [SDK HTTP authentication](https://github.com/googleapis/python-genai/blob/main/google/genai/_api_client.py), [SDK Live connection](https://github.com/googleapis/python-genai/blob/main/google/genai/live.py)

The translation guide's raw WebSocket example nests transcription configuration differently from the canonical schema. The API reference places `outputAudioTranscription` directly on **setup**, as CapFast does. Keep `translationConfig` in `generationConfig`, request AUDIO responses, and wait for `setupComplete` before streaming audio. [Canonical Live API schema](https://ai.google.dev/api/live), [Translation configuration](https://ai.google.dev/gemini-api/docs/live-api/live-translate)

## What the observed failure proves

The parent investigation verified macOS microphone permission was already granted. The earlier app recorded WebSocket close **1011** without the service's reason. That number alone does **not** identify credentials, microphone permission, quota, bad setup, model availability or an internal service problem. A successful WebSocket handshake also does not prove the later model setup was accepted.

Next evidence needed: the new build's classified close/setup error, followed by the same-project model/quota check above. Google distinguishes invalid authentication, permission denial, exhausted Prepay balance, missing model and quota errors; the specific service response determines which configuration step applies. [API errors](https://ai.google.dev/gemini-api/docs/api-errors)
