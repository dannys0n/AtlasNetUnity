# AtlasNet Unity

A Unity networking framework prototype exploring multi-worker worlds, spatial interest management, and authority handoffs through familiar GameObject and prefab workflows.

## Highlights

- Networked objects, transforms, Animator state, RPCs, and replicated variables.
- Client- and server-authoritative gameplay with configurable transports.
- Native UDP and browser WebSocket clients connected to the same server.
- Spatial interest filtering and authority transfers between workers.
- Interactive samples covering movement, physics, scaling, animation, and a small multiplayer soccer game.

## Current status

The project uses a **local dummy backend** to demonstrate and validate the framework while integration with the production AtlasNet C++ backend is pending. It is an experimental prototype; prediction, distributed physics, and production hardening remain future work.

The Unity project targets **Unity 6000.6.2f1**. A separate M2 presentation demonstrates the framework with persistent client connections, QR/IP joining, and synchronized slides.

## Install

In Unity Package Manager, choose **Install package from Git URL**:

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Import **00 Shared** from the package's Samples section first, then the demonstrations you want to explore. Soccer additionally requires URP, Unity UI, and TMP Essential Resources.

[Documentation and setup guides](https://dannys0n.github.io/AtlasNetUnity/)
