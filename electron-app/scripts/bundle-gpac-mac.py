#!/usr/bin/env python3
"""Copy MP4Box and its recursive arm64 dylibs into an empty, relocatable bundle.

Only the destination is modified. Source libraries and the Homebrew installation
are read-only. Uses the installed tools; never downloads or installs dependencies.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


def command(*args):
    return subprocess.check_output([str(arg) for arg in args], text=True)


def dependencies(binary):
    return [line.strip().split(" (compatibility", 1)[0]
            for line in command("/usr/bin/otool", "-L", binary).splitlines()[1:]
            if line.startswith("\t")]


def rpaths(binary):
    return re.findall(r"cmd LC_RPATH\s+cmdsize \d+\s+path (.*?) \(offset",
                      command("/usr/bin/otool", "-l", binary))


def is_system(path):
    return path.startswith(("/usr/lib/", "/System/Library/", "/Library/Apple/"))


def expand_path(value, loader, executable):
    return Path(value.replace("@loader_path", str(loader.parent))
                .replace("@executable_path", str(executable.parent)))


def resolve_dependency(dep, source, executable):
    if dep.startswith("@rpath/"):
        tail = dep[len("@rpath/"):]
        candidates = [expand_path(root, source, executable) / tail
                      for root in rpaths(source)]
        candidates += [expand_path(root, executable, executable) / tail
                       for root in rpaths(executable)]
    else:
        candidates = [expand_path(dep, source, executable)]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise RuntimeError(f"Cannot resolve {dep!r} from {source.name}; repair the source installation first")


def copy_local_licenses(source, destination):
    """Preserve notices available in this library's local package root."""
    for parent in list(source.resolve().parents)[:4]:
        if parent == Path(parent.anchor):
            break
        notices = [p for p in parent.iterdir()
                   if p.is_file() and re.match(r"(?i)^(copying|licen[cs]e|notice|third.?party)", p.name)]
        if not notices:
            continue
        label = parent.parent.name + "-" + parent.name
        target = destination / label
        target.mkdir(parents=True, exist_ok=True)
        for notice in notices:
            shutil.copy2(notice, target / notice.name)
        return label
    return None


def bundle(source, destination, licenses=None):
    source = source.absolute()
    destination = destination.absolute()
    if not source.is_file() or not os.access(source, os.X_OK):
        raise RuntimeError("--mp4box must point to an executable MP4Box")
    if destination.exists() and any(destination.iterdir()):
        raise RuntimeError("Destination must be empty; use a fresh build directory")
    destination.mkdir(parents=True, exist_ok=True)
    queue = [(source, "MP4Box")]
    seen = {}
    manifest = []
    while queue:
        library, name = queue.pop(0)
        resolved = library.resolve()
        if name in seen:
            if seen[name] != resolved:
                raise RuntimeError(f"Conflicting libraries share the basename {name}")
            continue
        seen[name] = resolved
        arches = command("/usr/bin/lipo", "-archs", library).split()
        if "arm64" not in arches:
            raise RuntimeError(f"{name} does not contain arm64")
        target = destination / name
        shutil.copy2(library, target)
        target.chmod(0o755)
        changes = []
        for dependency in dependencies(library):
            if is_system(dependency):
                continue
            dependency_path = resolve_dependency(dependency, library, source)
            if dependency_path.resolve() == resolved:
                continue  # LC_ID_DYLIB is listed by otool -L too.
            queue.append((dependency_path, dependency_path.name))
            changes += ["-change", dependency, "@loader_path/" + dependency_path.name]
        if name.endswith(".dylib"):
            changes += ["-id", "@loader_path/" + name]
        if changes:
            subprocess.run(["/usr/bin/install_name_tool", *changes, str(target)], check=True,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        subprocess.run(["/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", str(target)],
                       check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        license_group = copy_local_licenses(library, licenses) if licenses else None
        manifest.append({"file": name, "architectures": arches, "licenseGroup": license_group,
                         "sha256": hashlib.sha256(target.read_bytes()).hexdigest()})
    for item in manifest:
        for dependency in dependencies(destination / item["file"]):
            if is_system(dependency):
                continue
            if not dependency.startswith("@loader_path/"):
                raise RuntimeError(f"Unrelocated dependency in {item['file']}")
            if not (destination / dependency[len("@loader_path/"):]).is_file():
                raise RuntimeError(f"Missing bundled dependency in {item['file']}")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path, help="new or empty output directory")
    parser.add_argument("--mp4box", type=Path, required=True, help="source MP4Box executable")
    parser.add_argument("--licenses", type=Path, help="preserve locally supplied package licenses here")
    parser.add_argument("--manifest", type=Path, help="write a manifest without absolute source paths")
    args = parser.parse_args()
    if sys.platform != "darwin":
        parser.error("This bundler requires macOS and Xcode command-line tools")
    try:
        manifest = bundle(args.mp4box, args.destination, args.licenses)
        if args.manifest:
            args.manifest.parent.mkdir(parents=True, exist_ok=True)
            args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")
        print(f"Bundled MP4Box and {len(manifest) - 1} arm64 libraries; all dependencies are local or system")
    except (OSError, subprocess.CalledProcessError, RuntimeError) as exc:
        parser.exit(1, f"GPAC bundling failed: {exc}\n")


if __name__ == "__main__":
    main()
