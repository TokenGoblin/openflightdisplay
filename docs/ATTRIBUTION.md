# Attribution and Licensing of Reference Material

This document records what was consulted while designing OpenFlightDisplay, under what license, and what that means for what we're allowed to do with it. **No code in this repository is copied from any of the projects below.** Everything is an original implementation written against the normalized data model in `docs/ARCHITECTURE.md`.

## Reference projects (architectural inspiration only)

Checked via the GitHub API on 2026-07-24.

| Repository | License (SPDX) | What we may do | What we may NOT do |
|---|---|---|---|
| `AxisNimble/TheFlightWall_OSS` | Apache-2.0 | Read the code for architectural/technical reference (permitted by the license and explicitly invited by the project brief). | Copy its specific UI text, branding, imagery, or product name. It is the open-source companion to a commercial product — we are building a functionally-similar but originally-designed alternative, not a fork. |
| `rzeldent/esp32-flightradar24-ttgo` | **None declared** | Study its general approach to ESP32 + display + flight data for ideas. | Copy any of its source code. Under copyright law, "no license" means all rights reserved by the author — there is no grant to reuse, modify, or redistribute the code. |
| `smartbutnot/flightportal` | **None declared** | Same as above — study only. | Same as above — no code reuse. |
| `zackm-780/aero-display` | **None declared** | Same as above — study only. | Same as above — no code reuse. |
| `ColinWaddell/FlightTracker` | GPL-3.0 | Study its approach. Could technically incorporate GPL-3.0 code, but doing so would require the incorporating module (and, under a strict reading, potentially more of the codebase) to also be GPL-3.0. | We are **not** incorporating any of its code, to keep the whole project under the simpler MIT license without copyleft obligations. |
| `8bither0/whats-that-plane` | MIT | Permissive — could reuse snippets with attribution. | We are writing original code regardless; if a future contributor wants to port a specific MIT-licensed snippet, it must retain the MIT notice and be recorded in this file. |

None of the above repositories' README text, screenshots, icon sets, or product names are reused anywhere in this project.

## Branding

"FlightWall" and confusingly similar names/marks are not used anywhere in this project's name, code, UI copy, or documentation. The working name is **OpenFlightDisplay** (see root `README.md` for naming rationale and how to rebrand later — branding is isolated in `packages/design-tokens` and a small set of copy strings, not scattered through the codebase).

## Aviation data providers

See `docs/DATA_SOURCE_EVALUATION.md` for the full evaluation. Summary of terms that constrain usage:

| Provider | License / terms | Attribution required? |
|---|---|---|
| adsb.lol | Open Database License (ODbL) 1.0 | Yes — ODbL requires attribution and share-alike for the *database*; our use (querying live positions for display) is standard API consumption, but any redistributed derivative dataset must credit adsb.lol and remain ODbL. |
| airplanes.live | Custom ToS: educational / non-commercial / personal use only | Recommended, not contractually spelled out in the excerpt reviewed; credit airplanes.live in the app's data-source indicator regardless. |
| OpenSky Network | Custom ToS | Yes — required by their terms whenever their data is used. |
| ADS-B Exchange | Community API: non-commercial; full API is a paid RapidAPI subscription | Per their terms of use, reviewed at implementation time for whichever tier is configured. |

OpenFlightDisplay always displays which provider is currently active and its data age in the UI (Core2 and PWA), satisfying attribution and giving users visibility into data provenance.

## Map data (tablet PWA)

Phase 1 uses raster OpenStreetMap tiles via Leaflet. OpenStreetMap data is © OpenStreetMap contributors, licensed under the Open Database License — the standard OSM attribution string must remain visible on the map (Leaflet's default attribution control is not removed).

## Datasets and icon assets

Any airline/aircraft-type/airport enrichment dataset or icon set added in later phases must have its own license file recorded under `datasets/licenses/` before it is used, per `docs/DATA_SOURCE_EVALUATION.md` and `CONTRIBUTING.md`. None are bundled yet in Phase 1.
