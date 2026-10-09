#ifndef DESK_CONTROLLER_CORE_H
#define DESK_CONTROLLER_CORE_H

#include <stdint.h>

// Hardware-independent input interpretation. No transport or Arduino dependency.
namespace deskcore {

enum class Action : uint8_t {
  None, PlayPause, Previous, Next, Mute, VolumeUp, VolumeDown, Mode, Marker
};

class DebouncedButton {
 public:
  void reset(bool pressed, uint32_t now) {
    candidate_ = stable_ = pressed;
    candidateSince_ = now;
    initialized_ = true;
  }

  // Returns true only once when a new press remains stable for 25 ms.
  // Startup/reset while held establishes a baseline, rather than a press event.
  bool update(bool pressed, uint32_t now) {
    if (!initialized_) {
      reset(pressed, now);
      return false;
    }
    if (pressed != candidate_) {
      candidate_ = pressed;
      candidateSince_ = now;
    }
    if (candidate_ != stable_ &&
        static_cast<uint32_t>(now - candidateSince_) >= 25u) {
      stable_ = candidate_;
      return stable_;
    }
    return false;
  }

  bool pressed() const { return stable_; }

 private:
  uint32_t candidateSince_ = 0;
  bool candidate_ = false;
  bool stable_ = false;
  bool initialized_ = false;
};

enum class ButtonEdge : uint8_t { None, Pressed, Released };

// Counts debounced edges; holding a key never repeats an action. Resetting
// counters preserves both stable and candidate states, even mid-debounce.
class TrackedButton {
 public:
  void reset(bool pressed, uint32_t now) {
    button_.reset(pressed, now);
    resetCounters();
  }
  // Leaving an input test establishes a fresh physical baseline, without
  // erasing its accumulated diagnostic counters or replaying held keys.
  void rebase(bool pressed, uint32_t now) { button_.reset(pressed, now); }
  ButtonEdge update(bool pressed, uint32_t now) {
    const bool wasPressed = button_.pressed();
    button_.update(pressed, now);
    if (wasPressed == button_.pressed()) return ButtonEdge::None;
    if (button_.pressed()) {
      if (presses_ != UINT32_MAX) ++presses_;
      return ButtonEdge::Pressed;
    }
    if (releases_ != UINT32_MAX) ++releases_;
    return ButtonEdge::Released;
  }
  void resetCounters() { presses_ = releases_ = 0; }
  bool pressed() const { return button_.pressed(); }
  uint32_t presses() const { return presses_; }
  uint32_t releases() const { return releases_; }
 private:
  DebouncedButton button_;
  uint32_t presses_ = 0, releases_ = 0;
};

// Nominal Feather 100k/100k divider, internal 0.6 V reference / gain 1/5,
// 12-bit ADC (4096 codes). This is a voltage estimate, not state of charge.
inline uint16_t vbatEstimateMillivolts(uint16_t raw) {
  if (raw > 4095u) raw = 4095u;
  return static_cast<uint16_t>((static_cast<uint32_t>(raw) * 6000u + 2048u) / 4096u);
}

class EncoderDelta {
 public:
  void reset(int32_t position) {
    previous_ = static_cast<uint32_t>(position);
    initialized_ = true;
  }

  int32_t update(int32_t position) {
    if (!initialized_) {
      reset(position);
      return 0;
    }
    const uint32_t current = static_cast<uint32_t>(position);
    // Unsigned subtraction is defined modulo 2^32, including signed wrap.
    const uint32_t forward = current - previous_;
    previous_ = current;  // A discarded reset/jump must not accumulate.
    if (forward <= 64u) return static_cast<int32_t>(forward);
    const uint32_t backward = 0u - forward;
    if (backward <= 64u) return -static_cast<int32_t>(backward);
    return 0;
  }

 private:
  uint32_t previous_ = 0;
  bool initialized_ = false;
};

// Zero-based physical key index: K1 = 0, ..., K6 = 5.
inline Action actionForKey(uint8_t index) {
  switch (index) {
    case 0: return Action::PlayPause;
    case 1: return Action::Previous;
    case 2: return Action::Next;
    case 3: return Action::Mute;
    case 4: return Action::Mode;
    case 5: return Action::Marker;  // Local event; no implicit HID shortcut.
    default: return Action::None;
  }
}

inline const char* actionLabel(Action action) {
  switch (action) {
    case Action::PlayPause: return "Play/Pause";
    case Action::Previous: return "Previous";
    case Action::Next: return "Next";
    case Action::Mute: return "Mute";
    case Action::VolumeUp: return "Volume +";
    case Action::VolumeDown: return "Volume -";
    case Action::Mode: return "Mode";
    case Action::Marker: return "Marker";
    case Action::None: return "None";
  }
  return "None";
}

struct TouchCalibration {
  // Endpoint values are expressed AFTER swapXY is applied to raw samples.
  // Left/right and top/bottom may each be in either numerical order.
  int32_t xLeft;
  int32_t xRight;
  int32_t yTop;
  int32_t yBottom;
  bool swapXY;
};

namespace detail {
inline int16_t mapAxis(int32_t raw, int32_t start, int64_t signedSpan,
                       int16_t maximum) {
  int64_t offset = static_cast<int64_t>(raw) - start;
  int64_t span = signedSpan;
  if (span < 0) {
    span = -span;
    offset = -offset;
  }
  if (offset <= 0) return 0;
  if (offset >= span) return maximum;
  // Round to the nearest pixel. int64_t also covers full int32_t raw ranges.
  return static_cast<int16_t>((offset * maximum + span / 2) / span);
}
}  // namespace detail

// Maps to a 320 x 240 landscape screen, inclusive coordinates 0..319/0..239.
// Invalid calibration returns false and leaves both output references unchanged.
inline bool mapTouch(int32_t rawX, int32_t rawY,
                     const TouchCalibration& calibration,
                     int16_t& screenX, int16_t& screenY) {
  const int64_t xSpan = static_cast<int64_t>(calibration.xRight) -
                        calibration.xLeft;
  const int64_t ySpan = static_cast<int64_t>(calibration.yBottom) -
                        calibration.yTop;
  if ((xSpan > -500 && xSpan < 500) ||
      (ySpan > -500 && ySpan < 500)) return false;
  const int32_t x = calibration.swapXY ? rawY : rawX;
  const int32_t y = calibration.swapXY ? rawX : rawY;
  screenX = detail::mapAxis(x, calibration.xLeft, xSpan, 319);
  screenY = detail::mapAxis(y, calibration.yTop, ySpan, 239);
  return true;
}

}  // namespace deskcore

#endif  // DESK_CONTROLLER_CORE_H
