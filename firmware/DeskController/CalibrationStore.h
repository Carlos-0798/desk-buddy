#ifndef DESK_CALIBRATION_STORE_H
#define DESK_CALIBRATION_STORE_H

#include <stdint.h>
#include "ControllerCore.h"

namespace deskstore {

// Mount existing storage. On nRF52840 BSP 1.7.0 only, an entirely erased
// InternalFS partition may be initialized. Nonempty unmountable data is kept.
// Call once during setup, before load() or starting BLE.
bool prepare();

// Load the newest valid calibration. On failure both outputs are unchanged;
// the caller must keep safe defaults or enter calibration. No files are written.
bool load(deskcore::TouchCalibration& calibration, int8_t& encoderSign);

// Call once after an explicitly completed calibration, from the main loop
// (not an ISR). Identical data is a no-op. Returns true only after a verified
// record exists. Failure leaves the previous valid slot available.
// Extrapolated endpoints must be -1024..5119, each axis span 500..6143,
// encoderSign = +1/-1. save()/load() never format storage or delete other data.
bool save(const deskcore::TouchCalibration& calibration, int8_t encoderSign);

}  // namespace deskstore

#endif  // DESK_CALIBRATION_STORE_H
