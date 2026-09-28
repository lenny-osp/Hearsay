# Assets

- `icon-1024.png`: the app icon, 1024 x 1024 px, RGBA. Written by
  `mac/Scripts/make-icon.swift` (run from `mac/`: `swift Scripts/make-icon.swift`)
  from the same render as the macOS AppIcon set's `icon_1024.png`, so the two
  are byte-identical. Do not edit it by hand; change the drawing code and
  rerun the script.

The macOS app icon set (`mac/Hearsay/Resources/Assets.xcassets/AppIcon.appiconset`)
renders each size directly at its pixel size. Windows builds its `.ico`
from `icon-1024.png` (downscaled to 16, 24, 32, 48, 64, 128, and 256 px).
