# Discord Rich Presence art assets

The three images EDNexus's Discord Rich Presence refers to by key (see `DiscordPresenceMapper`). Each is
1024 x 1024 (Discord's recommended size). They are in the repo, not inside the app: Discord shows images
only from files uploaded to the Discord **application**, so they have to be uploaded once by whoever owns it.

| File | Asset key | Where it shows |
|---|---|---|
| `ednexus_logo.png` | `ednexus_logo` | The large image on every presence (the app icon). The ship is named in its hover text. |
| `docked.png` | `docked` | The small badge while docked. |
| `cruising.png` | `cruising` | The small badge while in flight. |

## Uploading

1. Open the application at <https://discord.com/developers/applications> (id `1557985945393827911`).
2. Left sidebar -> **Rich Presence** -> **Art Assets** -> **Add Image(s)**.
3. Select the three PNGs. The key defaults to the file name without the extension; Discord lower-cases it, so
   leave the names as they are.
4. Click **Save Changes**. New assets can take a few minutes to appear in presence.

If the sidebar entry is not where you expect, Discord moves things around: look under Rich Presence (older
layouts called the page "Rich Presence Assets") and check the keys afterwards match the table.

## Notes

- Earlier the large image was a per-ship key (`federal_corvette`, ...), which only renders if an asset with
  exactly that key was uploaded, and the journal's ship names are not stable enough to pre-generate. The large
  image is now always `ednexus_logo`; per-ship art is not used.
- The images are drawn from `docs/assets/logo/ednexus-icon.svg` and the brand palette; regenerate rather than
  hand-editing if the logo changes.
