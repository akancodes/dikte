# Dikte for iPhone

Not published: it is built for our own phones and goes only to TestFlight's internal group ("Us"), exported with
*TestFlight internal testing only*, so the build cannot be sent for review or to external testers.

## How it works

iOS never gives a keyboard the microphone, so the work is split, the way other dictation keyboards do it:

- **The keyboard** is a full keyboard to use instead of the system one, so a dictation can be fixed on the spot:
  Turkish Q (every English letter too) or English, shift and caps lock, 123 and #+=, delete that speeds up to whole
  words, sentence capitals, two spaces for ". ", and the space bar dragged sideways moves the cursor. Suggestions
  come from iOS's own Turkish and English dictionaries; nothing is corrected by itself, since a mixed sentence
  would be "fixed" wrongly. The strip above the keys holds **Talk**: tap to start and tap to send, or hold and let go.
- **The app** keeps the microphone open (a *keyboard session*) in the background, for 60 minutes after the last
  dictation by default (15/30/60/120, or until ended). Music keeps playing; the iPhone's own microphone listens even
  with AirPods in, so they stay in their good playback mode.
- With no session running, **Talk** explains what is about to happen and offers **Open Dikte**. The app turns the
  microphone on and shows where to go back (◀ at the top left); the keyboard starts listening by itself once it is
  back on screen, so nothing is recorded on the way.
- The two meet in a shared keychain group (the state and the newest transcript) and Darwin notifications (the
  nudges), so no App Group has to be registered by hand. The OpenAI key sits in the app's private keychain group,
  where the keyboard cannot read it. The keyboard needs **Allow Full Access** for the shared group; it sends nothing
  anywhere itself.
- The big button in the app dictates there too; the text is copied.

The settings, API client, WAV, history and stats are `DikteCore` from `../mac`, shared with the Mac app.

## Building

GitHub Actions only (`.github/workflows/ios.yml`): a pull request compiles for the simulator; a merge to main that
touches `ios/` or the core archives with cloud-managed signing and uploads. The version is the Windows project's
`<Version>`, the build number the run number. Secrets: `APPLE_TEAM_ID`, `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8`
(the AKES team's Admin API key, the same one AkanPanel's iOS build uses). `scripts/asc.mjs` registers the App IDs,
revokes the development certificates earlier runs left, and puts builds in front of the internal group.

App IDs: `dev.akan.dikte.ios` and `dev.akan.dikte.ios.keyboard`. The App Store Connect app record itself has to be
made once by hand (Apple's API cannot create apps).
