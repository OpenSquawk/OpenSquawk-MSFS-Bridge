import os
import threading

from bridge.main import register, telemetry_loop


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
    server_thread = threading.Thread(target=_run_server, name="opensquawk-server", daemon=True)
    server_thread.start()

    register()
    telemetry_loop()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("Stopped.")
