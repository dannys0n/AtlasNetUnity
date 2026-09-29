---
title: Preview and publish the docs
description: Maintain the Markdown documentation and deploy its static build to GitHub Pages.
---

## Site layout

The website is separate from Unity's runtime/package:

```text
website/
├── docs/                 # Markdown guides and genuine demo screenshots
├── src/css/custom.css    # Visual theme
├── static/               # Public social preview image
├── sidebars.js           # Reading order
├── docusaurus.config.js  # Site URL, Pages base path, navigation
├── package.json
└── pnpm-lock.yaml

.github/workflows/docs.yml
```

The existing package `Documentation~` and `ToDo` design notes remain intact. This site is the current **developer usage guide**; historical plans are not automatically treated as implemented features.

## Local preview

Install Node.js **24 LTS** and the pinned **pnpm 11.25.0** package manager. From the repository root:

```powershell
cd website
pnpm install --frozen-lockfile
pnpm start
```

Open the URL printed by Docusaurus, normally `http://localhost:3000/AtlasNetUnity/`. Markdown changes reload during development.

To test the production build, stop the development server and run:

```powershell
pnpm build
pnpm serve
```

`build` produces static HTML/assets in `website/build`. Internal broken links and anchors fail the build. A successful site build checks the documentation—not Unity gameplay or multi-worker runtime behavior.

Generated build/cache directories and dependency stores are ignored by Git. Dependency install scripts stay opt-in in `pnpm-workspace.yaml`; the current `core-js` postinstall is intentionally disabled.

## Publish on GitHub Pages

The workflow is prepared, but adding it locally does not publish a site.

1. In **AtlasNetUnity → Settings → Pages**, set **Build and deployment → Source** to **GitHub Actions**.
2. Commit and push the documentation/workflow changes to `main`, or merge a documentation pull request.
3. Inspect the **Documentation** Actions run. Pull requests build only; pushes to `main` and manual workflow runs build and deploy.
4. After deployment succeeds, open `https://dannys0n.github.io/AtlasNetUnity/`.

Only documentation/workflow path changes trigger automatic builds. You can also run the workflow manually. It uploads a Pages artifact and deploys with the scoped Pages/OIDC permissions; it does not push a generated `gh-pages` branch.

The config uses `url: 'https://dannys0n.github.io'` and `baseUrl: '/AtlasNetUnity/'`. Update both if the repository moves or you introduce a custom domain.

These deployment steps follow [GitHub's custom Pages workflow documentation](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages). Docusaurus's [deployment guide](https://docusaurus.io/docs/deployment) explains project-site base paths.

## Add or update a guide

1. Edit a Markdown file under `website/docs`, or add one with a `title` and short `description` front matter.
2. Add new pages to `sidebars.js` and use relative `.md` links between documents.
3. Use fenced `csharp` blocks for syntax highlighting. Clearly label fragments versus complete scripts.
4. Check examples against runtime source and the current serialized prefab configuration.
5. Run `pnpm build` and review the page in the local preview before committing.

Prefer small working examples over reproducing all runtime implementation details. Explain defaults, permissions, and the required Inspector setup beside each code sample.

## Screenshots

Current illustrations are actual frames/crops from the user's separate shooter recording. They are labeled as that demo, not the bundled capsule sample. No Inspector images have been fabricated.

For a later pass, capture current Unity Inspectors for `NetworkManager`, `NetworkPrefabsList`, the movement prefab, and runtime authority diagnostics. Crop to the relevant fields, remove secrets/private paths where necessary, store optimized images beside the docs, and give them useful alt text/captions. Avoid committing the large source video into the website; the overview links to the existing YouTube recording.
