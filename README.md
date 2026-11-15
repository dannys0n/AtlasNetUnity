# AtlasNet Unity · M2 + Client Prediction

An experimental Unity networking framework for shared worlds that span multiple simulation workers. Build with familiar GameObjects, prefabs, RPCs, and replicated variables. AtlasNet Unity demonstrates spatial interest and authority handoffs, with native UDP and browser WebSocket clients sharing one world.

This snapshot combines the M2 demo project with the client prediction samples. It uses the local development backend. The prediction API is provisional.

## Full Unity project

Check out the `m2-prediction` branch or the `v0.3.0-preview.1` tag, then open the repository folder with Unity 6000.6.2f1. The project includes the package as an embedded package.

## Install the UPM package

This snapshot ships AtlasNet Unity 0.3.0-preview.1.

In Unity Package Manager, choose **Install package from Git URL** and paste:

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#v0.3.0-preview.1
```

Import **00 Shared**, then **01 Client Authority**. Open **01_ClientAuthority** and select **Start World (Server)** in one instance and **Join as Client** in another.

For prediction, also import **09 Client Prediction** or **Soccer**.

[Snapshot documentation](website/docs/getting-started/installation.md) · [M2 presentation](https://dannys0n.github.io/AtlasNetUnity/m2/)
