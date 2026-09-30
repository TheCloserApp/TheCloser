# TheCloser for Windows

TheCloser is a real-time AI interview copilot. It listens to your interview, notices when the interviewer asks a question, and writes a short answer in a small always-on-top window that's hidden from screen sharing. This is the Windows app; it has the same screens and features as the [Mac app](https://github.com/TheCloserApp/MAC).

## Features

- **Answers as they ask**: each new line is checked by a small, fast model (Claude Haiku 4.5), which skips greetings and small talk. The answer streams in about a second after the interviewer pauses. Turn **Auto-generate responses** off to answer only when you press `Ctrl+Enter`.
- **Keywords stand out**: the model bolds the 2–3 key terms of each answer, drawn in the style you pick (coloured text or a highlighter) in the **…** menu or **Settings > General**.
- **Models**: Claude, GPT, Gemini, Grok and Kimi, all through one OpenRouter key. Pick the ones your model menu shows in **Settings > AI**, or request a missing one.
- **Transcription**:
  - **Automatic** (the default): ElevenLabs when you've added an ElevenLabs key, then Grok, otherwise Windows.
  - **Windows (on-device, free)**: Windows Live Captions, on Windows 11.
  - **ElevenLabs** (Scribe realtime) and **Grok Transcribe 2**: stream what your PC plays ("Them") and your microphone ("Me"), with live text while people are still talking.
- **Your interview**: a resume, context (role, company, job description) and files. Answers are grounded in them.
- **Screen analysis** (`Ctrl+Shift+Enter`): sends a screenshot, for coding problems, quizzes and slides.
- **Call prompt**: when Zoom, Teams or Meet starts using your microphone, a card in the top-right corner offers to start the interview.
- **Memory**: each answer can see the earlier ones in the interview, and optionally the latest from recent interviews (**Settings > Memory**).
- **Privacy**: the window is hidden from screen sharing, screenshots and recordings (Windows capture exclusion). It has no taskbar entry and sits in the tray. Keys are encrypted with Windows DPAPI and sent only to their provider. Sessions stay on this PC.
- **History**, export to Markdown, and your own system prompts.

## Quick start

1. Run `dist\TheCloser.exe`. A round capsule with a waveform appears on the right of your screen, and a tray icon appears. The welcome tour opens the first time.
2. Choose **Bring your own keys** and add your **OpenRouter** and **ElevenLabs** keys (or pick Grok as the engine and add an xAI key). Testers choose **We handle everything** and enter their code under **Have a tester code?**; they need no keys.
3. Hover the capsule to preview the bar, or click it to keep it open. The **monitor** button opens the interview setup, the **globe** the browser, and the **profile** button Settings (it's amber while keys are missing).
4. Pick a language, optionally add your resume and context, then press **Start interview**. With a screen open, the title bar and the capsule fade until you point at the panel.

The **…** menu at the top has everything you'd change mid-interview: pause or end, auto-generate, the model, the audio source (Mic, System or Both), transparency, background, text size, keywords, the transcription engine, the live transcript and focus mode.

## TheCloser Pro

Pro is shown as **coming soon**, the same as the Mac app's production build, because the Stripe prices are still test prices. The app talks to `https://www.thecloser.tech/api/`, the same service as the Mac. A **tester code** gives Pro on a shared test budget (see the website repo's README). Pro needs no keys and transcribes on this PC; Windows 10 has no Live Captions, so there it uses your own speech key if you've added one. Pro belongs to this Windows installation: its identifier is encrypted in `%APPDATA%\TheCloser\settings.json`.

## Shortcuts (work from any app)

| Keys | Action |
| --- | --- |
| `Ctrl+Alt+Space` | Show / hide the overlay |
| `Ctrl+Enter` | Get the answer now (during an interview) |
| `Ctrl+Shift+Enter` | Screenshot → send to AI (during an interview) |
| `Ctrl+Shift+Arrows` | Move the overlay (while it's showing) |
| `Ctrl+Alt+Shift+Arrows` | Resize the overlay |
| `Ctrl+N` | New session (when TheCloser is focused) |
| `Esc` | Stop the answer being written (when TheCloser is focused) |

`Ctrl+Shift+Arrows` is also the word-selection shortcut in text fields. The overlay claims it only while it's showing, like `⌘⇧` + arrows on the Mac.

## Testing it

| What to check | How |
| --- | --- |
| The app works on this PC | `.\test.cmd` (add `-Audio` to also check speaker and mic capture) |
| What the UI looks like, with no setup | `.\test.cmd -Demo` opens the window with a sample interview |
| Every screen as an image | `.\dist\TheCloser.exe --render <folder>` (CI uploads these as the `windows-screens` artifact) |
| Answers | `.\test.cmd -Api` sends one question through OpenRouter with the key saved in Settings |
| Listening and auto-answer | Play a mock interview video, then press **Start interview**. The questions appear in the transcript and get answered on their own. |
| Screen solving | Open a coding problem (for example on LeetCode), then press `Ctrl+Shift+Enter` |
| Hidden from screen share | Take a screenshot (`Win+Shift+S`) or share your screen in Zoom, Teams or Meet. TheCloser shouldn't appear. |

## Building from source

No SDK or package manager is needed. It builds with the C# compiler that ships with Windows (.NET Framework 4.8), which only accepts C# 5:

```powershell
.\build.cmd        # -> dist\TheCloser.exe
.\build.cmd -Run   # build and launch
```

Self-test, which writes results to `%TEMP%\thecloser-selftest.txt`:

```powershell
.\dist\TheCloser.exe --selftest                 # logic, rendering, parsing
.\dist\TheCloser.exe --selftest --audio         # also plays a quiet phrase and verifies speaker + mic capture
.\dist\TheCloser.exe --selftest --api           # also calls OpenRouter and xAI with your configured keys
.\test.ps1 -CI                                 # offline checks on Windows Server (no Live Captions install check)
```

The **Windows build and self-test** GitHub Actions workflow runs on pull requests and can be started manually. It builds the app, runs the offline self-tests (including subscription, tester-code and transcription protocol checks with simulated responses), draws every screen with `--render`, and uploads the test log, the screens and the app as artifacts. It needs no secrets. Audio capture, live transcription and the desktop experience need a separate Windows 11 smoke test.

## Layout

The UI is WPF (in `src/Ui/`), built in code with the plain C# compiler; there is no XAML compilation, so styles live in `Theme.xaml` and are merged at startup.

| File | What it does |
| --- | --- |
| `src/Program.cs` | Entry point: single-instance guard, WPF app bootstrap, modern TLS, `--selftest` and `--render` |
| `src/Ui/OverlayWindow.cs` | The always-on-top window: title bar, card, capsule, the … menu, hotkeys, tray, modals |
| `src/Ui/OnboardingView.cs` | The welcome tour |
| `src/Ui/SetupView.cs`, `SessionView.cs`, `SettingsView.cs`, `BrowserView.cs` | Interview setup, the live interview, Settings, the browser and History |
| `src/Ui/ProViews.cs` | Pro plans, the tester code and the usage bar |
| `src/Ui/CallPromptWindow.cs` | "On a call in Zoom?" in the top-right corner |
| `src/Ui/Markdown.cs`, `KeywordStyles.cs` | Markdown-to-WPF rendering for answers, and the keyword styles |
| `src/Ui/SessionController.cs` | Runs an interview: transcript source, question detection, streaming answers, memory, saving |
| `src/Ui/RenderShots.cs` | `--render`: draws every screen to a PNG |
| `src/OpenRouterClient.cs` | Streaming chat completions, for your key or the subscription |
| `src/SubscriptionClient.cs` | Device-bound Pro: passes, tester codes, usage, billing links |
| `src/Prompts.cs`, `PromptBuilder.cs` | System prompts (the Mac's) and the request payload |
| `src/StreamingSpeech.cs`, `GrokSpeech.cs` | Live transcription over WebSockets: ElevenLabs and xAI Grok |
| `src/LiveCaptionsSource.cs` | Reads Windows Live Captions through UI Automation and splits it into sentences |
| `src/Audio.cs` | WASAPI loopback and mic capture, 16 kHz resampling |

Settings are stored in `%APPDATA%\TheCloser\settings.json`, and crash details in `error.log` in the same folder.

## Responsible use

Some interviews, exams and meetings don't allow AI assistance or recording. Check the rules that apply to you, and get consent where the law requires it before transcribing other people.
