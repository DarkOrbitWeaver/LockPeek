# LockPeek Assets

Artwork and icon assets for the LockPeek application, Windows 11 Explorer context menu, and Microsoft Store packaging.

## Structure

- `brand/`: Official app icons (`lockpeek.ico`), vector sources (`lockpeek-symbol.svg`), and high-resolution master logos.
- `context-menu/`: Themed monochrome icons for Windows 11 Explorer context-menu integration (light and dark variants at 16, 20, 24, 32, 48, and 64 px).
- `taskbar/`: Application taskbar and title-bar icons.
- `msix/`: App package manifest visual assets (Square44x44Logo, Square150x150Logo, StoreLogo, splash screens, unplated and high-contrast variants).
- `store/`: Promo artwork for Microsoft Store listings.

## Asset Generation

To re-render multi-resolution icons from the vector masters:

```powershell
./tools/generate-assets.ps1
```
