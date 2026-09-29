# AtlasNet Unity

An early Unity networking framework with a local multi-worker backend for testing authority, interest, and handoff. The project targets Unity 6000.6.2f1.

## Install with Unity Package Manager

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Select **AtlasNet Unity** in Package Manager and import **Simple Authority Demo** from its Samples section if you want the example scenes. The package provides the framework runtime; the sample scenes are optional.

Install Unity's Multiplayer Play Mode package to test with multiple editor players, or use separate builds. Multiplayer Play Mode is a testing tool, not a dependency of the AtlasNet package.

The sample includes client- and server-authoritative player scenes with local worker handoff, a scale scene, and a separate Rigidbody check. The local backend is not the production AtlasNet C++ integration; client-side prediction, production ingress/transport integration, and distributed physics remain later work.

## Documentation

The first-pass developer guide lives in [website/docs](website/docs/intro.md), with installation, prefab/component setup, movement, variables, RPCs, physics, and local-worker interest/handoff examples.

Preview locally with Node.js 24 and pnpm 11.25.0:

```powershell
cd website
pnpm install --frozen-lockfile
pnpm start
```

The [publishing guide](website/docs/maintaining-docs.md) explains the prepared GitHub Pages workflow. After enabling Pages with **GitHub Actions** and deploying, the site will be available at [dannys0n.github.io/AtlasNetUnity](https://dannys0n.github.io/AtlasNetUnity/).

