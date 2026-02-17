import os
import threading
from pathlib import Path

from bridge.main import register, telemetry_loop


def _load_dotenv(path: Path | None = None, override: bool = False) -> None:
    env_path = path or (Path(__file__).resolve().parent / ".env")
    if not env_path.exists():
        return

    try:
        lines = env_path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return

    for raw_line in lines:
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue

        if line.startswith("export "):
            line = line[7:].strip()

        if "=" not in line:
            continue

        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip()

        if not key:
            continue

        if value and value[0] in {'"', "'"} and value[-1:] == value[0]:
            value = value[1:-1]
        else:
            inline_comment_pos = value.find(" #")
            if inline_comment_pos >= 0:
                value = value[:inline_comment_pos].rstrip()

        if not override and key in os.environ:
            continue

        os.environ[key] = value


def _env_bool(name: str, default: bool = False) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def _run_server() -> None:
    host = os.getenv("SERVER_HOST", "0.0.0.0")
    port = int(os.getenv("SERVER_PORT", "5000"))
    debug = _env_bool("SERVER_DEBUG", False)
    use_reloader = _env_bool("SERVER_RELOADER", False)

    try:
        from server.server import app
    except Exception as exc:
        print(f"[server] start failed: {exc}")
        return

    app.run(host=host, port=port, debug=debug, use_reloader=use_reloader)


def main() -> None:
    _load_dotenv()

    server_thread = threading.Thread(target=_run_server, name="opensquawk-server", daemon=True)
    server_thread.start()

    register()
    telemetry_loop()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("Stopped.")
