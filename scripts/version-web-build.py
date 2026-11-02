"""Give each Flutter web release distinct entrypoint and asset URLs."""
import hashlib
import json
from pathlib import Path
import shutil
import sys


def version_build(directory: Path) -> None:
    main = directory / "main.dart.js"
    bootstrap = directory / "flutter_bootstrap.js"
    assets = directory / "assets"
    content = bootstrap.read_text(encoding="utf-8")
    entrypoint = '"mainJsPath":"main.dart.js"'
    initializer = "engineInitializer.initializeEngine()"
    if entrypoint not in content or initializer not in content:
        raise ValueError(f"Unexpected Flutter bootstrap format: {directory}")
    digest = hashlib.sha256(main.read_bytes())
    for asset in sorted(assets.rglob("*")):
        if asset.is_file():
            digest.update(asset.relative_to(assets).as_posix().encode())
            digest.update(asset.read_bytes())
    version = digest.hexdigest()[:20]
    release = directory / "releases" / version
    release.mkdir(parents=True, exist_ok=True)
    shutil.copytree(assets, release / "assets", dirs_exist_ok=True)
    main_name = f"main.{version}.dart.js"
    shutil.copyfile(main, directory / main_name)
    content = content.replace(entrypoint, '"mainJsPath":' + json.dumps(main_name))
    asset_base = json.dumps(f"releases/{version}/")
    content = content.replace(initializer,
        "engineInitializer.initializeEngine({assetBase: new URL(" + asset_base + ", document.baseURI).href})")
    bootstrap.write_text(content, encoding="utf-8")
    print(f"Versioned {directory}: {version}")


if __name__ == "__main__":
    for argument in sys.argv[1:]:
        version_build(Path(argument))
