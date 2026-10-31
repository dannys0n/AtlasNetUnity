# AtlasNet Unity

An early Unity networking framework with a local multi-worker backend for testing authority, interest, and handoff. The project targets Unity 6000.6.2f1.

## Install with Unity Package Manager

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Select **AtlasNet Unity** in Package Manager and import **00 Shared**, then whichever numbered samples you want from its Samples section. The package provides the framework runtime; the sample scenes are optional.

Install Unity's Multiplayer Play Mode package to test with multiple editor players, or use separate builds. Multiplayer Play Mode is a testing tool, not a dependency of the AtlasNet package.

The samples include QuickStart, client- and server-authoritative movement, server and client Rigidbody physics, interest and scale, and Animator synchronization. The local backend is not the production AtlasNet C++ integration; client-side prediction, production ingress/transport integration, and distributed physics remain later work.

## Documentation
[visit the documentation website](https://dannys0n.github.io/AtlasNetUnity/)
