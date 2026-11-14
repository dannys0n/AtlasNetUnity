---
title: Client prediction
description: Set up a predicted character or provide a custom motor with input replay and server reconciliation.
---

The owner simulates its inputs immediately. The authoritative worker runs the same motor and sends snapshots. The owner applies the confirmed state and replays pending inputs. Other clients render received poses, with optional interpolation.

:::note
Prediction is experimental. Component settings and C# signatures may change while the workflow is tested.
:::

## Set up a character

1. Add **Predicted Character Controller** to the player prefab root alongside **NetworkObject** and Unity's **CharacterController**.
2. Assign **Visual Root** to a direct child if you want owner smoothing. Keep collision and simulation on the prefab root. Prediction already synchronizes that root's pose.
3. Register the prefab in a **Network Prefabs List** and assign it as the manager's **Default Player Prefab** if players should spawn automatically.
4. In your local input script, read input in `Update` and call `controller.SetInput(move, worldYawDegrees, sprint, jumpPressed)`. Pass a button-down event for jump. The component holds it until the next tick.

Use matching controller settings and static level colliders on the owner and authoritative worker. In the Inspector, **Smooth Owner Visuals** handles presentation around ticks and corrections. **Interpolate Remote Copies** smooths other players. **Advanced** exposes state send interval, interpolation buffer, and snap distances.

## Write a custom motor

Derive from `PredictedBehaviour<TInput, TState>`. Both types are unmanaged structs that implement `IPredictionData`. Serialize their fields in the same order on every peer.

| Hook | Responsibility |
| --- | --- |
| `ReadInput()` | Read buffered local intent for a new tick. |
| `SimulateTick(input, deltaTime)` | Run movement and gameplay state changes at a fixed step. This can run again during reconciliation. |
| `CaptureReconcileState()` | Return the additional state the next tick needs, such as vertical speed or a cooldown. |
| `ApplyReconcileState(state)` | Restore that state. This can run before `OnPredictionSpawn()`. |

AtlasNet captures and restores the root position and rotation. Use `IsReplaying` to suppress one-shot effects during replay. Use `HasAuthority` for gameplay side effects that must run only on the simulation worker. `SanitizeInput` and `GetMissingInput` let a custom motor handle invalid and missing input.

## Try it

Import **00 Shared** and **Soccer** to run **Client Predicted Soccer**. It predicts player movement and keeps ball contacts, ball physics, and scoring authoritative. The Soccer sample requires URP, Unity UI, and TMP Essential Resources.

For smaller examples, import **09 Client Prediction** after **00 Shared** in a URP project. Open **09_ClientPrediction** for the stock controller or **09_CustomPrediction** for a dash with a reconciled cooldown. Open the same scene in two instances, start **Host** in one, and select **Join** in the other.
