---
slug: /
title: AtlasNet Unity
description: Install AtlasNet Unity, build networked gameplay, and explore local multi-worker authority and handoff.
hide_table_of_contents: true
---

<p className="atlas-eyebrow">UNITY NETWORKING</p>

<p className="atlas-lead">Build gameplay with familiar objects, replicated values, and RPCs. Let the framework route those objects to their current simulation worker.</p>

AtlasNet Unity is an experimental Unity Package Manager package. Its current **local backend** lets Unity instances act as clients or workers in one shared development world. It is a development implementation, not the production AtlasNet C++ integration.

<div className="atlas-cards">
  <div className="atlas-card">

### Start with a working scene

[Install the package](./getting-started/installation.md), import the optional sample, then [start a local session](./getting-started/first-session.md).

  </div>
  <div className="atlas-card">

### Understand authority

[Separate ownership from simulation](./concepts/authority.md). An entity keeps its identity even when its authoring worker changes.

  </div>
  <div className="atlas-card">

### Add gameplay

Set up [prefabs and components](./guides/prefabs-components.md), then add [movement](./guides/movement.md), [state](./guides/network-variables.md), and [RPCs](./guides/rpcs.md).

  </div>
  <div className="atlas-card">

### Explore multiple workers

Learn how [interest and handoff](./guides/interest-handoffs.md) affect local replicas without exposing worker addresses to gameplay code.

  </div>
</div>

## A familiar starting point

```csharp
using AtlasNet;
using UnityEngine;

public sealed class ExamplePlayer : NetworkBehaviour
{
    private NetworkVariable<int> score = new(0);

    private void Update()
    {
        if (!IsOwner) return;

        if (Input.GetKeyDown(KeyCode.F))
            AddScoreRpc();
    }

    [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
    private void AddScoreRpc()
    {
        // Runs on this entity's current authoritative worker.
        score.Value += 1;
    }
}
```

Attach this behaviour beside a `NetworkObject` on a registered, spawned prefab. `IsOwner` selects the controlling client; `SendTo.Authority` selects the current simulation worker. They are deliberately different concepts.

## See the separate shooter demo

[![Two shooter clients above two top-down worker views](./assets/shooter-cross-server.jpg)](https://youtu.be/4-gM8oYjoGM)

<p className="atlas-caption">An actual frame from the separate shooter demonstration. The shooter map and weapons are not bundled with the package sample. Select the image to watch the recording.</p>

## Current scope

The Git package is version **0.2.0**. The repository project uses Unity **6000.6.2f1**. The package declares Unity **6000.0** as its minimum.

The default development server accepts native UDP and browser WebSocket clients in the same world. The framework includes replicated transforms and animation, RPCs, network variables, spatial interest, and local worker handoffs. [Client prediction](./guides/prediction.md) supports a built-in character controller and custom motors. Its API is provisional.

See [troubleshooting and limits](./troubleshooting.md#what-this-prototype-does-not-promise) and the [M2 presentation](https://dannys0n.github.io/AtlasNetUnity/m2/).
