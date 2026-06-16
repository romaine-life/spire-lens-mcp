"""Unit tests for exact-match collapse of false substring-ambiguous catalog lookups.

Network-free: the helpers under test are pure functions over the ``matches[]``
array that the C# model catalog returns, so no live bridge / STS2 is required.

Regression target: spirelens#148 run 4.1 aborted at llm-verify/prepare-scenario
because ``lookup_relic('Happy Flower')`` / ``lookup_relic('HAPPY_FLOWER')`` both
returned status=ambiguous -- the catalog substring-matches the real relic id
``HAPPY_FLOWER`` against the event relic ``FAKE_HAPPY_FLOWER`` (and normalizes
``Happy Flower???`` to the same key as ``Happy Flower``). The test-plan agent
aborts on ambiguous, so scenario_setup is never written and the verification
phase refuses to run.
"""

from __future__ import annotations

import json

import server


def _happy_flower_matches():
    # Mirrors the real ambiguous payload recorded in spirelens#148 run 4.1.
    return [
        {"id": "HAPPY_FLOWER", "name": "Happy Flower", "rarity": "Common",
         "description": "Every 3 turns, gain energy."},
        {"id": "FAKE_HAPPY_FLOWER", "name": "Happy Flower???", "rarity": "Event",
         "description": "Every 5 turns, gain energy."},
    ]


def _ambiguous(matches, query):
    return {
        "status": "ambiguous",
        "kind": "relic",
        "query": query,
        "match_count": len(matches),
        "matches": list(matches),
        "error": f"{len(matches)} relics matched '{query}'. The issue is ambiguous.",
    }


def test_exact_id_query_collapses_substring_ambiguity():
    data = _ambiguous(_happy_flower_matches(), "HAPPY_FLOWER")
    out = server._collapse_exact_catalog_match(data, "relic", "HAPPY_FLOWER", "RELIC")
    assert out["status"] == "ok"
    assert out["relic"]["id"] == "HAPPY_FLOWER"
    assert out["match_count"] == 1
    assert out["disambiguated_by"] == "exact_match"
    assert "error" not in out


def test_exact_name_query_excludes_punctuation_variant():
    # 'Happy Flower' must NOT also match 'Happy Flower???' (punctuation-sensitive).
    data = _ambiguous(_happy_flower_matches(), "Happy Flower")
    out = server._collapse_exact_catalog_match(data, "relic", "Happy Flower", "RELIC")
    assert out["status"] == "ok"
    assert out["relic"]["id"] == "HAPPY_FLOWER"


def test_prefixed_id_query_collapses():
    data = _ambiguous(_happy_flower_matches(), "RELIC.HAPPY_FLOWER")
    out = server._collapse_exact_catalog_match(data, "relic", "RELIC.HAPPY_FLOWER", "RELIC")
    assert out["status"] == "ok"
    assert out["relic"]["id"] == "HAPPY_FLOWER"


def test_lowercase_id_query_collapses():
    data = _ambiguous(_happy_flower_matches(), "happy_flower")
    out = server._collapse_exact_catalog_match(data, "relic", "happy_flower", "RELIC")
    assert out["status"] == "ok"
    assert out["relic"]["id"] == "HAPPY_FLOWER"


def test_exact_query_for_the_fake_resolves_to_fake_only():
    data = _ambiguous(_happy_flower_matches(), "FAKE_HAPPY_FLOWER")
    out = server._collapse_exact_catalog_match(data, "relic", "FAKE_HAPPY_FLOWER", "RELIC")
    assert out["status"] == "ok"
    assert out["relic"]["id"] == "FAKE_HAPPY_FLOWER"


def test_exact_name_with_question_marks_resolves_to_fake():
    data = _ambiguous(_happy_flower_matches(), "Happy Flower???")
    out = server._collapse_exact_catalog_match(data, "relic", "Happy Flower???", "RELIC")
    assert out["status"] == "ok"
    assert out["relic"]["id"] == "FAKE_HAPPY_FLOWER"


def test_genuine_partial_query_stays_ambiguous():
    # 'Flower' is an exact id/name of neither -> still genuinely ambiguous.
    data = _ambiguous(_happy_flower_matches(), "Flower")
    out = server._collapse_exact_catalog_match(data, "relic", "Flower", "RELIC")
    assert out["status"] == "ambiguous"
    assert out["match_count"] == 2


def test_two_exact_id_hits_stay_ambiguous():
    matches = [{"id": "DUP", "name": "Dup A"}, {"id": "DUP", "name": "Dup B"}]
    data = _ambiguous(matches, "DUP")
    out = server._collapse_exact_catalog_match(data, "relic", "DUP", "RELIC")
    assert out["status"] == "ambiguous"


def test_ok_result_passes_through_unchanged_identity():
    data = {"status": "ok", "kind": "relic", "relic": {"id": "X"}, "matches": [{"id": "X"}]}
    assert server._collapse_exact_catalog_match(data, "relic", "X", "RELIC") is data


def test_not_found_result_passes_through_unchanged_identity():
    data = {"status": "not_found", "kind": "relic", "matches": []}
    assert server._collapse_exact_catalog_match(data, "relic", "NOPE", "RELIC") is data


def test_character_path_without_prefix_uses_name_and_raw_id():
    matches = [
        {"id": "IRONCLAD", "name": "The Ironclad"},
        {"id": "FAKE_IRONCLAD", "name": "The Ironclad???"},
    ]
    data = _ambiguous(matches, "IRONCLAD")
    out = server._collapse_exact_catalog_match(data, "character", "IRONCLAD", None)
    assert out["status"] == "ok"
    assert out["character"]["id"] == "IRONCLAD"


def test_is_exact_hit_direct():
    real = {"id": "HAPPY_FLOWER", "name": "Happy Flower"}
    fake = {"id": "FAKE_HAPPY_FLOWER", "name": "Happy Flower???"}
    assert server._is_exact_catalog_hit(real, "HAPPY_FLOWER", "RELIC") is True
    assert server._is_exact_catalog_hit(real, "Happy Flower", "RELIC") is True
    assert server._is_exact_catalog_hit(fake, "HAPPY_FLOWER", "RELIC") is False
    assert server._is_exact_catalog_hit(fake, "Happy Flower", "RELIC") is False
    assert server._is_exact_catalog_hit(real, "Flower", "RELIC") is False


def test_passthrough_returns_valid_json_when_collapsed(monkeypatch):
    # End-to-end through the agent-facing seam: a stubbed catalog POST returning
    # the ambiguous payload must come back collapsed to status=ok JSON.
    async def fake_post(body):
        return json.dumps(_ambiguous(_happy_flower_matches(), body["query"]))

    monkeypatch.setattr(server, "_catalog_post", fake_post)

    import asyncio

    raw = asyncio.run(
        server._catalog_lookup_passthrough(
            {"action": "lookup_relic", "query": "HAPPY_FLOWER", "max_matches": 10},
            "relic", "HAPPY_FLOWER", "RELIC",
        )
    )
    parsed = json.loads(raw)
    assert parsed["status"] == "ok"
    assert parsed["relic"]["id"] == "HAPPY_FLOWER"
