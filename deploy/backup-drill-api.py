#!/usr/bin/env python3
"""API client for deploy/backup-drill.sh (#342). Python 3 standard library only.

    backup-drill-api.py seed        STATE SETUP_OUTPUT   sign in with setup's one-time password, set a new
                                                         password, create data, write STATE (chmod 600)
    backup-drill-api.py expect-gone STATE                the account from STATE cannot sign in (empty database)
    backup-drill-api.py verify      STATE                sign in again and find everything seed created,
                                                         including the encrypted LINE channel secret

Settings: DRILL_API (how this script reaches the api, e.g. http://127.0.0.1:18342) and DRILL_ORIGIN (the
deployment's public HTTPS origin, PUBLIC_BASE_URL, e.g. https://localhost:18342). Outside Development
/connect/* requires HTTPS, so every request carries X-Forwarded-Proto/Host as a TLS-ending reverse proxy
would (the drill's TRUSTED_PROXIES trusts every address).

Never prints a password, the one-time password, the channel secret or the access token: only ids,
HTTP statuses and comparisons.
"""

import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import string
import sys
import urllib.error
import urllib.parse
import urllib.request

API = os.environ["DRILL_API"].rstrip("/")
ORIGIN = os.environ["DRILL_ORIGIN"].rstrip("/")
FORWARDED = {
    "X-Forwarded-Proto": "https",
    "X-Forwarded-Host": urllib.parse.urlsplit(ORIGIN).netloc,
    "X-Forwarded-For": "127.0.0.1",
}
LOGIN = "drill-admin"


class Failure(Exception):
    pass


def ok(message):
    print(f"drill-api: ok   {message}", flush=True)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


OPENER = urllib.request.build_opener(NoRedirect)
LAST_BODY = [b""]


def call(method, path, *, token=None, json_body=None, form=None, raw=None, headers=None, cookie=None):
    """Returns (status, headers, body bytes); never raises for an HTTP status."""
    data = None
    request_headers = dict(FORWARDED)
    if json_body is not None:
        data = json.dumps(json_body).encode()
        request_headers["Content-Type"] = "application/json"
    elif form is not None:
        data = urllib.parse.urlencode(form).encode()
        request_headers["Content-Type"] = "application/x-www-form-urlencoded"
    elif raw is not None:
        data = raw
    if token:
        request_headers["Authorization"] = f"Bearer {token}"
    if cookie:
        request_headers["Cookie"] = cookie
    request_headers.update(headers or {})
    request = urllib.request.Request(API + path, data=data, method=method, headers=request_headers)
    try:
        with OPENER.open(request, timeout=30) as response:
            result = response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        result = error.code, error.headers, error.read()
    LAST_BODY[0] = result[2]
    return result


def expect(status, wanted, what):
    if status != wanted:
        # ProblemDetails' reason and title only (they never hold a secret), to tell refusals apart.
        detail = ""
        try:
            problem = json.loads(LAST_BODY[0].decode())
            detail = f" ({problem.get('reason') or ''} {problem.get('title') or ''} {list((problem.get('errors') or {}).keys())})"
        except (ValueError, AttributeError):
            pass
        raise Failure(f"{what}: HTTP {status}, expected {wanted}{detail}")


def as_json(body):
    return json.loads(body.decode() or "null")


def sign_in(password):
    """Cookie sign-in, then the admin's authorization code + PKCE flow. Returns (status, token)."""
    status, headers, _ = call(
        "POST", "/api/v1/auth/login", json_body={"organizationCode": None, "loginName": LOGIN, "password": password}
    )
    if status != 204:
        return status, None
    cookie = "; ".join(value.split(";", 1)[0] for value in headers.get_all("Set-Cookie") or [])
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    redirect_uri = f"{ORIGIN}/auth/callback"
    query = urllib.parse.urlencode(
        {
            "client_id": "admin-spa",
            "response_type": "code",
            "redirect_uri": redirect_uri,
            "scope": "openid",
            "state": secrets.token_urlsafe(16),
            "code_challenge": challenge,
            "code_challenge_method": "S256",
        }
    )
    status, headers, _ = call("GET", f"/connect/authorize?{query}", cookie=cookie)
    location = headers.get("Location") or ""
    if status not in (302, 303) or not location.startswith(redirect_uri):
        raise Failure(f"/connect/authorize: HTTP {status}, redirect to {location.split('?', 1)[0] or 'nowhere'}")
    code = urllib.parse.parse_qs(urllib.parse.urlsplit(location).query).get("code", [None])[0]
    if not code:
        raise Failure("/connect/authorize returned no code")
    status, _, body = call(
        "POST",
        "/connect/token",
        form={
            "grant_type": "authorization_code",
            "client_id": "admin-spa",
            "code": code,
            "redirect_uri": redirect_uri,
            "code_verifier": verifier,
        },
    )
    expect(status, 200, "/connect/token")
    return 204, as_json(body)["access_token"]


def new_password():
    # Identity's rules: at least 12 characters with upper, lower, digit and symbol.
    alphabet = string.ascii_letters + string.digits
    return "Dr1ll-" + "".join(secrets.choice(alphabet) for _ in range(20)) + "!a"


def webhook(assistant_id, secret, body):
    signature = base64.b64encode(hmac.new(secret.encode(), body, hashlib.sha256).digest()).decode()
    status, _, _ = call(
        "POST",
        f"/api/v1/line/webhook/{assistant_id}",
        raw=body,
        headers={"Content-Type": "application/json", "x-line-signature": signature},
    )
    return status


def check_secret_decrypts(state):
    """The webhook checks x-line-signature with the stored channel secret, which it has to decrypt with
    the Data Protection key ring: 200 for our signature proves the restored ciphertext opens; 401 for a
    signature made with another secret proves the check is real."""
    body = b'{"destination":"Ubackupdrill","events":[]}'
    status = webhook(state["assistantId"], state["channelSecret"], body)
    expect(status, 200, "LINE webhook signed with the stored channel secret (the secret must decrypt)")
    status = webhook(state["assistantId"], secrets.token_hex(16), body)
    expect(status, 401, "LINE webhook signed with another secret")
    ok("LINE channel secret decrypts: webhook signed with it 200, with another secret 401")


def seed(state_path, setup_output):
    with open(setup_output, encoding="utf-8") as handle:
        match = re.search(r"一次性密碼：(\S+)", handle.read())
    if not match:
        raise Failure("no one-time password in the setup output")
    one_time = match.group(1)

    status, token = sign_in(one_time)
    expect(status, 204, "sign-in with the one-time password")
    password = new_password()
    status, _, _ = call(
        "POST", "/api/v1/auth/change-password", token=token,
        json_body={"currentPassword": one_time, "newPassword": password},
    )
    expect(status, 204, "change-password")
    status, token = sign_in(password)
    expect(status, 204, "sign-in with the new password")
    status, _, body = call("GET", "/api/v1/me", token=token)
    expect(status, 200, "GET /api/v1/me")
    if as_json(body).get("passwordChangeRequired"):
        raise Failure("the password still has to be changed")
    ok("signed in with setup's one-time password, changed it, signed in again")

    status, _, body = call(
        "POST", "/api/v1/knowledge-bases", token=token, json_body={"name": "備份演練知識庫", "purpose": "備份與還原演練"}
    )
    expect(status, 201, "create a knowledge base")
    kb_id = as_json(body)["id"]
    question = "營業時間是幾點到幾點？"
    status, _, body = call(
        "POST", f"/api/v1/knowledge-bases/{kb_id}/faqs", token=token,
        json_body={"question": question, "answer": "每天早上九點到晚上六點。"},
    )
    expect(status, 201, "create an FAQ entry")
    faq_id = as_json(body)["document"]["id"]
    ok(f"knowledge base {kb_id} with FAQ entry {faq_id}")

    payload = {
        "name": "備份演練助理",
        "purpose": "回答營業時間",
        "tone": "friendly",
        "audience": "account-members",
        "roleInstructions": "",
        "sources": [{"id": kb_id, "type": "knowledge-base"}],
        "rules": {
            "knowledgeScope": "company-data-only",
            "refusalMessage": "目前的資料中找不到這個問題的答案。",
            "showCitations": True,
            "keepOwnConversations": True,
        },
    }
    status, _, body = call("POST", "/api/v1/assistant-drafts", token=token, json_body={"payload": payload})
    expect(status, 201, "create an assistant draft")
    status, _, body = call("POST", "/api/v1/assistants", token=token, json_body={"draftId": as_json(body)["id"]})
    expect(status, 201, "create the assistant from the draft")
    assistant_id = as_json(body)["id"]
    status, _, body = call("POST", f"/api/v1/assistants/{assistant_id}/chat/conversations", token=token)
    expect(status, 201, "create a conversation")
    thread_id = as_json(body)["threadId"]
    status, _, _ = call(
        "PATCH", f"/api/v1/assistants/{assistant_id}/chat/conversations/{thread_id}", token=token,
        json_body={"title": "備份演練對話"},
    )
    expect(status, 200, "rename the conversation")
    ok(f"assistant {assistant_id} with conversation {thread_id}")

    channel_secret = secrets.token_hex(16)  # 32 hexadecimal digits, as LINE's
    access_token = secrets.token_urlsafe(48)
    status, _, body = call(
        "PUT", f"/api/v1/assistants/{assistant_id}/publishing/line", token=token,
        json_body={
            "officialAccountId": "@backupdrill",
            "channelId": "".join(secrets.choice(string.digits) for _ in range(10)),
            "channelSecret": channel_secret,
            "accessToken": access_token,
            "welcomeMessage": "您好！這是備份演練。",
            "nonTextReply": "目前只能回答文字問題。",
            "revision": 0,
        },
    )
    expect(status, 200, "save the LINE channel with dummy credentials")
    ok("LINE channel saved with a dummy channel secret and access token (stored encrypted)")

    state = {
        "password": password,
        "knowledgeBaseId": kb_id,
        "faqId": faq_id,
        "faqQuestion": question,
        "assistantId": assistant_id,
        "threadId": thread_id,
        "channelSecret": channel_secret,
        "accessTokenLastFourSha256": hashlib.sha256(access_token[-4:].encode()).hexdigest(),
    }
    descriptor = os.open(state_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
        json.dump(state, handle)
    check_secret_decrypts(state)


def load(state_path):
    with open(state_path, encoding="utf-8") as handle:
        return json.load(handle)


def expect_gone(state_path):
    status, _ = sign_in(load(state_path)["password"])
    expect(status, 401, "sign-in on the empty database")
    ok("the account does not exist any more (sign-in 401)")


def verify(state_path):
    state = load(state_path)
    status, token = sign_in(state["password"])
    expect(status, 204, "sign-in with the password set before the backup")
    ok("same account signs in with the password set before the backup")

    status, _, body = call("GET", "/api/v1/assistants", token=token)
    expect(status, 200, "list assistants")
    if state["assistantId"] not in body.decode():
        raise Failure("the assistant is missing")
    status, _, body = call("GET", f"/api/v1/knowledge-bases/{state['knowledgeBaseId']}/faqs/{state['faqId']}", token=token)
    expect(status, 200, "read the FAQ entry")
    if state["faqQuestion"] not in body.decode():
        raise Failure("the FAQ entry has another question")
    status, _, body = call("GET", f"/api/v1/assistants/{state['assistantId']}/chat/conversations", token=token)
    expect(status, 200, "list conversations")
    if state["threadId"] not in body.decode() or "備份演練對話" not in body.decode():
        raise Failure("the conversation is missing")
    ok("assistant, knowledge base FAQ entry and conversation are all there")

    status, _, body = call("GET", f"/api/v1/assistants/{state['assistantId']}/publishing/line", token=token)
    expect(status, 200, "read the LINE channel")
    view = as_json(body)
    access = view.get("accessToken") or {}
    if not (view.get("channelSecret") or {}).get("configured") or not access.get("configured"):
        raise Failure("the LINE credentials are not configured any more")
    if hashlib.sha256((access.get("lastFour") or "").encode()).hexdigest() != state["accessTokenLastFourSha256"]:
        raise Failure("the access token's last four characters differ")
    ok("LINE channel still has both credentials (access token's last four match by hash)")
    check_secret_decrypts(state)


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    command, state_path = sys.argv[1], sys.argv[2]
    try:
        if command == "seed" and len(sys.argv) == 4:
            seed(state_path, sys.argv[3])
        elif command == "expect-gone":
            expect_gone(state_path)
        elif command == "verify":
            verify(state_path)
        else:
            raise SystemExit(__doc__)
    except Failure as failure:
        print(f"drill-api: FAIL: {failure}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
