(function () {
    "use strict";

    if (typeof OpenSquawkBridgeShared === "undefined") {
        throw new Error("OpenSquawkBridgeShared is required before OpenSquawkBridge.js.");
    }

    var STORAGE_KEYS = Object.freeze({
        config: "opensquawk.bridge.config.v1",
        token: "opensquawk.bridge.token.v1"
    });

    var TELEMETRY_VARIABLES = Object.freeze([
        {
            key: "latitude",
            parse: "number",
            candidates: [
                { name: "PLANE LATITUDE", unit: "degrees" }
            ]
        },
        {
            key: "longitude",
            parse: "number",
            candidates: [
                { name: "PLANE LONGITUDE", unit: "degrees" }
            ]
        },
        {
            key: "altitude",
            parse: "number",
            candidates: [
                { name: "PLANE ALTITUDE", unit: "feet" }
            ]
        },
        {
            key: "indicatedAltitude",
            parse: "number",
            candidates: [
                { name: "INDICATED ALTITUDE", unit: "feet" }
            ]
        },
        {
            key: "airspeedIndicated",
            parse: "number",
            candidates: [
                { name: "AIRSPEED INDICATED", unit: "knots" },
                { name: "INDICATED AIRSPEED", unit: "knots" }
            ]
        },
        {
            key: "airspeedTrue",
            parse: "number",
            candidates: [
                { name: "AIRSPEED TRUE", unit: "knots" },
                { name: "TRUE AIRSPEED", unit: "knots" }
            ]
        },
        {
            key: "groundVelocity",
            parse: "number",
            candidates: [
                { name: "GROUND VELOCITY", unit: "meters per second" },
                { name: "GROUND VELOCITY", unit: "m/s" }
            ]
        },
        {
            key: "turbineN1",
            parse: "number",
            candidates: [
                { name: "TURB ENG N1:1", unit: "percent" }
            ]
        },
        {
            key: "onGround",
            parse: "bool",
            candidates: [
                { name: "SIM ON GROUND", unit: "Bool" }
            ]
        },
        {
            key: "engineCombustion",
            parse: "bool",
            candidates: [
                { name: "GENERAL ENG COMBUSTION:1", unit: "Bool" }
            ]
        },
        {
            key: "transponderCode",
            parse: "int",
            candidates: [
                { name: "TRANSPONDER CODE:2", unit: "BCD16" }
            ]
        },
        {
            key: "adfActiveFrequency",
            parse: "int",
            candidates: [
                { name: "ADF ACTIVE FREQUENCY:1", unit: "Frequency ADF BCD32" }
            ]
        },
        {
            key: "adfStandbyFrequency",
            parse: "number",
            candidates: [
                { name: "ADF STANDBY FREQUENCY:1", unit: "Hz" }
            ]
        },
        {
            key: "verticalSpeed",
            parse: "number",
            candidates: [
                { name: "VERTICAL SPEED", unit: "feet per minute" }
            ]
        },
        {
            key: "planePitchDegrees",
            parse: "number",
            candidates: [
                { name: "PLANE PITCH DEGREES", unit: "degrees" }
            ]
        },
        {
            key: "turbineN1Engine2",
            parse: "number",
            candidates: [
                { name: "TURB ENG N1:2", unit: "percent" }
            ]
        },
        {
            key: "gearHandlePosition",
            parse: "bool",
            candidates: [
                { name: "GEAR HANDLE POSITION", unit: "Bool" }
            ]
        },
        {
            key: "flapsHandleIndex",
            parse: "int",
            candidates: [
                { name: "FLAPS HANDLE INDEX", unit: "number" }
            ]
        },
        {
            key: "brakeParkingPosition",
            parse: "bool",
            candidates: [
                { name: "BRAKE PARKING POSITION", unit: "Bool" }
            ]
        },
        {
            key: "autopilotMaster",
            parse: "bool",
            candidates: [
                { name: "AUTOPILOT MASTER", unit: "Bool" }
            ]
        }
    ]);

    function toIsoDate(valueMs) {
        return new Date(valueMs).toISOString();
    }

    function ensureFiniteNumber(value, fallback) {
        return Number.isFinite(value) ? value : fallback;
    }

    function safeStringify(value) {
        try {
            return JSON.stringify(value);
        } catch (_error) {
            return "<unserializable>";
        }
    }

    function delay(ms) {
        return new Promise(function (resolve) {
            setTimeout(resolve, ms);
        });
    }

    function classifyHttpError(error) {
        var name = error && error.name ? String(error.name) : "";
        var message = String(error && error.message ? error.message : error || "");
        var normalized = message.toLowerCase();

        var isLikelyFetchTypeError = name.toLowerCase() === "typeerror"
            || normalized.indexOf("failed to fetch") >= 0
            || normalized.indexOf("networkerror") >= 0
            || normalized.indexOf("load failed") >= 0;

        if (isLikelyFetchTypeError) {
            return {
                category: "network_or_cors",
                hint: "Likely blocked by CORS/preflight, TLS trust, DNS, or network reachability. For OpenSquawk API calls, ensure Access-Control-Allow-Origin and Access-Control-Allow-Headers include x-bridge-token, content-type, authorization."
            };
        }

        if (normalized.indexOf("timeout") >= 0 || normalized.indexOf("aborted") >= 0) {
            return {
                category: "timeout",
                hint: "Request timed out before completion. Check endpoint latency and timeout settings."
            };
        }

        return {
            category: "unknown",
            hint: null
        };
    }

    function RuntimeLogger(maxEntries, echoToConsole) {
        this.maxEntries = maxEntries;
        this.echoToConsole = !!echoToConsole;
        this.startMs = Date.now();
        this.seq = 0;
        this.entries = [];
        this.onEntry = null;
    }

    RuntimeLogger.prototype.setOptions = function (maxEntries, echoToConsole) {
        this.maxEntries = Math.max(50, maxEntries || this.maxEntries);
        this.echoToConsole = !!echoToConsole;
    };

    RuntimeLogger.prototype.clear = function () {
        this.entries = [];
    };

    RuntimeLogger.prototype.snapshot = function () {
        return this.entries.slice();
    };

    RuntimeLogger.prototype.log = function (level, event, message, details) {
        var now = Date.now();
        var entry = {
            seq: ++this.seq,
            ts: toIsoDate(now),
            tsMs: now,
            uptimeMs: now - this.startMs,
            level: level,
            event: event,
            message: message,
            details: details || null
        };

        this.entries.push(entry);
        if (this.entries.length > this.maxEntries) {
            this.entries.shift();
        }

        if (this.echoToConsole && typeof console !== "undefined") {
            var prefix = "[OpenSquawkBridge][" + entry.seq + "][" + level.toUpperCase() + "][" + event + "]";
            if (details) {
                console.log(prefix + " " + message, details);
            } else {
                console.log(prefix + " " + message);
            }
        }

        if (typeof this.onEntry === "function") {
            this.onEntry(entry);
        }

        return entry;
    };

    RuntimeLogger.prototype.debug = function (event, message, details) {
        return this.log("debug", event, message, details);
    };

    RuntimeLogger.prototype.info = function (event, message, details) {
        return this.log("info", event, message, details);
    };

    RuntimeLogger.prototype.warn = function (event, message, details) {
        return this.log("warn", event, message, details);
    };

    RuntimeLogger.prototype.error = function (event, message, details) {
        return this.log("error", event, message, details);
    };

    function StorageProvider(logger) {
        this.logger = logger;
        this.backend = "memory";
        this.memory = {};

        if (typeof GetStoredData === "function" && typeof SetStoredData === "function") {
            this.backend = "storedData";
        } else if (typeof localStorage !== "undefined") {
            this.backend = "localStorage";
        }

        this.logger.info("storage.backend.selected", "Storage backend selected", {
            backend: this.backend
        });
    }

    StorageProvider.prototype.readString = function (key) {
        try {
            if (this.backend === "storedData") {
                var value = GetStoredData(key);
                return typeof value === "string" && value.length > 0 ? value : null;
            }

            if (this.backend === "localStorage") {
                var lsValue = localStorage.getItem(key);
                return typeof lsValue === "string" && lsValue.length > 0 ? lsValue : null;
            }

            return this.memory[key] || null;
        } catch (error) {
            this.logger.error("storage.read.failed", "Failed to read storage key", {
                key: key,
                error: String(error && error.message ? error.message : error)
            });
            return null;
        }
    };

    StorageProvider.prototype.writeString = function (key, value) {
        try {
            if (this.backend === "storedData") {
                SetStoredData(key, value);
                return true;
            }

            if (this.backend === "localStorage") {
                localStorage.setItem(key, value);
                return true;
            }

            this.memory[key] = value;
            return true;
        } catch (error) {
            this.logger.error("storage.write.failed", "Failed to write storage key", {
                key: key,
                error: String(error && error.message ? error.message : error)
            });
            return false;
        }
    };

    StorageProvider.prototype.readJson = function (key) {
        var raw = this.readString(key);
        if (!raw) {
            return null;
        }

        try {
            return JSON.parse(raw);
        } catch (error) {
            this.logger.error("storage.json.parse_failed", "Stored JSON is invalid", {
                key: key,
                rawPreview: raw.slice(0, 200),
                error: String(error && error.message ? error.message : error)
            });
            return null;
        }
    };

    StorageProvider.prototype.writeJson = function (key, value) {
        return this.writeString(key, safeStringify(value));
    };

    function HttpClient(logger) {
        this.logger = logger;
        this.requestSeq = 0;
    }

    HttpClient.prototype.buildUrl = function (base, query) {
        if (!query || typeof query !== "object") {
            return base;
        }

        var keys = Object.keys(query);
        if (keys.length === 0) {
            return base;
        }

        var parts = [];
        for (var i = 0; i < keys.length; i++) {
            var key = keys[i];
            var value = query[key];
            if (value === undefined || value === null) {
                continue;
            }

            parts.push(encodeURIComponent(key) + "=" + encodeURIComponent(String(value)));
        }

        if (parts.length === 0) {
            return base;
        }

        return base + (base.indexOf("?") >= 0 ? "&" : "?") + parts.join("&");
    };

    HttpClient.prototype.request = function (options) {
        var self = this;
        var requestId = "req-" + (++this.requestSeq) + "-" + Date.now();
        var method = options.method || "GET";
        var url = this.buildUrl(options.url, options.query);
        var timeoutMs = ensureFiniteNumber(options.timeoutMs, 10000);
        var body = options.body;
        var headers = options.headers || {};
        var startMs = Date.now();

        this.logger.debug("http.request.start", "HTTP request started", {
            requestId: requestId,
            method: method,
            url: url,
            timeoutMs: timeoutMs
        });

        if (typeof fetch !== "function") {
            return this.requestViaXhr({
                requestId: requestId,
                method: method,
                url: url,
                headers: headers,
                body: body,
                timeoutMs: timeoutMs,
                startedMs: startMs
            });
        }

        var controller = typeof AbortController !== "undefined" ? new AbortController() : null;
        var timeoutHandle = null;
        var timeoutPromise = null;

        if (controller) {
            timeoutHandle = setTimeout(function () {
                controller.abort();
            }, timeoutMs);
        } else {
            timeoutPromise = new Promise(function (_resolve, reject) {
                timeoutHandle = setTimeout(function () {
                    reject(new Error("fetch timeout after " + timeoutMs + "ms"));
                }, timeoutMs);
            });
        }

        var fetchOptions = {
            method: method,
            headers: headers,
            body: body,
            signal: controller ? controller.signal : undefined
        };

        var requestPromise = fetch(url, fetchOptions)
            .then(function (response) {
                return response.text().then(function (text) {
                    var durationMs = Date.now() - startMs;
                    var parsedJson = null;

                    if (text && text.trim()) {
                        try {
                            parsedJson = JSON.parse(text);
                        } catch (_error) {
                            parsedJson = null;
                        }
                    }

                    self.logger.debug("http.request.finish", "HTTP request finished", {
                        requestId: requestId,
                        method: method,
                        url: url,
                        status: response.status,
                        ok: response.ok,
                        durationMs: durationMs,
                        responseBytes: text ? text.length : 0
                    });

                    return {
                        requestId: requestId,
                        ok: response.ok,
                        status: response.status,
                        statusText: response.statusText,
                        durationMs: durationMs,
                        text: text,
                        json: parsedJson
                    };
                });
            });

        var guardedPromise = timeoutPromise ? Promise.race([requestPromise, timeoutPromise]) : requestPromise;

        return guardedPromise
            .catch(function (error) {
                var durationMs = Date.now() - startMs;
                var diagnostic = classifyHttpError(error);
                self.logger.error("http.request.failed", "HTTP request failed", {
                    requestId: requestId,
                    method: method,
                    url: url,
                    durationMs: durationMs,
                    errorName: error && error.name ? String(error.name) : null,
                    error: String(error && error.message ? error.message : error),
                    diagnosticCategory: diagnostic.category,
                    diagnosticHint: diagnostic.hint
                });
                throw error;
            })
            .finally(function () {
                if (timeoutHandle) {
                    clearTimeout(timeoutHandle);
                }
            });
    };

    HttpClient.prototype.requestViaXhr = function (context) {
        var self = this;

        return new Promise(function (resolve, reject) {
            var xhr = new XMLHttpRequest();
            xhr.open(context.method, context.url, true);
            xhr.timeout = context.timeoutMs;

            var headerNames = Object.keys(context.headers);
            for (var i = 0; i < headerNames.length; i++) {
                xhr.setRequestHeader(headerNames[i], context.headers[headerNames[i]]);
            }

            xhr.onload = function () {
                var durationMs = Date.now() - context.startedMs;
                var text = xhr.responseText || "";
                var parsedJson = null;

                if (text && text.trim()) {
                    try {
                        parsedJson = JSON.parse(text);
                    } catch (_error) {
                        parsedJson = null;
                    }
                }

                var ok = xhr.status >= 200 && xhr.status < 300;

                self.logger.debug("http.request.finish", "HTTP request finished (XHR)", {
                    requestId: context.requestId,
                    method: context.method,
                    url: context.url,
                    status: xhr.status,
                    ok: ok,
                    durationMs: durationMs,
                    responseBytes: text.length
                });

                resolve({
                    requestId: context.requestId,
                    ok: ok,
                    status: xhr.status,
                    statusText: xhr.statusText || "",
                    durationMs: durationMs,
                    text: text,
                    json: parsedJson
                });
            };

            xhr.onerror = function () {
                var durationMs = Date.now() - context.startedMs;
                self.logger.error("http.request.failed", "HTTP request failed (XHR)", {
                    requestId: context.requestId,
                    method: context.method,
                    url: context.url,
                    durationMs: durationMs,
                    error: "network error"
                });
                reject(new Error("XMLHttpRequest network error"));
            };

            xhr.ontimeout = function () {
                var durationMs = Date.now() - context.startedMs;
                self.logger.error("http.request.failed", "HTTP request timeout (XHR)", {
                    requestId: context.requestId,
                    method: context.method,
                    url: context.url,
                    durationMs: durationMs,
                    timeoutMs: context.timeoutMs
                });
                reject(new Error("XMLHttpRequest timeout"));
            };

            xhr.send(context.body || null);
        });
    };

    function SimVarBridge(logger) {
        this.logger = logger;
        this.commandProfiles = {
            transponderCode: [
                { simvar: "TRANSPONDER CODE:2", unit: "BCD16", transform: "int" },
                { simvar: "K:XPNDR_SET", unit: "Frequency BCD16", transform: "int" }
            ],
            adfActiveFrequency: [
                { simvar: "ADF ACTIVE FREQUENCY:1", unit: "Frequency ADF BCD32", transform: "int" },
                { simvar: "K:ADF_COMPLETE_SET", unit: "Frequency ADF BCD32", transform: "int" }
            ],
            adfStandbyFrequencyHz: [
                { simvar: "ADF STANDBY FREQUENCY:1", unit: "Hz", transform: "number" },
                { simvar: "K:ADF_STBY_SET", unit: "Hz", transform: "number" }
            ],
            gearHandle: [
                { simvar: "GEAR HANDLE POSITION", unit: "Bool", transform: "bool" },
                { simvar: "K:GEAR_SET", unit: "Bool", transform: "bool" }
            ],
            flapsHandleIndex: [
                { simvar: "FLAPS HANDLE INDEX", unit: "number", transform: "int" },
                { simvar: "K:FLAPS_SET", unit: "number", transform: "int" }
            ],
            parkingBrake: [
                { simvar: "BRAKE PARKING POSITION", unit: "Bool", transform: "bool" },
                { simvar: "K:PARKING_BRAKES", unit: "Bool", transform: "bool" }
            ],
            autopilotMaster: [
                { simvar: "AUTOPILOT MASTER", unit: "Bool", transform: "bool" },
                { simvar: "K:AP_MASTER", unit: "Bool", transform: "bool" }
            ]
        };
    }

    SimVarBridge.prototype.hasApi = function () {
        return typeof SimVar !== "undefined" && SimVar && typeof SimVar.GetSimVarValue === "function";
    };

    SimVarBridge.prototype.getRawValue = function (name, unit) {
        if (!this.hasApi()) {
            throw new Error("SimVar API unavailable");
        }

        return SimVar.GetSimVarValue(name, unit);
    };

    SimVarBridge.prototype.normalizeReadValue = function (raw, parseMode) {
        if (parseMode === "bool") {
            if (typeof raw === "boolean") {
                return raw;
            }

            var numberValue = Number(raw);
            if (!Number.isFinite(numberValue)) {
                throw new Error("Expected bool-compatible value but got " + String(raw));
            }

            return numberValue !== 0;
        }

        if (parseMode === "int") {
            var intNumber = Number(raw);
            if (!Number.isFinite(intNumber)) {
                throw new Error("Expected int-compatible value but got " + String(raw));
            }

            return Math.round(intNumber);
        }

        var number = Number(raw);
        if (!Number.isFinite(number)) {
            throw new Error("Expected numeric value but got " + String(raw));
        }

        return number;
    };

    SimVarBridge.prototype.readTelemetry = function () {
        if (!this.hasApi()) {
            throw new Error("SimVar API unavailable");
        }

        var telemetry = {};
        var failures = [];

        for (var i = 0; i < TELEMETRY_VARIABLES.length; i++) {
            var definition = TELEMETRY_VARIABLES[i];
            var valueResolved = false;
            var lastError = null;

            for (var j = 0; j < definition.candidates.length; j++) {
                var candidate = definition.candidates[j];

                try {
                    var rawValue = this.getRawValue(candidate.name, candidate.unit);
                    var parsedValue = this.normalizeReadValue(rawValue, definition.parse);
                    telemetry[definition.key] = parsedValue;
                    valueResolved = true;
                    break;
                } catch (error) {
                    lastError = error;
                }
            }

            if (!valueResolved) {
                failures.push({
                    key: definition.key,
                    error: String(lastError && lastError.message ? lastError.message : lastError)
                });
            }
        }

        if (failures.length > 0) {
            var firstFailure = failures[0];
            throw new Error("Telemetry read failed for " + failures.length + " variable(s). First: " + firstFailure.key + " -> " + firstFailure.error);
        }

        return telemetry;
    };

    SimVarBridge.prototype.transformValue = function (rawValue, transformType) {
        var numberValue = Number(rawValue);
        if (!Number.isFinite(numberValue)) {
            throw new Error("Command value must be finite");
        }

        if (transformType === "bool") {
            return numberValue >= 0.5 ? 1 : 0;
        }

        if (transformType === "int") {
            return Math.round(numberValue);
        }

        return numberValue;
    };

    SimVarBridge.prototype.applySingleCommand = async function (command) {
        if (!this.hasApi() || typeof SimVar.SetSimVarValue !== "function") {
            throw new Error("SimVar set API unavailable");
        }

        var profile = this.commandProfiles[command.parameter];
        if (!profile) {
            throw new Error("Unsupported command parameter: " + command.parameter);
        }

        var attempts = [];

        for (var i = 0; i < profile.length; i++) {
            var step = profile[i];
            var mappedValue = this.transformValue(command.value, step.transform);

            try {
                await Promise.resolve(SimVar.SetSimVarValue(step.simvar, step.unit, mappedValue));
                return {
                    applied: true,
                    simvar: step.simvar,
                    unit: step.unit,
                    value: mappedValue,
                    attempts: attempts
                };
            } catch (error) {
                attempts.push({
                    simvar: step.simvar,
                    unit: step.unit,
                    value: mappedValue,
                    error: String(error && error.message ? error.message : error)
                });
            }
        }

        throw new Error("All command set attempts failed: " + safeStringify(attempts));
    };

    SimVarBridge.prototype.applyCommands = async function (commands) {
        var results = [];
        for (var i = 0; i < commands.length; i++) {
            var command = commands[i];

            try {
                var result = await this.applySingleCommand(command);
                results.push({
                    command: command,
                    ok: true,
                    detail: result
                });
            } catch (error) {
                results.push({
                    command: command,
                    ok: false,
                    error: String(error && error.message ? error.message : error)
                });
            }
        }

        return results;
    };

    function BridgeUI(instrument, logger) {
        this.instrument = instrument;
        this.logger = logger;
        this.bound = false;
        this.elements = {};
        this.maxLogRender = 350;
        this.renderedLogCount = 0;
    }

    BridgeUI.prototype.bind = function (runtime) {
        if (this.bound) {
            return;
        }

        this.elements = {
            uptime: this.instrument.querySelector("#osb-runtime-uptime"),
            token: this.instrument.querySelector("#osb-token"),
            loginUrl: this.instrument.querySelector("#osb-login-url"),
            userBadge: this.instrument.querySelector("#osb-state-user"),
            userName: this.instrument.querySelector("#osb-state-user-name"),
            simBadge: this.instrument.querySelector("#osb-state-sim"),
            flightDetail: this.instrument.querySelector("#osb-state-flight"),
            networkBadge: this.instrument.querySelector("#osb-state-network"),
            networkDetail: this.instrument.querySelector("#osb-state-network-detail"),
            commandBadge: this.instrument.querySelector("#osb-state-command"),
            commandDetail: this.instrument.querySelector("#osb-state-command-detail"),
            counters: this.instrument.querySelector("#osb-counters"),
            logs: this.instrument.querySelector("#osb-log-console"),
            inputBaseUrl: this.instrument.querySelector("#osb-base-url"),
            inputMeUrl: this.instrument.querySelector("#osb-me-url"),
            inputStatusUrl: this.instrument.querySelector("#osb-status-url"),
            inputTelemetryUrl: this.instrument.querySelector("#osb-telemetry-url"),
            inputAuthToken: this.instrument.querySelector("#osb-auth-token"),
            inputRemoteDebugUrl: this.instrument.querySelector("#osb-remote-debug-url"),
            inputActiveInterval: this.instrument.querySelector("#osb-active-interval"),
            inputIdleInterval: this.instrument.querySelector("#osb-idle-interval"),
            inputLoginPoll: this.instrument.querySelector("#osb-login-poll"),
            inputReadHz: this.instrument.querySelector("#osb-read-hz"),
            inputStaleSeconds: this.instrument.querySelector("#osb-stale-seconds"),
            inputTimeoutMs: this.instrument.querySelector("#osb-timeout-ms"),
            inputRemoteDebugEnabled: this.instrument.querySelector("#osb-remote-debug-enabled"),
            inputAutoOpenLogin: this.instrument.querySelector("#osb-auto-open-login"),
            inputSimCommandEnabled: this.instrument.querySelector("#osb-sim-command-enabled"),
            inputDebugConsole: this.instrument.querySelector("#osb-debug-console"),
            btnSaveConfig: this.instrument.querySelector("#osb-btn-save-config"),
            btnOpenLogin: this.instrument.querySelector("#osb-btn-open-login"),
            btnCopyLogin: this.instrument.querySelector("#osb-btn-copy-login"),
            btnNewToken: this.instrument.querySelector("#osb-btn-new-token"),
            btnForceLoginPoll: this.instrument.querySelector("#osb-btn-force-login-poll"),
            btnForceStatus: this.instrument.querySelector("#osb-btn-force-status"),
            btnForceTelemetry: this.instrument.querySelector("#osb-btn-force-telemetry"),
            btnPushDebug: this.instrument.querySelector("#osb-btn-push-debug"),
            btnExportLogs: this.instrument.querySelector("#osb-btn-export-logs"),
            btnClearLogs: this.instrument.querySelector("#osb-btn-clear-logs")
        };

        this.bindClick(this.elements.btnSaveConfig, function () {
            runtime.onSaveConfigRequested();
        });

        this.bindClick(this.elements.btnOpenLogin, function () {
            runtime.openLoginUrl("ui-button");
        });

        this.bindClick(this.elements.btnCopyLogin, function () {
            runtime.copyLoginUrl("ui-button");
        });

        this.bindClick(this.elements.btnNewToken, function () {
            runtime.regenerateToken("ui-button");
        });

        this.bindClick(this.elements.btnForceLoginPoll, function () {
            runtime.forceLoginPoll("ui-button");
        });

        this.bindClick(this.elements.btnForceStatus, function () {
            runtime.forceStatusHeartbeat("ui-button");
        });

        this.bindClick(this.elements.btnForceTelemetry, function () {
            runtime.forceTelemetryTick("ui-button");
        });

        this.bindClick(this.elements.btnPushDebug, function () {
            runtime.pushDebugSnapshot("ui-button");
        });

        this.bindClick(this.elements.btnExportLogs, function () {
            runtime.exportLogs("ui-button");
        });

        this.bindClick(this.elements.btnClearLogs, function () {
            runtime.clearLogs("ui-button");
        });

        this.bound = true;
    };

    BridgeUI.prototype.bindClick = function (element, handler) {
        if (!element) {
            return;
        }

        element.addEventListener("click", function () {
            try {
                handler();
            } catch (error) {
                if (typeof console !== "undefined") {
                    console.error(error);
                }
            }
        });
    };

    BridgeUI.prototype.applyBadgeState = function (element, text, stateClass) {
        if (!element) {
            return;
        }

        element.textContent = text;
        element.classList.remove("osb-badge-on", "osb-badge-off", "osb-badge-warn");
        element.classList.add(stateClass);
    };

    BridgeUI.prototype.updateUptime = function (uptimeMs) {
        if (!this.elements.uptime) {
            return;
        }

        var seconds = Math.floor(uptimeMs / 1000);
        this.elements.uptime.textContent = "uptime: " + seconds + "s";
    };

    BridgeUI.prototype.updateTokenSection = function (token, loginUrl) {
        if (this.elements.token) {
            this.elements.token.value = token || "";
        }

        if (this.elements.loginUrl) {
            this.elements.loginUrl.value = loginUrl || "";
        }
    };

    BridgeUI.prototype.updateConfigInputs = function (config) {
        if (!config) {
            return;
        }

        if (this.elements.inputBaseUrl) this.elements.inputBaseUrl.value = config.baseUrl || "";
        if (this.elements.inputMeUrl) this.elements.inputMeUrl.value = config.meUrl || "";
        if (this.elements.inputStatusUrl) this.elements.inputStatusUrl.value = config.statusUrl || "";
        if (this.elements.inputTelemetryUrl) this.elements.inputTelemetryUrl.value = config.telemetryUrl || "";
        if (this.elements.inputAuthToken) this.elements.inputAuthToken.value = config.authToken || "";
        if (this.elements.inputRemoteDebugUrl) this.elements.inputRemoteDebugUrl.value = config.remoteDebugUrl || "";
        if (this.elements.inputActiveInterval) this.elements.inputActiveInterval.value = config.activeIntervalSec;
        if (this.elements.inputIdleInterval) this.elements.inputIdleInterval.value = config.idleIntervalSec;
        if (this.elements.inputLoginPoll) this.elements.inputLoginPoll.value = config.loginPollSec;
        if (this.elements.inputReadHz) this.elements.inputReadHz.value = config.telemetryReadHz;
        if (this.elements.inputStaleSeconds) this.elements.inputStaleSeconds.value = config.staleTelemetrySec;
        if (this.elements.inputTimeoutMs) this.elements.inputTimeoutMs.value = config.requestTimeoutMs;
        if (this.elements.inputRemoteDebugEnabled) this.elements.inputRemoteDebugEnabled.checked = !!config.remoteDebugEnabled;
        if (this.elements.inputAutoOpenLogin) this.elements.inputAutoOpenLogin.checked = !!config.autoOpenLogin;
        if (this.elements.inputSimCommandEnabled) this.elements.inputSimCommandEnabled.checked = !!config.simCommandEnabled;
        if (this.elements.inputDebugConsole) this.elements.inputDebugConsole.checked = !!config.debugEchoToConsole;
    };

    BridgeUI.prototype.collectConfigFromInputs = function () {
        return {
            baseUrl: this.elements.inputBaseUrl ? this.elements.inputBaseUrl.value : "",
            meUrl: this.elements.inputMeUrl ? this.elements.inputMeUrl.value : "",
            statusUrl: this.elements.inputStatusUrl ? this.elements.inputStatusUrl.value : "",
            telemetryUrl: this.elements.inputTelemetryUrl ? this.elements.inputTelemetryUrl.value : "",
            authToken: this.elements.inputAuthToken ? this.elements.inputAuthToken.value : "",
            remoteDebugUrl: this.elements.inputRemoteDebugUrl ? this.elements.inputRemoteDebugUrl.value : "",
            activeIntervalSec: this.elements.inputActiveInterval ? this.elements.inputActiveInterval.value : "",
            idleIntervalSec: this.elements.inputIdleInterval ? this.elements.inputIdleInterval.value : "",
            loginPollSec: this.elements.inputLoginPoll ? this.elements.inputLoginPoll.value : "",
            telemetryReadHz: this.elements.inputReadHz ? this.elements.inputReadHz.value : "",
            staleTelemetrySec: this.elements.inputStaleSeconds ? this.elements.inputStaleSeconds.value : "",
            requestTimeoutMs: this.elements.inputTimeoutMs ? this.elements.inputTimeoutMs.value : "",
            remoteDebugEnabled: this.elements.inputRemoteDebugEnabled ? this.elements.inputRemoteDebugEnabled.checked : false,
            autoOpenLogin: this.elements.inputAutoOpenLogin ? this.elements.inputAutoOpenLogin.checked : false,
            simCommandEnabled: this.elements.inputSimCommandEnabled ? this.elements.inputSimCommandEnabled.checked : true,
            debugEchoToConsole: this.elements.inputDebugConsole ? this.elements.inputDebugConsole.checked : true
        };
    };

    BridgeUI.prototype.updateStateCards = function (state) {
        this.applyBadgeState(this.elements.userBadge, state.userConnected ? "connected" : "disconnected", state.userConnected ? "osb-badge-on" : "osb-badge-off");

        if (this.elements.userName) {
            this.elements.userName.textContent = state.userName ? "user: " + state.userName : "user: -";
        }

        this.applyBadgeState(this.elements.simBadge, state.simConnected ? "connected" : "offline", state.simConnected ? "osb-badge-on" : "osb-badge-off");

        if (this.elements.flightDetail) {
            this.elements.flightDetail.textContent = "flight: " + (state.flightLoaded ? "active" : "inactive");
        }

        var networkClass = state.networkLevel === "error" ? "osb-badge-off" : (state.networkLevel === "warn" ? "osb-badge-warn" : "osb-badge-on");
        this.applyBadgeState(this.elements.networkBadge, state.networkLabel || "idle", networkClass);

        if (this.elements.networkDetail) {
            this.elements.networkDetail.textContent = state.networkDetail || "";
        }

        var commandClass = state.commandFailedInLastRun ? "osb-badge-warn" : (state.commandAppliedInLastRun ? "osb-badge-on" : "osb-badge-off");
        var commandLabel = state.commandAppliedInLastRun ? "applied" : (state.commandFailedInLastRun ? "partial" : "none");
        this.applyBadgeState(this.elements.commandBadge, commandLabel, commandClass);

        if (this.elements.commandDetail) {
            this.elements.commandDetail.textContent = state.commandDetail || "0 applied / 0 failed";
        }
    };

    BridgeUI.prototype.updateCounters = function (counters) {
        if (!this.elements.counters) {
            return;
        }

        this.elements.counters.innerHTML = "";

        var names = Object.keys(counters);
        for (var i = 0; i < names.length; i++) {
            var name = names[i];
            var value = counters[name];

            var wrapper = document.createElement("div");
            wrapper.className = "osb-counter";

            var label = document.createElement("div");
            label.className = "osb-counter-name";
            label.textContent = name;

            var valueEl = document.createElement("div");
            valueEl.className = "osb-counter-value";
            valueEl.textContent = String(value);

            wrapper.appendChild(label);
            wrapper.appendChild(valueEl);
            this.elements.counters.appendChild(wrapper);
        }
    };

    BridgeUI.prototype.appendLogEntry = function (entry) {
        if (!this.elements.logs) {
            return;
        }

        var line = document.createElement("div");
        line.className = "osb-log-entry osb-log-level-" + entry.level;

        var details = entry.details ? " " + safeStringify(entry.details) : "";
        line.textContent = "#" + entry.seq + " " + entry.ts + " [" + entry.level + "] " + entry.event + " " + entry.message + details;

        this.elements.logs.appendChild(line);
        this.renderedLogCount++;

        while (this.elements.logs.childNodes.length > this.maxLogRender) {
            this.elements.logs.removeChild(this.elements.logs.firstChild);
        }

        this.elements.logs.scrollTop = this.elements.logs.scrollHeight;
    };

    BridgeUI.prototype.clearLogs = function () {
        if (this.elements.logs) {
            this.elements.logs.innerHTML = "";
        }
        this.renderedLogCount = 0;
    };

    function OpenSquawkBridgeRuntime(instrument) {
        this.instrument = instrument;
        this.shared = OpenSquawkBridgeShared;
        this.logger = new RuntimeLogger(this.shared.DEFAULT_CONFIG.debugMaxEntries, this.shared.DEFAULT_CONFIG.debugEchoToConsole);
        this.storage = new StorageProvider(this.logger);
        this.http = new HttpClient(this.logger);
        this.sim = new SimVarBridge(this.logger);
        this.ui = new BridgeUI(instrument, this.logger);

        this.config = this.shared.resolveConfig(this.storage.readJson(STORAGE_KEYS.config) || {});
        this.logger.setOptions(this.config.debugMaxEntries, this.config.debugEchoToConsole);

        this.token = this.storage.readString(STORAGE_KEYS.token) || "";
        this.userConnected = false;
        this.userName = null;

        this.rawSimConnected = false;
        this.rawFlightLoaded = false;
        this.simConnected = false;
        this.flightLoaded = false;

        this.latestTelemetry = null;
        this.latestTelemetryTsMs = 0;
        this.telemetryReadCount = 0;

        this.lastNetworkLevel = "warn";
        this.lastNetworkLabel = "idle";
        this.lastNetworkDetail = "no requests yet";

        this.lastCommandAppliedCount = 0;
        this.lastCommandFailedCount = 0;

        this.flags = {
            loginPollInFlight: false,
            statusPostInFlight: false,
            telemetryPostInFlight: false,
            debugPushInFlight: false
        };

        this.counters = {
            telemetry_reads: 0,
            telemetry_read_failures: 0,
            login_polls: 0,
            login_poll_failures: 0,
            login_connected_changes: 0,
            status_posts: 0,
            status_post_failures: 0,
            telemetry_posts: 0,
            telemetry_post_failures: 0,
            telemetry_post_skipped: 0,
            command_parsed: 0,
            command_applied: 0,
            command_failed: 0,
            debug_push_success: 0,
            debug_push_failures: 0
        };

        this.startMs = Date.now();
        this.nextTelemetryReadMs = this.startMs;
        this.nextLoginPollMs = this.startMs;
        this.nextActiveTickMs = this.startMs;
        this.nextIdleHeartbeatMs = this.startMs;
        this.nextRemoteDebugMs = this.startMs + 20000;

        this.logger.onEntry = this.onLogEntry.bind(this);
        this.ui.bind(this);

        this.initialize();
    }

    OpenSquawkBridgeRuntime.prototype.initialize = function () {
        this.logger.info("runtime.boot", "OpenSquawk Bridge instrument boot", {
            simVarApiAvailable: this.sim.hasApi(),
            config: this.config
        });

        this.ui.updateConfigInputs(this.config);

        var originalToken = this.token;
        var normalizedToken = this.shared.normalizeToken(this.token);

        if (this.shared.isTokenValid(normalizedToken)) {
            this.token = normalizedToken;
            if (this.token !== originalToken) {
                this.storage.writeString(STORAGE_KEYS.token, this.token);
            }
            this.logger.info("token.loaded", "Loaded existing bridge token", {
                tokenLength: this.token.length
            });
        } else {
            if (originalToken) {
                this.logger.warn("token.invalid", "Stored token is invalid for current policy; generating a new token", {
                    previousTokenLength: originalToken.length
                });
            }

            this.token = this.shared.generateToken();
            this.storage.writeString(STORAGE_KEYS.token, this.token);
            this.logger.info("token.generated", "Generated new bridge token", {
                tokenLength: this.token.length
            });
        }

        this.ui.updateTokenSection(this.token, this.getLoginUrl());
        this.refreshUi();

        this.forceLoginPoll("startup");
        this.forceStatusHeartbeat("startup");

        if (this.config.autoOpenLogin) {
            this.openLoginUrl("startup-auto");
        }
    };

    OpenSquawkBridgeRuntime.prototype.dispose = function () {
        this.logger.info("runtime.dispose", "Runtime disposed");
    };

    OpenSquawkBridgeRuntime.prototype.onLogEntry = function (entry) {
        this.ui.appendLogEntry(entry);
    };

    OpenSquawkBridgeRuntime.prototype.refreshUi = function () {
        this.ui.updateTokenSection(this.token, this.getLoginUrl());
        this.ui.updateStateCards({
            userConnected: this.userConnected,
            userName: this.userName,
            simConnected: this.simConnected,
            flightLoaded: this.flightLoaded,
            networkLevel: this.lastNetworkLevel,
            networkLabel: this.lastNetworkLabel,
            networkDetail: this.lastNetworkDetail,
            commandAppliedInLastRun: this.lastCommandAppliedCount > 0,
            commandFailedInLastRun: this.lastCommandFailedCount > 0,
            commandDetail: this.lastCommandAppliedCount + " applied / " + this.lastCommandFailedCount + " failed"
        });
        this.ui.updateCounters(this.counters);
    };

    OpenSquawkBridgeRuntime.prototype.setNetworkStatus = function (level, label, detail) {
        this.lastNetworkLevel = level;
        this.lastNetworkLabel = label;
        this.lastNetworkDetail = detail;
        this.refreshUi();
    };

    OpenSquawkBridgeRuntime.prototype.update = function (nowMs) {
        this.ui.updateUptime(nowMs - this.startMs);

        if (nowMs >= this.nextTelemetryReadMs) {
            this.nextTelemetryReadMs = nowMs + Math.max(1000 / this.config.telemetryReadHz, 200);
            this.readTelemetryCycle("interval");
        }

        if (nowMs >= this.nextLoginPollMs) {
            this.nextLoginPollMs = nowMs + this.config.loginPollSec * 1000;
            this.forceLoginPoll("interval");
        }

        if (this.userConnected && this.flightLoaded) {
            if (nowMs >= this.nextActiveTickMs) {
                this.nextActiveTickMs = nowMs + this.config.activeIntervalSec * 1000;
                this.forceTelemetryTick("active-interval");
            }
        } else {
            if (nowMs >= this.nextIdleHeartbeatMs) {
                this.nextIdleHeartbeatMs = nowMs + this.config.idleIntervalSec * 1000;
                this.forceStatusHeartbeat("idle-interval");
            }
        }

        if (this.config.remoteDebugEnabled && this.config.remoteDebugUrl && nowMs >= this.nextRemoteDebugMs) {
            this.nextRemoteDebugMs = nowMs + 20000;
            this.pushDebugSnapshot("scheduled");
        }
    };

    OpenSquawkBridgeRuntime.prototype.onSaveConfigRequested = function () {
        var input = this.ui.collectConfigFromInputs();
        this.updateConfig(input, "ui-save");
    };

    OpenSquawkBridgeRuntime.prototype.updateConfig = function (partialConfig, reason) {
        var merged = {};
        var currentKeys = Object.keys(this.config);

        for (var i = 0; i < currentKeys.length; i++) {
            var key = currentKeys[i];
            merged[key] = this.config[key];
        }

        var updateKeys = Object.keys(partialConfig || {});
        for (var j = 0; j < updateKeys.length; j++) {
            var updateKey = updateKeys[j];
            merged[updateKey] = partialConfig[updateKey];
        }

        this.config = this.shared.resolveConfig(merged);
        this.logger.setOptions(this.config.debugMaxEntries, this.config.debugEchoToConsole);

        this.storage.writeJson(STORAGE_KEYS.config, this.config);
        this.ui.updateConfigInputs(this.config);
        this.refreshUi();

        this.logger.info("config.updated", "Configuration updated", {
            reason: reason,
            config: this.config
        });
    };

    OpenSquawkBridgeRuntime.prototype.getLoginUrl = function () {
        return this.config.baseUrl + "/bridge/connect?token=" + encodeURIComponent(this.token || "");
    };

    OpenSquawkBridgeRuntime.prototype.openLoginUrl = async function (reason) {
        var url = this.getLoginUrl();

        this.logger.info("login.open.attempt", "Attempting to open login URL", {
            reason: reason,
            url: url
        });

        if (typeof Coherent !== "undefined" && Coherent && typeof Coherent.call === "function") {
            var coherentMethods = [
                "LAUNCH_EXTERNAL_BROWSER",
                "OPEN_URL",
                "OPEN_WEB_URL"
            ];

            for (var i = 0; i < coherentMethods.length; i++) {
                var method = coherentMethods[i];
                try {
                    await Promise.resolve(Coherent.call(method, url));
                    this.logger.info("login.open.success", "Opened login URL via Coherent", {
                        method: method
                    });
                    return true;
                } catch (error) {
                    this.logger.warn("login.open.coherent_failed", "Coherent open method failed", {
                        method: method,
                        error: String(error && error.message ? error.message : error)
                    });
                }
            }
        }

        if (typeof window !== "undefined" && typeof window.open === "function") {
            try {
                var handle = window.open(url, "_blank");
                this.logger.info("login.open.window", "window.open invoked for login URL", {
                    handleCreated: !!handle
                });
                return !!handle;
            } catch (error2) {
                this.logger.error("login.open.window_failed", "window.open failed", {
                    error: String(error2 && error2.message ? error2.message : error2)
                });
            }
        }

        this.logger.warn("login.open.manual_required", "Unable to auto-open login URL. Use copy/export and open externally.", {
            url: url
        });

        return false;
    };

    OpenSquawkBridgeRuntime.prototype.copyLoginUrl = async function (reason) {
        var url = this.getLoginUrl();
        this.logger.info("login.copy.attempt", "Attempting to copy login URL", {
            reason: reason,
            url: url
        });

        if (typeof navigator !== "undefined" && navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
            try {
                await navigator.clipboard.writeText(url);
                this.logger.info("login.copy.success", "Login URL copied to clipboard");
                return true;
            } catch (error) {
                this.logger.warn("login.copy.failed", "Clipboard write failed", {
                    error: String(error && error.message ? error.message : error)
                });
            }
        }

        this.logger.warn("login.copy.unavailable", "Clipboard API unavailable in this context", {
            url: url
        });
        return false;
    };

    OpenSquawkBridgeRuntime.prototype.regenerateToken = function (reason) {
        var previousToken = this.token;
        this.token = this.shared.generateToken();
        this.storage.writeString(STORAGE_KEYS.token, this.token);

        this.logger.info("token.regenerated", "Bridge token regenerated", {
            reason: reason,
            previousTokenPrefix: previousToken ? previousToken.slice(0, this.shared.TOKEN_LENGTH) : null,
            newTokenPrefix: this.token.slice(0, this.shared.TOKEN_LENGTH)
        });

        this.ui.updateTokenSection(this.token, this.getLoginUrl());
        this.forceLoginPoll("token-regenerated");
        this.forceStatusHeartbeat("token-regenerated");
    };

    OpenSquawkBridgeRuntime.prototype.forceLoginPoll = function (reason) {
        if (this.flags.loginPollInFlight) {
            this.logger.debug("login.poll.skip", "Login poll skipped because another poll is in flight", {
                reason: reason
            });
            return;
        }

        this.flags.loginPollInFlight = true;
        this.counters.login_polls++;

        this.pollLogin(reason)
            .catch(function () {
                // Error already logged in pollLogin
            })
            .finally(function () {
                this.flags.loginPollInFlight = false;
                this.refreshUi();
            }.bind(this));
    };

    OpenSquawkBridgeRuntime.prototype.pollLogin = async function (reason) {
        var response;

        try {
            response = await this.http.request({
                method: "GET",
                url: this.config.meUrl,
                headers: this.buildHeaders(false),
                timeoutMs: this.config.requestTimeoutMs
            });
        } catch (error) {
            this.counters.login_poll_failures++;
            this.setNetworkStatus("error", "login error", "network failure");
            this.logger.error("login.poll.failed", "Login poll request failed", {
                reason: reason,
                error: String(error && error.message ? error.message : error)
            });
            return;
        }

        this.setNetworkStatus(response.ok ? "info" : "warn", "login " + response.status, response.statusText || "");

        var connected = false;
        var userName = null;

        if (response.ok && response.json && typeof response.json === "object") {
            if (!(response.json.connected === false)) {
                connected = true;
                userName = this.shared.extractUserName(response.json);
            }
        }

        this.applyUserState(connected, userName, "poll:" + reason + ":" + response.status);
    };

    OpenSquawkBridgeRuntime.prototype.applyUserState = function (connected, userName, reason) {
        var changed = connected !== this.userConnected || (userName || null) !== (this.userName || null);

        this.userConnected = connected;
        this.userName = userName || null;

        if (changed) {
            this.counters.login_connected_changes++;
            this.logger.info("state.user.changed", "User connection state changed", {
                reason: reason,
                userConnected: this.userConnected,
                userName: this.userName
            });
        }

        this.recomputeEffectiveSimState("user-state");
        this.refreshUi();

        if (changed) {
            this.forceStatusHeartbeat("user-state-change");
            if (this.userConnected && this.flightLoaded) {
                this.forceTelemetryTick("user-state-change");
            }
        }
    };

    OpenSquawkBridgeRuntime.prototype.readTelemetryCycle = function (reason) {
        this.counters.telemetry_reads++;

        var telemetry;

        try {
            telemetry = this.sim.readTelemetry();
        } catch (error) {
            this.counters.telemetry_read_failures++;
            this.rawSimConnected = false;
            this.rawFlightLoaded = false;
            this.recomputeEffectiveSimState("telemetry-read-failure");

            this.logger.error("telemetry.read.failed", "Telemetry read failed", {
                reason: reason,
                error: String(error && error.message ? error.message : error)
            });
            this.refreshUi();
            return;
        }

        this.latestTelemetry = telemetry;
        this.latestTelemetryTsMs = Date.now();
        this.telemetryReadCount++;

        this.rawSimConnected = true;
        this.rawFlightLoaded = this.shared.isValidCoordinate(telemetry.latitude, -90, 90)
            && this.shared.isValidCoordinate(telemetry.longitude, -180, 180);

        this.recomputeEffectiveSimState("telemetry-read");

        var sampleEvery = Math.max(1, this.config.debugSampleEveryNReads);
        if ((this.telemetryReadCount % sampleEvery) === 0) {
            this.logger.debug("telemetry.read.ok", "Telemetry sampled", {
                reason: reason,
                latitude: telemetry.latitude,
                longitude: telemetry.longitude,
                altitude: telemetry.altitude,
                ias: telemetry.airspeedIndicated,
                onGround: telemetry.onGround,
                flightLoaded: this.flightLoaded,
                userConnected: this.userConnected
            });
        }

        this.refreshUi();
    };

    OpenSquawkBridgeRuntime.prototype.recomputeEffectiveSimState = function (reason) {
        var effectiveSimConnected = this.userConnected ? this.rawSimConnected : false;
        var effectiveFlightLoaded = this.userConnected ? this.rawFlightLoaded : false;

        var changed = effectiveSimConnected !== this.simConnected || effectiveFlightLoaded !== this.flightLoaded;

        this.simConnected = effectiveSimConnected;
        this.flightLoaded = effectiveFlightLoaded;

        if (changed) {
            this.logger.info("state.sim.changed", "Simulator state changed", {
                reason: reason,
                userConnected: this.userConnected,
                rawSimConnected: this.rawSimConnected,
                rawFlightLoaded: this.rawFlightLoaded,
                simConnected: this.simConnected,
                flightLoaded: this.flightLoaded
            });

            this.nextActiveTickMs = Date.now();
            this.nextIdleHeartbeatMs = Date.now();
            this.forceStatusHeartbeat("sim-state-change");

            if (this.userConnected && this.flightLoaded) {
                this.forceTelemetryTick("sim-state-change");
            }
        }
    };

    OpenSquawkBridgeRuntime.prototype.forceStatusHeartbeat = function (reason) {
        if (this.flags.statusPostInFlight) {
            this.logger.debug("status.post.skip", "Status post skipped because request is already in flight", {
                reason: reason
            });
            return;
        }

        this.flags.statusPostInFlight = true;

        this.sendStatusHeartbeat(reason)
            .catch(function () {
                // Error already logged
            })
            .finally(function () {
                this.flags.statusPostInFlight = false;
                this.refreshUi();
            }.bind(this));
    };

    OpenSquawkBridgeRuntime.prototype.sendStatusHeartbeat = async function (reason) {
        this.counters.status_posts++;

        var payload = {
            token: this.token,
            simConnected: this.simConnected,
            flightActive: this.flightLoaded
        };

        this.logger.debug("status.post.prepare", "Preparing status heartbeat", {
            reason: reason,
            payload: payload
        });

        var response;
        try {
            response = await this.http.request({
                method: "POST",
                url: this.config.statusUrl,
                body: JSON.stringify(payload),
                headers: this.buildHeaders(true),
                timeoutMs: this.config.requestTimeoutMs
            });
        } catch (error) {
            this.counters.status_post_failures++;
            this.setNetworkStatus("error", "status error", "network failure");
            this.logger.error("status.post.failed", "Status heartbeat request failed", {
                reason: reason,
                error: String(error && error.message ? error.message : error)
            });
            return;
        }

        if (!response.ok) {
            this.counters.status_post_failures++;
            this.setNetworkStatus("warn", "status " + response.status, response.statusText || "");
            this.logger.warn("status.post.non_success", "Status heartbeat returned non-success status", {
                reason: reason,
                status: response.status,
                responseTextPreview: response.text ? response.text.slice(0, 200) : ""
            });
            return;
        }

        this.setNetworkStatus("info", "status ok", "status=" + response.status + " in " + response.durationMs + "ms");
        this.logger.info("status.post.success", "Status heartbeat sent", {
            reason: reason,
            status: response.status,
            durationMs: response.durationMs
        });
    };

    OpenSquawkBridgeRuntime.prototype.forceTelemetryTick = function (reason) {
        if (this.flags.telemetryPostInFlight) {
            this.logger.debug("telemetry.post.skip", "Telemetry tick skipped because request is already in flight", {
                reason: reason
            });
            return;
        }

        this.flags.telemetryPostInFlight = true;

        this.sendTelemetryTick(reason)
            .catch(function () {
                // Error already logged
            })
            .finally(function () {
                this.flags.telemetryPostInFlight = false;
                this.refreshUi();
            }.bind(this));
    };

    OpenSquawkBridgeRuntime.prototype.sendTelemetryTick = async function (reason) {
        if (!this.userConnected || !this.simConnected || !this.flightLoaded) {
            this.counters.telemetry_post_skipped++;
            this.logger.debug("telemetry.post.gated", "Telemetry tick skipped due to state gating", {
                reason: reason,
                userConnected: this.userConnected,
                simConnected: this.simConnected,
                flightLoaded: this.flightLoaded
            });
            return;
        }

        if (!this.latestTelemetry) {
            this.counters.telemetry_post_skipped++;
            this.logger.debug("telemetry.post.gated", "Telemetry tick skipped because telemetry is unavailable", {
                reason: reason
            });
            return;
        }

        var ageMs = Date.now() - this.latestTelemetryTsMs;
        if (ageMs > this.config.staleTelemetrySec * 1000) {
            this.counters.telemetry_post_skipped++;
            this.logger.warn("telemetry.post.gated", "Telemetry tick skipped because telemetry is stale", {
                reason: reason,
                ageMs: ageMs,
                staleLimitMs: this.config.staleTelemetrySec * 1000
            });
            return;
        }

        if (!this.shared.isValidCoordinate(this.latestTelemetry.latitude, -90, 90)
            || !this.shared.isValidCoordinate(this.latestTelemetry.longitude, -180, 180)) {
            this.counters.telemetry_post_skipped++;
            this.logger.warn("telemetry.post.gated", "Telemetry tick skipped because coordinates are invalid", {
                reason: reason,
                latitude: this.latestTelemetry.latitude,
                longitude: this.latestTelemetry.longitude
            });
            return;
        }

        await this.sendStatusHeartbeat("pre-telemetry:" + reason);

        var payload = this.shared.buildTelemetryPayload(this.token, this.latestTelemetry);
        this.counters.telemetry_posts++;

        this.logger.debug("telemetry.post.prepare", "Preparing telemetry payload", {
            reason: reason,
            payloadPreview: payload
        });

        var response;
        try {
            response = await this.http.request({
                method: "POST",
                url: this.config.telemetryUrl,
                body: JSON.stringify(payload),
                headers: this.buildHeaders(true),
                timeoutMs: this.config.requestTimeoutMs
            });
        } catch (error) {
            this.counters.telemetry_post_failures++;
            this.setNetworkStatus("error", "telemetry error", "network failure");
            this.logger.error("telemetry.post.failed", "Telemetry request failed", {
                reason: reason,
                error: String(error && error.message ? error.message : error)
            });
            return;
        }

        if (!response.ok) {
            this.counters.telemetry_post_failures++;
            this.setNetworkStatus("warn", "telemetry " + response.status, response.statusText || "");
            this.logger.warn("telemetry.post.non_success", "Telemetry request returned non-success status", {
                reason: reason,
                status: response.status,
                responseTextPreview: response.text ? response.text.slice(0, 300) : ""
            });
            return;
        }

        this.setNetworkStatus("info", "telemetry ok", "status=" + response.status + " in " + response.durationMs + "ms");
        this.logger.info("telemetry.post.success", "Telemetry payload sent", {
            reason: reason,
            status: response.status,
            durationMs: response.durationMs
        });

        if (!response.json || typeof response.json !== "object") {
            return;
        }

        await this.applyCommandsFromResponse(response.json, reason);
    };

    OpenSquawkBridgeRuntime.prototype.applyCommandsFromResponse = async function (responseJson, reason) {
        var commands = this.shared.parseCommandPayload(responseJson);
        this.counters.command_parsed += commands.length;

        if (commands.length === 0) {
            this.lastCommandAppliedCount = 0;
            this.lastCommandFailedCount = 0;
            this.logger.debug("command.parse.none", "No simulator commands found in telemetry response", {
                reason: reason
            });
            return;
        }

        this.logger.info("command.parse.found", "Simulator command(s) parsed from telemetry response", {
            reason: reason,
            count: commands.length,
            commands: commands
        });

        if (!this.config.simCommandEnabled) {
            this.logger.warn("command.apply.disabled", "Command application disabled by config", {
                reason: reason,
                count: commands.length
            });
            return;
        }

        var results = await this.sim.applyCommands(commands);
        var applied = 0;
        var failed = 0;

        for (var i = 0; i < results.length; i++) {
            var item = results[i];
            if (item.ok) {
                applied++;
                this.counters.command_applied++;
                this.logger.info("command.apply.success", "Simulator command applied", {
                    reason: reason,
                    command: item.command,
                    detail: item.detail
                });
            } else {
                failed++;
                this.counters.command_failed++;
                this.logger.error("command.apply.failed", "Simulator command failed", {
                    reason: reason,
                    command: item.command,
                    error: item.error
                });
            }
        }

        this.lastCommandAppliedCount = applied;
        this.lastCommandFailedCount = failed;
        this.refreshUi();
    };

    OpenSquawkBridgeRuntime.prototype.buildHeaders = function (isJsonBody) {
        var headers = {};

        if (isJsonBody) {
            headers["Content-Type"] = "application/json";
        }

        if (this.token) {
            headers["x-bridge-token"] = this.token;
        }

        if (this.config.authToken) {
            headers.Authorization = "Bearer " + this.config.authToken;
        }

        return headers;
    };

    OpenSquawkBridgeRuntime.prototype.pushDebugSnapshot = function (reason) {
        if (!this.config.remoteDebugEnabled || !this.config.remoteDebugUrl) {
            this.logger.debug("debug.push.skip", "Remote debug push skipped because endpoint is disabled", {
                reason: reason,
                remoteDebugEnabled: this.config.remoteDebugEnabled,
                remoteDebugUrl: this.config.remoteDebugUrl
            });
            return;
        }

        if (this.flags.debugPushInFlight) {
            this.logger.debug("debug.push.skip", "Remote debug push skipped because another push is in flight", {
                reason: reason
            });
            return;
        }

        this.flags.debugPushInFlight = true;

        this.sendDebugSnapshot(reason)
            .catch(function () {
                // Error already logged
            })
            .finally(function () {
                this.flags.debugPushInFlight = false;
                this.refreshUi();
            }.bind(this));
    };

    OpenSquawkBridgeRuntime.prototype.sendDebugSnapshot = async function (reason) {
        var logs = this.logger.snapshot();
        var payload = {
            source: "OpenSquawkBridgeInstrument",
            reason: reason,
            ts: toIsoDate(Date.now()),
            uptimeMs: Date.now() - this.startMs,
            state: {
                userConnected: this.userConnected,
                userName: this.userName,
                simConnected: this.simConnected,
                flightLoaded: this.flightLoaded,
                telemetryAgeMs: this.latestTelemetry ? Date.now() - this.latestTelemetryTsMs : null
            },
            counters: this.counters,
            recentLogs: logs.slice(-200)
        };

        this.logger.info("debug.push.start", "Pushing debug snapshot", {
            reason: reason,
            endpoint: this.config.remoteDebugUrl,
            logCount: payload.recentLogs.length
        });

        var response;
        try {
            response = await this.http.request({
                method: "POST",
                url: this.config.remoteDebugUrl,
                body: JSON.stringify(payload),
                headers: this.buildHeaders(true),
                timeoutMs: this.config.requestTimeoutMs
            });
        } catch (error) {
            this.counters.debug_push_failures++;
            this.logger.error("debug.push.failed", "Debug snapshot push failed", {
                reason: reason,
                error: String(error && error.message ? error.message : error)
            });
            return;
        }

        if (!response.ok) {
            this.counters.debug_push_failures++;
            this.logger.warn("debug.push.non_success", "Debug snapshot endpoint returned non-success status", {
                reason: reason,
                status: response.status,
                responseTextPreview: response.text ? response.text.slice(0, 200) : ""
            });
            return;
        }

        this.counters.debug_push_success++;
        this.logger.info("debug.push.success", "Debug snapshot pushed", {
            reason: reason,
            status: response.status,
            durationMs: response.durationMs
        });
    };

    OpenSquawkBridgeRuntime.prototype.exportLogs = async function (reason) {
        var payload = {
            exportedAt: toIsoDate(Date.now()),
            reason: reason,
            tokenPrefix: this.token ? this.token.slice(0, this.shared.TOKEN_LENGTH) : null,
            state: {
                userConnected: this.userConnected,
                userName: this.userName,
                simConnected: this.simConnected,
                flightLoaded: this.flightLoaded
            },
            counters: this.counters,
            logs: this.logger.snapshot()
        };

        var serialized = safeStringify(payload);

        if (typeof navigator !== "undefined" && navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
            try {
                await navigator.clipboard.writeText(serialized);
                this.logger.info("logs.export.clipboard", "Exported logs JSON to clipboard", {
                    reason: reason,
                    bytes: serialized.length
                });
                return;
            } catch (error) {
                this.logger.warn("logs.export.clipboard_failed", "Clipboard export failed", {
                    reason: reason,
                    error: String(error && error.message ? error.message : error)
                });
            }
        }

        this.logger.info("logs.export.console", "Clipboard unavailable, logging exported JSON to console", {
            reason: reason,
            bytes: serialized.length
        });

        if (typeof console !== "undefined") {
            console.log("OpenSquawkBridge exported logs:\n" + serialized);
        }
    };

    OpenSquawkBridgeRuntime.prototype.clearLogs = function (reason) {
        this.logger.info("logs.clear", "Clearing in-memory log buffer", {
            reason: reason
        });
        this.logger.clear();
        this.ui.clearLogs();
        this.refreshUi();
    };

    if (typeof window !== "undefined") {
        window.OpenSquawkBridgeRuntime = OpenSquawkBridgeRuntime;
    } else if (typeof globalThis !== "undefined") {
        globalThis.OpenSquawkBridgeRuntime = OpenSquawkBridgeRuntime;
    }

    if (typeof BaseInstrument !== "undefined" && typeof registerInstrument === "function") {
        function OpenSquawkBridgeInstrument() {
            BaseInstrument.call(this);
            this.runtime = null;
        }

        OpenSquawkBridgeInstrument.prototype = Object.create(BaseInstrument.prototype);
        OpenSquawkBridgeInstrument.prototype.constructor = OpenSquawkBridgeInstrument;

        Object.defineProperty(OpenSquawkBridgeInstrument.prototype, "templateID", {
            get: function () {
                return "OpenSquawkBridgeTemplate";
            }
        });

        OpenSquawkBridgeInstrument.prototype.connectedCallback = function () {
            BaseInstrument.prototype.connectedCallback.call(this);

            if (!this.runtime) {
                this.runtime = new OpenSquawkBridgeRuntime(this);
            }
        };

        OpenSquawkBridgeInstrument.prototype.disconnectedCallback = function () {
            if (this.runtime) {
                this.runtime.dispose();
                this.runtime = null;
            }

            if (typeof BaseInstrument.prototype.disconnectedCallback === "function") {
                BaseInstrument.prototype.disconnectedCallback.call(this);
            }
        };

        OpenSquawkBridgeInstrument.prototype.Update = function () {
            BaseInstrument.prototype.Update.call(this);

            if (this.runtime) {
                this.runtime.update(Date.now());
            }
        };

        registerInstrument("opensquawk-bridge", OpenSquawkBridgeInstrument);
    }
})();
