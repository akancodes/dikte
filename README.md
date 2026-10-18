<p align="center"><img src="docs/icon.png" width="96" alt=""></p>

<h1 align="center">Dikte</h1>

<p align="center">Hold a key, speak, let go: your words appear wherever you are typing.<br>
A small, open-source voice-typing tool for Windows 11 and macOS that uses your own OpenAI API key.</p>

<p align="center"><a href="README.tr.md">Türkçe</a></p>

---

Dikte is for people who talk to their tools: Claude Code in a terminal, a chat box, an email, a commit message.
It sits in the notification area (the menu bar on a Mac), listens only while you hold the hotkey, sends the recording to a speech-to-text
model and pastes the result into the window in front of you.

It is built as a replacement for subscription dictation apps when you would rather pay for what you use:
with `gpt-transcribe` an hour of talking costs about **$0.27**.

## Use it

**Windows 11**

1. Download `Dikte.exe` from the [latest release](../../releases/latest/download/Dikte.exe) and run it.
   It is not code-signed, so Windows SmartScreen may ask first: **More info → Run anyway**.
2. Paste your API key when it asks. It is checked, then stored encrypted for your Windows user (DPAPI).
3. Click into any text field and **hold `Ctrl + Win`**, speak, release.

**macOS 13 or later** (Apple silicon and Intel)

1. Download `Dikte-mac.zip` from the [latest release](../../releases/latest/download/Dikte-mac.zip), open it and drag
   **Dikte** into **Applications**.
2. It is not notarized by Apple, so the first launch is refused once. Open **System Settings → Privacy & Security**,
   scroll down and click **Open Anyway** next to Dikte.
3. Turn Dikte on under **Accessibility** when it asks (it hears the shortcut and pastes with it) and allow the
   **microphone**. Then paste your API key; it is kept in your login keychain.
4. Click into any text field and **hold `Fn` (🌐)**, speak, release. If the 🌐 key also opens emoji or switches the
   keyboard language, set **System Settings → Keyboard → Press 🌐 key to → Do Nothing**; Dikte's window says so too.

After an update, macOS forgets the Accessibility permission of an unsigned app: remove Dikte from the list with **−**
and turn it on again.

The window (open it from the tray icon or by starting `Dikte.exe` again) shows what it is doing, your recent
dictations and what you have spent, and holds every setting. Closing it leaves Dikte running in the tray.

| Do this | And |
|---|---|
| Hold `Ctrl + Win` (Mac: `Fn`) | Listens while you hold, pastes when you let go |
| Tap the tap shortcut (none by default, e.g. `Mouse 5`) | Starts listening hands-free; the next tap sends |
| Press `Space` while holding | Goes hands-free: keep talking with your hands off; the shortcut again sends |
| `Esc` | Cancels the recording, or the transcript still on its way |
| Left-click the tray icon (Mac: the menu-bar icon's menu) | Starts/stops hands-free dictation with the mouse |

**Voice tools** (Windows; each needs a shortcut set in Settings first):

| Do this | And |
|---|---|
| Hold the translate shortcut and talk in any language | It is written in English (or `translateTo`) |
| Select text, hold the edit shortcut, say "shorter" / "more formal" / "fix the grammar" | The selection is replaced by the edited text; with nothing selected it writes what you ask for |
| Tap the paste-again shortcut | Pastes your last dictation again, where you are now |
| Say "new line" / "new paragraph" (Turkish: "yeni satır" / "yeni paragraf") | A line break instead of the words |
| End with "send" or "gönder" as its own sentence | Presses Enter after the text: sends a prompt in Claude Code or a chat |
| Say only "delete that" (or "sil") | Takes the last dictation back out |

**Modes** (Windows, Mac and iPhone): how your words are written. Each mode is an instruction in your own words that a
chat model applies to the transcript, never answering it. Five come built in and can be edited, deleted or added to:

| Mode | Writes |
|---|---|
| As spoken | The words as heard, with no chat model (the default) |
| Clean up | Your wording, tidied: no "um"s, "no wait" corrections applied |
| Prompt | A clear prompt for Claude Code or another AI assistant: a short request stays a sentence, a long ramble is put in order |
| List | A bullet list, one point per line |
| Commit message | A git commit message in English |
| Email | A short email in the language you spoke |

Which one is used: a mode's own hold shortcut wins, then the app in front (Prompt is used in Windows Terminal, VS Code,
Cursor, the Claude app, Terminal, iTerm2 and other terminals out of the box), then the active mode, picked on the
window's Home page, from the tray or menu-bar menu, with the next-mode shortcut, or on iPhone from the keyboard's
strip. Each mode can use its own chat model; the default `gpt-4.1-mini` answers in under a second.

Both shortcuts are picked in Settings by pressing them: any key, key combination, or the middle and side mouse
buttons (`Mouse 4` / `Mouse 5`, which then stop doing "back" and "forward"). A short hold that turns into a shortcut (`Ctrl + Win + →`, `Fn + F1`...) is cancelled silently and the shortcut
works as usual. Mixing languages is fine: pick the ones you speak in Settings and the model is told to write each word
in the language it was spoken, never translating (measured: it keeps "refactor" English in a Turkish sentence).

While you talk, music and videos are turned down (to 15% by default, or muted) and come back when you let go.
Windows turns down each app; macOS has no per-app volume, so there the output device itself is turned down.
If the network fails the recording is kept: **Retry last recording** is in the tray menu.

## Settings

Everything is in the window's Settings tab; it is stored in `settings.json`, which can also be edited by hand (changes
apply at once): `%APPDATA%\Dikte\` on Windows, `~/Library/Application Support/Dikte/` on a Mac.

| Key | Default | |
|---|---|---|
| `hotkey` | `ctrl+win`, Mac `fn` | Hold to talk. Any combination: `rctrl`, `f8`, `mouse4`, `ctrl+shift+space`; on a Mac also `rcmd`, `ctrl+opt`... |
| `toggleHotkey` | `""` | Tap to start hands-free, tap again to send; `mouse5`, `f9`... |
| `handsFreeKey` | `space` | Pressed while holding the hotkey |
| `translateHotkey` | `""` | Windows: hold to talk in any language, written in `translateTo` |
| `translateTo` | `English` | |
| `editHotkey` | `""` | Windows: hold to say what to do with the selected text |
| `repasteHotkey` | `""` | Windows: tap to paste the last dictation again |
| `voiceCommands` | `true` | Windows: "new line", "send" at the end, "delete that" alone act instead of being written |
| `model` | `gpt-transcribe` | Also `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`, `whisper-1` |
| `languages` | `[]` | Codes of the languages you speak: none = detect, one = pinned, several = mixed freely |
| `vocabulary` | `Claude Code, ...` | Names and terms to spell your way, sent as the model's prompt |
| `apiBase` | `https://api.openai.com/v1` | Any OpenAI-compatible server, e.g. `https://api.groq.com/openai/v1` with `whisper-large-v3-turbo` |
| `insertMode` | `paste` | `paste` (clipboard + shortcut, the clipboard is restored) or `type` (key by key, leaves the clipboard alone) |
| `pasteShortcut` | `ctrl+v` | Windows: `ctrl+shift+v` or `shift+insert` for terminals that want them (a Mac always uses `⌘V`) |
| `modes` | Clean up, Prompt, List, Commit message, Email | How your words are written: `name`, `instruction` (your own words), `model` (empty = `cleanupModel`), `apps` it is always used in, its own hold `hotkey` |
| `activeMode` | `plain` | The mode used when no shortcut or app picks one; `plain` writes the words as heard |
| `nextModeHotkey` | `""` | Tap to switch the active mode |
| `cleanupModel` | `gpt-4.1-mini` | Chat model for modes, translate and edit |
| `appendSpace` | `true` | So two dictations in a row do not run together |
| `microphone` | `""` | Empty = the system default |
| `sounds` | `true` | |
| `quietOtherAudio` | `true` | Turn other sounds down while recording (put back afterwards) |
| `quietLevel` | `0.15` | What they are turned down to: `0` mutes |
| `maxMinutes` | `10` | A recording stops and sends itself at this length |

## Privacy

Audio is recorded only while you hold the key (or in hands-free mode), kept in memory, and sent only to the
`apiBase` you set. Pasted text is marked so it stays out of Windows clipboard history and cloud clipboard.
The log (`%APPDATA%\Dikte\dikte.log`) holds timings and errors, never what you said; the recent list
(`history.json`) is on your disk only and can be cleared from the menu.

## Good to know

- On a Mac, the API key lives in the login keychain; after an update macOS may ask once whether Dikte may read it.

- Windows does not let a normal app type into a window running **as administrator**. Run Dikte as administrator too
  if your terminal is elevated.
- Claude Code in Windows Terminal: `Ctrl+V` is the terminal's paste, so the text arrives as a paste (a long one shows
  as `[Pasted text]` until you send it).

## Build

**Windows** needs the .NET 10 SDK; it builds on Linux and macOS as well (`EnableWindowsTargeting`).

```sh
dotnet test tests/Dikte.Tests          # the hotkey logic, WAV, settings and API client: runs on any OS
dotnet publish src/Dikte -c Release -o dist   # dist/Dikte.exe, self-contained, one file
```

`src/Dikte/Core` is plain .NET (no Windows APIs) and is what the tests cover; the rest is the Windows shell around it:
a low-level keyboard hook, `waveIn` recording at 16 kHz mono through NAudio, per-app volume through WASAPI,
`SendInput` for pasting, a WinForms overlay that never takes focus, and the window: one HTML page (`src/Dikte/ui`)
in WebView2, which Windows 11 already has. The icons are drawn by `tools/make-icon.py` (standard library only), one bitmap per size Windows asks for.
**macOS** is a Swift package in `mac/`: `DikteCore` is the same core in Swift (plain Foundation, tested on any OS),
`Dikte` the menu-bar app around it (a CGEvent tap, AVAudioEngine, the same `ui/index.html` in WKWebView).

```sh
swift test --package-path mac        # the core: runs on Linux too
mac/build-app.sh                     # on a Mac with Xcode: dist/Dikte.app and dist/Dikte-mac.zip, universal
```

Releases are built by GitHub Actions on a `v*` tag: one release carries `Dikte.exe` and `Dikte-mac.zip`.

## License

MIT © 2026 Bayram Akan
