# Twitch extension listing assets

Images for the extension's listing in the Twitch developer console (the "Assets" / listing step; see the
submission checklist in [`extension/README.md`](../../extension/README.md)). They are deliberately outside
`extension/` so they are never part of the uploaded extension zip.

| File | Size | Console field |
|---|---|---|
| `extension-icon-100x100.png` | 100 x 100 | Extension icon |
| `discovery-image-300x200.png` | 300 x 200 | Discovery image |
| `screenshot-1024x768.png` | 1024 x 768 | Screenshot |

Sizes follow Twitch's listing requirements as last checked; confirm them in the console when uploading
(verify in console), since Twitch changes these occasionally.

## Notes

- **Icon and discovery image** use the EDNexus mark and the brand palette from the design system
  (`extension/css/tokens.css`). The icon is an opaque square; Twitch applies its own rounding.
- **Screenshot** is the real overlay (`extension/video_overlay.html`) rendered in its local mock mode
  (`?mock=1`, see `extension/README.md`) with the bundled sample payload, panel opened, over an illustrative
  space backdrop standing in for a stream. The commander shown ("Vega") is fictional sample data. Twitch
  reviewers may prefer a screenshot taken from a real test channel while the desktop app publishes (developer
  mode can drive the card); replace this file if so.
- Re-generate the screenshot by serving `extension/` locally, opening
  `http://127.0.0.1:8931/video_overlay.html?mock=1` in a 1024 x 768 window and opening the panel.
