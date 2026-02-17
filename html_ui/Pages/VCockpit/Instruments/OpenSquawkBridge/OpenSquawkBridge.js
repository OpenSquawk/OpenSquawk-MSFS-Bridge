/* OpenSquawk Bridge host (Coherent/JS side)
 * Handles auth, telemetry POST, and applies response commands via WASM exports.
 */

(function () {
  const DEFAULTS = {
    baseUrl: "https://opensquawk.de",
    meUrl: null,
    statusUrl: null,
    dataUrl: null,
    activeIntervalSec: 30,
    idleIntervalSec: 120,
    requestTimeoutMs: 10000,
    authToken: "",
  };

  const TOKEN_KEY = "opensquawk.bridge.token.v1";
  const CONFIG_KEY = "opensquawk.bridge.config.v1";

  const state = {
    token: null,
    tokenCreatedAt: null,
    connected: false,
    lastStatusAt: 0,
    lastTelemetryAt: 0,
    inFlight: false,
  };

  function loadConfig() {
    try {
      const raw = window.localStorage.getItem(CONFIG_KEY);
      if (!raw) return { ...DEFAULTS };
      const parsed = JSON.parse(raw);
      return { ...DEFAULTS, ...parsed };
    } catch (err) {
      return { ...DEFAULTS };
    }
  }

  function saveToken(token, createdAt) {
    const payload = { token, createdAt };
    window.localStorage.setItem(TOKEN_KEY, JSON.stringify(payload));
  }

  function loadToken() {
    try {
      const raw = window.localStorage.getItem(TOKEN_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw);
      if (!parsed || !parsed.token) return null;
      return parsed;
    } catch (err) {
      return null;
    }
  }

  function base64UrlEncode(bytes) {
    let binary = "";
    for (let i = 0; i < bytes.length; i++) {
      binary += String.fromCharCode(bytes[i]);
    }
    const base64 = btoa(binary);
    return base64.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
  }

  function ensureToken() {
    const existing = loadToken();
    if (existing) {
      state.token = existing.token;
      state.tokenCreatedAt = existing.createdAt;
      return;
    }

    const bytes = new Uint8Array(32);
    window.crypto.getRandomValues(bytes);
    const token = base64UrlEncode(bytes);
    const createdAt = new Date().toISOString();
    saveToken(token, createdAt);
    state.token = token;
    state.tokenCreatedAt = createdAt;
  }

  function buildUrls(config) {
    const baseUrl = config.baseUrl.replace(/\/$/, "");
    return {
      baseUrl,
      meUrl: config.meUrl || `${baseUrl}/api/bridge/me`,
      statusUrl: config.statusUrl || `${baseUrl}/api/bridge/status`,
      dataUrl: config.dataUrl || `${baseUrl}/api/bridge/data`,
      loginUrl: `${baseUrl}/bridge/connect?token=${encodeURIComponent(state.token)}`,
    };
  }

  function wasm() {
    return window.Module || window.OpenSquawkBridgeWasm || null;
  }

  function callWasm(name, retType, argTypes, args) {
    const mod = wasm();
    if (!mod || !mod.ccall) return null;
    return mod.ccall(name, retType, argTypes, args);
  }

  function getSnapshot() {
    const json = callWasm("osb_get_snapshot_json", "string", [], []);
    if (!json) return null;
    try {
      const parsed = JSON.parse(json);
      if (!parsed || !Object.keys(parsed).length) return null;
      return parsed;
    } catch (err) {
      return null;
    }
  }

  function snapshotAgeMs() {
    return callWasm("osb_get_snapshot_age_ms", "number", [], []) || 0;
  }

  function headers(config) {
    const result = {
      "Content-Type": "application/json",
      "X-Bridge-Token": state.token,
    };
    if (config.authToken) {
      result["Authorization"] = `Bearer ${config.authToken}`;
    }
    return result;
  }

  function shouldSendTelemetry(snapshot, now, config) {
    if (!snapshot) return false;
    if (snapshotAgeMs() > 10000) return false;
    if (now - state.lastTelemetryAt < config.activeIntervalSec * 1000) return false;
    const lat = snapshot.latitude;
    const lon = snapshot.longitude;
    if (typeof lat !== "number" || typeof lon !== "number") return false;
    if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return false;
    return true;
  }

  function buildTelemetryPayload(snapshot) {
    const nowSec = Math.floor(Date.now() / 1000);
    const n1 = snapshot.turbine_n1_pct || 0;
    const engOn = (snapshot.engine_combustion || 0) > 0 || n1 > 5;

    return {
      token: state.token,
      status: "active",
      ts: nowSec,
      latitude: +snapshot.latitude.toFixed(6),
      longitude: +snapshot.longitude.toFixed(6),
      altitude_ft_true: Math.round(snapshot.altitude_ft_true),
      altitude_ft_indicated: Math.round(snapshot.altitude_ft_indicated),
      ias_kt: +snapshot.ias_kt.toFixed(1),
      tas_kt: +snapshot.tas_kt.toFixed(1),
      groundspeed_kt: +(snapshot.ground_velocity_mps * 1.943844).toFixed(1),
      on_ground: !!snapshot.on_ground,
      eng_on: engOn,
      n1_pct: +n1.toFixed(1),
      transponder_code: Math.round(snapshot.transponder_code || 0),
      adf_active_freq: snapshot.adf_active_freq_khz || 0,
      adf_standby_freq_hz: Math.round((snapshot.adf_standby_freq_khz || 0) * 1000),
      vertical_speed_fpm: Math.round(snapshot.vertical_speed_fpm || 0),
      pitch_deg: +(snapshot.pitch_deg || 0).toFixed(1),
      n1_pct_2: +(snapshot.turbine_n1_pct_2 || 0).toFixed(1),
      gear_handle: !!snapshot.gear_handle,
      flaps_index: Math.round(snapshot.flaps_index || 0),
      parking_brake: !!snapshot.parking_brake,
      autopilot_master: !!snapshot.autopilot_master,
    };
  }

  function buildStatusPayload(simConnected, flightActive) {
    return {
      token: state.token,
      simConnected: !!simConnected,
      flightActive: !!flightActive,
    };
  }

  function normalizeKey(key) {
    return key.toLowerCase().replace(/-/g, "_");
  }

  function coerceBool(value) {
    if (typeof value === "boolean") return value;
    if (typeof value === "number") return value >= 0.5;
    if (typeof value === "string") {
      const v = value.trim().toLowerCase();
      if (["1", "true", "yes", "on"].includes(v)) return true;
      if (["0", "false", "no", "off"].includes(v)) return false;
    }
    return null;
  }

  function coerceInt(value) {
    if (typeof value === "number" && Number.isFinite(value)) return Math.round(value);
    if (typeof value === "boolean") return value ? 1 : 0;
    if (typeof value === "string") {
      const n = Number(value);
      if (Number.isFinite(n)) return Math.round(n);
    }
    return null;
  }

  function coerceNumber(value) {
    if (typeof value === "number" && Number.isFinite(value)) return value;
    if (typeof value === "string") {
      const n = Number(value);
      if (Number.isFinite(n)) return n;
    }
    if (typeof value === "boolean") return value ? 1 : 0;
    return null;
  }

  function applyCommandsFromResponse(response) {
    if (!response || typeof response !== "object") return;
    const buckets = [response];
    ["keys", "commands", "sim", "simvars", "controls"].forEach((key) => {
      if (response[key] && typeof response[key] === "object") {
        buckets.push(response[key]);
      }
    });

    buckets.forEach((bucket) => {
      Object.keys(bucket).forEach((rawKey) => {
        const key = normalizeKey(rawKey);
        const value = bucket[rawKey];

        switch (key) {
          case "transponder_code":
          case "transponder":
          case "xpdr":
          case "squawk": {
            const v = coerceInt(value);
            if (v !== null) callWasm("osb_set_transponder_code", "number", ["number"], [v]);
            break;
          }
          case "adf_active_freq":
          case "adf_active_frequency": {
            const v = coerceNumber(value);
            if (v === null) break;
            const khz = v > 2000 ? v / 1000 : v;
            callWasm("osb_set_adf_active_khz", "number", ["number"], [khz]);
            break;
          }
          case "adf_standby_freq_hz":
          case "adf_standby_frequency_hz":
          case "adf_standby_freq": {
            const v = coerceNumber(value);
            if (v === null) break;
            const khz = key.includes("_hz") ? v / 1000 : (v > 2000 ? v / 1000 : v);
            if (khz >= 0) callWasm("osb_set_adf_standby_khz", "number", ["number"], [khz]);
            break;
          }
          case "gear_handle": {
            const v = coerceBool(value);
            if (v !== null) callWasm("osb_set_gear_handle", "number", ["number"], [v ? 1 : 0]);
            break;
          }
          case "flaps_index":
          case "flaps_handle_index": {
            const v = coerceInt(value);
            if (v !== null && v >= 0) {
              callWasm("osb_set_flaps_index", "number", ["number"], [v]);
            }
            break;
          }
          case "parking_brake": {
            const v = coerceBool(value);
            if (v !== null) callWasm("osb_set_parking_brake", "number", ["number"], [v ? 1 : 0]);
            break;
          }
          case "autopilot_master": {
            const v = coerceBool(value);
            if (v !== null) callWasm("osb_set_autopilot_master", "number", ["number"], [v ? 1 : 0]);
            break;
          }
          default:
            break;
        }
      });
    });
  }

  async function postJson(url, payload, config) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), config.requestTimeoutMs);
    try {
      const resp = await fetch(url, {
        method: "POST",
        headers: headers(config),
        body: JSON.stringify(payload),
        signal: controller.signal,
      });
      if (!resp.ok) return null;
      const text = await resp.text();
      if (!text) return null;
      return JSON.parse(text);
    } catch (err) {
      return null;
    } finally {
      clearTimeout(timeout);
    }
  }

  async function pollLogin(config, urls) {
    if (!state.token) return;
    const url = `${urls.meUrl}?token=${encodeURIComponent(state.token)}`;
    try {
      const resp = await fetch(url, { headers: headers(config) });
      if (!resp.ok) return;
      const data = await resp.json();
      if (data && data.connected !== false) {
        state.connected = true;
      }
    } catch (err) {
      // ignore
    }
  }

  async function sendStatus(config, urls, simConnected, flightActive) {
    const now = Date.now();
    if (now - state.lastStatusAt < config.idleIntervalSec * 1000) return;
    state.lastStatusAt = now;
    const payload = buildStatusPayload(simConnected, flightActive);
    await postJson(urls.statusUrl, payload, config);
  }

  async function sendTelemetry(config, urls, snapshot) {
    const now = Date.now();
    if (!shouldSendTelemetry(snapshot, now, config)) return;
    const payload = buildTelemetryPayload(snapshot);
    state.lastTelemetryAt = now;
    const response = await postJson(urls.dataUrl, payload, config);
    if (response) applyCommandsFromResponse(response);
  }

  function start() {
    const config = loadConfig();
    ensureToken();
    const urls = buildUrls(config);

    callWasm("osb_init", "number", [], []);

    setInterval(() => {
      callWasm("osb_tick", null, [], []);
    }, 250);

    setInterval(() => {
      pollLogin(config, urls);
    }, 10000);

    setInterval(() => {
      const snapshot = getSnapshot();
      const simConnected = !!callWasm("osb_is_connected", "number", [], []);
      const flightActive = !!snapshot;
      state.inFlight = flightActive;

      if (state.connected) {
        if (flightActive) {
          sendTelemetry(config, urls, snapshot);
        } else {
          sendStatus(config, urls, simConnected, flightActive);
        }
      }
    }, 1000);

    // Expose login URL for UI (optional)
    window.OpenSquawkBridge = {
      loginUrl: urls.loginUrl,
    };
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start);
  } else {
    start();
  }
})();
