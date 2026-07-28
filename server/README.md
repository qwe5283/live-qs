# LiveQs Server

Express 5 and MongoDB API for the LiveQs web, Android, and Windows clients. It preserves the routes and JSON contracts from `server_deprecated/` while replacing Bun and SQLite with Node.js and Mongoose.

## Local development

Requirements: Node.js 20+ and MongoDB 7+.

```powershell
docker compose up -d mongo
Copy-Item .env.example .env
npm install
npm run dev
```

Replace all placeholder secrets in `.env`. `HASH_SECRET` must contain at least 32 characters. Device credentials use `token:device-id:device name:platform`; add more devices as `DEVICE_TOKEN_2`, `DEVICE_TOKEN_3`, and so on.

The API listens on `http://localhost:8787`. `GET /health` is public. Query and administration routes require `Authorization: Bearer <USER_TOKEN>`; ingestion routes require a configured device token.

## Checks

```powershell
npm run typecheck
npm test
npm run build
```

MongoDB collections and indexes are created by Mongoose. Event documents store `data` as a nested object, making fields queryable without parsing JSON strings.
