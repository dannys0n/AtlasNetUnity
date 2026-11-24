# AtlasNet Unity

An experimental Unity networking framework for shared worlds that span multiple simulation workers. Build with familiar GameObjects, prefabs, RPCs, and replicated variables. AtlasNet Unity demonstrates spatial interest and authority handoffs, with native UDP and browser WebSocket clients sharing one world.

The package connects to the AtlasNet C++ backend. Editor Loopback provides a single in-process host for local development. Client prediction is available for testing and its API is provisional.

## Quick start

In Unity Package Manager, choose **Install package from Git URL** and paste:

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Import **00 Shared**, then **01 Client Authority**. Open **01_ClientAuthority** and select **Start Local Host** in Play Mode. To connect to a deployed AtlasNet world, turn off **Editor Loopback** on the NetworkManager and use a scene with the same registered prefabs as the shard.

[Documentation](https://dannys0n.github.io/AtlasNetUnity/) Â· [M2 presentation](https://dannys0n.github.io/AtlasNetUnity/m2/)
