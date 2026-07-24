# Security Policy

OpenFlightDisplay is a local-first home/hobby project, but it does handle a user's approximate home location, Wi-Fi credentials (on the Core2 during provisioning), and potentially third-party API keys (on the gateway). Please report security issues responsibly.

## Reporting a vulnerability

- Do **not** open a public GitHub issue for a suspected vulnerability.
- Open a private security advisory via the repository's "Security" tab ("Report a vulnerability"), or contact the maintainer listed in the repository metadata directly.
- Include: affected component (firmware / PWA / gateway), reproduction steps, and potential impact.
- We aim to acknowledge reports within 7 days. This is a volunteer-maintained project — fix timelines depend on severity and maintainer availability.

## Scope

In scope:
- The Core2 firmware's provisioning, pairing, and configuration-write flows.
- The gateway's REST/WebSocket endpoints and provider-adapter credential handling.
- The tablet PWA's handling of geolocation, stored configuration, and any pairing tokens.

Out of scope:
- Vulnerabilities in third-party data providers (adsb.lol, airplanes.live, OpenSky, ADS-B Exchange, etc.) — report those directly to the provider.
- Vulnerabilities requiring physical access to an already-compromised home network.

## Supported versions

This project is pre-1.0. Only the `main` branch receives security fixes until a stable release process exists (a `docs/RELEASE_PROCESS.md` will document it once Phase 5 defines one).

See `docs/SECURITY_AND_PRIVACY.md` for the full threat model, data-handling policy, and dependency-update policy.
