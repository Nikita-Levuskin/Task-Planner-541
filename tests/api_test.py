"""HTTP integration tests. Uses a private temporary database and a local server."""
import http.cookiejar
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DOTNET = os.environ.get("DOTNET_EXE", "dotnet")


class Client:
    def __init__(self, base):
        self.base = base
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))
        self.token = None

    def call(self, method, path, body=None, headers=None):
        h = {"Content-Type": "application/json", "X-Planner-Request": "1"}
        if self.token:
            h["Authorization"] = "Bearer " + self.token
        h.update(headers or {})
        req = urllib.request.Request(self.base + "/api" + path,
            data=json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None),
            headers=h, method=method)
        try:
            response = self.opener.open(req, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        raw = response.read()
        try:
            payload = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            payload = raw.decode(errors="replace")
        return response.status, payload

    def register(self, email):
        return self.call("POST", "/auth/register", {"email": email, "password": "Coursework-pass-123"})

    def login(self, email):
        status, body = self.call("POST", "/auth/login", {"email": email, "password": "Coursework-pass-123"})
        if status == 200:
            self.token = body["accessToken"]
        return status, body


class ApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="task-planner-api-")
        sock = socket.socket()
        sock.bind(("127.0.0.1", 0))
        cls.port = sock.getsockname()[1]
        sock.close()
        cls.base = f"http://127.0.0.1:{cls.port}"
        cls.log = open(Path(cls.temp.name) / "server.log", "w+", encoding="utf-8")
        cls.start_server()
        cls.owner, cls.other = Client(cls.base), Client(cls.base)
        for client, email in [(cls.owner, "owner@example.com"), (cls.other, "other@example.com")]:
            assert client.register(email)[0] == 201
            assert client.login(email)[0] == 200

    @classmethod
    def start_server(cls):
        env = dict(os.environ, ASPNETCORE_URLS=cls.base, DataDirectory=cls.temp.name,
                   DOTNET_ENVIRONMENT="Production")
        cls.process = subprocess.Popen([DOTNET, str(ROOT / "src/TaskPlanner/bin/Release/net10.0/TaskPlanner.dll")],
            cwd=ROOT / "src/TaskPlanner", env=env, stdout=cls.log, stderr=subprocess.STDOUT)
        for _ in range(100):
            if cls.process.poll() is not None:
                cls.log.flush()
                raise RuntimeError(Path(cls.log.name).read_text())
            try:
                if Client(cls.base).call("GET", "/health")[0] == 200:
                    return
            except OSError:
                time.sleep(0.1)
        raise RuntimeError("Server startup timed out")

    @classmethod
    def stop_server(cls):
        cls.process.terminate()
        try:
            cls.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            cls.process.kill()
            cls.process.wait(timeout=5)

    @classmethod
    def tearDownClass(cls):
        cls.stop_server()
        cls.log.close()
        cls.temp.cleanup()

    def task(self, title="Task", due="2090-01-01T12:00:00Z"):
        status, body = self.owner.call("POST", "/tasks", {"title": title, "description": "text", "dueAt": due})
        self.assertEqual(status, 201, body)
        return body["id"]

    def test_01_unauthorized(self):
        self.assertEqual(Client(self.base).call("GET", "/tasks")[0], 401)

    def test_02_duplicate_account(self):
        self.assertEqual(Client(self.base).register("OWNER@example.com")[0], 409)

    def test_03_invalid_account(self):
        status, _ = Client(self.base).call("POST", "/auth/register", {"email": "not-email", "password": "x"})
        self.assertEqual(status, 400)

    def test_04_wrong_password(self):
        status, _ = Client(self.base).call("POST", "/auth/login", {"email": "owner@example.com", "password": "wrong-password"})
        self.assertEqual(status, 401)

    def test_05_create_and_edit(self):
        task_id = self.task("Before")
        self.assertEqual(self.owner.call("PUT", f"/tasks/{task_id}", {"title": "After", "description": None, "dueAt": "2090-02-01T12:00:00Z"})[0], 204)
        _, rows = self.owner.call("GET", "/tasks?search=After")
        self.assertTrue(any(x["id"] == task_id and x["title"] == "After" for x in rows))

    def test_06_invalid_task(self):
        for title in [" ", "x" * 201]:
            with self.subTest(title=title[:10]):
                self.assertEqual(self.owner.call("POST", "/tasks", {"title": title, "dueAt": "2090-01-01T12:00:00Z"})[0], 400)

    def test_07_task_isolation(self):
        task_id = self.task("Private")
        _, rows = self.other.call("GET", "/tasks")
        self.assertFalse(any(x["id"] == task_id for x in rows))
        self.assertEqual(self.other.call("DELETE", f"/tasks/{task_id}")[0], 403)
        self.assertEqual(self.other.call("PUT", f"/tasks/{task_id}", {"title": "attack", "dueAt": "2090-01-01T12:00:00Z"})[0], 403)

    def test_08_completion_idempotent(self):
        task_id = self.task()
        for _ in range(2):
            self.assertEqual(self.owner.call("PATCH", f"/tasks/{task_id}/completion", {"completed": True})[0], 204)
        _, rows = self.owner.call("GET", "/tasks?state=completed")
        self.assertEqual(sum(x["id"] == task_id for x in rows), 1)

    def test_09_sorting(self):
        self.task("Sort early", "2089-01-01T00:00:00Z")
        self.task("Sort late", "2091-01-01T00:00:00Z")
        _, rows = self.owner.call("GET", "/tasks?sort=due")
        self.assertEqual([r["dueAt"] for r in rows], sorted(r["dueAt"] for r in rows))

    def test_10_filters(self):
        self.assertEqual(self.owner.call("GET", "/tasks?state=unknown")[0], 400)
        self.assertEqual(self.owner.call("GET", "/tasks?sort=unknown")[0], 400)

    def test_11_sharing_read_and_write(self):
        task_id = self.task("Share me")
        self.assertEqual(self.owner.call("POST", f"/tasks/{task_id}/shares", {"email": "other@example.com", "canEdit": False})[0], 204)
        _, rows = self.other.call("GET", "/tasks")
        shared = next(x for x in rows if x["id"] == task_id)
        self.assertFalse(shared["isOwner"] or shared["canEdit"])
        self.assertEqual(self.other.call("PATCH", f"/tasks/{task_id}/completion", {"completed": True})[0], 403)
        self.owner.call("POST", f"/tasks/{task_id}/shares", {"email": "other@example.com", "canEdit": True})
        self.assertEqual(self.other.call("PATCH", f"/tasks/{task_id}/completion", {"completed": True})[0], 204)
        self.assertEqual(self.other.call("DELETE", f"/tasks/{task_id}")[0], 403)
        self.assertEqual(self.other.call("GET", f"/tasks/{task_id}/shares")[0], 403)
        _, shares = self.owner.call("GET", f"/tasks/{task_id}/shares")
        self.owner.call("DELETE", f"/tasks/{task_id}/shares/{shares[0]['userId']}")
        _, rows = self.other.call("GET", "/tasks")
        self.assertFalse(any(x["id"] == task_id for x in rows))

    def test_12_refresh_rotation_and_csrf(self):
        client = Client(self.base)
        self.assertEqual(client.login("owner@example.com")[0], 200)
        old_cookie = next(c.value for c in client.cookies if c.name == "planner_refresh")
        self.assertEqual(client.call("POST", "/auth/refresh", headers={"Origin": "https://evil.invalid"})[0], 403)
        self.assertEqual(client.call("POST", "/auth/refresh")[0], 200)
        attacker = Client(self.base)
        self.assertEqual(attacker.call("POST", "/auth/refresh", headers={"Cookie": "planner_refresh=" + old_cookie})[0], 401)

    def test_13_logout_revokes_refresh(self):
        client = Client(self.base)
        client.login("owner@example.com")
        cookie = next(c.value for c in client.cookies if c.name == "planner_refresh")
        self.assertEqual(client.call("POST", "/auth/logout")[0], 204)
        self.assertEqual(Client(self.base).call("POST", "/auth/refresh", headers={"Cookie": "planner_refresh=" + cookie})[0], 401)

    def test_14_tampered_token(self):
        client = Client(self.base)
        token = self.owner.token
        assert token
        parts = token.split(".")
        parts[2] = ("a" if parts[2][0] != "a" else "b") + parts[2][1:]
        client.token = ".".join(parts)
        self.assertEqual(client.call("GET", "/tasks")[0], 401)

    def test_15_delete_and_missing(self):
        task_id = self.task()
        self.assertEqual(self.owner.call("DELETE", f"/tasks/{task_id}")[0], 204)
        self.assertEqual(self.owner.call("DELETE", f"/tasks/{task_id}")[0], 404)

    def test_16_persistence_and_notifications(self):
        task_id = self.task("Due reminder", "2020-01-01T00:00:00Z")
        for _ in range(70):
            _, inbox = self.owner.call("GET", "/notifications")
            if any(n["taskId"] == task_id for n in inbox):
                break
            time.sleep(0.1)
        else:
            self.fail("Background worker did not deliver reminder")
        item = next(n for n in inbox if n["taskId"] == task_id)
        self.assertEqual(self.other.call("PATCH", f"/notifications/{item['id']}/read")[0], 403)
        self.assertEqual(self.owner.call("PATCH", f"/notifications/{item['id']}/read")[0], 204)
        self.stop_server()
        self.start_server()
        _, rows = self.owner.call("GET", "/tasks")
        self.assertTrue(any(x["id"] == task_id for x in rows))
        _, inbox = self.owner.call("GET", "/notifications")
        self.assertEqual(sum(n["taskId"] == task_id for n in inbox), 1)
        self.assertTrue(next(n for n in inbox if n["taskId"] == task_id)["isRead"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
