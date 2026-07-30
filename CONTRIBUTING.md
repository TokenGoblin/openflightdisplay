# Contributing to OpenFlightDisplay

Thanks for your interest in contributing. This is a hobbyist open-source project — be kind, be patient, and prefer small reviewable pull requests over large ones.

## Before you start

- Read `docs/ARCHITECTURE.md` and `docs/IMPLEMENTATION_PLAN.md` to see what phase the project is in.
- Check `docs/FEATURE_PARITY_MATRIX.md` before building a feature — it may already be tracked with a planned approach.
- If you're adding a new aviation data provider adapter, read `docs/PROVIDER_ADAPTERS.md` first and evaluate licensing/rate-limit/redistribution terms *before* writing code. Document your findings in `docs/DATA_SOURCE_EVALUATION.md`.

## Licensing and originality

- Do not copy code from proprietary or unlicensed reference projects (see `docs/ATTRIBUTION.md`). Studying them for ideas is fine; copy-pasting code is not, even for repos we admire.
- Do not use "FlightWall" or any confusingly similar branding anywhere in code, UI text, or documentation.
- Any dataset or graphic asset you add must have its license recorded in `datasets/licenses/`.

## Project layout

```
firmware/display/  Device firmware, one tree for both boards (PlatformIO, Arduino framework)
apps/tablet-pwa/   React + Vite PWA
services/gateway/  Node/TS gateway (provider polling, normalization, WS/REST)
packages/          Shared TS models and wire protocol used by gateway + PWA
docs/              Architecture, planning, and reference documentation
```

## Development setup

Each component has its own README with setup steps:

- `firmware/display/README.md` (PlatformIO)
- `apps/tablet-pwa/README.md` (Node + Vite)
- `services/gateway/README.md` (Node + Fastify)

## Coding standards

- Strict TypeScript everywhere in `apps/`, `services/`, `packages/`.
- Keep provider-specific logic out of UI components; keep aviation-domain logic independent of hardware APIs.
- Validate all external input at boundaries (Zod schemas in `packages/shared-models`).
- Comment *why*, not *what*. No speculative abstractions.
- Every new configuration field needs a one-line doc comment and a matching entry in `docs/PROTOCOL.md` if it crosses the wire.

## Tests

A pull request should not reduce test coverage for the code it touches. See `docs/TEST_PLAN.md` for what's expected per component. Firmware domain logic must remain testable under PlatformIO's `native` environment (no Arduino/hardware dependency).

## Reporting security issues

See `SECURITY.md`. Do not open a public issue for a security vulnerability.
