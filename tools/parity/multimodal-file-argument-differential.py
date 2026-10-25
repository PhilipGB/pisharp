#!/usr/bin/env python3
"""Compare positional @file processing with Pi at the pinned revision."""
import argparse
import base64
import json
import os
import pathlib
import struct
import subprocess
import tempfile
import zlib

from pi_reference import verify_pi_checkout


def png(width, height, rgba=(30, 90, 160, 255)):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

    raw = b"".join(b"\x00" + bytes(rgba) * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def bmp_1x1():
    data = bytearray(58)
    data[0:2] = b"BM"
    struct.pack_into("<I", data, 2, len(data))
    struct.pack_into("<I", data, 10, 54)
    struct.pack_into("<I", data, 14, 40)
    struct.pack_into("<iiHHII", data, 18, 1, 1, 1, 24, 0, 4)
    data[56:59] = b"\x00\x00\xff"
    return bytes(data)


JPEG_2X2 = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAACAAIDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEBAAAAAAAAAAAAAAAAAAAABv/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AJ+AZiz/2Q=="
)
GIF_2X2 = base64.b64decode("R0lGODdhAgACAIAAAAAAAAAAACwAAAAAAgACAAAIBgABCAQQEAA7")
WEBP_2X2 = base64.b64decode("UklGRjgAAABXRUJQVlA4ICwAAAAwAQCdASoCAAIAAgA0JaAAA3AA/vBsr//8lP/vK/13bhv//hWv5Wv5WrfAAA==")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    pi = args.pi.resolve()
    pin = verify_pi_checkout(pi)
    repo = pathlib.Path(__file__).resolve().parents[2]
    probe_dir = pathlib.Path(__file__).resolve().parent
    files = {
        "notes λ.txt": b"\xef\xbb\xbfnotes: caf\xc3\xa9\nsecond line",
        "diagram one.data": png(3, 2),
        "empty.txt": b"",
        "photo two.bmp": bmp_1x1(),
        "photo.jpg": JPEG_2X2,
        "animation.gif": GIF_2X2,
        "picture.webp": WEBP_2X2,
    }

    with tempfile.TemporaryDirectory(prefix="pisharp-file-differential-") as directory:
        work = pathlib.Path(directory)
        root = work / "fixture files λ"
        root.mkdir()
        for name, content in files.items():
            (root / name).write_bytes(content)
        home = root / "home"
        home.mkdir()
        (root / "absolute image.png").write_bytes(png(4, 3))
        (root / "symlink target.png").write_bytes(png(5, 2))
        os.symlink("symlink target.png", root / "symlink image.png")
        (home / "home image.png").write_bytes(png(2, 4))
        scenarios = [
            {"id": "text-png-duplicate-unicode-paths", "prompt": "review these", "files": ["notes λ.txt", "diagram one.data", "diagram one.data"]},
            {"id": "many-images-preserve-order-256", "prompt": "review all images", "files": ["diagram one.data"] * 256},
            {"id": "empty-and-bmp-conversion", "prompt": "continue", "files": ["empty.txt", "photo two.bmp"]},
            {"id": "supported-image-format-passthrough", "prompt": "read these formats", "files": ["photo.jpg", "animation.gif", "picture.webp"]},
            {"id": "absolute-image-path", "prompt": "read absolute", "files": [str(root / "absolute image.png")]},
            {"id": "symlinked-relative-image-path", "prompt": "read symlink", "files": ["symlink image.png"]},
            {"id": "home-expanded-image-path", "prompt": "read home", "files": ["~/home image.png"]},
        ]
        encoded = json.dumps(scenarios, ensure_ascii=False)
        (work / "Program.cs").write_text((probe_dir / "multimodal-file-argument-probe.cs").read_text())
        (work / "probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.FileProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="4.151.1" /></ItemGroup>
</Project>""")
        environment = dict(os.environ)
        environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
        subprocess.run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false",
                        "-m:1", "--nologo"], env=environment, check=True, stdout=subprocess.DEVNULL, timeout=240)
        environment["HOME"] = str(home)
        pisharp = json.loads(subprocess.check_output([
            "dotnet", str(work / "bin/Debug/net10.0/PiSharp.FileProbe.dll"), str(root), encoded
        ], env=environment, text=True, timeout=60))

        oracle = pi / "packages/coding-agent/test/pisharp-file-argument-differential.test.ts"
        if oracle.exists():
            raise RuntimeError(f"Refusing to overwrite {oracle}")
        oracle.write_text((probe_dir / "multimodal-file-argument-probe.pi.test.ts").read_text())
        try:
            environment["PISHARP_FILE_SCENARIOS"] = encoded
            environment["PISHARP_FILE_ROOT"] = str(root)
            environment["PISHARP_FILE_OUTPUT"] = str(work / "pi.json")
            subprocess.run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                            str(pi / "packages/coding-agent/vitest.config.ts"), str(oracle), "--reporter=dot"],
                           cwd=pi / "packages/coding-agent", env=environment, check=True, timeout=180)
        finally:
            oracle.unlink(missing_ok=True)
        capture = json.loads((work / "pi.json").read_text())

    root_path = str(root)
    upstream = {row["id"]: row for row in capture["first"]}
    local = {row["id"]: row for row in pisharp}
    stable = capture["first"] == capture["second"]
    rows = []
    for scenario in scenarios:
        case_id = scenario["id"]
        pi_row, sharp_row = upstream[case_id], local[case_id]
        text_match = pi_row["text"] == sharp_row["text"]
        image_matches = len(pi_row["images"]) == len(sharp_row["images"])
        sequence = []
        for pi_image, sharp_image in zip(pi_row["images"], sharp_row["images"]):
            strict = scenario["id"] != "empty-and-bmp-conversion"
            match = (pi_image["mimeType"], pi_image["width"], pi_image["height"]) == (
                sharp_image["mimeType"], sharp_image["width"], sharp_image["height"])
            if strict:
                match = match and pi_image["data"] == sharp_image["data"]
            signature = {"mimeType": pi_image["mimeType"],
                         "dimensions": [pi_image["width"], pi_image["height"]],
                         "classification": "strict" if strict else "codec-equivalent", "match": match}
            if sequence and {key: sequence[-1][key] for key in signature} == signature:
                sequence[-1]["count"] += 1
            else:
                sequence.append({**signature, "count": 1})
            image_matches = image_matches and match
        rows.append({"id": case_id, "textMatch": text_match, "imagesMatch": image_matches,
                     "imageCount": len(pi_row["images"]), "imageSequence": sequence,
                     "text": pi_row["text"].replace(root_path, "<ROOT>")})
    report = {"piSha": pin, "scope": "processFileArguments versus CliFileArguments.ProcessFilesAsync for shared positional-file corpus",
              "samePiCalibration": {"runs": 2, "stable": stable}, "cases": rows,
              "passed": sum(row["textMatch"] and row["imagesMatch"] for row in rows), "total": len(rows)}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    if not stable or report["passed"] != report["total"]:
        raise SystemExit(f"File argument differential failed; report: {args.output}")
    print(f"Matched {report['passed']}/{report['total']} positional-file cases.")


if __name__ == "__main__":
    main()
