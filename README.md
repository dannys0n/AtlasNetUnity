# AtlasNet Unity

An early Unity networking framework with a local single-server demo. Tested with Unity 6000.6.2f1.

## Install with Unity Package Manager

After the package changes are merged into `main`, open **Window > Package Management > Package Manager** in your Unity project. Choose **+ > Install package from git URL**, then paste:

```text
https://github.com/dannys0n/AtlasNetUnity.git?path=/Packages/com.atlasnet.unity#main
```

Select **AtlasNet Unity** in Package Manager and import **Simple Authority Demo** from its Samples section if you want the example scenes. The package provides the framework runtime; the sample scenes are optional.

This first pass uses a local single-server adapter. Cross-server handoff, prediction, and the production AtlasNet backend are not included yet.

