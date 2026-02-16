// OpenSquawk Bridge – MSFS InGamePanel
// Reads SimVars, sends telemetry to OpenSquawk API, receives commands.

// ---------------------------------------------------------------------------
// Defaults (overridable via settings)
// ---------------------------------------------------------------------------
const OSQ_DEFAULTS = {
    apiBase:            "https://opensquawk.de",
    telemetryInterval:  30,     // seconds
    heartbeatInterval:  120,    // seconds
    authPollInterval:   10,     // seconds
};

const OSQ_TOKEN_KEY = "osq_bridge_token";
const OSQ_SETTINGS_KEY = "osq_settings";
const OSQ_TOKEN_LEN = 6;
const OSQ_DEBUG_MAX_LINES = 500;
const OSQ_TOKEN_CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

// Log levels for color coding
const OSQ_LOG_LEVEL = {
    INFO:    "info",
    OK:      "ok",
    WARN:    "warn",
    ERROR:   "error",
    HTTP:    "http",
    CMD:     "cmd",
    DEBUG:   "debug",
};

// ---------------------------------------------------------------------------
// SimVar definitions: [displayName, simVarName, unit, apiFieldName]
// ---------------------------------------------------------------------------
const OSQ_SIMVARS = [
    ["lat",             "PLANE LATITUDE",               "degrees",          "lat"],
    ["lon",             "PLANE LONGITUDE",              "degrees",          "lon"],
    ["alt_ft",          "PLANE ALTITUDE",               "feet",             "PLANE_ALTITUDE"],
    ["alt_indicated",   "INDICATED ALTITUDE",           "feet",             "altitude_ft_indicated"],
    ["ias_kt",          "AIRSPEED INDICATED",           "knots",            "AIRSPEED_INDICATED"],
    ["tas_kt",          "AIRSPEED TRUE",                "knots",            "AIRSPEED_TRUE"],
    ["gs_kt",           "GROUND VELOCITY",              "knots",            "GROUND_VELOCITY"],
    ["vs_fpm",          "VERTICAL SPEED",               "feet per minute",  "VERTICAL_SPEED"],
    ["pitch_deg",       "PLANE PITCH DEGREES",          "degrees",          "PLANE_PITCH_DEGREES"],
    ["hdg_deg",         "PLANE HEADING DEGREES TRUE",   "degrees",          "hdg_deg"],
    ["n1_1",            "TURB ENG N1:1",                "percent",          "TURB_ENG_N1_1"],
    ["n1_2",            "TURB ENG N1:2",                "percent",          "TURB_ENG_N1_2"],
    ["eng_on",          "GENERAL ENG COMBUSTION:1",     "bool",             "ENG_COMBUSTION"],
    ["on_ground",       "SIM ON GROUND",                "bool",             "SIM_ON_GROUND"],
    ["xpdr",            "TRANSPONDER CODE:1",           "Bco16",            "TRANSPONDER_CODE"],
    ["gear",            "GEAR HANDLE POSITION",         "bool",             "GEAR_HANDLE_POSITION"],
    ["flaps",           "FLAPS HANDLE INDEX",           "number",           "FLAPS_HANDLE_INDEX"],
    ["park_brake",      "BRAKE PARKING POSITION",       "bool",             "BRAKE_PARKING_POSITION"],
    ["ap_master",       "AUTOPILOT MASTER",             "bool",             "AUTOPILOT_MASTER"],
    ["adf_active",      "ADF ACTIVE FREQUENCY:1",       "Hz",               "ADF_ACTIVE_FREQUENCY"],
    ["adf_standby",     "ADF STANDBY FREQUENCY:1",      "Hz",               "ADF_STANDBY_FREQUENCY"],
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
        this.settingsOpen = false;
        this.telemetry = {};
        this._logEntries = []; // stored for copy-all

        // Settings (loaded from localStorage or defaults)
        this.settings = Object.assign({}, OSQ_DEFAULTS);

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
            badgeAuth:          this.querySelector("#osq-badge-auth"),
            badgeSim:           this.querySelector("#osq-badge-sim"),
            badgeFlight:        this.querySelector("#osq-badge-flight"),
            tokenValue:         this.querySelector("#osq-token-value"),
            tokenCopy:          this.querySelector("#osq-token-copy"),
            tokenReset:         this.querySelector("#osq-token-reset"),
            loginUrl:           this.querySelector("#osq-login-url"),
            userName:           this.querySelector("#osq-user-name"),
            userRow:            this.querySelector("#osq-user-row"),
            loginRow:           this.querySelector("#osq-login-row"),
            settingsToggle:     this.querySelector("#osq-settings-toggle"),
            settingsSection:    this.querySelector("#osq-settings-section"),
            settingsSave:       this.querySelector("#osq-settings-save"),
            settingsDefaults:   this.querySelector("#osq-settings-defaults"),
            setUrl:             this.querySelector("#osq-set-url"),
            setTelemInterval:   this.querySelector("#osq-set-telem-interval"),
            setHeartbeatInterval: this.querySelector("#osq-set-heartbeat-interval"),
            setAuthInterval:    this.querySelector("#osq-set-auth-interval"),
            debugToggle:        this.querySelector("#osq-debug-toggle"),
            debugSection:       this.querySelector("#osq-debug-section"),
            debugLog:           this.querySelector("#osq-debug-log"),
            debugClear:         this.querySelector("#osq-debug-clear"),
            debugCopyAll:       this.querySelector("#osq-debug-copy-all"),
            debugRaw:           this.querySelector("#osq-debug-raw"),
            telemIas:           this.querySelector("#osq-t-ias"),
            telemAlt:           this.querySelector("#osq-t-alt"),
            telemVs:            this.querySelector("#osq-t-vs"),
            telemGs:            this.querySelector("#osq-t-gs"),
            telemHdg:           this.querySelector("#osq-t-hdg"),
            telemXpdr:          this.querySelector("#osq-t-xpdr"),
        };

        // Bind events
        this.el.tokenCopy.addEventListener("click", () => this._copyToken());
        this.el.tokenReset.addEventListener("click", () => this._resetToken());
        this.el.settingsToggle.addEventListener("click", () => this._toggleSettings());
        this.el.settingsSave.addEventListener("click", () => this._saveSettings());
        this.el.settingsDefaults.addEventListener("click", () => this._resetSettings());
        this.el.debugToggle.addEventListener("click", () => this._toggleDebug());
        this.el.debugClear.addEventListener("click", () => this._clearDebugLog());
        this.el.debugCopyAll.addEventListener("click", () => this._copyAllLogs());

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

        // Load settings, token, and boot
        this._loadSettings();
        this._loadOrCreateToken();
        this._populateSettingsUI();
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
    // Settings management
    // -----------------------------------------------------------------------
    _loadSettings() {
        try {
            const raw = localStorage.getItem(OSQ_SETTINGS_KEY);
            if (raw) {
                const saved = JSON.parse(raw);
                this.settings = Object.assign({}, OSQ_DEFAULTS, saved);
            }
        } catch (_) {}
    }

    _populateSettingsUI() {
        this.el.setUrl.value = this.settings.apiBase;
        this.el.setTelemInterval.value = this.settings.telemetryInterval;
        this.el.setHeartbeatInterval.value = this.settings.heartbeatInterval;
        this.el.setAuthInterval.value = this.settings.authPollInterval;
    }

    _saveSettings() {
        const newUrl = this.el.setUrl.value.trim().replace(/\/+$/, "");
        const newTelem = Math.max(5, Math.min(300, parseInt(this.el.setTelemInterval.value) || OSQ_DEFAULTS.telemetryInterval));
        const newHeartbeat = Math.max(10, Math.min(600, parseInt(this.el.setHeartbeatInterval.value) || OSQ_DEFAULTS.heartbeatInterval));
        const newAuth = Math.max(3, Math.min(120, parseInt(this.el.setAuthInterval.value) || OSQ_DEFAULTS.authPollInterval));

        this.settings.apiBase = newUrl || OSQ_DEFAULTS.apiBase;
        this.settings.telemetryInterval = newTelem;
        this.settings.heartbeatInterval = newHeartbeat;
        this.settings.authPollInterval = newAuth;

        try {
            localStorage.setItem(OSQ_SETTINGS_KEY, JSON.stringify(this.settings));
        } catch (_) {}

        this._populateSettingsUI();
        this._log("Settings saved", OSQ_LOG_LEVEL.OK);

        // Restart timers with new intervals
        this._stop();
        this._start();
        this._updateUI();
    }

    _resetSettings() {
        this.settings = Object.assign({}, OSQ_DEFAULTS);
        try { localStorage.removeItem(OSQ_SETTINGS_KEY); } catch (_) {}
        this._populateSettingsUI();
        this._log("Settings reset to defaults", OSQ_LOG_LEVEL.OK);
        this._stop();
        this._start();
        this._updateUI();
    }

    _toggleSettings() {
        this.settingsOpen = !this.settingsOpen;
        if (this.settingsOpen) {
            this.el.settingsSection.classList.remove("osq-hidden");
            this.el.settingsToggle.classList.add("osq-btn-active");
        } else {
            this.el.settingsSection.classList.add("osq-hidden");
            this.el.settingsToggle.classList.remove("osq-btn-active");
        }
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
                this._log("Token loaded: " + this.token, OSQ_LOG_LEVEL.INFO);
            } else {
                this.token = this._generateToken();
                localStorage.setItem(OSQ_TOKEN_KEY, this.token);
                this._log("New token generated: " + this.token, OSQ_LOG_LEVEL.OK);
            }
        } catch (e) {
            this.token = this._generateToken();
            this._log("Token generated (no storage): " + this.token, OSQ_LOG_LEVEL.WARN);
        }
    }

    _resetToken() {
        this.token = this._generateToken();
        try { localStorage.setItem(OSQ_TOKEN_KEY, this.token); } catch (_) {}
        this.userConnected = false;
        this.userName = "";
        this._log("Token reset: " + this.token, OSQ_LOG_LEVEL.OK);
        this._updateUI();
    }

    _copyToken() {
        this._tryClipboard(this.token, "Token");
        this.el.tokenValue.classList.add("osq-flash");
        setTimeout(() => this.el.tokenValue.classList.remove("osq-flash"), 400);
    }

    _tryClipboard(text, label) {
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text);
                this._log(label + " copied to clipboard", OSQ_LOG_LEVEL.OK);
                return;
            }
        } catch (_) {}
        this._log(label + " copy not supported – select manually", OSQ_LOG_LEVEL.WARN);
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------
    _start() {
        this._log("Panel started", OSQ_LOG_LEVEL.INFO);
        this._startAuthPolling();
        this._startSimCheck();
    }

    _stop() {
        this._log("Panel stopped", OSQ_LOG_LEVEL.INFO);
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
        this._pollAuth();
        this._authPollTimer = setInterval(() => this._pollAuth(), this.settings.authPollInterval * 1000);
    }

    async _pollAuth() {
        const url = this.settings.apiBase + "/api/bridge/me";
        const headers = { "x-bridge-token": this.token };
        try {
            const res = await fetch(url, { headers });
            const data = await res.json();
            const wasConnected = this.userConnected;
            this.userConnected = !!data.connected;
            this.userName = data.user ? (data.user.name || data.user.email || "") : "";

            this._logHttp("GET", url, headers, null, res.status, data);

            if (!wasConnected && this.userConnected) {
                this._log("User connected: " + this.userName, OSQ_LOG_LEVEL.OK);
                this._startTelemetryLoop();
            } else if (wasConnected && !this.userConnected) {
                this._log("User disconnected", OSQ_LOG_LEVEL.WARN);
                this._stopTelemetryLoop();
            }
            this._updateUI();
        } catch (e) {
            this._log("Auth poll error: " + e.message, OSQ_LOG_LEVEL.ERROR);
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
                this._log("SimVar readable: " + this.simReadable, this.simReadable ? OSQ_LOG_LEVEL.OK : OSQ_LOG_LEVEL.WARN);
                this._updateUI();
            }

            if (this.simReadable) {
                const engOn = SimVar.GetSimVarValue("GENERAL ENG COMBUSTION:1", "bool");
                const onGround = SimVar.GetSimVarValue("SIM ON GROUND", "bool");
                const newFlightActive = !!engOn || !onGround;
                if (newFlightActive !== this.flightActive) {
                    this.flightActive = newFlightActive;
                    this._log("Flight active: " + this.flightActive, this.flightActive ? OSQ_LOG_LEVEL.OK : OSQ_LOG_LEVEL.INFO);
                    this._sendStatus();
                    this._updateUI();
                }

                this._readTelemetry();
                this._updateTelemetryDisplay();
            }
        } catch (e) {
            if (this.simReadable) {
                this.simReadable = false;
                this._log("SimVar read failed: " + e.message, OSQ_LOG_LEVEL.ERROR);
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
        this._log("Telemetry loop started (every " + this.settings.telemetryInterval + "s)", OSQ_LOG_LEVEL.INFO);
        this._telemetryTimer = setInterval(() => this._sendTelemetry(), this.settings.telemetryInterval * 1000);
        if (!this._heartbeatTimer) {
            this._heartbeatTimer = setInterval(() => this._sendHeartbeat(), this.settings.heartbeatInterval * 1000);
        }
    }

    _stopTelemetryLoop() {
        clearInterval(this._telemetryTimer);
        clearInterval(this._heartbeatTimer);
        this._telemetryTimer = null;
        this._heartbeatTimer = null;
        this._log("Telemetry loop stopped", OSQ_LOG_LEVEL.INFO);
    }

    async _sendTelemetry() {
        if (!this.userConnected || !this.simReadable) return;

        const t = this._readTelemetry();
        this._updateTelemetryDisplay();

        const url = this.settings.apiBase + "/api/bridge/data";
        const headers = {
            "Content-Type": "application/json",
            "x-bridge-token": this.token,
        };
        const body = JSON.stringify(t);

        try {
            const res = await fetch(url, { method: "POST", headers, body });
            const data = await res.json();

            this._logHttp("POST", url, headers, t, res.status, data);
            this._applyCommands(data);

            if (this.debugMode) {
                this.el.debugRaw.textContent = JSON.stringify(t, null, 2);
            }
        } catch (e) {
            this._log("Telemetry send error: " + e.message, OSQ_LOG_LEVEL.ERROR);
        }
    }

    async _sendHeartbeat() {
        if (!this.userConnected) return;
        if (this.flightActive) return;
        this._sendStatus();
    }

    async _sendStatus() {
        const url = this.settings.apiBase + "/api/bridge/status";
        const headers = {
            "Content-Type": "application/json",
            "x-bridge-token": this.token,
        };
        const payload = { simConnected: this.simReadable, flightActive: this.flightActive };

        try {
            const res = await fetch(url, { method: "POST", headers, body: JSON.stringify(payload) });
            const data = await res.json();
            this._logHttp("POST", url, headers, payload, res.status, data);
        } catch (e) {
            this._log("Status send error: " + e.message, OSQ_LOG_LEVEL.ERROR);
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
                    if (unit === "bool") val = !!val;
                    SimVar.SetSimVarValue(simvar, unit, val);
                    this._log("CMD " + apiField + " = " + JSON.stringify(val) + " \u2192 " + simvar, OSQ_LOG_LEVEL.CMD);
                } catch (e) {
                    this._log("CMD error (" + apiField + "): " + e.message, OSQ_LOG_LEVEL.ERROR);
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
            this.el.badgeAuth.className = "osq-badge osq-badge-ok";
        } else {
            this.el.badgeAuth.textContent = "\u25CF Not linked";
            this.el.badgeAuth.className = "osq-badge osq-badge-off";
        }

        // Sim badge
        this.el.badgeSim.className = "osq-badge " + (this.simReadable ? "osq-badge-ok" : "osq-badge-off");

        // Flight badge
        this.el.badgeFlight.className = "osq-badge " + (this.flightActive ? "osq-badge-active" : "osq-badge-off");

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
            this.el.loginUrl.textContent = this.settings.apiBase + "/bridge/connect?token=" + this.token;
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
    // Debug mode & logging
    // -----------------------------------------------------------------------
    _toggleDebug() {
        this.debugMode = !this.debugMode;
        if (this.debugMode) {
            this.el.debugSection.classList.remove("osq-hidden");
            this.el.debugToggle.classList.add("osq-btn-active");
            this._log("Debug mode ON", OSQ_LOG_LEVEL.DEBUG);
        } else {
            this.el.debugSection.classList.add("osq-hidden");
            this.el.debugToggle.classList.remove("osq-btn-active");
        }
    }

    _clearDebugLog() {
        this.el.debugLog.innerHTML = "";
        this.el.debugRaw.textContent = "";
        this._logEntries = [];
    }

    _copyAllLogs() {
        const text = this._logEntries.map(e => e.plain).join("\n");
        this._tryClipboard(text, "Logs");
    }

    // Standard log entry
    _log(msg, level) {
        level = level || OSQ_LOG_LEVEL.INFO;
        const ts = new Date().toISOString().substr(11, 12);
        const plain = "[" + ts + "] [" + level.toUpperCase() + "] " + msg;

        console.log("[OSQ] " + plain);
        this._logEntries.push({ plain, level });

        if (!this.el || !this.el.debugLog) return;

        const row = document.createElement("div");
        row.className = "osq-log-line osq-log-" + level;

        // Timestamp
        const tsSpan = document.createElement("span");
        tsSpan.className = "osq-log-ts";
        tsSpan.textContent = ts;

        // Level tag
        const lvlSpan = document.createElement("span");
        lvlSpan.className = "osq-log-level osq-log-level-" + level;
        lvlSpan.textContent = level.toUpperCase();

        // Message
        const msgSpan = document.createElement("span");
        msgSpan.className = "osq-log-msg";
        msgSpan.textContent = msg;

        // Copy button
        const copyBtn = document.createElement("button");
        copyBtn.className = "osq-log-copy";
        copyBtn.textContent = "\u2398";
        copyBtn.title = "Copy this line";
        copyBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            this._tryClipboard(plain, "Line");
        });

        row.appendChild(tsSpan);
        row.appendChild(lvlSpan);
        row.appendChild(msgSpan);
        row.appendChild(copyBtn);
        this.el.debugLog.appendChild(row);

        this._trimLog();
        this.el.debugLog.scrollTop = this.el.debugLog.scrollHeight;
    }

    // HTTP log entry with expandable request/response details
    _logHttp(method, url, headers, requestBody, status, responseBody) {
        const ts = new Date().toISOString().substr(11, 12);
        const isError = status >= 400;
        const level = isError ? OSQ_LOG_LEVEL.ERROR : OSQ_LOG_LEVEL.HTTP;
        const shortUrl = url.replace(this.settings.apiBase, "");
        const summary = method + " " + shortUrl + " \u2192 " + status;
        const plain = "[" + ts + "] [" + level.toUpperCase() + "] " + summary;

        console.log("[OSQ] " + plain);
        this._logEntries.push({ plain, level, method, url, headers, requestBody, status, responseBody });

        if (!this.el || !this.el.debugLog) return;

        const row = document.createElement("div");
        row.className = "osq-log-line osq-log-" + level + " osq-log-expandable";

        // Timestamp
        const tsSpan = document.createElement("span");
        tsSpan.className = "osq-log-ts";
        tsSpan.textContent = ts;

        // Status badge
        const statusSpan = document.createElement("span");
        statusSpan.className = "osq-log-status osq-log-status-" + (isError ? "err" : "ok");
        statusSpan.textContent = status;

        // Method + path
        const msgSpan = document.createElement("span");
        msgSpan.className = "osq-log-msg";
        msgSpan.textContent = method + " " + shortUrl;

        // Expand indicator
        const expandIcon = document.createElement("span");
        expandIcon.className = "osq-log-expand-icon";
        expandIcon.textContent = "\u25B6";

        // Copy button
        const copyBtn = document.createElement("button");
        copyBtn.className = "osq-log-copy";
        copyBtn.textContent = "\u2398";
        copyBtn.title = "Copy full request/response";
        const fullText = [
            method + " " + url,
            "Status: " + status,
            "Headers: " + JSON.stringify(headers, null, 2),
            requestBody ? "Body: " + JSON.stringify(requestBody, null, 2) : null,
            "Response: " + JSON.stringify(responseBody, null, 2),
        ].filter(Boolean).join("\n\n");
        copyBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            this._tryClipboard(fullText, "Request");
        });

        // Detail pane (hidden)
        const detail = document.createElement("div");
        detail.className = "osq-log-detail osq-hidden";
        detail.innerHTML = this._buildDetailHtml(method, url, headers, requestBody, status, responseBody);

        // Toggle expand on row click
        row.addEventListener("click", () => {
            const hidden = detail.classList.toggle("osq-hidden");
            expandIcon.textContent = hidden ? "\u25B6" : "\u25BC";
        });

        row.appendChild(tsSpan);
        row.appendChild(statusSpan);
        row.appendChild(msgSpan);
        row.appendChild(expandIcon);
        row.appendChild(copyBtn);

        const wrapper = document.createElement("div");
        wrapper.className = "osq-log-entry";
        wrapper.appendChild(row);
        wrapper.appendChild(detail);
        this.el.debugLog.appendChild(wrapper);

        this._trimLog();
        this.el.debugLog.scrollTop = this.el.debugLog.scrollHeight;
    }

    _buildDetailHtml(method, url, headers, requestBody, status, responseBody) {
        let html = '<div class="osq-detail-section">';
        html += '<div class="osq-detail-label">Request</div>';
        html += '<pre class="osq-detail-pre">' + method + " " + this._esc(url) + "\n";
        for (const [k, v] of Object.entries(headers)) {
            html += this._esc(k) + ": " + this._esc(v) + "\n";
        }
        html += "</pre>";
        if (requestBody) {
            html += '<div class="osq-detail-label">Body</div>';
            html += '<pre class="osq-detail-pre">' + this._esc(JSON.stringify(requestBody, null, 2)) + "</pre>";
        }
        html += '<div class="osq-detail-label">Response <span class="osq-detail-status-' + (status >= 400 ? "err" : "ok") + '">' + status + "</span></div>";
        html += '<pre class="osq-detail-pre">' + this._esc(JSON.stringify(responseBody, null, 2)) + "</pre>";
        html += "</div>";
        return html;
    }

    _esc(str) {
        if (!str) return "";
        return String(str).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }

    _trimLog() {
        while (this.el.debugLog.childNodes.length > OSQ_DEBUG_MAX_LINES) {
            this.el.debugLog.removeChild(this.el.debugLog.firstChild);
        }
        if (this._logEntries.length > OSQ_DEBUG_MAX_LINES) {
            this._logEntries = this._logEntries.slice(-OSQ_DEBUG_MAX_LINES);
        }
    }
}

window.customElements.define("ingamepanel-opensquawk", IngamePanelOpenSquawk);
checkAutoload();
