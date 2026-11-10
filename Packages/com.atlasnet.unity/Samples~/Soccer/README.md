# Client Predicted Soccer

Open **Client Predicted Soccer** in each Unity instance. Select **Host** and **Join**
to play, or use **Start server only**, **Join as worker**, and **Join** to try
authority handoff. Enter the same server address in each instance.

Use WASD, the arrow keys, or the touch stick to move. Run into the ball to
kick it. Cross the middle of the pitch with a worker running to see the
player's handoff indicator.

**PredictedSoccerMotor** uses the AtlasNet prediction API to move and turn the
player locally, then reconcile to the worker's simulation. Its state is empty
because the motor moves on a plane and AtlasNet already snapshots the pose.
The worker handling the player reports the kick to the ball's worker.
Ball physics and scoring run on the authoritative server.

For comparison, open **Server Authoritative Soccer** with the same number of Unity instances.
