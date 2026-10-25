#!/usr/bin/env python3
"""Compare prompt-image normalization with the pinned Pi processor."""
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


def jpeg_exif_orientation(data, orientation):
    tiff = (b"II\x2a\x00\x08\x00\x00\x00" + b"\x01\x00" +
            b"\x12\x01\x03\x00\x01\x00\x00\x00" +
            struct.pack("<H", orientation) + b"\x00\x00" + b"\x00\x00\x00\x00")
    payload = b"Exif\x00\x00" + tiff
    return data[:2] + b"\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload + data[2:]


JPEG_2X2 = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAACAAIDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEBAAAAAAAAAAAAAAAAAAAABv/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AJ+AZiz/2Q=="
)
GIF_2X2 = base64.b64decode("R0lGODdhAgACAIAAAAAAAAAAACwAAAAAAgACAAAIBgABCAQQEAA7")
WEBP_2X2 = base64.b64decode("UklGRjgAAABXRUJQVlA4ICwAAAAwAQCdASoCAAIAAgA0JaAAA3AA/vBsr//8lP/vK/13bhv//hWv5Wv5WrfAAA==")
JPEG_8X4 = base64.b64decode("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAEAAgDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAABgf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwC0AqYY/9k=")


def scenarios():
    profile = {"maxWidth": 12, "maxHeight": 20, "maxBytes": 100000, "jpegQuality": 80}
    malformed_png = bytearray(png(2, 2))
    malformed_png[-1] ^= 0xff
    malformed_webp = bytearray(WEBP_2X2)
    struct.pack_into("<I", malformed_webp, 4, 0xffffffff)
    corrupt_webp_payload = bytearray(WEBP_2X2)
    corrupt_webp_payload[40] ^= 0xff
    return [
        {"id": "png-no-resize", "mimeType": "image/png", "data": png(4, 2).hex(), "resize": profile},
        {"id": "png-model-resize", "mimeType": "image/png", "data": png(80, 40).hex(), "resize": profile},
        {"id": "png-auto-resize-disabled", "mimeType": "image/png", "data": png(80, 40).hex(),
         "resize": {"maxWidth": 12, "maxHeight": 20, "maxBytes": 100000, "jpegQuality": 80},
         "autoResizeImages": False},
        {"id": "bmp-to-png", "mimeType": "image/bmp", "data": bmp_1x1().hex(), "autoResizeImages": False},
        {"id": "truncated-png", "mimeType": "image/png", "data": bytes.fromhex("89504e470d0a1a0a").hex()},
        {"id": "truncated-jpeg", "mimeType": "image/jpeg", "data": bytes.fromhex("ffd8ffe0").hex()},
        {"id": "truncated-gif", "mimeType": "image/gif", "data": bytes.fromhex("474946383961").hex()},
        {"id": "truncated-webp", "mimeType": "image/webp", "data": bytes.fromhex("524946460000000057454250").hex()},
        {"id": "malformed-png-crc", "mimeType": "image/png", "data": bytes(malformed_png).hex()},
        {"id": "malformed-jpeg-marker", "mimeType": "image/jpeg", "data": bytes.fromhex("ffd8ff00").hex()},
        {"id": "malformed-gif-screen", "mimeType": "image/gif", "data": bytes.fromhex("47494638396100000000000000").hex()},
        {"id": "malformed-webp-riff-size", "mimeType": "image/webp", "data": bytes(malformed_webp).hex()},
        {"id": "corrupt-webp-payload", "mimeType": "image/webp", "data": bytes(corrupt_webp_payload).hex()},
        {"id": "jpeg-pass-through", "mimeType": "image/jpeg", "data": JPEG_2X2.hex(), "resize": profile},
        {"id": "gif-pass-through", "mimeType": "image/gif", "data": GIF_2X2.hex(), "resize": profile},
        {"id": "webp-pass-through", "mimeType": "image/webp", "data": WEBP_2X2.hex(), "resize": profile},
        {"id": "jpeg-model-resize", "mimeType": "image/jpeg", "data": JPEG_2X2.hex(),
         "resize": {"maxWidth": 1, "maxHeight": 1, "maxBytes": 100000, "jpegQuality": 80}},
        {"id": "gif-model-resize", "mimeType": "image/gif", "data": GIF_2X2.hex(),
         "resize": {"maxWidth": 1, "maxHeight": 1, "maxBytes": 100000, "jpegQuality": 80}},
        {"id": "webp-model-resize", "mimeType": "image/webp", "data": WEBP_2X2.hex(),
         "resize": {"maxWidth": 1, "maxHeight": 1, "maxBytes": 100000, "jpegQuality": 80}},
        {"id": "jpeg-exif-orientation-resize", "mimeType": "image/jpeg",
         "data": jpeg_exif_orientation(JPEG_8X4, 6).hex(),
         "resize": {"maxWidth": 4, "maxHeight": 4, "maxBytes": 100000, "jpegQuality": 80}},
    ]


def normalize(rows):
    return {row["id"]: row for row in rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    pi = args.pi.resolve()
    pin = verify_pi_checkout(pi)
    repo = pathlib.Path(__file__).resolve().parents[2]
    probe_dir = pathlib.Path(__file__).resolve().parent

    cases = scenarios()
    for case in cases:
        case["data"] = base64.b64encode(bytes.fromhex(case["data"])).decode("ascii")

    with tempfile.TemporaryDirectory(prefix="pisharp-image-differential-") as directory:
        work = pathlib.Path(directory)
        (work / "Program.cs").write_text((probe_dir / "multimodal-image-probe.cs").read_text())
        (work / "probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="4.151.1" /></ItemGroup>
</Project>""")
        environment = dict(os.environ)
        environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
        subprocess.run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false",
                        "-m:1", "--nologo"], env=environment, check=True, stdout=subprocess.DEVNULL, timeout=180)
        encoded = json.dumps(cases)
        pisharp = json.loads(subprocess.check_output([
            "dotnet", str(work / "bin/Debug/net10.0/PiSharp.Tests.dll"), encoded
        ], env=environment, text=True, timeout=60))

        oracle = pi / "packages/coding-agent/test/pisharp-image-differential.test.ts"
        if oracle.exists():
            raise RuntimeError(f"Refusing to overwrite {oracle}")
        oracle.write_text((probe_dir / "multimodal-image-probe.pi.test.ts").read_text())
        try:
            environment["PISHARP_IMAGE_SCENARIOS"] = encoded
            environment["PISHARP_IMAGE_OUTPUT"] = str(work / "pi.json")
            subprocess.run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                            str(pi / "packages/coding-agent/vitest.config.ts"), str(oracle), "--reporter=dot"],
                           cwd=pi / "packages/coding-agent", env=environment, check=True, timeout=120)
        finally:
            oracle.unlink(missing_ok=True)
        pi_capture = json.loads((work / "pi.json").read_text())

    pi_rows = pi_capture["first"]
    same_pi_stable = pi_rows == pi_capture["second"]
    pi_by_id = normalize(pi_rows)
    pisharp_by_id = normalize(pisharp)
    rows = []
    for case in cases:
        case_id = case["id"]
        upstream = pi_by_id[case_id]
        local = pisharp_by_id[case_id]
        safety_difference = case_id == "malformed-webp-riff-size"
        strict = case_id in {"png-no-resize", "png-auto-resize-disabled", "truncated-png",
                             "truncated-jpeg", "truncated-gif", "truncated-webp", "malformed-png-crc", "malformed-jpeg-marker",
                             "malformed-gif-screen", "corrupt-webp-payload", "jpeg-pass-through",
                             "gif-pass-through", "webp-pass-through"}
        expected_match = upstream.get("ok") == local.get("ok")
        if upstream.get("ok"):
            expected_match = expected_match and upstream.get("mimeType") == local.get("mimeType")
            expected_match = expected_match and upstream.get("hints", []) == local.get("hints", [])
            expected_match = expected_match and (upstream.get("width"), upstream.get("height")) == (
                local.get("width"), local.get("height"))
            if strict:
                expected_match = expected_match and upstream.get("data") == local.get("data")
        else:
            expected_match = expected_match and upstream.get("message") == local.get("message")
        classification = "accepted-safety-difference" if safety_difference else "strict" if strict else "codec-equivalent"
        row = {"id": case_id, "classification": classification,
               "match": expected_match, "pinned": {key: upstream.get(key) for key in
               ("ok", "mimeType", "width", "height", "hints", "message")},
               "pisharp": {key: local.get(key) for key in
               ("ok", "mimeType", "width", "height", "hints", "message")}}
        if safety_difference:
            row["safetyRationale"] = "Pinned Pi's Photon decoder accepts and forwards a WebP whose RIFF length field exceeds the available bytes; PiSharp's Skia decoder rejects it and emits the normal omission note. The stricter rejection avoids forwarding malformed data."
        rows.append(row)

    unexplained_mismatches = [row["id"] for row in rows
                              if not row["match"] and row["classification"] != "accepted-safety-difference"]
    report = {"piSha": pin, "scope": "Pinned processImage prompt normalization across PNG, JPEG, GIF and WebP pass-through, model-limited resize, EXIF orientation, BMP conversion and malformed/truncated-input handling; provider wire, clipboard acquisition and full session lifecycle have separate reports",
              "samePiCalibration": {"runs": 2, "stable": same_pi_stable,
                                   "comparison": "Exact processImage result, including output bytes, MIME and hints."},
              "strictPasses": sum(row["classification"] == "strict" and row["match"] for row in rows),
              "strictTotal": sum(row["classification"] == "strict" for row in rows),
              "codecEquivalentPasses": sum(row["classification"] == "codec-equivalent" and row["match"] for row in rows),
              "codecEquivalentTotal": sum(row["classification"] == "codec-equivalent" for row in rows),
              "acceptedSafetyDifferences": sum(row["classification"] == "accepted-safety-difference" for row in rows),
              "unexplainedMismatches": unexplained_mismatches,
              "cases": rows}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    if not same_pi_stable or unexplained_mismatches:
        raise SystemExit(f"Image differential failed; report: {args.output}")
    print(f"Matched {report['strictPasses']}/{report['strictTotal']} strict and "
          f"{report['codecEquivalentPasses']}/{report['codecEquivalentTotal']} codec-equivalent image cases; "
          f"accepted {report['acceptedSafetyDifferences']} documented safety difference(s).")


if __name__ == "__main__":
    main()
