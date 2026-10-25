# Simple Listen

A separate native Mac app that plays an audio input live through an audio output. No recording, network, API key, or CapFast process is needed.

Build from the repository root with `bash simple-listen/build.sh`, then open `artifacts/Simple Listen.app`. Requires Apple's Command Line Tools and the existing **CapFast Local Signing** identity (override with `SIMPLE_LISTEN_SIGN_IDENTITY`).

Choose an input and output, click **Listen**, and allow microphone access. Start with headphones and low volume to avoid feedback. Click **Stop** or close the window to stop audio. The volume slider changes only this app's monitoring volume. Device selection does not change your Mac's system defaults. Refresh reloads connected devices while stopped. An audio configuration change stops monitoring; select your devices and start again.

If permission is denied, enable **Simple Listen** in System Settings → Privacy & Security → Microphone, then reopen the app. This is a locally signed build for this Mac, not a notarized distribution.
