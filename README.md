<p align="center"><img src="docs/icon.png" width="96" alt=""></p>

<h1 align="center">Dikte</h1>

<p align="center">Hold a key, speak, let go: your words appear wherever you are typing.<br>
A small, open-source voice-typing tool for Windows 11 that uses your own OpenAI API key.</p>

<p align="center"><a href="README.tr.md">Türkçe</a></p>

---

Dikte is for people who talk to their tools: Claude Code in a terminal, a chat box, an email, a commit message.
It sits in the notification area, listens only while you hold the hotkey, sends the recording to a speech-to-text
model and pastes the result into the window in front of you.

It is built as a replacement for subscription dictation apps when you would rather pay for what you use:
with `gpt-transcribe` an hour of talking costs about **$0.27**.

## Use it

1. Download `Dikte.exe` from the [latest release](../../releases/latest/download/Dikte.exe) and run it.
   It is not code-signed, so Windows SmartScreen may ask first: **More info → Run anyway**.
2. Paste your API key when it asks. It is checked, then stored encrypted for your Windows user (DPAPI).
3. Click into any text field and **hold `Ctrl + Win`**, speak, release.

| Do this | And |
|---|---|
| Hold `Ctrl + Win` | Listens while you hold, pastes when you let go |
| Press `Space` while holding | Goes hands-free: keep talking with your hands off; `Ctrl + Win` again sends |
| `Esc` | Cancels the recording, or the transcript still on its way |
| Left-click the tray icon | Starts/stops hands-free dictation with the mouse |

A short hold that turns into a shortcut (`Ctrl + Win + →`, `Ctrl + Win + D`...) is cancelled silently and the shortcut
works as usual. Mixed languages are fine: leave `language` empty and the model follows you between Turkish and English
mid-sentence.

The tray menu has the last 20 transcripts (click one to copy it again), the microphone picker, **Retry last recording**
when the network failed (the audio is kept), an optional tidy-up pass, sounds, and start with Windows.

## Settings

`%APPDATA%\Dikte\settings.json`, opened from the tray menu. Saved changes apply at once.

| Key | Default | |
|---|---|---|
| `hotkey` | `ctrl+win` | Any combination: `rctrl`, `f8`, `ctrl+shift+space`, `ralt+f12`... |
| `handsFreeKey` | `space` | Pressed while holding the hotkey |
| `model` | `gpt-transcribe` | Also `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`, `whisper-1` |
| `language` | `""` | Empty = detect; `tr`, `en`... pins it |
| `vocabulary` | `Claude Code, ...` | Names and terms to spell your way, sent as the model's prompt |
| `apiBase` | `https://api.openai.com/v1` | Any OpenAI-compatible server, e.g. `https://api.groq.com/openai/v1` with `whisper-large-v3-turbo` |
| `insertMode` | `paste` | `paste` (clipboard + shortcut, the clipboard is restored) or `type` (key by key, leaves the clipboard alone) |
| `pasteShortcut` | `ctrl+v` | `ctrl+shift+v` or `shift+insert` for terminals that want them |
| `cleanup` | `false` | Runs the text through `cleanupModel` to drop "um"s and apply "no wait" corrections (+~1 s) |
| `cleanupModel` | `gpt-4.1-mini` | |
| `appendSpace` | `true` | So two dictations in a row do not run together |
| `microphone` | `""` | Empty = the Windows default |
| `sounds` | `true` | |
| `maxMinutes` | `10` | A recording stops and sends itself at this length |

## Privacy

Audio is recorded only while you hold the key (or in hands-free mode), kept in memory, and sent only to the
`apiBase` you set. Pasted text is marked so it stays out of Windows clipboard history and cloud clipboard.
The log (`%APPDATA%\Dikte\dikte.log`) holds timings and errors, never what you said; the recent list
(`history.json`) is on your disk only and can be cleared from the menu.

## Good to know

- Windows does not let a normal app type into a window running **as administrator**. Run Dikte as administrator too
  if your terminal is elevated.
- Claude Code in Windows Terminal: `Ctrl+V` is the terminal's paste, so the text arrives as a paste (a long one shows
  as `[Pasted text]` until you send it).

## Build

Needs the .NET 10 SDK; it builds on Linux and macOS as well (`EnableWindowsTargeting`).

```sh
dotnet test tests/Dikte.Tests          # the hotkey logic, WAV, settings and API client: runs on any OS
dotnet publish src/Dikte -c Release -o dist   # dist/Dikte.exe, self-contained, one file
```

`src/Dikte/Core` is plain .NET (no Windows APIs) and is what the tests cover; the rest is the Windows shell around it:
a low-level keyboard hook, `waveIn` recording at 16 kHz mono through NAudio, `SendInput` for pasting, and a WinForms
overlay that never takes focus. Releases are built by GitHub Actions on a `v*` tag.

## License

MIT © 2026 Bayram Akan
