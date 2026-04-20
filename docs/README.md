# Jint.Workflows Documentation

VitePress source for the Jint.Workflows documentation site.

## Run locally

```bash
cd docs
npm install
npm run dev
```

Available at `http://localhost:5173/`.

## Build

```bash
npm run build
```

Output: `docs/.vitepress/dist`.

## Structure

- `index.md` — home hero
- `guide/*.md` — guide pages
- `.vitepress/config.ts` — navigation, theme, search

Deployed via `.github/workflows/docs.yml` on tag push.
