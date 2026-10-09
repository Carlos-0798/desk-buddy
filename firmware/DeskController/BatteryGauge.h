#ifndef DESK_BATTERY_GAUGE_H
#define DESK_BATTERY_GAUGE_H

#include <stdint.h>

namespace deskbattery {

// Display policy only. No charger control, cell-presence detection, current
// integration, protection threshold or calibrated state-of-charge measurement.
// USB power suppresses percentage: charging can bias voltage, and voltage alone
// cannot prove a cell is present. Label every valid battery estimate approximate.
struct State {
  bool valid = false;
  int16_t percent = -1;        // -1 means display "--", never a stale percentage.
  uint16_t millivolts = 0;     // Filtered estimate; may be retained when stale.
  bool usbBiased = false;      // USB voltage can bias the estimate upward.
  uint32_t ageMs = UINT32_MAX;
};

constexpr uint32_t StaleAfterMs = 5000;
// Conservative display-domain bounds, not cell safety/cutoff specifications.
constexpr uint16_t MinimumEstimateMv = 2800, MaximumEstimateMv = 4350;

// Coarse heuristic from Adafruit nRF52 BSP 1.7.0 adc_vbat/mvToPercent:
// 3300 mV -> 0%, 3600 mV -> 10%, 4200 mV -> 100%, piecewise linear.
// https://github.com/adafruit/Adafruit_nRF52_Arduino/blob/1.7.0/libraries/Bluefruit52Lib/examples/Hardware/adc_vbat/adc_vbat.ino
// This is not a measured curve for the user's #258 cell. Clamp the endpoints
// and round to 5% steps rather than implying 1% measurement precision.
inline int16_t approximatePercent(uint16_t millivolts) {
  if (millivolts <= 3300) return 0;
  if (millivolts >= 4200) return 100;
  const uint32_t steps = millivolts < 3600
      ? (static_cast<uint32_t>(millivolts - 3300) + 75u) / 150u
      : 2u + (3u * (millivolts - 3600) + 50u) / 100u;
  return static_cast<int16_t>(steps * 5u);
}

class Gauge {
 public:
  void reset() {
    seen_ = inRange_ = false;
    usbVbus_ = needsFreshSample_ = reseed_ = false;
    sampledAt_ = filteredQ4_ = 0;
  }

  // Call before the ADC scheduling early return, and before any new sample.
  // Keep state() const: rendering/status reads never create power transitions.
  // After either transition retain diagnostic voltage/age but require a new
  // battery-side sample before showing a percentage. Never reuse the USB EMA.
  void setUsbVbus(bool present) {
    if (present == usbVbus_) return;
    usbVbus_ = present;
    needsFreshSample_ = true;
    reseed_ = true;
  }

  // Call exactly once after each completed ADC sample, using that sample's
  // timestamp. Do not call repeatedly with a cached reading during UI draws.
  void observe(uint16_t millivolts, uint32_t sampledAtMs) {
    const uint32_t gap = sampledAtMs - sampledAt_;
    if (seen_ && (gap == 0 || gap >= 0x80000000u)) return;
    const bool usable = millivolts >= MinimumEstimateMv &&
                        millivolts <= MaximumEstimateMv;
    if (!usable) {
      filteredQ4_ = 0;
    } else if (reseed_ || !seen_ || !inRange_ || gap >= StaleAfterMs) {
      filteredQ4_ = static_cast<uint32_t>(millivolts) * 16u;
    } else {
      // Quarter-weight EMA, Q4 arithmetic avoids permanent integer deadband.
      filteredQ4_ = (3u * filteredQ4_ +
                     static_cast<uint32_t>(millivolts) * 16u + 2u) / 4u;
    }
    sampledAt_ = sampledAtMs;
    seen_ = true;
    inRange_ = usable;
    reseed_ = false;
    if (!usbVbus_) needsFreshSample_ = false;
  }

  State state(uint32_t now, bool usbVbus) const {
    State result;
    result.usbBiased = usbVbus;  // Does not assert charging or battery presence.
    if (!seen_) return result;
    result.ageMs = now - sampledAt_;
    result.millivolts = static_cast<uint16_t>((filteredQ4_ + 8u) / 16u);
    // The live argument suppresses immediately, even if setUsbVbus has not
    // run yet. The cached flag covers the opposite ordering: a status query
    // after unplug but before the control loop records the transition/sample.
    result.valid = !usbVbus && !usbVbus_ && !needsFreshSample_ &&
                   inRange_ && result.ageMs < StaleAfterMs;
    if (result.valid) result.percent = approximatePercent(result.millivolts);
    return result;
  }

 private:
  bool seen_ = false, inRange_ = false;
  bool usbVbus_ = false, needsFreshSample_ = false, reseed_ = false;
  uint32_t sampledAt_ = 0, filteredQ4_ = 0;
};

}  // namespace deskbattery
#endif
