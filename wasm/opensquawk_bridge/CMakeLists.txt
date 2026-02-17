cmake_minimum_required(VERSION 3.19)
project(opensquawk_bridge_wasm)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(opensquawk_bridge_wasm MODULE
  src/Bridge.cpp
  src/exports.cpp
)

# MSFS SDK / SimConnect headers are expected to be available via include paths.
# Example: -I"${MSFS_SDK}/SimConnect/include"

target_include_directories(opensquawk_bridge_wasm PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)

# Emscripten exports (names are referenced from JS via Module.ccall)
set_target_properties(opensquawk_bridge_wasm PROPERTIES
  LINK_FLAGS "-s EXPORTED_FUNCTIONS=['_osb_init','_osb_tick','_osb_is_connected','_osb_get_snapshot_json','_osb_get_snapshot_age_ms','_osb_set_transponder_code','_osb_set_adf_active_khz','_osb_set_adf_standby_khz','_osb_set_gear_handle','_osb_set_flaps_index','_osb_set_parking_brake','_osb_set_autopilot_master','_module_init','_module_deinit']"
)
