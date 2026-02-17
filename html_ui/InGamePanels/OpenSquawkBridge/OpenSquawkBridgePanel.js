(function () {
  const TOKEN_KEY = "opensquawk.bridge.token.v1";
  const CONFIG_KEY = "opensquawk.bridge.config.v1";

  function loadConfig() {
    try {
      const raw = window.localStorage.getItem(CONFIG_KEY);
      return raw ? JSON.parse(raw) : {};
    } catch (err) {
      return {};
    }
  }

  function saveConfig(config) {
    window.localStorage.setItem(CONFIG_KEY, JSON.stringify(config));
  }

  function loadToken() {
    try {
      const raw = window.localStorage.getItem(TOKEN_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch (err) {
      return null;
    }
  }

  function setStatus(text, ok) {
    const el = document.getElementById("osb-status");
    if (!el) return;
    el.textContent = text;
    el.style.color = ok ? "#2ed3a2" : "#e05d5d";
  }

  function updateUi() {
    const token = loadToken();
    const config = loadConfig();
    const tokenInput = document.getElementById("osb-token");
    const loginInput = document.getElementById("osb-login");
    const baseInput = document.getElementById("osb-base");
    const authInput = document.getElementById("osb-auth");

    if (tokenInput) tokenInput.value = token?.token || "";
    if (loginInput) loginInput.value = window.OpenSquawkBridge?.loginUrl || "";
    if (baseInput) baseInput.value = config.baseUrl || "https://opensquawk.de";
    if (authInput) authInput.value = config.authToken || "";

    const sim = document.getElementById("osb-sim");
    const flight = document.getElementById("osb-flight");
    const last = document.getElementById("osb-last");

    if (sim) sim.textContent = window.Module ? "connected" : "unknown";
    if (flight) flight.textContent = window.OpenSquawkBridge?.lastFlightActive ? "active" : "idle";
    if (last) last.textContent = window.OpenSquawkBridge?.lastTelemetryAt
      ? new Date(window.OpenSquawkBridge.lastTelemetryAt).toLocaleTimeString()
      : "-";
  }

  function openLogin() {
    const url = window.OpenSquawkBridge?.loginUrl;
    if (url) {
      window.open(url, "_blank");
    }
  }

  function resetToken() {
    window.localStorage.removeItem(TOKEN_KEY);
    window.location.reload();
  }

  function copyToken() {
    const token = loadToken();
    if (!token?.token) return;
    navigator.clipboard.writeText(token.token).catch(() => {});
  }

  function forcePoll() {
    if (window.OpenSquawkBridge?.forceLoginPoll) {
      window.OpenSquawkBridge.forceLoginPoll();
    }
  }

  function saveConfigFromUi() {
    const baseInput = document.getElementById("osb-base");
    const authInput = document.getElementById("osb-auth");
    const config = loadConfig();
    config.baseUrl = baseInput?.value?.trim() || "https://opensquawk.de";
    config.authToken = authInput?.value?.trim() || "";
    saveConfig(config);
    window.location.reload();
  }

  function wire() {
    document.getElementById("osb-open")?.addEventListener("click", openLogin);
    document.getElementById("osb-reset")?.addEventListener("click", resetToken);
    document.getElementById("osb-copy")?.addEventListener("click", copyToken);
    document.getElementById("osb-poll")?.addEventListener("click", forcePoll);
    document.getElementById("osb-save")?.addEventListener("click", saveConfigFromUi);

    updateUi();
    setInterval(updateUi, 1000);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", wire);
  } else {
    wire();
  }
})();
