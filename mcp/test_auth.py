"""Unit tests for the ``--auth-mode jwt`` validation path in server.py.

Network-free: an ephemeral RSA keypair is generated, tokens are signed with
PyJWT, and verified against the public key via ``server._verify_bearer_jwt`` --
no live JWKS endpoint is required.
"""

from __future__ import annotations

import datetime

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
