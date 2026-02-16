// OpenSquawk Bridge – MSFS InGamePanel
// Reads SimVars, sends telemetry to OpenSquawk API, receives commands.

const OSQ_API_BASE = "https://opensquawk.de";
const OSQ_TOKEN_KEY = "osq_bridge_token";
const OSQ_TOKEN_LEN = 6;
const OSQ_TELEMETRY_INTERVAL_MS = 30000;
const OSQ_HEARTBEAT_INTERVAL_MS = 120000;
const OSQ_AUTH_POLL_INTERVAL_MS = 10000;
const OSQ_DEBUG_MAX_LINES = 200;
const OSQ_TOKEN_CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no ambiguous chars

// ---------------------------------------------------------------------------
// SimVar definitions: [displayName, simVarName, unit, apiFieldName]
// ---------------------------------------------------------------------------
const OSQ_SIMVARS = [
    ["lat",             "PLANE LATITUDE",           "degrees",          "lat"],
    ["lon",             "PLANE LONGITUDE",          "degrees",          "lon"],
    ["alt_ft",          "PLANE ALTITUDE",           "feet",             "PLANE_ALTITUDE"],
    ["alt_indicated",   "INDICATED ALTITUDE",       "feet",             "altitude_ft_indicated"],
    ["ias_kt",          "AIRSPEED INDICATED",       "knots",            "AIRSPEED_INDICATED"],
    ["tas_kt",          "AIRSPEED TRUE",            "knots",            "AIRSPEED_TRUE"],
    ["gs_kt",           "GROUND VELOCITY",          "knots",            "GROUND_VELOCITY"],
    ["vs_fpm",          "VERTICAL SPEED",           "feet per minute",  "VERTICAL_SPEED"],
    ["pitch_deg",       "PLANE PITCH DEGREES",      "degrees",          "PLANE_PITCH_DEGREES"],
    ["hdg_deg",         "PLANE HEADING DEGREES TRUE","degrees",         "hdg_deg"],
    ["n1_1",            "TURB ENG N1:1",            "percent",          "TURB_ENG_N1_1"],
    ["n1_2",            "TURB ENG N1:2",            "percent",          "TURB_ENG_N1_2"],
    ["eng_on",          "GENERAL ENG COMBUSTION:1", "bool",             "ENG_COMBUSTION"],
    ["on_ground",       "SIM ON GROUND",            "bool",             "SIM_ON_GROUND"],
    ["xpdr",            "TRANSPONDER CODE:1",       "Bco16",            "TRANSPONDER_CODE"],
    ["gear",            "GEAR HANDLE POSITION",     "bool",             "GEAR_HANDLE_POSITION"],
    ["flaps",           "FLAPS HANDLE INDEX",       "number",           "FLAPS_HANDLE_INDEX"],
    ["park_brake",      "BRAKE PARKING POSITION",   "bool",             "BRAKE_PARKING_POSITION"],
    ["ap_master",       "AUTOPILOT MASTER",         "bool",             "AUTOPILOT_MASTER"],
    ["adf_active",      "ADF ACTIVE FREQUENCY:1",   "Hz",               "ADF_ACTIVE_FREQUENCY"],
    ["adf_standby",     "ADF STANDBY FREQUENCY:1",  "Hz",               "ADF_STANDBY_FREQUENCY"],
];

// Commands the backend can send → [apiField, simVarName, unit]
const OSQ_COMMANDS = [
    ["parking_brake",       "BRAKE PARKING POSITION",   "bool"],
    ["gear_handle",         "GEAR HANDLE POSITION",     "bool"],
    ["transponder_code",    "TRANSPONDER CODE:1",       "Bco16"],
    ["flaps_index",         "FLAPS HANDLE INDEX",       "number"],
    ["autopilot_master",    "AUTOPILOT MASTER",         "bool"],
];

// ---------------------------------------------------------------------------
// Panel class
// ---------------------------------------------------------------------------
class IngamePanelOpenSquawk extends TemplateElement {
    constructor() {
        super(...arguments);
        this.panelActive = false;
        this.started = false;
        this.ingameUi = null;

        // State
        this.token = "";
        this.userConnected = false;
        this.userName = "";
        this.simReadable = false;
        this.flightActive = false;
        this.debugMode = false;
        this.telemetry = {};

        // Timers
        this._authPollTimer = null;
        this._telemetryTimer = null;
        this._heartbeatTimer = null;
        this._simCheckTimer = null;

        this.initialize();
    }

    connectedCallback() {
        super.connectedCallback();
        this.ingameUi = this.querySelector("ingame-ui");

        // Cache DOM refs
        this.el = {
            badgeAuth:      this.querySelector("#osq-badge-auth"),
            badgeSim:       this.querySelector("#osq-badge-sim"),
            badgeFlight:    this.querySelector("#osq-badge-flight"),
            tokenValue:     this.querySelector("#osq-token-value"),
            tokenCopy:      this.querySelector("#osq-token-copy"),
            tokenReset:     this.querySelector("#osq-token-reset"),
            loginUrl:       this.querySelector("#osq-login-url"),
            userName:       this.querySelector("#osq-user-name"),
            userRow:        this.querySelector("#osq-user-row"),
            loginRow:       this.querySelector("#osq-login-row"),
            debugToggle:    this.querySelector("#osq-debug-toggle"),
            debugSection:   this.querySelector("#osq-debug-section"),
            debugLog:       this.querySelector("#osq-debug-log"),
            debugClear:     this.querySelector("#osq-debug-clear"),
            debugRaw:       this.querySelector("#osq-debug-raw"),
            telemIas:       this.querySelector("#osq-t-ias"),
            telemAlt:       this.querySelector("#osq-t-alt"),
            telemVs:        this.querySelector("#osq-t-vs"),
            telemGs:        this.querySelector("#osq-t-gs"),
            telemHdg:       this.querySelector("#osq-t-hdg"),
            telemXpdr:      this.querySelector("#osq-t-xpdr"),
        };

        // Bind events
        this.el.tokenCopy.addEventListener("click", () => this._copyToken());
        this.el.tokenReset.addEventListener("click", () => this._resetToken());
        this.el.debugToggle.addEventListener("click", () => this._toggleDebug());
        this.el.debugClear.addEventListener("click", () => this._clearDebugLog());

        if (this.ingameUi) {
            this.ingameUi.addEventListener("panelActive", () => {
                this.panelActive = true;
                this._start();
            });
            this.ingameUi.addEventListener("panelInactive", () => {
                this.panelActive = false;
                this._stop();
            });
        }

        // Load token and boot
        this._loadOrCreateToken();
        this._updateUI();
        this._start();
    }

    initialize() {
        if (this.started) return;
        this.started = true;
    }

    disconnectedCallback() {
        super.disconnectedCallback();
        this._stop();
    }

    // -----------------------------------------------------------------------
    // Token management
    // -----------------------------------------------------------------------
    _generateToken() {
        let t = "";
        for (let i = 0; i < OSQ_TOKEN_LEN; i++) {
            t += OSQ_TOKEN_CHARS[Math.floor(Math.random() * OSQ_TOKEN_CHARS.length)];
        }
        return t;
    }

    _loadOrCreateToken() {
        try {
            const stored = localStorage.getItem(OSQ_TOKEN_KEY);
            if (stored && stored.length === OSQ_TOKEN_LEN) {
                this.token = stored;
                this._log("Token loaded: " + this.token);
            } else {
                this.token = this._generateToken();
                localStorage.setItem(OSQ_TOKEN_KEY, this.token);
                this._log("New token generated: " + this.token);
            }
        } catch (e) {
            // localStorage may not be available in all MSFS contexts
            this.token = this._generateToken();
            this._log("Token generated (no storage): " + this.token);
        }
    }

    _resetToken() {
        this.token = this._generateToken();
        try { localStorage.setItem(OSQ_TOKEN_KEY, this.token); } catch (_) {}
        this.userConnected = false;
        this.userName = "";
        this._log("Token reset: " + this.token);
        this._updateUI();
    }

    _copyToken() {
        // Coherent GT may not support clipboard API; select text as fallback
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(this.token);
                this._log("Token copied to clipboard");
            }
        } catch (_) {
            this._log("Copy not supported – select token manually");
        }
        // Flash the token value to give visual feedback
        this.el.tokenValue.classList.add("osq-flash");
        setTimeout(() => this.el.tokenValue.classList.remove("osq-flash"), 400);
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------
    _start() {
        this._log("Panel started");
        this._startAuthPolling();
        this._startSimCheck();
    }

    _stop() {
        this._log("Panel stopped");
        this._clearAllTimers();
    }

    _clearAllTimers() {
        clearInterval(this._authPollTimer);
        clearInterval(this._telemetryTimer);
        clearInterval(this._heartbeatTimer);
        clearInterval(this._simCheckTimer);
        this._authPollTimer = null;
        this._telemetryTimer = null;
        this._heartbeatTimer = null;
        this._simCheckTimer = null;
    }

    // -----------------------------------------------------------------------
    // Auth polling – GET /api/bridge/me
    // -----------------------------------------------------------------------
    _startAuthPolling() {
        if (this._authPollTimer) return;
        this._pollAuth(); // immediate first call
        this._authPollTimer = setInterval(() => this._pollAuth(), OSQ_AUTH_POLL_INTERVAL_MS);
    }

    async _pollAuth() {
        try {
            const res = await fetch(OSQ_API_BASE + "/api/bridge/me", {
                headers: { "x-bridge-token": this.token }
            });
            const data = await res.json();
            const wasConnected = this.userConnected;
            this.userConnected = !!data.connected;
            this.userName = data.user ? (data.user.name || data.user.email || "") : "";

            if (!wasConnected && this.userConnected) {
                this._log("User connected: " + this.userName);
                this._startTelemetryLoop();
            } else if (wasConnected && !this.userConnected) {
                this._log("User disconnected");
                this._stopTelemetryLoop();
            }
            this._updateUI();
        } catch (e) {
            this._log("Auth poll error: " + e.message);
        }
    }

    // -----------------------------------------------------------------------
    // Sim check – detect if SimVars are readable
    // -----------------------------------------------------------------------
    _startSimCheck() {
        if (this._simCheckTimer) return;
        this._checkSim();
        this._simCheckTimer = setInterval(() => this._checkSim(), 5000);
    }

    _checkSim() {
        try {
            const lat = SimVar.GetSimVarValue("PLANE LATITUDE", "degrees");
            const wasReadable = this.simReadable;
            this.simReadable = (typeof lat === "number" && !isNaN(lat));
            if (this.simReadable !== wasReadable) {
                this._log("SimVar readable: " + this.simReadable);
                this._updateUI();
            }

            // Detect flight active: engine on OR airborne
            if (this.simReadable) {
                const engOn = SimVar.GetSimVarValue("GENERAL ENG COMBUSTION:1", "bool");
                const onGround = SimVar.GetSimVarValue("SIM ON GROUND", "bool");
                const newFlightActive = !!engOn || !onGround;
                if (newFlightActive !== this.flightActive) {
                    this.flightActive = newFlightActive;
                    this._log("Flight active: " + this.flightActive);
                    this._sendStatus();
                    this._updateUI();
                }

                // Update telemetry display even without sending
                this._readTelemetry();
                this._updateTelemetryDisplay();
            }
        } catch (e) {
            if (this.simReadable) {
                this.simReadable = false;
                this._log("SimVar read failed: " + e.message);
                this._updateUI();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Telemetry – read SimVars & POST /api/bridge/data
    // -----------------------------------------------------------------------
    _readTelemetry() {
        const t = {};
        for (const [key, simvar, unit, apiField] of OSQ_SIMVARS) {
            try {
                t[apiField] = SimVar.GetSimVarValue(simvar, unit);
            } catch (_) {
                t[apiField] = null;
            }
        }
        t.timestamp = Date.now();
        this.telemetry = t;
        return t;
    }

    _startTelemetryLoop() {
        if (this._telemetryTimer) return;
        this._log("Telemetry loop started (every " + (OSQ_TELEMETRY_INTERVAL_MS / 1000) + "s)");
        this._telemetryTimer = setInterval(() => this._sendTelemetry(), OSQ_TELEMETRY_INTERVAL_MS);
        // Also start heartbeat for idle periods
        if (!this._heartbeatTimer) {
            this._heartbeatTimer = setInterval(() => this._sendHeartbeat(), OSQ_HEARTBEAT_INTERVAL_MS);
        }
    }

    _stopTelemetryLoop() {
        clearInterval(this._telemetryTimer);
        clearInterval(this._heartbeatTimer);
        this._telemetryTimer = null;
        this._heartbeatTimer = null;
        this._log("Telemetry loop stopped");
    }

    async _sendTelemetry() {
        if (!this.userConnected || !this.simReadable) return;

        const t = this._readTelemetry();
        this._updateTelemetryDisplay();

        try {
            const res = await fetch(OSQ_API_BASE + "/api/bridge/data", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "x-bridge-token": this.token,
                },
                body: JSON.stringify(t),
            });
            const data = await res.json();
            this._log("Telemetry sent (" + res.status + ")");

            // Process commands from response
            this._applyCommands(data);

            if (this.debugMode) {
                this.el.debugRaw.textContent = JSON.stringify(t, null, 2);
            }
        } catch (e) {
            this._log("Telemetry send error: " + e.message);
        }
    }

    async _sendHeartbeat() {
        if (!this.userConnected) return;
        if (this.flightActive) return; // telemetry loop handles active flights
        this._sendStatus();
    }

    async _sendStatus() {
        try {
            await fetch(OSQ_API_BASE + "/api/bridge/status", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "x-bridge-token": this.token,
                },
                body: JSON.stringify({
                    simConnected: this.simReadable,
                    flightActive: this.flightActive,
                }),
            });
            this._log("Status sent (sim=" + this.simReadable + " flight=" + this.flightActive + ")");
        } catch (e) {
            this._log("Status send error: " + e.message);
        }
    }

    // -----------------------------------------------------------------------
    // Command execution – write SimVars from backend response
    // -----------------------------------------------------------------------
    _applyCommands(responseData) {
        if (!responseData || typeof responseData !== "object") return;

        for (const [apiField, simvar, unit] of OSQ_COMMANDS) {
            if (apiField in responseData && responseData[apiField] !== undefined && responseData[apiField] !== null) {
                try {
                    let val = responseData[apiField];
                    // Type coercion for booleans
                    if (unit === "bool") val = !!val;
                    SimVar.SetSimVarValue(simvar, unit, val);
                    this._log("CMD: " + apiField + " = " + val + " → " + simvar);
                } catch (e) {
                    this._log("CMD error (" + apiField + "): " + e.message);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // UI updates
    // -----------------------------------------------------------------------
    _updateUI() {
        // Auth badge
        if (this.userConnected) {
            this.el.badgeAuth.textContent = "\u25CF " + this.userName;
            this.el.badgeAuth.classList.add("osq-badge-ok");
            this.el.badgeAuth.classList.remove("osq-badge-off");
        } else {
            this.el.badgeAuth.textContent = "\u25CF Not linked";
            this.el.badgeAuth.classList.remove("osq-badge-ok");
            this.el.badgeAuth.classList.add("osq-badge-off");
        }

        // Sim badge
        if (this.simReadable) {
            this.el.badgeSim.textContent = "\u25CF Sim";
            this.el.badgeSim.classList.add("osq-badge-ok");
            this.el.badgeSim.classList.remove("osq-badge-off");
        } else {
            this.el.badgeSim.textContent = "\u25CF Sim";
            this.el.badgeSim.classList.remove("osq-badge-ok");
            this.el.badgeSim.classList.add("osq-badge-off");
        }

        // Flight badge
        if (this.flightActive) {
            this.el.badgeFlight.textContent = "\u25CF Flight";
            this.el.badgeFlight.classList.add("osq-badge-active");
            this.el.badgeFlight.classList.remove("osq-badge-off");
        } else {
            this.el.badgeFlight.textContent = "\u25CF Flight";
            this.el.badgeFlight.classList.remove("osq-badge-active");
            this.el.badgeFlight.classList.add("osq-badge-off");
        }

        // Token display
        this.el.tokenValue.textContent = this.token || "------";

        // Login URL vs user name
        if (this.userConnected) {
            this.el.loginRow.classList.add("osq-hidden");
            this.el.userRow.classList.remove("osq-hidden");
            this.el.userName.textContent = this.userName;
        } else {
            this.el.loginRow.classList.remove("osq-hidden");
            this.el.userRow.classList.add("osq-hidden");
            this.el.loginUrl.textContent = OSQ_API_BASE + "/bridge/connect?token=" + this.token;
        }
    }

    _updateTelemetryDisplay() {
        const t = this.telemetry;
        if (!t) return;
        this.el.telemIas.textContent = this._fmt(t.AIRSPEED_INDICATED, 0);
        this.el.telemAlt.textContent = this._fmt(t.altitude_ft_indicated || t.PLANE_ALTITUDE, 0);
        this.el.telemVs.textContent = this._fmt(t.VERTICAL_SPEED, 0);
        this.el.telemGs.textContent = this._fmt(t.GROUND_VELOCITY, 0);
        this.el.telemHdg.textContent = this._fmt(t.hdg_deg, 0);
        this.el.telemXpdr.textContent = this._fmtXpdr(t.TRANSPONDER_CODE);
    }

    _fmt(val, decimals) {
        if (val === null || val === undefined || isNaN(val)) return "---";
        return Number(val).toFixed(decimals);
    }

    _fmtXpdr(val) {
        if (val === null || val === undefined || isNaN(val)) return "----";
        return String(Math.round(val)).padStart(4, "0");
    }

    // -----------------------------------------------------------------------
    // Debug mode
    // -----------------------------------------------------------------------
    _toggleDebug() {
        this.debugMode = !this.debugMode;
        if (this.debugMode) {
            this.el.debugSection.classList.remove("osq-hidden");
            this.el.debugToggle.classList.add("osq-debug-on");
            this._log("Debug mode ON");
        } else {
            this.el.debugSection.classList.add("osq-hidden");
            this.el.debugToggle.classList.remove("osq-debug-on");
        }
    }

    _clearDebugLog() {
        this.el.debugLog.innerHTML = "";
        this.el.debugRaw.textContent = "";
    }

    _log(msg) {
        const ts = new Date().toISOString().substr(11, 8);
        const line = "[" + ts + "] " + msg;

        // Always console.log for external debugging
        console.log("[OSQ] " + line);

        if (!this.el || !this.el.debugLog) return;

        const div = document.createElement("div");
        div.className = "osq-log-line";
        div.textContent = line;
        this.el.debugLog.appendChild(div);

        // Trim old lines
        while (this.el.debugLog.childNodes.length > OSQ_DEBUG_MAX_LINES) {
            this.el.debugLog.removeChild(this.el.debugLog.firstChild);
        }

        // Auto-scroll
        this.el.debugLog.scrollTop = this.el.debugLog.scrollHeight;
    }
}

window.customElements.define("ingamepanel-opensquawk", IngamePanelOpenSquawk);
checkAutoload();
