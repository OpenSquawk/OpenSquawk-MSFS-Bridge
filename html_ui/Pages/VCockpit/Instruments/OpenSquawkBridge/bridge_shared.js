(function (root, factory) {
    var api = factory();
    if (typeof module === "object" && module.exports) {
        module.exports = api;
    }
    root.OpenSquawkBridgeShared = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
    "use strict";

    var COMMAND_CONTAINER_KEYS = Object.freeze([
        "keys",
        "commands",
        "sim",
        "simvars",
        "controls"
    ]);

    var DEFAULT_CONFIG = Object.freeze({
        baseUrl: "https://opensquawk.de",
        meUrl: "",
        statusUrl: "",
        telemetryUrl: "",
        authToken: "",
        activeIntervalSec: 30,
        idleIntervalSec: 120,
        loginPollSec: 10,
        telemetryReadHz: 1,
        staleTelemetrySec: 10,
        requestTimeoutMs: 10000,
        debugMaxEntries: 800,
        remoteDebugUrl: "",
        remoteDebugEnabled: false,
        autoOpenLogin: false,
        simCommandEnabled: true,
        debugEchoToConsole: true,
        debugSampleEveryNReads: 1
    });

    var COMMAND_NAME_MAP = Object.freeze({
        transponder_code: "transponderCode",
        transponder: "transponderCode",
        xpdr: "transponderCode",
        squawk: "transponderCode",
        adf_active_freq: "adfActiveFrequency",
        adf_active_frequency: "adfActiveFrequency",
        adf_standby_freq_hz: "adfStandbyFrequencyHz",
        adf_standby_frequency_hz: "adfStandbyFrequencyHz",
        adf_standby_freq: "adfStandbyFrequencyHz",
        gear_handle: "gearHandle",
        flaps_index: "flapsHandleIndex",
        flaps_handle_index: "flapsHandleIndex",
        parking_brake: "parkingBrake",
        autopilot_master: "autopilotMaster"
    });

    function normalizePropertyName(name) {
        return String(name || "")
            .trim()
            .replace(/-/g, "_")
            .replace(/\s+/g, "_")
            .toLowerCase();
    }

    function mapCommandNameToParameter(name) {
        return COMMAND_NAME_MAP[normalizePropertyName(name)] || null;
    }

    function isFiniteNumber(value) {
        return typeof value === "number" && Number.isFinite(value);
    }

    function isWholeNumber(value) {
        return isFiniteNumber(value) && Math.abs(value - Math.round(value)) < 1e-7;
    }

    function toNumberFromUnknown(raw) {
        if (typeof raw === "number") {
            return Number.isFinite(raw) ? raw : null;
        }

        if (typeof raw === "boolean") {
            return raw ? 1 : 0;
        }

        if (typeof raw !== "string") {
            return null;
        }

        var trimmed = raw.trim();
        if (!trimmed) {
            return null;
        }

        var parsed = Number(trimmed);
        return Number.isFinite(parsed) ? parsed : null;
    }

    function toIntegerFromUnknown(raw) {
        var value = toNumberFromUnknown(raw);
        if (value === null || !isWholeNumber(value)) {
            return null;
        }

        if (value < -2147483648 || value > 2147483647) {
            return null;
        }

        return Math.round(value);
    }

    function toBooleanFromUnknown(raw) {
        if (typeof raw === "boolean") {
            return raw;
        }

        if (typeof raw === "number") {
            if (!Number.isFinite(raw)) {
                return null;
            }

            if (raw === 0) {
                return false;
            }

            if (raw === 1) {
                return true;
            }

            return null;
        }

        if (typeof raw !== "string") {
            return null;
        }

        var normalized = raw.trim().toLowerCase();
        if (!normalized) {
            return null;
        }

        if (normalized === "1" || normalized === "true" || normalized === "yes" || normalized === "on") {
            return true;
        }

        if (normalized === "0" || normalized === "false" || normalized === "no" || normalized === "off") {
            return false;
        }

        return null;
    }

    function isObject(value) {
        return !!value && typeof value === "object" && !Array.isArray(value);
    }

    function parseCommandValue(parameter, rawValue) {
        if (parameter === "gearHandle" || parameter === "parkingBrake" || parameter === "autopilotMaster") {
            var boolValue = toBooleanFromUnknown(rawValue);
            if (boolValue === null) {
                return null;
            }

            return boolValue ? 1 : 0;
        }

        if (parameter === "transponderCode" || parameter === "adfActiveFrequency" || parameter === "flapsHandleIndex") {
            var intValue = toIntegerFromUnknown(rawValue);
            if (intValue === null) {
                return null;
            }

            if (parameter === "flapsHandleIndex" && intValue < 0) {
                return null;
            }

            return intValue;
        }

        if (parameter === "adfStandbyFrequencyHz") {
            var numberValue = toNumberFromUnknown(rawValue);
            if (numberValue === null || numberValue < 0) {
                return null;
            }

            return numberValue;
        }

        return null;
    }

    function collectCommandsFromObject(source, collector) {
        if (!isObject(source)) {
            return;
        }

        var keys = Object.keys(source);
        for (var i = 0; i < keys.length; i++) {
            var propertyName = keys[i];
            var parameter = mapCommandNameToParameter(propertyName);
            if (!parameter) {
                continue;
            }

            var parsedValue = parseCommandValue(parameter, source[propertyName]);
            if (parsedValue === null) {
                continue;
            }

            collector[parameter] = parsedValue;
        }
    }

    function parseCommandPayload(payload) {
        if (!isObject(payload)) {
            return [];
        }

        var collected = {};

        collectCommandsFromObject(payload, collected);

        for (var i = 0; i < COMMAND_CONTAINER_KEYS.length; i++) {
            var key = COMMAND_CONTAINER_KEYS[i];
            if (!Object.prototype.hasOwnProperty.call(payload, key)) {
                continue;
            }

            collectCommandsFromObject(payload[key], collected);
        }

        var parameters = Object.keys(collected);
        var commands = [];

        for (var j = 0; j < parameters.length; j++) {
            var parameter = parameters[j];
            commands.push({
                parameter: parameter,
                value: collected[parameter]
            });
        }

        return commands;
    }

    function round(value, precision) {
        var factor = Math.pow(10, precision);
        return Math.round(value * factor) / factor;
    }

    function isValidCoordinate(value, min, max) {
        return isFiniteNumber(value) && value >= min && value <= max;
    }

    function buildTelemetryPayload(token, telemetry) {
        if (!telemetry || typeof telemetry !== "object") {
            throw new Error("Telemetry object is required.");
        }

        return {
            token: token,
            status: "active",
            ts: Math.floor(Date.now() / 1000),
            latitude: round(telemetry.latitude, 6),
            longitude: round(telemetry.longitude, 6),
            altitude_ft_true: Math.round(telemetry.altitude),
            altitude_ft_indicated: Math.round(telemetry.indicatedAltitude),
            ias_kt: round(telemetry.airspeedIndicated, 1),
            tas_kt: round(telemetry.airspeedTrue, 1),
            groundspeed_kt: round(telemetry.groundVelocity * 1.943844, 1),
            on_ground: !!telemetry.onGround,
            eng_on: !!telemetry.engineCombustion || telemetry.turbineN1 > 5,
            n1_pct: round(telemetry.turbineN1, 1),
            transponder_code: telemetry.transponderCode,
            adf_active_freq: telemetry.adfActiveFrequency,
            adf_standby_freq_hz: Math.round(telemetry.adfStandbyFrequency),
            vertical_speed_fpm: Math.round(telemetry.verticalSpeed),
            pitch_deg: round(telemetry.planePitchDegrees, 1),
            n1_pct_2: round(telemetry.turbineN1Engine2, 1),
            gear_handle: !!telemetry.gearHandlePosition,
            flaps_index: telemetry.flapsHandleIndex,
            parking_brake: !!telemetry.brakeParkingPosition,
            autopilot_master: !!telemetry.autopilotMaster
        };
    }

    function sanitizeUrl(value) {
        if (typeof value !== "string") {
            return "";
        }

        return value.trim().replace(/\/$/, "");
    }

    function positiveInt(value, fallback) {
        if (typeof value === "number" && Number.isFinite(value) && value > 0) {
            return Math.round(value);
        }

        if (typeof value === "string" && value.trim()) {
            var parsed = Number(value);
            if (Number.isFinite(parsed) && parsed > 0) {
                return Math.round(parsed);
            }
        }

        return fallback;
    }

    function toBoolean(value, fallback) {
        var parsed = toBooleanFromUnknown(value);
        return parsed === null ? fallback : parsed;
    }

    function resolveConfig(raw) {
        var input = isObject(raw) ? raw : {};
        var baseUrl = sanitizeUrl(input.baseUrl || DEFAULT_CONFIG.baseUrl) || DEFAULT_CONFIG.baseUrl;

        var meUrl = sanitizeUrl(input.meUrl);
        var statusUrl = sanitizeUrl(input.statusUrl);
        var telemetryUrl = sanitizeUrl(input.telemetryUrl);

        return {
            baseUrl: baseUrl,
            meUrl: meUrl || baseUrl + "/api/bridge/me",
            statusUrl: statusUrl || baseUrl + "/api/bridge/status",
            telemetryUrl: telemetryUrl || baseUrl + "/api/bridge/data",
            authToken: typeof input.authToken === "string" ? input.authToken.trim() : "",
            activeIntervalSec: positiveInt(input.activeIntervalSec, DEFAULT_CONFIG.activeIntervalSec),
            idleIntervalSec: positiveInt(input.idleIntervalSec, DEFAULT_CONFIG.idleIntervalSec),
            loginPollSec: positiveInt(input.loginPollSec, DEFAULT_CONFIG.loginPollSec),
            telemetryReadHz: positiveInt(input.telemetryReadHz, DEFAULT_CONFIG.telemetryReadHz),
            staleTelemetrySec: positiveInt(input.staleTelemetrySec, DEFAULT_CONFIG.staleTelemetrySec),
            requestTimeoutMs: positiveInt(input.requestTimeoutMs, DEFAULT_CONFIG.requestTimeoutMs),
            debugMaxEntries: positiveInt(input.debugMaxEntries, DEFAULT_CONFIG.debugMaxEntries),
            remoteDebugUrl: sanitizeUrl(input.remoteDebugUrl),
            remoteDebugEnabled: toBoolean(input.remoteDebugEnabled, DEFAULT_CONFIG.remoteDebugEnabled),
            autoOpenLogin: toBoolean(input.autoOpenLogin, DEFAULT_CONFIG.autoOpenLogin),
            simCommandEnabled: toBoolean(input.simCommandEnabled, DEFAULT_CONFIG.simCommandEnabled),
            debugEchoToConsole: toBoolean(input.debugEchoToConsole, DEFAULT_CONFIG.debugEchoToConsole),
            debugSampleEveryNReads: positiveInt(input.debugSampleEveryNReads, DEFAULT_CONFIG.debugSampleEveryNReads)
        };
    }

    function extractUserName(payload) {
        if (!isObject(payload)) {
            return null;
        }

        var direct = payload.username || payload.userName || payload.name || payload.displayName || payload.email;
        if (typeof direct === "string" && direct.trim()) {
            return direct.trim();
        }

        if (isObject(payload.user)) {
            var nested = payload.user.username || payload.user.userName || payload.user.name || payload.user.displayName || payload.user.email;
            if (typeof nested === "string" && nested.trim()) {
                return nested.trim();
            }
        }

        return null;
    }

    function generateToken() {
        var bytes = new Uint8Array(32);

        if (typeof crypto !== "undefined" && crypto && typeof crypto.getRandomValues === "function") {
            crypto.getRandomValues(bytes);
        } else {
            for (var i = 0; i < bytes.length; i++) {
                bytes[i] = Math.floor(Math.random() * 256);
            }
        }

        var binary = "";
        for (var j = 0; j < bytes.length; j++) {
            binary += String.fromCharCode(bytes[j]);
        }

        var base64;
        if (typeof btoa === "function") {
            base64 = btoa(binary);
        } else if (typeof Buffer !== "undefined") {
            base64 = Buffer.from(binary, "binary").toString("base64");
        } else {
            throw new Error("No base64 encoder available to generate token.");
        }

        return base64.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
    }

    return {
        COMMAND_CONTAINER_KEYS: COMMAND_CONTAINER_KEYS,
        DEFAULT_CONFIG: DEFAULT_CONFIG,
        mapCommandNameToParameter: mapCommandNameToParameter,
        parseCommandPayload: parseCommandPayload,
        buildTelemetryPayload: buildTelemetryPayload,
        extractUserName: extractUserName,
        resolveConfig: resolveConfig,
        isValidCoordinate: isValidCoordinate,
        generateToken: generateToken,
        toBooleanFromUnknown: toBooleanFromUnknown,
        toIntegerFromUnknown: toIntegerFromUnknown,
        toNumberFromUnknown: toNumberFromUnknown,
        parseCommandValue: parseCommandValue
    };
});
