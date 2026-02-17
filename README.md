# EveDeck (Next.js)

Fresh Next.js rebuild of https://evedeck.space that keeps the WonderCMS visual style while using modern app routing.

## Product

EveDeck is an EVE-O Preview-like .NET application specifically made to provide EVE-O Preview features for tablets using [Spacedesk](https://www.spacedesk.net/) to simulate a monitor.

## Routes

- `/home`
- `/features`
- `/releases`
- `/readme`
- `/legal-notice`

## Local development

```bash
npm ci
npm run dev
```

Dev server runs on `http://localhost:3006`.

## Deploy notes

- Reverse proxy target: `127.0.0.1:3006`
- PM2 app config: `deploy/ecosystem.config.cjs`

Deploy with existing scripts:

```bash
env -u REMOTE_PATH bash deploy/deploy.sh --dry-run
env -u REMOTE_PATH bash deploy/deploy.sh
```
