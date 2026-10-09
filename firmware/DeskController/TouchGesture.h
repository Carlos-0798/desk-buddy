#pragma once
#include <stdint.h>

// The production Arduino toolchain uses C++11. C++14 host static_assert tests
// execute these same transitions during compilation, without a host executable.
#if __cplusplus >= 201402L
#define DESK_TOUCH_CONSTEXPR constexpr
#else
#define DESK_TOUCH_CONSTEXPR
#endif

namespace deskcore {

// Resistive touch gestures deliberately do not share the physical-key debouncer.
// down is the physical PENIRQ level; valid only qualifies this coordinate sample.
// target zero means no actionable control. context is the page/layout generation.
// Pass context even for invalid/up samples. Dispatch an Event only if its context
// still matches the current page; a queued event must never be re-hit-tested on a
// different page. Changing context while contact is held blocks until release.
class TouchGesture {
 public:
  static constexpr uint32_t PressStableMs = 25;
  static constexpr uint32_t ReleaseStableMs = 15;
  static constexpr uint32_t InvalidGraceMs = 120;
  static constexpr int16_t MoveTolerancePx = 14;
  static constexpr uint8_t RequiredSamples = 3;

  struct Event {
    bool accepted = false;
    int16_t x = 0, y = 0;
    uint16_t target = 0, context = 0;
  };

  DESK_TOUCH_CONSTEXPR void reset() {
    clearContact();
    contextKnown_ = false;
    context_ = 0;
  }

  // Use after remote navigation/calibration or when contact at startup must not
  // act. A fresh physical release, rather than a timer alone, clears this gate.
  DESK_TOUCH_CONSTEXPR void blockUntilRelease(uint32_t now, uint16_t context) {
    clearContact();
    held_ = blocked_ = true;
    contextKnown_ = true;
    context_ = context;
    lastValidAt_ = now;
  }

  DESK_TOUCH_CONSTEXPR bool held() const { return held_; }

  DESK_TOUCH_CONSTEXPR Event update(uint32_t now, bool down, bool valid,
      int16_t x, int16_t y, uint16_t target, uint16_t context) {
    Event result;
    if (!contextKnown_) {
      contextKnown_ = true;
      context_ = context;
    } else if (context != context_) {
      if (held_ || down) blockUntilRelease(now, context);
      else { clearContact(); context_ = context; }
    }

    if (!down) {
      if (!held_) return result;
      if (!releaseTracking_) { releaseTracking_ = true; releaseAt_ = now; }
      else if (elapsed(now, releaseAt_) >= ReleaseStableMs) clearContact();
      return result;
    }
    releaseTracking_ = false;
    held_ = true;
    if (blocked_ || accepted_) return result;

    // A short conversion rejection does not restart the entire gesture. It
    // never contributes a sample or emits an action. A long unknown gap cancels.
    if (!valid) {
      if (candidate_ && elapsed(now, lastValidAt_) > InvalidGraceMs) blocked_ = true;
      return result;
    }
    if (candidate_ && elapsed(now, lastValidAt_) > InvalidGraceMs) {
      blocked_ = true;
      return result;
    }
    if (!candidate_) {
      candidate_ = true;
      target_ = target;
      anchorX_ = x; anchorY_ = y;
      beganAt_ = now;
      if (!target) { blocked_ = true; return result; }
    } else if (target != target_ || distance(x, anchorX_) > MoveTolerancePx ||
               distance(y, anchorY_) > MoveTolerancePx) {
      blocked_ = true;
      return result;
    }

    lastValidAt_ = now;
    samplesX_[sampleIndex_] = x;
    samplesY_[sampleIndex_] = y;
    sampleIndex_ = static_cast<uint8_t>((sampleIndex_ + 1u) % RequiredSamples);
    if (sampleCount_ < RequiredSamples) ++sampleCount_;
    if (sampleCount_ < RequiredSamples || elapsed(now, beganAt_) < PressStableMs)
      return result;
    accepted_ = true;
    result.accepted = true;
    result.x = median(samplesX_[0], samplesX_[1], samplesX_[2]);
    result.y = median(samplesY_[0], samplesY_[1], samplesY_[2]);
    result.target = target_;
    result.context = context_;
    return result;
  }

 private:
  static DESK_TOUCH_CONSTEXPR uint32_t elapsed(uint32_t now, uint32_t since) {
    return static_cast<uint32_t>(now - since);
  }
  static DESK_TOUCH_CONSTEXPR int32_t distance(int16_t a, int16_t b) {
    return a >= b ? static_cast<int32_t>(a) - b : static_cast<int32_t>(b) - a;
  }
  static DESK_TOUCH_CONSTEXPR int16_t median(int16_t a, int16_t b, int16_t c) {
    return a < b ? (b < c ? b : (a < c ? c : a)) : (a < c ? a : (b < c ? c : b));
  }
  DESK_TOUCH_CONSTEXPR void clearContact() {
    held_ = blocked_ = accepted_ = candidate_ = releaseTracking_ = false;
    sampleCount_ = sampleIndex_ = 0;
    target_ = 0;
  }

  bool held_ = false, blocked_ = false, accepted_ = false;
  bool candidate_ = false, releaseTracking_ = false, contextKnown_ = false;
  uint16_t target_ = 0, context_ = 0;
  uint32_t beganAt_ = 0, lastValidAt_ = 0, releaseAt_ = 0;
  int16_t anchorX_ = 0, anchorY_ = 0;
  int16_t samplesX_[RequiredSamples] = {}, samplesY_[RequiredSamples] = {};
  uint8_t sampleCount_ = 0, sampleIndex_ = 0;
};
}  // namespace deskcore

#undef DESK_TOUCH_CONSTEXPR
