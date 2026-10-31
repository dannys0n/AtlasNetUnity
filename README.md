# AtlasNet Unity

An early Unity networking framework with a local multi-worker backend for testing authority, interest, and handoff. The project targets Unity 6000.6.2f1.

## Install with Unity Package Manager

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Select **AtlasNet Unity** in Package Manager and import **00 Shared**, then whichever numbered samples you want from its Samples section. The package provides the framework runtime; the sample scenes are optional.

Install Unity's Multiplayer Play Mode package to test with multiple editor players, or use separate builds. Multiplayer Play Mode is a testing tool, not a dependency of the AtlasNet package.

The samples include QuickStart, client- and server-authoritative movement, server and client Rigidbody physics, interest and scale, and Animator synchronization. The optional **08 Soccer** game sample requires **00 Shared**, URP, Unity UI, and imported TMP Essential Resources. Open `08_Soccer`, start **Host** or **Start server only** in one instance, then **Join** or **Join as worker** in others. Move with WASD/arrows or the larger circular on-screen stick, inset from the corner. Teams alternate over the admitted-player roster and rebalance after departures; worker sessions do not count. Soccer players use the Player tag and SoccerPlayer layer; its self-collision is disabled in the Physics collision matrix while ball and arena collisions remain enabled. Movement and animation are server-authoritative; the scoring team waves automatically after each goal. The pitch runs horizontally, with goals on the left and right. Solid moving-player contacts kick the ball forward and slightly upward. Gravity returns it to the pitch, and stalled contacts provide rate-limited lift to help release a trapped ball. Invisible inset walls and a ceiling contain the ball without exposed board ledges; goal walls are three times taller. Goals reset the ball to centre. Players are 1.6x scale and move at 16 m/s (2x the original speed); the ball has a 1 m radius and stronger kicks. Player torsos and the ball flash red during departure and green for 0.5 seconds after a successful handoff, then return to their normal team/white colors. A local arrow at the ball's centre height points to each player's attacking goal. A team-colored screen-edge arrow points from the player toward the ball while it is off-screen and hides when it returns to view. Debug regions and boundaries start enabled. Interest discs always appear on servers; Show On Clients is off for Soccer. Soccer uses a 53.33 m network interest radius (one third of the previous 160 m); the debug disc shows the actual radius. Distant entities, including the ball and its attached match state, follow normal backend interest culling. Set SoccerSession's Web Base URL to the served Soccer build (the serving script defaults to port 8094). The large QR and address contain the plain Web URL; the serving script adds connection parameters. The QR does not start an HTTP server.

The local backend is not the production AtlasNet C++ integration; client-side prediction, production ingress/transport integration, and distributed physics remain later work.

## Documentation
[visit the documentation website](https://dannys0n.github.io/AtlasNetUnity/)
