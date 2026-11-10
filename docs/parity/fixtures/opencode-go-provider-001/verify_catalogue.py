#!/usr/bin/env python3
"""Verify PiSharp OpenCode catalogues against the pinned generated Pi model data."""
import collections
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PISHARP_DATA = ROOT.parents[3] / "src" / "PiSharp.Cli" / "Providers" / "Data"
SOURCES = {
    "opencode": ROOT / "source" / "pinned-pi-opencode.json",
    "opencode-go": ROOT / "source" / "pinned-pi-opencode-go.json",
}
CATALOGUES = {
    "opencode": PISHARP_DATA / "opencode-models.json",
    "opencode-go": PISHARP_DATA / "opencode-go-models.json",
}


def convert(provider, source):
    models = []
    for api, group in source.items():
        for model in group.values():
            if model.get("type") != "chat":
                continue
            item = collections.OrderedDict()
            item["id"] = model["id"]
            item["owner"] = provider
            item["contextLength"] = model["contextWindow"]
            item["status"] = "pinned catalogue"
            item["reasoning"] = model.get("reasoning")
            cost = model.get("cost")
            if cost is not None:
                pricing = collections.OrderedDict()
                pricing["input"] = cost["input"]
                pricing["output"] = cost["output"]
                if "cacheRead" in cost:
                    pricing["cachedInput"] = cost["cacheRead"]
                if "cacheWrite" in cost:
                    pricing["cachedWrite"] = cost["cacheWrite"]
                if cost.get("tiers"):
                    tiers = []
                    for tier in cost["tiers"]:
                        value = collections.OrderedDict()
                        value["inputTokensAbove"] = tier["inputTokensAbove"]
                        value["input"] = tier["input"]
                        value["output"] = tier["output"]
                        if "cacheRead" in tier:
                            value["cachedInput"] = tier["cacheRead"]
                        if "cacheWrite" in tier:
                            value["cachedWrite"] = tier["cacheWrite"]
                        tiers.append(value)
                    pricing["tiers"] = tiers
                item["pricing"] = pricing
            item["provider"] = provider
            item["name"] = model["name"]
            item["maxOutputTokens"] = model.get("maxTokens")
            item["input"] = model.get("input")
            item["api"] = model["api"]
            item["baseUrl"] = model["baseUrl"]
            if "inputLimits" in model:
                item["inputLimits"] = model["inputLimits"]
            if "thinkingLevelMap" in model:
                item["thinkingLevelMap"] = model["thinkingLevelMap"]
            if "compat" in model:
                item["compatibility"] = model["compat"]
            models.append(item)
    return models


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


receipt = {"providers": {}, "ok": True}
for provider in SOURCES:
    source = json.loads(SOURCES[provider].read_text())
    expected = convert(provider, source)
    actual = json.loads(CATALOGUES[provider].read_text())
    if actual != expected:
        receipt["ok"] = False
        receipt["providers"][provider] = {
            "match": False,
            "sourceSha256": digest(SOURCES[provider]),
            "catalogueSha256": digest(CATALOGUES[provider]),
        }
        continue
    receipt["providers"][provider] = {
        "match": True,
        "chatModels": len(actual),
        "sourceSha256": digest(SOURCES[provider]),
        "catalogueSha256": digest(CATALOGUES[provider]),
        "protocols": dict(collections.Counter(model["api"] for model in actual)),
    }

print(json.dumps(receipt, indent=1))
raise SystemExit(0 if receipt["ok"] else 1)
