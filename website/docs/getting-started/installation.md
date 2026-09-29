---
title: Install the package
description: Install AtlasNet Unity through Unity Package Manager and import its optional sample.
---

## Requirements

Use the repository's Unity **6000.6.2f1** version for the closest match to these examples. The package declares Unity `6000.0` as its minimum, but broader version compatibility needs separate testing.

No AtlasNet service, Docker container, WSL setup, or other networking framework is required for the local demo. The package does not depend on NGO, FishNet, or Multiplayer Play Mode.

## Add the Git package

Open Unity's Package Manager, choose **Install package from git URL** (wording varies by Unity version), and enter:

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Unity imports the runtime, editor inspectors, and RPC code generator as a package. Your gameplay scripts and prefabs stay in your project's `Assets` folder; you should not copy package runtime files there.

:::tip Pin a revision for repeatable projects
`#main` follows the development branch. For a reproducible project, replace it with a verified commit hash or an existing release tag. Do not assume a package version implies a matching Git tag.
:::

Unity can also install from a local `Packages/com.atlasnet.unity/package.json` checkout when you are developing the framework itself.

## Import the optional sample

Select **AtlasNet Unity** in Package Manager. In **Samples**, import **Simple Authority Demo**.

Unity copies the sample into a path similar to:

```text
Assets/Samples/AtlasNet Unity/0.1.0/Simple Authority/
├── Scenes/
├── Prefabs/
├── Scripts/
└── Materials/
```

The imported copy is yours to inspect and modify. Updating the package does not automatically rewrite an already imported sample. Keep custom changes separate before reimporting.

## Configure input for the sample

The sample intentionally uses Unity's **legacy Input Manager** for readable code. In **Project Settings → Player → Active Input Handling**, enable **Input Manager (Old)** or **Both**, then restart if Unity requests it.

In **Project Settings → Input Manager**, check that `Horizontal`, `Vertical`, `Jump`, `Mouse X`, and `Mouse Y` exist. The debug view reads middle mouse with `GetMouseButton(2)` and scroll with `Input.mouseScrollDelta`; those do not require named axes. If other imported scripts use `Mouse ScrollWheel`, retain that axis too.

This is a sample requirement, not a restriction on your game: AtlasNet does not prescribe an input package.

## Choose your test instances

Install Unity's **Multiplayer Play Mode** package if you want several editor players. The current repository demo uses `3.0.0`; select a version compatible with your installed editor. Separate standalone builds are another option.

Continue with [your first local session](./first-session.md). Do not start multiple listening servers on port `7777`; additional servers must **join as workers**.
