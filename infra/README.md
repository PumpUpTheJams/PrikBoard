# Infrastructure

Nothing is deployed yet. Local development needs no infrastructure: the API stores its data in PostgreSQL from the root `compose.yaml` and the Vite dev server proxies API calls.

This folder will hold everything needed to run PrikBoard somewhere other than a laptop:

- **Container image.** A Dockerfile that builds the React app and serves it from the API's `wwwroot`, so one container hosts everything.
- **Infrastructure as code.** Bicep or Terraform for the hosting platform, database and secrets.
- **Environment configuration.** Per-environment settings such as the connection string (`ConnectionStrings__PrikBoard`), sign-in provider client ids and secrets (`Auth__Providers__{id}__ClientId`, `...__ClientSecret`), and trusted token issuers (`Auth__Tokens__Issuers__{id}__Authority`).
- **Persistent Data Protection keys.** Session cookies and sign-ins are encrypted with them, so they must survive restarts and be shared between instances. See [Authentication](../docs/authentication.md#before-deploying).

The CI workflow lives in [`.github/workflows`](../.github/workflows), because GitHub requires that location.

## Open decisions

- Hosting platform, for example Azure Container Apps or App Service.
- Whether to run PostgreSQL in a container with a persistent volume or use a managed PostgreSQL service.
- Which sign-in providers to enable, and whether to restrict access with `Auth:AllowedEmails`/`AllowedEmailDomains`. Without an allowlist, anyone with an account at a configured provider can sign in and see every board.
