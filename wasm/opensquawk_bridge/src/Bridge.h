#pragma once

#include <SimConnect.h>
#include <cstdint>
#include <string>

namespace osb {

struct TelemetrySnapshot {
  double latitude_deg = 0.0;
  double longitude_deg = 0.0;
  double altitude_ft_true = 0.0;
  double altitude_ft_indicated = 0.0;
  double ias_kt = 0.0;
  double tas_kt = 0.0;
  double ground_velocity_mps = 0.0;
  double turbine_n1_pct = 0.0;
  double on_ground = 0.0;
  double engine_combustion = 0.0;
  double transponder_code = 0.0;
  double adf_active_freq_khz = 0.0;
  double adf_standby_freq_khz = 0.0;
  double vertical_speed_fpm = 0.0;
  double pitch_deg = 0.0;
  double turbine_n1_pct_2 = 0.0;
  double gear_handle = 0.0;
  double flaps_index = 0.0;
  double parking_brake = 0.0;
  double autopilot_master = 0.0;
};

class Bridge {
 public:
  static Bridge &Get();

  void Tick();
  bool EnsureConnected();
  void Close();

  bool IsConnected() const { return connected_; }
  bool HasSnapshot() const { return snapshot_valid_; }
  uint64_t SnapshotAgeMs() const;

  const char *GetSnapshotJson();

  bool SetTransponderCode(int code);
  bool SetAdfActiveKHz(double value_khz);
  bool SetAdfStandbyKHz(double value_khz);
  bool SetGearHandle(bool on);
  bool SetFlapsIndex(int index);
  bool SetParkingBrake(bool on);
  bool SetAutopilotMaster(bool on);

 private:
  Bridge();
  void BuildSnapshotJson();
  void RequestTelemetry();

  static void DispatchProc(SIMCONNECT_RECV *data, DWORD cbData, void *context);
  void HandleDispatch(SIMCONNECT_RECV *data, DWORD cbData);

  HANDLE simconnect_ = nullptr;
  bool connected_ = false;
  bool snapshot_valid_ = false;
  uint64_t snapshot_ts_ms_ = 0;
  uint64_t last_connect_attempt_ms_ = 0;

  TelemetrySnapshot snapshot_{};
  std::string snapshot_json_;
};

}  // namespace osb
