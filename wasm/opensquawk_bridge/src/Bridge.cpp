#include "Bridge.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <chrono>

namespace osb {

namespace {

constexpr DWORD kUserObjectId = SIMCONNECT_OBJECT_ID_USER;
constexpr DWORD kRequestTelemetry = 1;
constexpr DWORD kDefTelemetry = 1;

constexpr DWORD kDefTransponder = 10;
constexpr DWORD kDefAdfActive = 11;
constexpr DWORD kDefAdfStandby = 12;
constexpr DWORD kDefGearHandle = 13;
constexpr DWORD kDefFlapsIndex = 14;
constexpr DWORD kDefParkingBrake = 15;
constexpr DWORD kDefAutopilot = 16;

uint64_t NowMs() {
  using namespace std::chrono;
  return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

struct TelemetryData {
  double latitude_deg;
  double longitude_deg;
  double altitude_ft_true;
  double altitude_ft_indicated;
  double ias_kt;
  double tas_kt;
  double ground_velocity_mps;
  double turbine_n1_pct;
  double on_ground;
  double engine_combustion;
  double transponder_code;
  double adf_active_freq_khz;
  double adf_standby_freq_khz;
  double vertical_speed_fpm;
  double pitch_deg;
  double turbine_n1_pct_2;
  double gear_handle;
  double flaps_index;
  double parking_brake;
  double autopilot_master;
};

}  // namespace

Bridge &Bridge::Get() {
  static Bridge instance;
  return instance;
}

Bridge::Bridge() = default;

bool Bridge::EnsureConnected() {
  if (connected_) {
    return true;
  }

  const uint64_t now = NowMs();
  if (now - last_connect_attempt_ms_ < 2000) {
    return false;
  }
  last_connect_attempt_ms_ = now;

  if (SUCCEEDED(SimConnect_Open(&simconnect_, "OpenSquawkBridge", nullptr, 0, 0, 0))) {
    connected_ = true;

    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "PLANE LATITUDE", "degrees");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "PLANE LONGITUDE", "degrees");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "PLANE ALTITUDE", "feet");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "INDICATED ALTITUDE", "feet");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "AIRSPEED INDICATED", "knots");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "AIRSPEED TRUE", "knots");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "GROUND VELOCITY", "meters per second");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "TURB ENG N1:1", "percent");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "SIM ON GROUND", "bool");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "ENG COMBUSTION:1", "bool");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "TRANSPONDER CODE:1", "bco16");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "ADF ACTIVE FREQUENCY:1", "kilohertz");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "ADF STANDBY FREQUENCY:1", "kilohertz");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "VERTICAL SPEED", "feet per minute");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "PLANE PITCH DEGREES", "degrees");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "TURB ENG N1:2", "percent");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "GEAR HANDLE POSITION", "bool");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "FLAPS HANDLE INDEX", "number");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "BRAKE PARKING POSITION", "bool");
    SimConnect_AddToDataDefinition(simconnect_, kDefTelemetry, "AUTOPILOT MASTER", "bool");

    SimConnect_AddToDataDefinition(simconnect_, kDefTransponder, "TRANSPONDER CODE:1", "bco16");
    SimConnect_AddToDataDefinition(simconnect_, kDefAdfActive, "ADF ACTIVE FREQUENCY:1", "kilohertz");
    SimConnect_AddToDataDefinition(simconnect_, kDefAdfStandby, "ADF STANDBY FREQUENCY:1", "kilohertz");
    SimConnect_AddToDataDefinition(simconnect_, kDefGearHandle, "GEAR HANDLE POSITION", "bool");
    SimConnect_AddToDataDefinition(simconnect_, kDefFlapsIndex, "FLAPS HANDLE INDEX", "number");
    SimConnect_AddToDataDefinition(simconnect_, kDefParkingBrake, "BRAKE PARKING POSITION", "bool");
    SimConnect_AddToDataDefinition(simconnect_, kDefAutopilot, "AUTOPILOT MASTER", "bool");

    RequestTelemetry();
    return true;
  }

  return false;
}

void Bridge::RequestTelemetry() {
  SimConnect_RequestDataOnSimObject(simconnect_, kRequestTelemetry, kDefTelemetry, kUserObjectId,
                                    SIMCONNECT_PERIOD_SECOND, SIMCONNECT_DATA_REQUEST_FLAG_CHANGED);
}

void Bridge::Tick() {
  EnsureConnected();
  if (connected_) {
    SimConnect_CallDispatch(simconnect_, &Bridge::DispatchProc, this);
  }
}

void Bridge::Close() {
  if (simconnect_) {
    SimConnect_Close(simconnect_);
  }
  simconnect_ = nullptr;
  connected_ = false;
  snapshot_valid_ = false;
}

uint64_t Bridge::SnapshotAgeMs() const {
  if (!snapshot_valid_) {
    return 0;
  }
  const uint64_t now = NowMs();
  return now >= snapshot_ts_ms_ ? now - snapshot_ts_ms_ : 0;
}

const char *Bridge::GetSnapshotJson() {
  if (!snapshot_valid_) {
    snapshot_json_.assign("{}");
    return snapshot_json_.c_str();
  }

  return snapshot_json_.c_str();
}

bool Bridge::SetTransponderCode(int code) {
  if (!connected_) {
    return false;
  }
  const uint32_t value = static_cast<uint32_t>(code);
  return SUCCEEDED(SimConnect_SetDataOnSimObject(simconnect_, kDefTransponder, kUserObjectId, 0, 0,
                                                 sizeof(value), &value));
}

bool Bridge::SetAdfActiveKHz(double value_khz) {
  if (!connected_) {
    return false;
  }
  const double value = value_khz;
  return SUCCEEDED(SimConnect_SetDataOnSimObject(simconnect_, kDefAdfActive, kUserObjectId, 0, 0,
                                                 sizeof(value), &value));
}

bool Bridge::SetAdfStandbyKHz(double value_khz) {
  if (!connected_) {
    return false;
  }
  const double value = value_khz;
  return SUCCEEDED(SimConnect_SetDataOnSimObject(simconnect_, kDefAdfStandby, kUserObjectId, 0, 0,
                                                 sizeof(value), &value));
}

bool Bridge::SetGearHandle(bool on) {
  if (!connected_) {
    return false;
  }
  const double value = on ? 1.0 : 0.0;
  return SUCCEEDED(SimConnect_SetDataOnSimObject(simconnect_, kDefGearHandle, kUserObjectId, 0, 0,
                                                 sizeof(value), &value));
}

bool Bridge::SetFlapsIndex(int index) {
  if (!connected_) {
    return false;
  }
  const double value = static_cast<double>(index);
  return SUCCEEDED(SimConnect_SetDataOnSimObject(simconnect_, kDefFlapsIndex, kUserObjectId, 0, 0,
                                                 sizeof(value), &value));
}

bool Bridge::SetParkingBrake(bool on) {
  if (!connected_) {
    return false;
  }
  const double value = on ? 1.0 : 0.0;
  return SUCCEEDED(SimConnect_SetDataOnSimObject(simconnect_, kDefParkingBrake, kUserObjectId, 0, 0,
                                                 sizeof(value), &value));
}

bool Bridge::SetAutopilotMaster(bool on) {
  if (!connected_) {
    return false;
  }
  const double value = on ? 1.0 : 0.0;
  return SUCCEEDED(SimConnect_SetDataOnSimObject(simconnect_, kDefAutopilot, kUserObjectId, 0, 0,
                                                 sizeof(value), &value));
}

void Bridge::DispatchProc(SIMCONNECT_RECV *data, DWORD cbData, void *context) {
  static_cast<Bridge *>(context)->HandleDispatch(data, cbData);
}

void Bridge::HandleDispatch(SIMCONNECT_RECV *data, DWORD cbData) {
  switch (data->dwID) {
    case SIMCONNECT_RECV_ID_OPEN:
      break;
    case SIMCONNECT_RECV_ID_QUIT:
      Close();
      break;
    case SIMCONNECT_RECV_ID_SIMOBJECT_DATA: {
      auto *recv = reinterpret_cast<SIMCONNECT_RECV_SIMOBJECT_DATA *>(data);
      if (recv->dwRequestID != kRequestTelemetry) {
        break;
      }
      const auto *telemetry = reinterpret_cast<const TelemetryData *>(&recv->dwData);
      snapshot_.latitude_deg = telemetry->latitude_deg;
      snapshot_.longitude_deg = telemetry->longitude_deg;
      snapshot_.altitude_ft_true = telemetry->altitude_ft_true;
      snapshot_.altitude_ft_indicated = telemetry->altitude_ft_indicated;
      snapshot_.ias_kt = telemetry->ias_kt;
      snapshot_.tas_kt = telemetry->tas_kt;
      snapshot_.ground_velocity_mps = telemetry->ground_velocity_mps;
      snapshot_.turbine_n1_pct = telemetry->turbine_n1_pct;
      snapshot_.on_ground = telemetry->on_ground;
      snapshot_.engine_combustion = telemetry->engine_combustion;
      snapshot_.transponder_code = telemetry->transponder_code;
      snapshot_.adf_active_freq_khz = telemetry->adf_active_freq_khz;
      snapshot_.adf_standby_freq_khz = telemetry->adf_standby_freq_khz;
      snapshot_.vertical_speed_fpm = telemetry->vertical_speed_fpm;
      snapshot_.pitch_deg = telemetry->pitch_deg;
      snapshot_.turbine_n1_pct_2 = telemetry->turbine_n1_pct_2;
      snapshot_.gear_handle = telemetry->gear_handle;
      snapshot_.flaps_index = telemetry->flaps_index;
      snapshot_.parking_brake = telemetry->parking_brake;
      snapshot_.autopilot_master = telemetry->autopilot_master;

      snapshot_valid_ = true;
      snapshot_ts_ms_ = NowMs();
      BuildSnapshotJson();
      break;
    }
    default:
      break;
  }
}

void Bridge::BuildSnapshotJson() {
  char buffer[1024];
  std::snprintf(
      buffer,
      sizeof(buffer),
      "{\"latitude\":%.8f,\"longitude\":%.8f,\"altitude_ft_true\":%.2f,\"altitude_ft_indicated\":%.2f,"
      "\"ias_kt\":%.2f,\"tas_kt\":%.2f,\"ground_velocity_mps\":%.3f,\"turbine_n1_pct\":%.2f,"
      "\"on_ground\":%.0f,\"engine_combustion\":%.0f,\"transponder_code\":%.0f,"
      "\"adf_active_freq_khz\":%.3f,\"adf_standby_freq_khz\":%.3f,\"vertical_speed_fpm\":%.1f,"
      "\"pitch_deg\":%.2f,\"turbine_n1_pct_2\":%.2f,\"gear_handle\":%.0f,\"flaps_index\":%.0f,"
      "\"parking_brake\":%.0f,\"autopilot_master\":%.0f}",
      snapshot_.latitude_deg,
      snapshot_.longitude_deg,
      snapshot_.altitude_ft_true,
      snapshot_.altitude_ft_indicated,
      snapshot_.ias_kt,
      snapshot_.tas_kt,
      snapshot_.ground_velocity_mps,
      snapshot_.turbine_n1_pct,
      snapshot_.on_ground,
      snapshot_.engine_combustion,
      snapshot_.transponder_code,
      snapshot_.adf_active_freq_khz,
      snapshot_.adf_standby_freq_khz,
      snapshot_.vertical_speed_fpm,
      snapshot_.pitch_deg,
      snapshot_.turbine_n1_pct_2,
      snapshot_.gear_handle,
      snapshot_.flaps_index,
      snapshot_.parking_brake,
      snapshot_.autopilot_master);
  snapshot_json_.assign(buffer);
}

}  // namespace osb
