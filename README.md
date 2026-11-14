# AtlasNet Unity

An experimental Unity networking framework for shared worlds that span multiple simulation workers. Build with familiar GameObjects, prefabs, RPCs, and replicated variables. AtlasNet Unity demonstrates spatial interest and authority handoffs, with native UDP and browser WebSocket clients sharing one world.

The current prototype runs on a local development backend. Integration with the AtlasNet C++ backend is pending. Client prediction is available for testing and its API is provisional.

## Quick start

In Unity Package Manager, choose **Install package from Git URL** and paste:

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Import **00 Shared**, then **01 Client Authority**. Open **01_ClientAuthority** and select **Start World (Server)** in one instance and **Join as Client** in another.

[Documentation](https://dannys0n.github.io/AtlasNetUnity/) · [M2 presentation](https://dannys0n.github.io/AtlasNetUnity/m2/)
