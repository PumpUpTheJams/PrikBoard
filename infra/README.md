# Infrastructure

Nothing is deployed yet. Local development needs no infrastructure: the API stores its data in a SQLite file and the Vite dev server proxies API calls.

This folder will hold everything needed to run PrikBoard somewhere other than a laptop:

- **Container image.** A Dockerfile that builds the React app and serves it from the API's `wwwroot`, so one container hosts everything.
- **Infrastructure as code.** Bicep or Terraform for the hosting platform, database and secrets.
- **Environment configuration.** Per-environment settings such as the connection string (`ConnectionStrings__PrikBoard`) and API keys.

The CI workflow lives in [`.github/workflows`](../.github/workflows), because GitHub requires that location.

## Open decisions

- Hosting platform, for example Azure Container Apps or App Service.
- Whether to stay on SQLite on a persistent volume or move to a managed database. EF Core makes switching providers a small change.
- Authentication for both people and AI agents. The API is currently open and must not be exposed publicly until this is in place.
