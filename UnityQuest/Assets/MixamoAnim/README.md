# Mixamo animations (not included)

This folder needs two humanoid animation files that are not stored in the repository,
because Mixamo's terms do not allow redistributing the source files on their own.

Download from https://www.mixamo.com (FBX for Unity, "Without Skin"):

- `Idle`    -> save as `Idle.fbx`
- `Walking` -> save as `Walking.fbx` (enable "In Place")

Put both files in this folder. The `.meta` files next to this note already set them up as
Humanoid, so Unity will import them correctly. Then run `Mystery Sort / Generate scene and settings`.
