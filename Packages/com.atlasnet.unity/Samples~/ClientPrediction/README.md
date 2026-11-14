# 09 Client Prediction

Open
**09_ClientPrediction**. In Play Mode, select **Host**. In a second Editor instance
or standalone player running the same scene, select **Join** using the host's
address and port. Use WASD to move, Left Shift to sprint, and Space to jump.
The scene includes a wall and steps for testing collision and correction.

To reuse the prefab, assign it as the Network Manager's player prefab and add
it to a Network Prefabs List. The prefab has a Network Object, a Unity Character
Controller, and an AtlasNet Predicted Character Controller. Its optional Visual
Root is a direct child and can hold the character mesh or camera rig.

For your own controls, read input in Update and call
`SetInput(move, worldYawDegrees, sprint, jumpPressed)` on the local controller.
The controller buffers jump presses between ticks. It sends movement intent
to the server, reconciles the owner, and interpolates other players. The controller
owns the root pose, so remove any separate Network Transform or movement script
that also writes that root. Use matching Character Controller settings and
static level colliders on the server and clients.

## Transform settings

The Predicted Character Controller Inspector has separate settings for local
visual smoothing and spectator interpolation. Use **Advanced → State Send Interval (Ticks)**
to tune how often the server sends complete movement state. Input and
simulation still run every tick. **Interpolation Buffer (Ticks)** adds display delay
to smooth packet arrival, and **Snap on Large Movement** skips smoothing
across jumps larger than **Snap Distance**.

## Custom prediction

Open **09_CustomPrediction** in this same folder. Use the same Host, Server,
Worker, and Join buttons as the quick start. Use WASD to move and Space to
dash in the direction you are holding. The HUD shows the predicted dash
cooldown and the current worker and epoch. Run the custom scene in every
instance for this test.

Open **PredictedDashController.cs** to see the custom path. Put a concrete
**PredictedBehaviour<TInput, TState>** subclass on a **NetworkObject** prefab.
Use value structs implementing **IPredictionData** and serialize their fields
in the same order on every peer. The controller implements four main hooks.

- **ReadInput** consumes buffered local input. Latch a short button
  press until this hook reads it.
- **SimulateTick** runs movement at the network tick rate and can run again during
  reconciliation. Use **IsReplaying** to guard effects that should play once.
- **CaptureReconcileState** records gameplay state needed by the next tick, such as
  the dash cooldown and vertical speed.
- **ApplyReconcileState** applies those values. AtlasNet captures and restores the
  root position and rotation and handles input history, snapshots, and handoff.

**SanitizeInput** lets the server clamp untrusted controls. Assign a visual
child for local smoothing. Set snapshot cadence and spectator interpolation in
the component's Inspector.

### Coming from another framework

- PurrDiction's **PredictedIdentity<INPUT, STATE>** maps to
  **PredictedBehaviour<TInput, TState>**. Its **GetFinalInput** maps to
  **ReadInput**. [PurrDiction Predicted Identities](https://purrnet.dev/docs/client-side-prediction/predicted-identities)
- FishNet's **[Replicate]** maps to **SimulateTick**. The state used by
  **[Reconcile]** maps to **CaptureReconcileState** and **ApplyReconcileState**. [FishNet Controlling an Object](https://fish-networking.gitbook.io/docs/guides/features/prediction/creating-code/controlling-an-object)
- Fusion's **FixedUpdateNetwork** and Netick's **NetworkFixedUpdate** are the
  equivalent fixed simulation callbacks. The AtlasNet quick-start prefab
  follows the ready-made character setup of Fusion's Simple KCC. [Photon Fusion NetworkBehaviour](https://doc.photonengine.com/fusion/v2/manual/network-behaviour) · [Photon Fusion Simple KCC](https://doc.photonengine.com/fusion/v2/addons/simple-kcc) · [Netick Writing Gameplay Code](https://netick.net/docs/2/articles/understanding-client-side-prediction/writing-gameplay-code)

## Local worker handoff

Start **Server** (or **Host**) in the first instance, **Worker** in the second,
and **Join** in the third. Use the same coordinator address and port in all
three. With two workers including the coordinator, cross X = 0 with A/D while
staying clear of the wall. Watch the local player’s **Worker** and **Epoch**
change as authority transfers. The worker must run the same scene and colliders.
The server and worker views shade their own regions on the floor. The joined
client shows nearby boundary walls, which you can toggle in the debug panel.
