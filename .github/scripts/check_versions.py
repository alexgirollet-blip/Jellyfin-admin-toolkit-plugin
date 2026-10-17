#!/usr/bin/env python3
"""Fail if the Jellyfin version the plugin is compiled against differs from the targetAbi it declares.

Checks that these all name the same Jellyfin version (e.g. 10.11.10):
  - src/Jellyfin.Plugin.AdminToolkit/Jellyfin.Plugin.AdminToolkit.csproj   <JellyfinPackageVersion>
  - tests/.../Jellyfin.Plugin.AdminToolkit.Tests.csproj                   Jellyfin.Controller / Jellyfin.Model versions
  - tools/ApiCompat/ApiCompat.csproj                                      default JellyfinVersion
  - build.yaml                                                            targetAbi (must be <version>.0)
  - src/.../Core/Errors.cs                                                ErrorText.BuiltForJellyfin
Also checks that the SkiaSharp version is the same everywhere (the server's copy is used at runtime).
"""
import re
import sys


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


errors = []
csproj = read("src/Jellyfin.Plugin.AdminToolkit/Jellyfin.Plugin.AdminToolkit.csproj")
pkg = re.search(r"<JellyfinPackageVersion>([^<]+)</JellyfinPackageVersion>", csproj).group(1).strip()
skia = re.search(r"<SkiaSharpVersion>([^<]+)</SkiaSharpVersion>", csproj).group(1).strip()

abi = re.search(r'^targetAbi:\s*"([^"]+)"', read("build.yaml"), re.M).group(1)
if abi != pkg + ".0":
    errors.append(f"build.yaml targetAbi is {abi} but the csproj builds against Jellyfin {pkg} (expected \"{pkg}.0\")")

tests = read("tests/Jellyfin.Plugin.AdminToolkit.Tests/Jellyfin.Plugin.AdminToolkit.Tests.csproj")
for name in ("Jellyfin.Controller", "Jellyfin.Model"):
    tm = re.search(rf'Include="{re.escape(name)}" Version="([^"]+)"', tests)
    if not tm or tm.group(1) != pkg:
        errors.append(f"tests reference {name} {tm.group(1) if tm else '?'} but the plugin builds against {pkg}")
for name in ("SkiaSharp", "SkiaSharp.NativeAssets.Linux"):
    tm = re.search(rf'Include="{re.escape(name)}" Version="([^"]+)"', tests)
    if not tm or tm.group(1) != skia:
        errors.append(f"tests reference {name} {tm.group(1) if tm else '?'} but the plugin uses SkiaSharp {skia}")

compat = read("tools/ApiCompat/ApiCompat.csproj")
cm = re.search(r"<JellyfinVersion Condition=[^>]*>([^<]+)</JellyfinVersion>", compat)
if not cm or cm.group(1) != pkg:
    errors.append(f"tools/ApiCompat default JellyfinVersion is {cm.group(1) if cm else '?'} but the plugin builds against {pkg}")

err = read("src/Jellyfin.Plugin.AdminToolkit/Core/Errors.cs")
bm = re.search(r'BuiltForJellyfin = "([^"]+)"', err)
if not bm or bm.group(1) != pkg:
    errors.append(f"ErrorText.BuiltForJellyfin is {bm.group(1) if bm else '?'} but the plugin builds against {pkg}")

if errors:
    print("Jellyfin version mismatch:")
    for e in errors:
        print("  - " + e)
    sys.exit(1)
print(f"OK: compiled against Jellyfin {pkg} (SkiaSharp {skia}), targetAbi {abi}")
