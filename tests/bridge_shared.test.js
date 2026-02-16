const test = require("node:test");
const assert = require("node:assert/strict");

const shared = require("../html_ui/Pages/VCockpit/Instruments/OpenSquawkBridge/bridge_shared.js");

test("parseCommandPayload reads commands from root and containers", () => {
    const payload = {
        transponder: 7000,
        controls: {
            gear_handle: true,
            flaps_index: 2
        },
        commands: {
            parking_brake: "off"
        }
    };

    const commands = shared.parseCommandPayload(payload);
    const map = Object.fromEntries(commands.map((item) => [item.parameter, item.value]));

    assert.equal(map.transponderCode, 7000);
    assert.equal(map.gearHandle, 1);
    assert.equal(map.flapsHandleIndex, 2);
    assert.equal(map.parkingBrake, 0);
});

test("parseCommandPayload rejects invalid values", () => {
    const payload = {
        commands: {
            flaps_index: -1,
            autopilot_master: "maybe",
            adf_standby_freq_hz: -100
        }
    };

    const commands = shared.parseCommandPayload(payload);
    assert.equal(commands.length, 0);
});

test("value coercion helpers parse booleans and numbers", () => {
    assert.equal(shared.toBooleanFromUnknown("on"), true);
    assert.equal(shared.toBooleanFromUnknown("off"), false);
    assert.equal(shared.toBooleanFromUnknown(1), true);
    assert.equal(shared.toBooleanFromUnknown(0), false);
    assert.equal(shared.toBooleanFromUnknown(2), null);

    assert.equal(shared.toIntegerFromUnknown("42"), 42);
    assert.equal(shared.toIntegerFromUnknown(12.0), 12);
    assert.equal(shared.toIntegerFromUnknown(12.5), null);

    assert.equal(shared.toNumberFromUnknown("12.25"), 12.25);
    assert.equal(shared.toNumberFromUnknown(true), 1);
});

test("buildTelemetryPayload applies required conversions", () => {
    const telemetry = {
        latitude: 48.12345678,
        longitude: 11.65432198,
        altitude: 2400.49,
        indicatedAltitude: 2380.11,
        airspeedIndicated: 121.44,
        airspeedTrue: 132.91,
        groundVelocity: 55,
        turbineN1: 66.66,
        onGround: false,
        engineCombustion: true,
        transponderCode: 7500,
        adfActiveFrequency: 1123,
        adfStandbyFrequency: 950,
        verticalSpeed: 501.2,
        planePitchDegrees: 2.26,
        turbineN1Engine2: 64.44,
        gearHandlePosition: true,
        flapsHandleIndex: 1,
        brakeParkingPosition: false,
        autopilotMaster: true
    };

    const payload = shared.buildTelemetryPayload("tok", telemetry);

    assert.equal(payload.token, "tok");
    assert.equal(payload.latitude, 48.123457);
    assert.equal(payload.longitude, 11.654322);
    assert.equal(payload.altitude_ft_true, 2400);
    assert.equal(payload.altitude_ft_indicated, 2380);
    assert.equal(payload.groundspeed_kt, 106.9);
    assert.equal(payload.eng_on, true);
    assert.equal(payload.n1_pct, 66.7);
    assert.equal(payload.pitch_deg, 2.3);
    assert.equal(payload.n1_pct_2, 64.4);
});

test("resolveConfig fills endpoint defaults from base URL", () => {
    const resolved = shared.resolveConfig({
        baseUrl: "https://example.test/",
        activeIntervalSec: "45",
        remoteDebugEnabled: "yes"
    });

    assert.equal(resolved.baseUrl, "https://example.test");
    assert.equal(resolved.meUrl, "https://example.test/api/bridge/me");
    assert.equal(resolved.statusUrl, "https://example.test/api/bridge/status");
    assert.equal(resolved.telemetryUrl, "https://example.test/api/bridge/data");
    assert.equal(resolved.activeIntervalSec, 45);
    assert.equal(resolved.remoteDebugEnabled, true);
});

test("generateToken returns a 6-char token from non-ambiguous alphabet", () => {
    const token = shared.generateToken();
    assert.equal(token.length, shared.TOKEN_LENGTH);
    assert.ok(/^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{6}$/.test(token));
    assert.equal(shared.isTokenValid(token), true);
    assert.equal(shared.isTokenValid("0OIL11"), false);
    assert.equal(shared.isTokenValid("ABCD23"), true);
    assert.equal(shared.isTokenValid("abc2de"), true);
    assert.equal(shared.normalizeToken(" ab2cde "), "AB2CDE");
});
