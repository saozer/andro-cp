"""Generate committed UI/Windows assets from the author's original SVG.

Requires Inkscape in PATH and Pillow: python -m pip install Pillow.
Normal .NET builds use the generated assets and do not need these tools.
"""
from pathlib import Path
import subprocess

from PIL import Image


def main():
    root = Path(__file__).resolve().parent.parent
    assets = root / 'src' / 'AndroCp.App' / 'Assets'
    assets.mkdir(parents=True, exist_ok=True)
    png = assets / 'andro-cp.png'
    icon = assets / 'andro-cp.ico'
    subprocess.run(['inkscape', str(root / 'logo-vector.svg'), '--export-area-page',
                    '--export-width=512', '--export-height=512',
                    '--export-background-opacity=0', '--export-filename=' + str(png)], check=True)
    sizes = [(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
    with Image.open(png) as source:
        if source.convert('RGBA').getchannel('A').getbbox() is None:
            raise RuntimeError('Le PNG généré est entièrement transparent : vérifier le SVG.')
        source.convert('RGBA').save(icon, format='ICO', sizes=sizes)
    with Image.open(icon) as generated:
        for size in sizes:
            if generated.ico.getimage(size).convert('RGBA').getchannel('A').getbbox() is None:
                raise RuntimeError(f'Icône entièrement transparente : {size}.')
    print('Icônes générées :', png, icon, sep='\n')


if __name__ == '__main__':
    main()
