# @openflightdisplay/design-tokens

Isolates branding (product name, tagline) and the visual palette/spacing/font-scale tokens used by the tablet PWA, so the project can be renamed or re-themed later without hunting through component code. See `docs/ATTRIBUTION.md` for why this isolation matters (no "FlightWall"-confusable branding anywhere).

## Usage

```ts
import { BRAND, COLOR, SPACING_PX } from "@openflightdisplay/design-tokens";
```

## Scripts

```
npm run build       # tsc -> dist/
npm run typecheck   # tsc --noEmit
```
