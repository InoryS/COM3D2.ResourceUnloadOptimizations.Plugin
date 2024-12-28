# COM3D2.ResourceUnloadOptimizations.Plugin

A Fork from https://github.com/BepInEx/BepInEx.Utility

GNU GENERAL PUBLIC LICENSE Version 3


### Why Fork
Because I wanted to add some hooks specific to the unity version, which may not be suitable for all games

### Function

This plugin overrides the original game's GC.Collect() and Resources.UnloadUnusedAssets() calls to make the game use as much memory as possible to avoid some random stuttering.

Besides that, I made it highly configurable so you can combine the behavior you want.

