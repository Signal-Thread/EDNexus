# Exobiology reference data — sources and licensing

Where every number in `Exobio/` came from, what licence (if any) covers that source, and what is
still unverified. Only `*.json` in this folder is embedded (`EDNexus.Core.csproj`), so this file
does not ship.

Last reviewed: **2026-11-01** (sample distances and spawn rules; payout notes from 2026-09-14). Not legal advice — have the licensing position reviewed before release.

## Why this matters for EDNexus specifically

EDNexus's repository is public but has **no licence file**, and packaging declares it proprietary
(`packaging/flatpak/io.github.Signal_Thread_LLC.EDNexus.metainfo.xml`:
`<project_license>LicenseRef-proprietary</project_license>`, with a TODO to set the real licence).
A proprietary product cannot incorporate code or data files distributed under GPL/AGPL. So:

- **Never copy** files, tables or structure from the copyleft projects listed below, and never
  translate their code.
- Facts about the game (a species' payout, "grows only on icy bodies") are not copyrightable in
  themselves, but a wholesale extraction from one compiled source is weaker ground — especially in
  the EU/UK, where a separate database right exists. Prefer primary research, Frontier's own data,
  or our own derivation from observations, and cite them here.

## Data in this repository

| Data | File | Source | Licence of source | Status |
|---|---|---|---|---|
| Species list and codex symbols | `exobiology-species.json` | Journal codex symbols (`ScanOrganic`, `SAASignalsFound`) — Frontier game data | Game data, © Frontier Developments | OK — symbols are what the game emits |
| Vista Genomics payouts | `exobiology-species.json` | [EDMC-BioScan](https://github.com/Silarn/EDMC-BioScan), cross-checked against [StratumFinder](https://github.com/lynnel1/StratumFinder); two conflicts settled against [ed-dsn.net](https://ed-dsn.net/en/vista-genomics-values/) (see PR #107) | BioScan **GPL-2.0**; StratumFinder **AGPL-3.0**; ed-dsn.net no terms stated, and says its list is "taken from the list from Cannon with their autorization" | **Partly re-verified.** Payouts are Frontier's figures (facts), but the first source was a GPL project. On 2026-09-14, the `Value` in the maintainer's own `SellOrganicData` events matched the catalog exactly for all **24 species sold** (of 118). The other 94 still rest on the original sources. |
| Genus sample distance (`sampleDistance`) | `exobiology-species.json` | Re-checked 2026-11-01 against the Elite Dangerous Wiki (Fandom) page [Exobiology Sample Values and Details](https://elite-dangerous.fandom.com/wiki/Exobiology_Sample_Values_and_Details) (revision 170549, last edited 2026-06-21), which states a "colony range" per genus. Retrieved via the MediaWiki API (`api.php?action=parse&page=Exobiology_Sample_Values_and_Details&prop=wikitext`). | Fandom community content, CC BY-SA by default; only the numeric facts are used, nothing is copied. | **21 of 22 genera confirmed, values unchanged** (Aleoida 150, Amphora Plant 100, Anemone 100, Bacterium 500, Bark Mound 100, Brain Tree 100, Cactoida 300, Clypeus 150, Concha 150, Crystalline Shards 100, Electricae 1000, Fonticulua 500, Frutexa 150, Fumerola 100, Fungoida 300, Osseus 800, Recepta 150, Sinuous Tubers 100, Stratum 500, Tubus 800, Tussock 200). Pinned by `ExobiologySampleDistanceTests`. **Radicoida (100) is still UNCONFIRMED**: the wiki's Radicoida page says "clonal colony range of ? m", so no source supports our 100. A community wiki is a secondary source; an in-game codex screenshot or a Canonn publication would be the primary one. |
| Spawn rules | `../ExobiologyPredictionRules.cs` | Community Odyssey biology research, written from general knowledge while implementing issue #96. Not copied from any file. | — | **Mostly unverified; partial check 2026-11-01** (see "Spawn-rule check" below). No rule was changed: the only source found is a secondary community wiki, and it disagrees with the rules in one place that our own journals cannot settle. |

Validation so far: replaying the maintainer's local journals, physics-only prediction included
53/58 DSS-confirmed genera and 45/51 sampled species. Every miss was Brain Tree, which the rules
deliberately never predict without a DSS scan.

## Spawn-rule check (2026-11-01)

Compared `ExobiologyPredictionRules` with the per-genus statements on the Elite Dangerous Wiki page above
(secondary, community-edited), and with the maintainer's own journals (47 files; `Scan` body physics
joined to completed `ScanOrganic` samples on the same body; local analysis only, no journal data committed).

| Genus | Wiki statement | Our rule | Result |
|---|---|---|---|
| Aleoida | Rocky/High Metal Content, max gravity 0.27 | Rocky family, g <= 0.27 | Agrees (our rule also admits metal-rich: permissive by design) |
| Clypeus | Rocky/HMC, > 190 K, water or CO2, g <= 0.27 | same | Agrees |
| Electricae | Icy, helium/neon/argon, g <= 0.27 | same | Agrees |
| Fumerola | needs volcanism | `RequiresVolcanism` | Agrees |
| Recepta | "sulphur dioxide ... gravity less than 0.27" | **CO2 or SO2**, g <= 0.27 | **Disagrees on CO2.** Not changed: one wiki sentence is not solid evidence, and the permissive rule only costs a spurious candidate. No Recepta sample exists in the local journals to settle it. Needs an observation or a Canonn citation. |
| Brain Tree, Bark Mound, Amphora Plant, Anemone | star/nebula/neighbour dependent | never predicted from physics | Consistent (wiki confirms they depend on system context) |
| All others | no conditions stated beyond colony range / terrain | per rules file | **Unverified** |

Observed in the local journals: 57 completed samples (`ScanType` Analyse) across 10 genera (Bacterium,
Brain Tree, Cactoida, Fonticulua, Frutexa, Fungoida, Osseus, Stratum, Tubus, Tussock). Checked by eye on
planet class and atmosphere only (not gravity, temperature or species), the 9 non-Brain-Tree genera all sat
on a class/atmosphere combination their rule allows; Brain Tree grew on airless bodies, and the rules never
predict it. That is consistent with the rules but far too few to call them verified (it cannot detect an over-permissive rule). Species temperature bands, the 175-270 K Aleoida
window, Tubus' 160-190 K / 0.15 g, and Stratum's 165 K floor are **unconfirmed** by any source found.

Payout note: the same wiki shows Amphora Plant at 3,626,400 and Concha Biconcavis at 16,777,215 (both
flagged in italics as doubtful on the wiki), against 1,628,800 and 19,010,800 here. Not changed; the
catalog values are the ones to re-verify against `SellOrganicData` (open action 1).

Not reachable on 2026-11-01 (so not used): canonn.science (no response from this environment), EDSM web
pages (Cloudflare challenge), Frontier forums (search is JavaScript-only). Canonn's in-game-confirmed
tables remain the better primary source if access can be arranged.

## POI data (EDAstro Galactic Exploration Catalog)

`ExobiologyPoiService` / `EdAstroClient.GetPointsOfInterestAsync` read `https://edastro.com/gec/json/all`
(the documented GEC API on [edastro.com/api-details.html](https://edastro.com/api-details.html)).
EDSM serves the same data shape at `https://www.edsm.net/en/galactic-mapping/json`; a single `curl` with a
plain User-Agent on 2026-11-01 got `HTTP 403` with `Cf-Mitigated: challenge` (Cloudflare) from that URL,
whereas EDAstro answered `200 application/json` with `RateLimit-Limit: 100`. That is one probe from one
network; it says nothing about the `api-v1/*` endpoints `EdsmClient` already uses, which were not re-tested.

Terms found: the API is rate limited (documented 100 requests per 15 minutes); site content is
**CC BY-NC-SA 3.0** (footer of edastro.com/gec/).

**OPEN OWNER DECISION - licence.** BY-NC-SA requires attribution, non-commercial use, and ShareAlike on
adaptations; EDNexus is packaged `LicenseRef-proprietary`. Fetching and caching a user's own copy on their
disk is not redistribution, but displaying it in a shipped product, or bundling any of it, is the question.
Not decided here. Until it is, the data is opt-in, never bundled or committed, stored trimmed (summaries
capped at 300 characters, descriptions and images dropped), and the code exposes
`ExobiologyPoiService.AttributionText` / `AttributionUrl` that any UI must show. Alternatives for the
owner: obtain EDAstro's permission, show only a link to the POI page, or drop the feature.
Related: the Radicoida 100 m value and the wiki revision / journal analysis above are not reproducible from
the repo (no script committed); treat them as maintainer notes, not verifiable provenance.

## Candidate sources for future work

Researched 2026-09-14 for system-context rules (star class, Guardian zones, nebulae, regions).

| Source | What it offers | Licence / terms found | Usable? |
|---|---|---|---|
| **Our own journals** (`FSDJump`/`Location` `StarPos`, star `Scan` `StarType`, `Parents`, `DistanceFromArrivalLS`, `SAASignalsFound` `Genuses`, `ScanOrganic`, `CodexEntry` `Region`) | System coordinates, star classes, system bodies, confirmed genera/species, codex region when an entry fires | The player's own game output | **Yes** — primary source for runtime context and for validating rules |
| [klightspeed/EliteDangerousRegionMap](https://github.com/klightspeed/EliteDangerousRegionMap) | `findRegion(x, y, z)` → codex region; ships `RegionMapData.json` (~240 KB) and a C# implementation | **MIT** (© 2020 Ben Peddell); README: "Please feel free to use and modify any of these implementations" | **Yes**, with the MIT notice included in third-party notices |
| [Spansh galaxy dumps](https://spansh.co.uk/dumps) (`galaxy.json.gz`, schema at `docs.spansh.co.uk/galaxy.schema.json`) | Every known body's physics, plus `signals.genuses` (genus level only — no species) | **No licence or terms found** on the dumps page or schema | **Not yet.** Would allow deriving genus rules from observations; ask Spansh for permission first. Species-level rules would still need another source. |
| [EDSM nightly dumps](https://www.edsm.net/en/nightly-dumps) | Systems and bodies | **Could not check** — the page returned HTTP 403 to automated fetches | Unknown — check manually |
| Canonn biostats (`/codex/biostats` in [Canonn-GCloud](https://github.com/canonn-science/Canonn-GCloud), backed by a Google Drive JSON) | Observed per-species min/max conditions from EDMC-Canonn submissions | Code is **GPL-3.0**; the data has **no licence stated**. The endpoint returned `{"error": "no spansh data"}` on 2026-09-14. | **Not without permission.** This is Canonn's compiled observation database — ask Canonn. |
| [Canonn research site](https://canonn.science/) ([research policy](https://canonn.science/research-policy/), [guidelines](https://canonn.science/website-guidelines/)) | Published findings (spawn conditions, Guardian site locations) | No data licence. Research policy says results should be "open to all" and posted to the forum. Site states it uses Frontier assets "with the permission of Frontier Developments plc for non-commercial purposes" (that covers the site's assets, not reuse of findings). | **Cite findings as facts**; don't bulk-copy tables. Contact: `webadmin@canonn.science`. |
| [EDMC-BioScan](https://github.com/Silarn/EDMC-BioScan) | Complete rulesets, Guardian zones, tuber zones, nebula reference stars | **GPL-2.0** | **No** — do not copy or port. Useful only to point at the original research it cites. |
| [StratumFinder](https://github.com/lynnel1/StratumFinder) | Payout list | **AGPL-3.0** | **No** |
| [ed-dsn.net Vista Genomics values](https://ed-dsn.net/en/vista-genomics-values/) | Payout table sourced from Canonn | No terms stated | Cross-check only |

## Open actions

1. Re-verify the remaining 94 payouts in `exobiology-species.json` against `SellOrganicData` from
   real journals (24 of 118 done), and replace the BioScan/StratumFinder provenance above.
2. Confirm Radicoida's `sampleDistance` (100 is unsupported) and replace the secondary wiki citation for the other 21 with a primary one (in-game codex or Canonn).
3. Before extending the rules with system context, either get written permission from Spansh or
   Canonn to derive rules from their data, or derive them from journals plus cited published
   research. Record whichever is chosen here.
4. If the region map is adopted, add its MIT notice to the app's third-party notices.
5. Settle EDNexus's own licence (the packaging TODO). It decides how strict items 1–3 must be.
