Sources of the PNGs in `../public`, which chat apps and phones need as bitmaps. Regenerate with ImageMagick:

```bash
magick -background none icons/og-signed-out.svg public/og-signed-out.png
magick -background none icons/icon.svg public/icon-512.png
magick -background none icons/icon.svg -resize 180x180 public/apple-touch-icon.png
```
