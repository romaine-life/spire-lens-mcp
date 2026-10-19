"""Unit tests for the ``--auth-mode jwt`` validation path in server.py.

Network-free: an ephemeral RSA keypair is generated, tokens are signed with
PyJWT, and verified against the public key via ``server._verify_bearer_jwt`` --
no live JWKS endpoint is required.
"""

from __future__ import annotations

import asyncio
import datetime
import json
from pathlib import Path

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa

import server

ISSUER = "https://auth.romaine.life"


def _keypair():
    priv = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    return priv, priv.public_key()


def _sign(priv, **claims):
    return jwt.encode(claims, priv, algorithm="RS256")


def _now():
    return datetime.datetime.now(datetime.timezone.utc)


def _verify(token, pub, *, required_role="service", allowed=None):
    return server._verify_bearer_jwt(
        token,
        pub,
        issuer=ISSUER,
        required_role=required_role,
        allowed_actor_emails=allowed,
    )


def test_valid_service_token_returns_claims():
    priv, pub = _keypair()
    tok = _sign(
        priv,
        iss=ISSUER,
        role="service",
        actor_email="dev@example.com",
        exp=_now() + datetime.timedelta(minutes=5),
    )
    claims = _verify(tok, pub)
    assert claims["role"] == "service"
    assert claims["actor_email"] == "dev@example.com"


def test_actor_email_allowlist_accepts_listed():
    priv, pub = _keypair()
    tok = _sign(
        priv,
        iss=ISSUER,
        role="service",
        actor_email="ok@example.com",
        exp=_now() + datetime.timedelta(minutes=5),
    )
    claims = _verify(tok, pub, allowed={"ok@example.com"})
    assert claims["actor_email"] == "ok@example.com"


def test_actor_email_allowlist_rejects_unlisted():
    priv, pub = _keypair()
    tok = _sign(
        priv,
        iss=ISSUER,
        role="service",
        actor_email="nope@example.com",
        exp=_now() + datetime.timedelta(minutes=5),
    )
    with pytest.raises(server._AuthError):
        _verify(tok, pub, allowed={"only@example.com"})


def test_wrong_role_rejected():
    priv, pub = _keypair()
    tok = _sign(priv, iss=ISSUER, role="user", exp=_now() + datetime.timedelta(minutes=5))
    with pytest.raises(server._AuthError):
        _verify(tok, pub)


def test_role_check_skipped_when_required_role_none():
    priv, pub = _keypair()
    tok = _sign(
        priv, iss=ISSUER, role="anything", exp=_now() + datetime.timedelta(minutes=5)
    )
    claims = _verify(tok, pub, required_role=None)
    assert claims["role"] == "anything"


def test_wrong_issuer_rejected():
    priv, pub = _keypair()
    tok = _sign(
        priv,
        iss="https://evil.example",
        role="service",
        exp=_now() + datetime.timedelta(minutes=5),
    )
    with pytest.raises(jwt.InvalidIssuerError):
        _verify(tok, pub)


def test_expired_token_rejected():
    priv, pub = _keypair()
    tok = _sign(
        priv, iss=ISSUER, role="service", exp=_now() - datetime.timedelta(minutes=5)
    )
    with pytest.raises(jwt.ExpiredSignatureError):
        _verify(tok, pub)


def test_missing_exp_rejected():
    priv, pub = _keypair()
    tok = _sign(priv, iss=ISSUER, role="service")  # no exp claim
    with pytest.raises(jwt.MissingRequiredClaimError):
        _verify(tok, pub)


def test_signature_from_wrong_key_rejected():
    priv1, _ = _keypair()
    _, pub2 = _keypair()
    tok = _sign(
        priv1, iss=ISSUER, role="service", exp=_now() + datetime.timedelta(minutes=5)
    )
    with pytest.raises(jwt.InvalidSignatureError):
        _verify(tok, pub2)


def test_header_helper_requires_bearer_prefix():
    # The Bearer-prefix guard runs before any JWKS use, so __new__ (no fetch) is fine.
    verifier = server._JwtVerifier.__new__(server._JwtVerifier)
    with pytest.raises(server._AuthError):
        verifier.verify_authorization_header("Basic abc123")


def test_sts2_launcher_prefers_configured_scheduled_task(monkeypatch):
    monkeypatch.setenv("SPIRELENS_HOST_STS2_LAUNCH_TASK", "SpireLens STS2 Launch")
    monkeypatch.delenv("STS2_GAME_DIR", raising=False)

    assert server._resolve_sts2_launcher() == {
        "available": True,
        "kind": "scheduled_task",
        "task_name": "SpireLens STS2 Launch",
    }


def test_sts2_launcher_finds_executable_under_configured_game_dir(tmp_path, monkeypatch):
    monkeypatch.delenv("SPIRELENS_HOST_STS2_LAUNCH_TASK", raising=False)
    game_dir = tmp_path / "Slay the Spire 2"
    game_dir.mkdir()
    exe = game_dir / "SlayTheSpire2.exe"
    exe.write_text("", encoding="utf-8")
    monkeypatch.setenv("STS2_GAME_DIR", str(game_dir))

    assert server._resolve_sts2_launcher() == {
        "available": True,
        "kind": "executable",
        "path": str(exe),
        "working_directory": str(game_dir),
    }


def test_sts2_launcher_reports_missing_executable(tmp_path, monkeypatch):
    monkeypatch.delenv("SPIRELENS_HOST_STS2_LAUNCH_TASK", raising=False)
    game_dir = tmp_path / "Slay the Spire 2"
    game_dir.mkdir()
    monkeypatch.setenv("STS2_GAME_DIR", str(game_dir))

    launcher = server._resolve_sts2_launcher()

    assert launcher["available"] is False
    assert launcher["game_dir"] == str(Path(game_dir))
    assert "SlayTheSpire2.exe" in launcher["reason"]


def test_host_status_reports_game_not_running_with_start_action(monkeypatch):
    async def probe_bridge():
        return {
            "reachable": False,
            "error_type": "connect_error",
            "error": "connection refused",
        }

    monkeypatch.setattr(server, "_probe_bridge", probe_bridge)
    monkeypatch.setattr(server, "_list_sts2_processes", lambda: [])
    monkeypatch.setenv("SPIRELENS_HOST_STS2_LAUNCH_TASK", "SpireLens STS2 Launch")
    monkeypatch.delenv("STS2_GAME_DIR", raising=False)

    status = asyncio.run(server._host_status_payload())

    assert status["status"] == "game_not_running"
    assert status["game"]["running"] is False
    assert status["bridge"]["reachable"] is False
    assert status["launcher"] == {
        "available": True,
        "kind": "scheduled_task",
        "task_name": "SpireLens STS2 Launch",
    }
    assert "start_sts2" in status["next_actions"][0]


def test_bridge_connect_error_is_structured_host_status(monkeypatch):
    monkeypatch.setattr(server, "_list_sts2_processes", lambda: [])
    monkeypatch.setenv("SPIRELENS_HOST_STS2_LAUNCH_TASK", "SpireLens STS2 Launch")
    monkeypatch.delenv("STS2_GAME_DIR", raising=False)

    status = json.loads(server._handle_error(server.httpx.ConnectError("connection refused")))

    assert status["status"] == "game_not_running"
    assert status["bridge"]["error_type"] == "connect_error"
    assert status["launcher"]["kind"] == "scheduled_task"


def test_stop_sts2_processes_uses_taskkill_on_windows(monkeypatch):
    calls = []

    class Result:
        returncode = 0
        stdout = "SUCCESS"
        stderr = ""

    def fake_run(cmd, **kwargs):
        calls.append((cmd, kwargs))
        return Result()

    monkeypatch.setattr(server.platform, "system", lambda: "Windows")
    monkeypatch.setattr(server.subprocess, "run", fake_run)

    result = server._stop_sts2_processes([
        {
            "image_name": "SlayTheSpire2.exe",
            "pid": "61248",
        }
    ])

    assert result["requested"] is True
    assert result["success"] is True
    assert result["processes"][0]["success"] is True
    assert calls[0][0] == ["taskkill.exe", "/F", "/T", "/PID", "61248"]
    assert calls[0][1]["timeout"] == 20


def test_stop_sts2_returns_already_stopped_without_stop_request(monkeypatch):
    async def host_status():
        return {
            "status": "game_not_running",
            "game": {
                "running": False,
                "processes": [],
            },
        }

    def fail_stop(_processes):
        raise AssertionError("stop should not be requested")

    monkeypatch.setattr(server, "_host_status_payload", host_status)
    monkeypatch.setattr(server, "_stop_sts2_processes", fail_stop)

    result = json.loads(asyncio.run(server.stop_sts2()))

    assert result["action"] == "already_stopped"
    assert result["status"] == "game_not_running"


def test_stop_sts2_waits_until_process_exits(monkeypatch):
    statuses = [
        {
            "status": "ready",
            "game": {
                "running": True,
                "processes": [
                    {
                        "image_name": "SlayTheSpire2.exe",
                        "pid": "61248",
                    }
                ],
            },
        },
        {
            "status": "game_not_running",
            "game": {
                "running": False,
                "processes": [],
            },
        },
    ]

    async def host_status():
        return statuses.pop(0)

    waited = {}

    def fake_wait_for_exit(pids, timeout):
        waited["pids"] = pids
        waited["timeout"] = timeout
        return True

    monkeypatch.setattr(server, "_host_status_payload", host_status)
    monkeypatch.setattr(server, "_stop_sts2_processes", lambda _processes: {"requested": True, "success": True})
    monkeypatch.setattr(server, "_wait_for_pids_exit_blocking", fake_wait_for_exit)

    result = json.loads(asyncio.run(server.stop_sts2(wait_for_exit=True, timeout_seconds=5)))

    assert waited == {"pids": [61248], "timeout": 5}
    assert result["action"] == "stopped"
    assert result["status"] == "game_not_running"
    assert result["stop_result"] == {"requested": True, "success": True}


def test_stop_sts2_reports_timeout_when_process_handles_stay_open(monkeypatch):
    statuses = [
        {"status": "ready", "game": {"running": True, "processes": [{"image_name": "SlayTheSpire2.exe", "pid": "61248"}]}},
        {"status": "ready", "game": {"running": True, "processes": [{"image_name": "SlayTheSpire2.exe", "pid": "61248"}]}},
    ]

    async def host_status():
        return statuses.pop(0)

    monkeypatch.setattr(server, "_host_status_payload", host_status)
    monkeypatch.setattr(server, "_stop_sts2_processes", lambda _processes: {"requested": True, "success": True})
    monkeypatch.setattr(server, "_wait_for_pids_exit_blocking", lambda _pids, _timeout: False)

    result = json.loads(asyncio.run(server.stop_sts2(wait_for_exit=True, timeout_seconds=5)))

    assert result["action"] == "stop_timeout"


def test_start_sts2_waits_on_bridge_ready_file(tmp_path, monkeypatch):
    statuses = [
        {"status": "game_not_running", "game": {"running": False, "processes": []}, "launcher": {"available": True, "kind": "scheduled_task", "task_name": "t"}},
        {"status": "ready", "game": {"running": True, "processes": [{"image_name": "SlayTheSpire2.exe", "pid": "61248"}]}},
    ]

    async def host_status():
        return statuses.pop(0)

    ready_file = tmp_path / "bridge-ready.json"
    waited = {}

    def fake_wait_for_ready(path, timeout):
        waited["path"] = path
        waited["timeout"] = timeout
        return {"pid": 61248}

    monkeypatch.setenv("SPIRELENS_BRIDGE_READY_FILE", str(ready_file))
    monkeypatch.setattr(server, "_host_status_payload", host_status)
    monkeypatch.setattr(server, "_start_sts2_with_launcher", lambda _launcher: {"launcher": "scheduled_task"})
    monkeypatch.setattr(server, "_wait_for_ready_file_blocking", fake_wait_for_ready)

    result = json.loads(asyncio.run(server.start_sts2(wait_for_bridge=True, timeout_seconds=7)))

    assert waited == {"path": ready_file, "timeout": 7}
    assert result["action"] == "started_and_ready"


def test_ready_file_must_name_a_live_sts2_process(tmp_path, monkeypatch):
    ready_file = tmp_path / "bridge-ready.json"
    ready_file.write_text(json.dumps({"pid": 61248, "port": 15526}), encoding="utf-8")

    monkeypatch.setattr(server, "_list_sts2_processes", lambda: [{"image_name": "SlayTheSpire2.exe", "pid": "61248"}])
    assert server._read_valid_ready_file(ready_file) == {"pid": 61248, "port": 15526}

    monkeypatch.setattr(server, "_list_sts2_processes", lambda: [])
    assert server._read_valid_ready_file(ready_file) is None


def test_restart_sts2_combines_stop_and_start(monkeypatch):
    async def fake_stop_sts2(wait_for_exit=True, timeout_seconds=45):
        assert wait_for_exit is True
        assert timeout_seconds == 30
        return json.dumps({
            "action": "stopped",
            "status": "game_not_running",
            "game": {
                "running": False,
                "processes": [],
            },
        })

    async def fake_start_sts2(wait_for_bridge=True, timeout_seconds=90):
        assert wait_for_bridge is True
        assert 0 <= timeout_seconds <= 30
        return json.dumps({
            "action": "started_and_ready",
            "status": "ready",
            "game": {
                "running": True,
                "processes": [
                    {
                        "image_name": "SlayTheSpire2.exe",
                        "pid": "61248",
                    }
                ],
            },
        })

    monkeypatch.setattr(server, "stop_sts2", fake_stop_sts2)
    monkeypatch.setattr(server, "start_sts2", fake_start_sts2)

    result = json.loads(asyncio.run(server.restart_sts2(wait_for_bridge=True, timeout_seconds=30)))

    assert result["action"] == "restarted_ready"
    assert result["status"] == "ready"
    assert result["stop_result"]["action"] == "stopped"
