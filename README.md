# TheCloser: a real-time AI copilot for Windows

TheCloser is a Windows desktop app in the spirit of HuddleMate. It listens to your call (support, sales or a meeting), notices when the other person asks a question, and writes a short answer in a small always-on-top window. Only you see the window.

## Features

- **Live transcript** from one of these sources:
  - **xAI Grok Voice Transcribe 2.0** (the default): streams your speakers ("Them") and your microphone ("Me") to xAI over WebSockets. Words appear while people are still talking, and each sentence is finalized when they stop. Optional **key terms** help it spell names and jargon correctly. About $0.20 per hour per stream.
  - **Windows Live Captions**: free and on-device. Captions everything your PC plays (Zoom, Teams, Meet, browser), without speaker labels.
  - **OpenAI or Groq Whisper** (or any OpenAI-compatible server): sends each phrase as a short clip, with speaker labels.
- **Auto-answer**: each new line is checked by a small, fast model (Claude Haiku 4.5, about half a second) that decides whether it's a real question worth answering; greetings and small talk are skipped. A Claude answer then streams in about a second after the speaker pauses. Until the other side has said anything (you're testing alone, or listening with the mic only), questions you ask yourself are answered too.
- **Audio input**: choose **Both** (what your PC plays, as "Them", plus your microphone, as "Me"), **System audio** or **Microphone** on the Start call screen or in Settings > Models. System audio listens to both of Windows' default outputs: the normal one, and the one call apps use, which with a Bluetooth headset is often its separate Hands-Free device.
- **Language lock**: pick the call's language on the Start call screen (or in Settings > Models). Speech in other languages is dropped instead of transcribed; Indian languages also keep English mixed into them. Whisper is told the language directly. xAI Grok transcribes whatever it hears (its `language` option only formats numbers and currency), so TheCloser drops text written in another script, e.g. Telugu or Devanagari while English is picked. Two languages that share a script, like English and Spanish, can't be told apart this way.
- **Quick controls**: the **…** menu at the top switches the answer model and adjusts opacity, background and answer text size, the same controls as in Settings.
- **Your context**: add a reference file, product or account details, notes and PDFs. Answers are grounded in this material, and it's prompt-cached, so it stays fast and cheap.
- **Screen analysis** (`Ctrl+Alt+S`): sends a screenshot to Claude, which solves the coding problem, quiz or slide it sees.
- **Ask box**: type your own question, or a follow-up like "shorter" or "give a code example".
- **Modes**: Call, Sales call, Meeting, General Q&A.
- **Privacy**: the window is hidden from screen sharing, screenshots and recordings by default (Windows capture-exclusion API). It has no taskbar entry and sits in the tray. API keys are encrypted with Windows DPAPI.
- **Answer history**, copy button, and "Export session" to Markdown.

## Quick start

1. Run `dist\TheCloser.exe`. A round capsule with a waveform appears on the right of your screen, and a tray icon appears. Hover the capsule to slide it open, then click the **monitor** button to open the panel (the **X** at the top closes it again). During a live call the title bar and the capsule fade out until you point at them.
2. Click the **profile** button in the capsule and go to **Models**. Paste an Anthropic API key from https://console.anthropic.com/settings/keys, then click **Test**.
3. Under **Your context**, add a reference file (product sheet, FAQ, script) and any notes about the call.
4. Under **Listening**, paste your xAI API key from https://console.x.ai (or set the `XAI_API_KEY` environment variable), then click **Test speech key**.
5. Pick the conversation type (top left), then press **Start call**.
   - If you switch to Live Captions instead, the first run shows a Microsoft consent bar at the top of the screen. Click **Yes, continue**. To caption your own voice, open Live Captions > Settings > Preferences and turn on **Include microphone audio**. To keep the caption bar out of the way, set its position to **Floating**.

## Hotkeys (work from any app)

| Keys | Action |
| --- | --- |
| `Ctrl+Alt+Space` | Show / hide the window |
| `Ctrl+Enter` | Answer the latest question now |
| `Ctrl+Shift+Enter` | Analyze the screen |
| `Ctrl+Alt+Arrows` | Move the window |
| `Ctrl+Alt+Shift+Arrows` | Resize the window |
| `Ctrl+N` | New call (when TheCloser is focused) |
| `Esc` | Stop the current answer (when TheCloser is focused) |

## Testing it

| What to check | How |
| --- | --- |
| The app works on this PC | `.\test.cmd` (add `-Audio` to also check speaker and mic capture) |
| What the UI looks like, with no setup | `.\test.cmd -Demo` opens the window filled with sample content |
| Claude answers | Add your key, then click **Try a sample question** in the window (or the tray menu). `.\test.cmd -Api` does the same from the terminal. |
| Listening and auto-answer | Play a sample customer-service call video on YouTube, then press **Start call**. The caller's questions appear in the transcript and get answered on their own. |
| Screen solving | Open a coding problem (for example on LeetCode), then press `Ctrl+Shift+Enter` |
| Hidden from screen share | Take a screenshot (`Win+Shift+S`) or share your screen in Zoom, Teams or Meet. TheCloser shouldn't appear. |

## Models

The default is `claude-opus-5` with **low** thinking effort, which gives good answers and starts responding quickly. For even lower latency, switch to `claude-sonnet-5` or `claude-haiku-4-5` in Settings. You can type any model ID. On Opus 5, TheCloser turns on the API's server-side refusal fallback (`fallbacks: "default"`), so a declined request is retried on another model automatically.

## Building from source

No SDK or package manager is needed. It builds with the C# compiler that ships with Windows (.NET Framework 4.8):

```powershell
.\build.cmd        # -> dist\TheCloser.exe
.\build.cmd -Run   # build and launch
```

Self-test, which writes results to `%TEMP%\thecloser-selftest.txt`:

```powershell
.\dist\TheCloser.exe --selftest                 # logic, rendering, parsing
.\dist\TheCloser.exe --selftest --audio         # also plays a quiet phrase and verifies speaker + mic capture
.\dist\TheCloser.exe --selftest --api           # also calls Claude with your configured key
```

The Claude API is called over raw HTTPS with server-sent events (`src/ClaudeClient.cs`). The official Anthropic C# SDK needs NuGet and the .NET SDK. This project deliberately builds with only what's already on Windows.

## Layout

The UI is WPF (in `src/Ui/`), built in code with the plain C# compiler; there is no XAML compilation, so styles live in `Theme.xaml` and are merged at startup.

| File | What it does |
| --- | --- |
| `src/Program.cs` | Entry point: single-instance guard, WPF app bootstrap, modern TLS |
| `src/Ui/OverlayWindow.cs` | The always-on-top window: chrome, views, dock, global hotkeys, tray, modals |
| `src/Ui/SessionController.cs` | Runs a call: transcript source, question detection, streaming answers, saving |
| `src/Ui/SetupView.cs`, `SessionView.cs`, `SettingsView.cs`, `BrowserView.cs` | The setup, live-call, settings, browser and history screens |
| `src/Ui/Theme.xaml` + `src/Theme.cs` | Control styles and palette (embedded and merged at startup) |
| `src/Ui/Markdown.cs` | Markdown-to-WPF rendering for the answer view |
| `src/ClaudeClient.cs` | Streaming Messages API client, with retries and refusal fallback |
| `src/PromptBuilder.cs` | Mode prompts, cached context and PDFs, transcript and screenshot turns |
| `src/GrokSpeech.cs` | xAI Grok streaming transcription: WebSocket session per speaker, live and final text, reconnects |
| `src/LiveCaptionsSource.cs` | Reads Windows Live Captions through UI Automation and splits it into sentences |
| `src/Audio.cs` | WASAPI loopback and mic capture, 16 kHz resampling, voice-activity phrase detection |
| `src/Transcription.cs` | Cloud speech-to-text client and the two-channel ("Them" / "Me") source |

Settings are stored in `%APPDATA%\TheCloser\settings.json`, and crash details in `error.log` in the same folder. The app was previously called CueCard; on first run, settings and sessions from `%APPDATA%\CueCard` are copied over.

## Responsible use

Some interviews, exams and meetings don't allow AI assistance or recording. Check the rules that apply to you, and get consent where the law requires it before transcribing other people.
