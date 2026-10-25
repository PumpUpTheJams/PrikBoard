# Documentation

- [Architecture](architecture.md): how the frontend, the API and the database fit together, plus open decisions
- [API guide](api.md): endpoints, conventions and the typical workflow for AI agents
- [Authentication](authentication.md): sign-in providers (GitHub, Google and other OpenID Connect providers, Discord, Slack, Steam), and Client Credentials tokens and scopes for machines
- [Design: personal access tokens and MCP](design-access-tokens-and-mcp.md): how agent tokens and the `/mcp` endpoint work and what is still planned (OAuth for connectors)
- [Design: Discord link previews](discord.md): one fixed "not signed in" card for PrikBoard links in any chat, and the Discord bot inside the API (set up by an admin while PrikBoard runs) that previews boards, cards and invites in the channels board owners pick
- [Slack integration](slack.md): setting up the Slack app (link previews that follow their cards, board updates, `/prikboard board`, Save to PrikBoard, Sign in with Slack), its design, and how to test it without Slack (`tools/FakeSlack`)
- [Infrastructure](../infra/README.md): hosting and deployment plans
- [Scrum Board — Night shift](Scrum%20Board%20—%20Night%20shift.html): the visual design the web UI follows (open it in a browser)
