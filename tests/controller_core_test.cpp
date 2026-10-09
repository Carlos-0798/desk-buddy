#include "../firmware/DeskController/ControllerCore.h"

#include <limits.h>
#include <stdio.h>
#include <string.h>

static int failures = 0;
static int assertions = 0;
#define CHECK(condition) do { \
  ++assertions; \
  if (!(condition)) { \
    ++failures; \
    fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #condition); \
  } \
} while (false)

using deskcore::Action;
using deskcore::DebouncedButton;
using deskcore::EncoderDelta;
using deskcore::TouchCalibration;

static void buttonBounceAndHold() {
  DebouncedButton button;
  button.reset(false, 0);
  CHECK(!button.pressed());
  CHECK(!button.update(true, 10));
  CHECK(!button.update(false, 13));
  CHECK(!button.update(true, 19));
  CHECK(!button.update(true, 43));
  CHECK(!button.pressed());
  CHECK(button.update(true, 44));
  CHECK(button.pressed());
  CHECK(!button.update(true, 45));
  CHECK(!button.update(true, 10000));
  // A short low pulse does not release or retrigger a held key.
  CHECK(!button.update(false, 10001));
  CHECK(!button.update(true, 10010));
  CHECK(!button.update(true, 10040));
  CHECK(button.pressed());
  CHECK(!button.update(false, 10050));
  CHECK(!button.update(false, 10074));
  CHECK(button.pressed());
  CHECK(!button.update(false, 10075));
  CHECK(!button.pressed());
  CHECK(!button.update(true, 10080));
  CHECK(button.update(true, 10105));
  CHECK(!button.update(true, 10106));
}

static void buttonStartupResetAndWrap() {
  DebouncedButton button;
  CHECK(!button.update(true, 123));
  CHECK(button.pressed());
  CHECK(!button.update(true, 2000));
  button.reset(true, 5000);
  CHECK(!button.update(true, 6000));
  CHECK(!button.update(false, 6001));
  CHECK(!button.update(false, 6026));
  CHECK(!button.pressed());
  CHECK(!button.update(true, 6030));
  CHECK(button.update(true, 6055));
  button.reset(false, UINT32_MAX - 30u);
  CHECK(!button.update(true, UINT32_MAX - 10u));
  CHECK(!button.update(true, 13u));  // 24 ms after candidate change.
  CHECK(button.update(true, 14u));  // 25 ms across millis() wrap.
  CHECK(!button.update(true, 15u));
  button.reset(false, 0);
  CHECK(!button.pressed());
  CHECK(!button.update(false, 100));
}

static void trackedButtonCountsAndReset() {
  using deskcore::ButtonEdge;
  deskcore::TrackedButton key;
  key.reset(false, 0);
  CHECK(key.update(true, 10) == ButtonEdge::None);
  CHECK(key.update(false, 13) == ButtonEdge::None);
  CHECK(key.update(true, 19) == ButtonEdge::None);
  CHECK(key.update(true, 43) == ButtonEdge::None);
  CHECK(key.update(true, 44) == ButtonEdge::Pressed);
  CHECK(key.presses() == 1 && key.releases() == 0 && key.pressed());
  CHECK(key.update(true, 10000) == ButtonEdge::None);  // No hold repeat.
  key.resetCounters();
  CHECK(key.presses() == 0 && key.releases() == 0 && key.pressed());
  CHECK(key.update(true, 11000) == ButtonEdge::None);  // No artificial press.
  CHECK(key.update(false, 11001) == ButtonEdge::None);
  CHECK(key.update(true, 11012) == ButtonEdge::None);  // Release bounce.
  CHECK(key.update(false, 11020) == ButtonEdge::None);
  CHECK(key.update(false, 11045) == ButtonEdge::Released);
  CHECK(key.presses() == 0 && key.releases() == 1 && !key.pressed());
  key.reset(true, 12000);  // Held at boot is a baseline, not a new press.
  CHECK(key.update(true, 12030) == ButtonEdge::None);
  CHECK(key.presses() == 0 && key.releases() == 0 && key.pressed());
  CHECK(key.update(false, 12040) == ButtonEdge::None);
  CHECK(key.update(false, 12065) == ButtonEdge::Released);
  CHECK(key.update(true, 12070) == ButtonEdge::None);
  key.resetCounters();  // Counter reset must not restart candidate timing.
  CHECK(key.update(true, 12095) == ButtonEdge::Pressed);
  CHECK(key.presses() == 1 && key.releases() == 0);
  key.reset(false, UINT32_MAX - 30u);
  CHECK(key.update(true, UINT32_MAX - 10u) == ButtonEdge::None);
  CHECK(key.update(true, 14u) == ButtonEdge::Pressed);
  CHECK(key.presses() == 1);
  deskcore::TrackedButton bank[6];
  for(auto& item:bank)item.reset(false, 0);
  for(uint8_t i=0;i<6;++i){
    CHECK(bank[i].update(true, 100) == ButtonEdge::None);
    CHECK(bank[i].update(true, 125) == ButtonEdge::Pressed);
    for(uint8_t j=0;j<6;++j)CHECK(bank[j].presses() == (j<=i?1u:0u));
  }
}

static void batteryNominalScale() {
  CHECK(deskcore::vbatEstimateMillivolts(0) == 0);
  CHECK(deskcore::vbatEstimateMillivolts(2048) == 3000);
  CHECK(deskcore::vbatEstimateMillivolts(2526) == 3700);
  CHECK(deskcore::vbatEstimateMillivolts(2867) == 4200);
  CHECK(deskcore::vbatEstimateMillivolts(4095) == 5999);
  CHECK(deskcore::vbatEstimateMillivolts(65535) == 5999);
}

static void encoderMovementWrapAndJumps() {
  EncoderDelta encoder;
  CHECK(encoder.update(345) == 0);
  CHECK(encoder.update(345) == 0);
  CHECK(encoder.update(347) == 2);
  CHECK(encoder.update(343) == -4);
  encoder.reset(INT32_MAX - 1);
  CHECK(encoder.update(INT32_MAX) == 1);
  CHECK(encoder.update(INT32_MIN) == 1);
  CHECK(encoder.update(INT32_MIN + 2) == 2);
  CHECK(encoder.update(INT32_MAX) == -3);
  encoder.reset(0);
  CHECK(encoder.update(64) == 64);
  CHECK(encoder.update(0) == -64);
  CHECK(encoder.update(65) == 0);
  CHECK(encoder.update(66) == 1);  // Baseline follows rejected +65 jump.
  CHECK(encoder.update(1) == 0);
  CHECK(encoder.update(0) == -1);  // Baseline follows rejected -65 jump.
  encoder.reset(12000);
  CHECK(encoder.update(0) == 0);  // Device count restart.
  CHECK(encoder.update(1) == 1);
  CHECK(encoder.update(INT32_MIN) == 0);
  CHECK(encoder.update(INT32_MIN + 1) == 1);
  encoder.reset(42);
  CHECK(encoder.update(42) == 0);
  CHECK(encoder.update(41) == -1);
}

static void keyActions() {
  const Action expected[] = {Action::PlayPause, Action::Previous, Action::Next,
                             Action::Mute, Action::Mode, Action::Marker};
  for (uint8_t i = 0; i < 6; ++i) CHECK(deskcore::actionForKey(i) == expected[i]);
  CHECK(deskcore::actionForKey(6) == Action::None);
  CHECK(deskcore::actionForKey(255) == Action::None);
  CHECK(strcmp(deskcore::actionLabel(Action::PlayPause), "Play/Pause") == 0);
  CHECK(strcmp(deskcore::actionLabel(Action::VolumeUp), "Volume +") == 0);
  CHECK(strcmp(deskcore::actionLabel(Action::VolumeDown), "Volume -") == 0);
  CHECK(strcmp(deskcore::actionLabel(Action::Marker), "Marker") == 0);
  CHECK(strcmp(deskcore::actionLabel(static_cast<Action>(255)), "None") == 0);
}

static void touchNormalClampAndInvalid() {
  const TouchCalibration normal = {200, 3800, 300, 3700, false};
  int16_t x = -1, y = -1;
  CHECK(deskcore::mapTouch(200, 300, normal, x, y));
  CHECK(x == 0 && y == 0);
  CHECK(deskcore::mapTouch(3800, 3700, normal, x, y));
  CHECK(x == 319 && y == 239);
  CHECK(deskcore::mapTouch(2000, 2000, normal, x, y));
  CHECK(x == 160 && y == 120);
  CHECK(deskcore::mapTouch(INT32_MIN, INT32_MAX, normal, x, y));
  CHECK(x == 0 && y == 239);
  const TouchCalibration invalid[] = {
    {0, 499, 0, 1000, false}, {500, 1, 0, 1000, false},
    {0, 1000, 0, 499, false}, {0, 1000, 500, 1, true},
    {100, 100, 0, 1000, false}, {0, 1000, 100, 100, false}
  };
  for (const auto& calibration : invalid) {
    x = 17; y = 23;
    CHECK(!deskcore::mapTouch(100, 200, calibration, x, y));
    CHECK(x == 17 && y == 23);
  }
  const TouchCalibration minimumSpan = {0, 500, 500, 0, false};
  CHECK(deskcore::mapTouch(250, 250, minimumSpan, x, y));
  CHECK(x == 160 && y == 120);
  const TouchCalibration fullRange = {INT32_MIN, INT32_MAX,
                                     INT32_MAX, INT32_MIN, false};
  CHECK(deskcore::mapTouch(INT32_MAX, INT32_MIN, fullRange, x, y));
  CHECK(x == 319 && y == 239);
  CHECK(deskcore::mapTouch(0, 0, fullRange, x, y));
  CHECK(x == 160 && y == 119);
}

static void touchAllOrientations() {
  // All 8 combinations of swap and independent reversal must map corners.
  for (int swap = 0; swap < 2; ++swap) {
    for (int flipX = 0; flipX < 2; ++flipX) {
      for (int flipY = 0; flipY < 2; ++flipY) {
        const TouchCalibration calibration = {
          flipX ? 3800 : 200, flipX ? 200 : 3800,
          flipY ? 3700 : 300, flipY ? 300 : 3700, swap != 0
        };
        for (int right = 0; right < 2; ++right) {
          for (int bottom = 0; bottom < 2; ++bottom) {
            const int32_t axisX = right ? calibration.xRight : calibration.xLeft;
            const int32_t axisY = bottom ? calibration.yBottom : calibration.yTop;
            int16_t x = -1, y = -1;
            CHECK(deskcore::mapTouch(swap ? axisY : axisX, swap ? axisX : axisY,
                                     calibration, x, y));
            CHECK(x == (right ? 319 : 0));
            CHECK(y == (bottom ? 239 : 0));
          }
        }
      }
    }
  }
}

static void touchExtrapolatedScreenEdges() {
  // Calibration targets lie inside the glass. Their fitted screen-edge values
  // can lie beyond the raw ADC range even though every sampled target is valid.
  const TouchCalibration extrapolated = {-65, 4165, -41, 4041, false};
  const int32_t samples[5][2] = {
    {200, 300}, {3900, 300}, {3900, 3700}, {200, 3700}, {2050, 2000}
  };
  const int16_t expected[5][2] = {
    {20, 20}, {299, 20}, {299, 219}, {20, 219}, {160, 120}
  };
  for (int i = 0; i < 5; ++i) {
    int16_t x = -1, y = -1;
    CHECK(deskcore::mapTouch(samples[i][0], samples[i][1], extrapolated, x, y));
    CHECK(x == expected[i][0]);
    CHECK(y == expected[i][1]);
  }
}

int main() {
  buttonBounceAndHold();
  buttonStartupResetAndWrap();
  trackedButtonCountsAndReset();
  batteryNominalScale();
  encoderMovementWrapAndJumps();
  keyActions();
  touchNormalClampAndInvalid();
  touchAllOrientations();
  touchExtrapolatedScreenEdges();
  printf("ControllerCore: %d assertions, %d failures\n", assertions, failures);
  return failures == 0 ? 0 : 1;
}
