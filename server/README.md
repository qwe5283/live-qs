# LiveQs Server

Express 5 and MongoDB API for the LiveQs V1 protocol defined in `contracts/openapi.yaml`. Windows and Android collectors upload through device tokens; the Owner administers the system through a revocable session cookie.

## Local development

Requirements: Node.js 20+ and MongoDB 7+.

```powershell
docker compose up -d mongo
Copy-Item .env.example .env
npm install
npm run dev
```

Replace all placeholder secrets in `.env`. `HASH_SECRET` must contain at least 32 characters. Device credentials use `token:device-id:device name:platform`; add more devices as `DEVICE_TOKEN_2`, `DEVICE_TOKEN_3`, and so on.

The API listens on `HOST:PORT` (default `0.0.0.0:8787`). `GET /health` is public.

- `POST /api/v1/owner/setup` creates the single Owner password on an uninitialized instance (no username).
- `POST /api/v1/owner/login` establishes a revocable HttpOnly, SameSite session in the `liveqs_session` cookie.
- Query and administration routes require that Owner session; ingestion routes require a configured device token.
- `CORS_ORIGINS` is an explicit comma-separated origin allowlist; `COOKIE_SECURE=true` switches the session cookie to the future HTTPS policy.

## Checks

```powershell
npm run typecheck
npm test
npm run build
```

The Owner login tests in `test/owner-auth.test.ts` run against a real MongoDB at `mongodb://127.0.0.1:27017/live_qs_test` (start it with `docker compose up -d mongo`; they skip with a warning when it is unreachable). MongoDB collections and indexes are created by Mongoose. Event documents store `data` as a nested object, making fields queryable without parsing JSON strings.
