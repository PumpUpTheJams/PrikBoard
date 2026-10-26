# Contributing to PrikBoard

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Before you start

- **Bugs and ideas:** open an issue. For anything bigger than a small fix, discuss it in an issue first, so the work fits where PrikBoard is going.
- **Security problems:** don't open an issue; follow [`SECURITY.md`](SECURITY.md).

## Running it locally

The [README's *Developing* section](README.md#developing) has the full steps. In short, with the .NET 10 SDK, Node.js 22+ and Docker:

```bash
docker compose up -d                            # PostgreSQL for development and tests
dotnet run --project src/PrikBoard.Api          # API on http://localhost:5080
cd src/web && npm install && npm run dev        # web app on http://localhost:5173
```

[`CLAUDE.md`](CLAUDE.md) and [`docs/architecture.md`](docs/architecture.md) explain how the code is organized and the rules it follows: the words to use (boards, buckets, cards, components), REST and MCP parity, the server owning the geometry, and more.

## Checks

CI runs these on every pull request. Run the ones for the parts you changed before you push:

```bash
dotnet test --solution PrikBoard.slnx           # API tests; need the compose database running
cd src/web && npm run lint && npm run build     # web app
cd src/vscode && npm run lint && npm run build  # VS Code extension
```

CI also scans the whole history for secrets (gitleaks), so never commit tokens, keys or passwords, not even ones you remove again later.

## Pull requests

- Branch off `main` and keep a pull request to one change.
- Add or update tests for API changes. The web app has no test suite, so describe how you checked a UI change.
- When you change an endpoint or contract, update `src/web/src/api/types.ts`, `src/web/src/api/client.ts` and the endpoint table in [`docs/api.md`](docs/api.md) too.
- Explain what changed and why in the description.

## License of contributions

PrikBoard is licensed under the [MIT license with the Commons Clause](LICENSE). By submitting a contribution, you agree that it is licensed under the same terms.
