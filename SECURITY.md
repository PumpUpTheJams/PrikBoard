# Security policy

## Reporting a vulnerability

Please report security problems privately, never in a public issue, pull request or discussion.

Use GitHub's private vulnerability reporting: on this repository, open **Security**, then **Report a vulnerability**. Only the maintainers see the report.

Include what you can of:

- what an attacker can do, and what they need first (an account, a board role, a token, network access);
- the steps or a request that shows it, and the PrikBoard version (`GET /api/auth/version`);
- whether it affects a self-hosted setup only (for example a reverse proxy or compose change) or PrikBoard itself.

## What to expect

- We acknowledge a report within a few days and keep you posted in the advisory.
- Once it is fixed, the fix ships in a new release and the advisory is published, crediting you unless you'd rather not be named.
- Please give us a reasonable time to fix it before you disclose it elsewhere.

## Supported versions

Fixes go into the latest release only. Self-hosted instances get them by moving to that release (the `latest` image, or the new version tag).

## Scope

In scope: the API, the web app, the VS Code extension, the Discord and Slack integrations, the container image, and the compose files in `infra/`.

Out of scope: problems that need an already compromised server or admin account, denial of service by sheer volume, and setups that ignore the documented requirements (for example publishing the app's port without a reverse proxy, although it trusts `X-Forwarded-*` headers; see [`infra/README.md`](infra/README.md)).
