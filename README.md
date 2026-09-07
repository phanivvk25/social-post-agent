# Social Post Agent

A .NET-first agent that generates AI copy and graphics from a brief, runs them through
a configurable guardrail engine, and publishes to a single brand's **Facebook Page**,
**Instagram Business account**, and **LinkedIn Company Page** — either with a human
approval step (**HITL**) or fully autonomously, per post or globally.

This repository is the backend (API + orchestration + persistence). The React review
dashboard discussed alongside this design is a separate, not-yet-built piece — see
[What's not built yet](#whats-not-built-yet).

## Architecture

```
React Dashboard (separate project, not in this repo)
        │  REST / JSON
        ▼
ASP.NET Core Web API  (SocialPostAgent.Api)
        │
        ▼
Semantic Kernel Orchestrator  (SocialPostAgent.Application/Orchestration)
        │
        ├─► Content Generation Service ──► Anthropic API
        ├─► Graphics Generation Service ──► Image-gen API
        ├─► Guardrail Engine  (format · brand-safety · image · duplicate · operational)
        ├─► Scheduler (Hangfire)  ──► Facebook / Instagram / LinkedIn publishers
        │                                       │
        └──────────────► PostgreSQL (EF Core) ◄─┘
```

Guardrails run the same way regardless of mode. The only difference is the consequence:
in **Autonomous** mode a blocking failure routes the post back to **Pending Review**
instead of publishing it; in **HITL** mode every post goes to Pending Review regardless,
with the guardrail findings shown alongside it.

## Folder structure

```
src/
  SocialPostAgent.Domain/          Entities, enums, generic repository contracts — no framework dependencies
    Entities/                      Post, PlatformResult, GuardrailLog, PlatformCredential
    Repositories/                  IRepository<T>, IUnitOfWork

  SocialPostAgent.Infrastructure/  EF Core, encryption, scheduling — all technology-specific code
    Persistence/                   DbContext, IEntityTypeConfiguration<T> classes, generic Repository<T>, UnitOfWork
    Security/                      Data-Protection-backed credential encryption
    Scheduling/                    Hangfire implementation of IPostScheduler

  SocialPostAgent.Application/     Business logic — depends only on Domain
    Guardrails/                    Every guardrail rule + the engine that runs them
    Platforms/                     One publisher per platform (Facebook, Instagram, LinkedIn)
    Generation/                    Content (LLM) and graphics (image-gen) generation services
    Orchestration/                 The pipeline itself + its Semantic Kernel plugin
    DTOs/                          API request/response shapes

  SocialPostAgent.Api/             ASP.NET Core host — composition root
    Controllers/                   PostsController (compose / list / detail / approve / reject)
    Program.cs                     DI wiring, Hangfire, Swagger, Serilog

docker-compose.yml                 Local PostgreSQL for development
```

## Design choices worth knowing about

- **Generic repository, not one per entity.** `IRepository<TEntity>` (Domain) and
  `Repository<TEntity>` (Infrastructure) work for every entity that derives from
  `BaseEntity`. Adding a new aggregate later needs a new `IEntityTypeConfiguration<T>`
  and a new accessor on `IUnitOfWork` — nothing else.
- **`IEntityTypeConfiguration<T>` for every entity.** All four live in one file,
  `Infrastructure/Persistence/Configurations/EntityConfigurations.cs`, organized with
  `#region` blocks — `OnModelCreating` just calls `ApplyConfigurationsFromAssembly` and
  never touches a property directly.
- **No EF Core Migrations, on purpose, for now.** The schema is still moving during
  local development, so `Program.cs` calls `EnsureCreatedAsync()` on every startup
  instead. This is a deliberate simplification, not an oversight — **switch to a real
  migrations workflow (`dotnet ef migrations add InitialCreate`) before this ever runs
  against a database with real data you can't afford to lose**, because
  `EnsureCreated` cannot evolve an existing schema.
- **Semantic Kernel as a function-calling surface, not a planner.** Which platforms to
  publish to is a deterministic list (`Post.TargetPlatforms`), not something that needs
  LLM reasoning, so `SocialPublishingPlugin` exposes each platform publisher as a
  `[KernelFunction]` and the orchestrator invokes them directly. This keeps the pipeline
  auditable — a failed LinkedIn publish is just a failed function call.
- **Encrypted credentials.** `PlatformCredential` never stores a raw token —
  `ICredentialProtector` (backed by ASP.NET Core Data Protection) encrypts it going in
  and decrypts it only at the moment a publisher needs it.
- **File sizes.** Related, small classes (all the guardrail rules; all four entity
  configurations) are deliberately kept in one file each rather than scattered across
  many tiny files, so a reviewer can read a whole concern top-to-bottom. Trivial data
  classes (entities, DTOs) were kept small rather than padded — inflating a five-property
  record to hit a line count would hurt readability for no benefit.

## Prerequisites

- .NET 8 SDK
- Docker (for local PostgreSQL) — or a PostgreSQL 16 instance of your own
- API keys: an Anthropic API key (content generation), an image-generation API key
  (OpenAI by default), a Meta App with Page + Instagram Graph API permissions, and a
  LinkedIn App approved for the Community Management API. None of these are needed to
  build the solution — only to actually publish or generate content.

## Running locally

```bash
# 1. Start Postgres
docker compose up -d

# 2. Restore and build
dotnet restore
dotnet build

# 3. Configure secrets (don't put real keys in appsettings.json)
cd src/SocialPostAgent.Api
dotnet user-secrets init
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
dotnet user-secrets set "ImageGeneration:ApiKey" "sk-..."
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=social_post_agent;Username=postgres;Password=CHANGE_ME"

# 4. Run
dotnet run
```

The API comes up with Swagger UI at `/swagger` and the Hangfire dashboard at `/hangfire`.
The database schema is created automatically on first run (see [No EF Core Migrations](#design-choices-worth-knowing-about) above).

## Setting up platform credentials for a test post

There's no credentials-management UI yet, so tokens go in via `dotnet user-secrets` and
get encrypted into the database automatically on startup (`PlatformCredentialSeeder`,
called from `Program.cs`). **Never put real tokens in `appsettings.json` — user-secrets
only**, since that file lives outside the repo.

```bash
cd src/SocialPostAgent.Api
dotnet user-secrets set "PlatformCredentials:Facebook:AccessToken" "<page access token>"
dotnet user-secrets set "PlatformCredentials:Facebook:AccountId" "<facebook page id>"

dotnet user-secrets set "PlatformCredentials:Instagram:AccessToken" "<same page access token works — Instagram rides on the Graph API>"
dotnet user-secrets set "PlatformCredentials:Instagram:AccountId" "<instagram business account id>"

dotnet user-secrets set "PlatformCredentials:LinkedIn:AccessToken" "<linkedin access token>"
dotnet user-secrets set "PlatformCredentials:LinkedIn:AccountId" "<linkedin organization id>"
```

Restart the app (`dotnet run`) and the seeder upserts a `PlatformCredential` row for
each platform that has a token configured. Only set the ones you're actually testing —
a platform with no token configured is left alone and just fails to publish.

### Where each token actually comes from

**Facebook Page + Instagram** (same Meta App, since Instagram publishing rides on the
Graph API):
1. Create an app at [developers.facebook.com](https://developers.facebook.com) and add
   the **Facebook Login** and **Instagram Graph API** products.
2. In **Graph API Explorer**, select your app, request the `pages_manage_posts` and
   `pages_read_engagement` permissions, and generate a User Access Token.
3. Call `GET /me/accounts` with that token to get a **Page Access Token** — that's the
   value for `PlatformCredentials:Facebook:AccessToken`, and the Page's `id` field is
   `AccountId`.
4. Exchange it for a **long-lived token** (~60 days) via the
   `oauth/access_token?grant_type=fb_exchange_token` endpoint — short-lived tokens expire
   in about an hour.
5. For Instagram, the Page must have a linked **Instagram Business or Creator account**.
   Call `GET /{page-id}?fields=instagram_business_account` with the Page token to get the
   Instagram account ID — that's `PlatformCredentials:Instagram:AccountId`. The access
   token is the same Page token from step 3.
6. This requires **Meta App Review** for these permissions before it works against a
   Page you don't personally administer — for testing against your own Page/account in
   development mode, no review is needed.

**LinkedIn Company Page:**
1. Create an app at [developer.linkedin.com](https://www.linkedin.com/developers/apps).
2. Request access to the **Community Management API** product — this requires LinkedIn's
   approval and can take days; start this early.
3. Once approved, complete the OAuth 2.0 3-legged flow as an admin of the target Company
   Page, requesting the `w_organization_social` scope, to get an access token.
4. `PlatformCredentials:LinkedIn:AccountId` is the numeric organization ID (visible in
   the Page admin URL) — the publisher accepts either the bare number or the full
   `urn:li:organization:<id>` form.

### Simulating a test post without spending on real generation APIs

`ComposeAsync` calls the Anthropic and image-generation APIs for every post, so an empty
`Anthropic:ApiKey`/`ImageGeneration:ApiKey` will fail generation before guardrails or
publishing ever run. To test the *publishing* path in isolation without those keys, call
`PublishScheduledPostAsync` directly against a manually-inserted `Post` row (with
captions and `TargetPlatforms` already set) instead of going through `POST /api/posts`.

## API endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/posts` | Generate a post from a brief; routes it to review or scheduling per its mode |
| `GET` | `/api/posts?status=PendingReview` | List posts, optionally filtered by status |
| `GET` | `/api/posts/{id}` | Full detail: captions, image, guardrail findings, publish results |
| `POST` | `/api/posts/{id}/approve` | Approve a Pending Review post, optionally scheduling it |
| `POST` | `/api/posts/{id}/reject` | Reject a Pending Review post |

## Guardrails

Configured under the `Guardrails` section in `appsettings.json`:

| Category | Rules | Severity |
|---|---|---|
| Format / mechanical | caption length per platform, Instagram requires media, hashtag count | Block / Warn |
| Brand-safety | banned words, sensitive topics, near-duplicate content | Block / Warn |
| Operational | daily post cap, circuit breaker (repeated publish failures), kill switch | Block |

Every rule writes a `GuardrailLog` row regardless of outcome, so the dashboard can show
*why* a post was flagged, not just that it was.

## What's not built yet

- **The React dashboard.** This backend exposes everything it needs (compose, review
  queue, approve/reject, guardrail findings, publish results) but the UI itself is a
  separate project.
- **A credentials management UI/endpoint.** Tokens go in via user-secrets and an
  automatic startup seeder (see [Setting up platform credentials](#setting-up-platform-credentials-for-a-test-post))
  rather than a proper admin flow with token refresh and expiry handling.
- **Real image post-processing** (brand overlay, per-platform aspect-ratio cropping) —
  `GraphicsGenerationService` calls the image-gen API and saves the result as-is.
- **Automated tests.**
- **EF Core Migrations** — required before this touches any database with real data.
- **Public image hosting for Instagram.** Instagram's publish API requires the generated
  image to be reachable at a public URL; running locally, that means a tunnel (ngrok,
  Cloudflare Tunnel) pointed at the API's `wwwroot/generated-images` folder, or swapping
  `GraphicsGenerationService` to upload to real object storage (S3, Cloudflare R2) before
  deployment.
