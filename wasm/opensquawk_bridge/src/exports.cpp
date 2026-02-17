#include "Bridge.h"

#if defined(__EMSCRIPTEN__)
#include <emscripten/emscripten.h>
#define OSB_KEEPALIVE EMSCRIPTEN_KEEPALIVE
#else
#define OSB_KEEPALIVE
#endif

extern "C" {

OSB_KEEPALIVE int osb_init() {
  return osb::Bridge::Get().EnsureConnected() ? 1 : 0;
}

OSB_KEEPALIVE void osb_tick() {
  osb::Bridge::Get().Tick();
}

OSB_KEEPALIVE int osb_is_connected() {
  return osb::Bridge::Get().IsConnected() ? 1 : 0;
}

OSB_KEEPALIVE const char *osb_get_snapshot_json() {
  return osb::Bridge::Get().GetSnapshotJson();
}

OSB_KEEPALIVE unsigned long long osb_get_snapshot_age_ms() {
  return static_cast<unsigned long long>(osb::Bridge::Get().SnapshotAgeMs());
}

OSB_KEEPALIVE int osb_set_transponder_code(int code) {
  return osb::Bridge::Get().SetTransponderCode(code) ? 1 : 0;
}

OSB_KEEPALIVE int osb_set_adf_active_khz(double value_khz) {
  return osb::Bridge::Get().SetAdfActiveKHz(value_khz) ? 1 : 0;
}

OSB_KEEPALIVE int osb_set_adf_standby_khz(double value_khz) {
  return osb::Bridge::Get().SetAdfStandbyKHz(value_khz) ? 1 : 0;
}

OSB_KEEPALIVE int osb_set_gear_handle(int on) {
  return osb::Bridge::Get().SetGearHandle(on != 0) ? 1 : 0;
}

OSB_KEEPALIVE int osb_set_flaps_index(int index) {
  return osb::Bridge::Get().SetFlapsIndex(index) ? 1 : 0;
}

OSB_KEEPALIVE int osb_set_parking_brake(int on) {
  return osb::Bridge::Get().SetParkingBrake(on != 0) ? 1 : 0;
}

OSB_KEEPALIVE int osb_set_autopilot_master(int on) {
  return osb::Bridge::Get().SetAutopilotMaster(on != 0) ? 1 : 0;
}

OSB_KEEPALIVE void module_init() {
  osb::Bridge::Get().EnsureConnected();
}

OSB_KEEPALIVE void module_deinit() {
  osb::Bridge::Get().Close();
}

}  // extern "C"
