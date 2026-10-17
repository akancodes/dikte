# Dikte for iPhone

Not published: it is built for our own phones and goes only to TestFlight's internal group ("Us"), exported with
*TestFlight internal testing only*, so the build cannot be sent for review or to external testers.

## How it works

iOS never gives a keyboard the microphone, so the work is split, the way other dictation keyboards do it:

- **The app** opens a *keyboard session*: the microphone stays open in the background (the orange dot shows) for
  15 minutes after the last dictation (5/15/30/60 in Settings). Music keeps playing; the iPhone's own microphone
  listens even with AirPods in, so they stay in their good playback mode.
- **The keyboard** has one big button: tap to start and tap to send, or hold to talk and let go to send. It asks the
  app to listen, and types the text when it comes back. **Last** types the previous dictation again.
- With no session running, the keyboard's button opens the app, which starts one and listens at once; tap ◀ at the
  top left to go back, talk, and tap the button.
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
