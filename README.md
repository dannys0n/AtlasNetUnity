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

The Unity project targets **Unity 6000.6.2f1**. Package version **0.2.0** adds per-peer transport metrics and a configurable UDP idle timeout.

## M2 presentation

The presentation scenes live in `Assets/M2/Scenes`. The demo keeps client connections active while switching between synchronized slides and gameplay. Join by QR code or IP address. During a session, press **Tab** to see outbound traffic and per-client send rate (KiB/s) and ping (ms). Ping displays a dash when a measurement is unavailable.

## Install

In Unity Package Manager, choose **Install package from Git URL**:

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Import **00 Shared** from the package's Samples section first, then the demonstrations you want to explore. Soccer additionally requires URP, Unity UI, and TMP Essential Resources.

The UPM package contains the networking runtime and importable samples. Open this repository as a Unity project to run the M2 presentation.

[Documentation and setup guides](https://dannys0n.github.io/AtlasNetUnity/)
