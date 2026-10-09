#pragma once
#include <Adafruit_seesaw.h>

// The upstream convenience getters ignore read() failure and then decode an
// uninitialized buffer. Keep transport success separate from the input value.
class DeskEncoder : public Adafruit_seesaw {
 public:
  using Adafruit_seesaw::Adafruit_seesaw;

  bool checkedSnapshot(int32_t& position, bool& down, uint8_t switchPin) {
    if (switchPin >= 64) return false;
    uint8_t positionBytes[4] = {};
    uint8_t gpioBytes[8] = {};
    if (!read(SEESAW_ENCODER_BASE, SEESAW_ENCODER_POSITION, positionBytes, 4))
      return false;
    const bool portB = switchPin >= 32;
    if (!read(SEESAW_GPIO_BASE, SEESAW_GPIO_BULK, gpioBytes, portB ? 8 : 4))
      return false;
    const uint8_t* pins = gpioBytes + (portB ? 4 : 0);
    const uint32_t mask = uint32_t(1) << (switchPin & 31);
    // Commit both only after every transaction succeeds. Callers may safely
    // discard the snapshot without manufacturing motion or a button edge.
    position = static_cast<int32_t>(decodeBigEndian(positionBytes));
    down = (decodeBigEndian(pins) & mask) == 0;
    return true;
  }

  static constexpr uint32_t decodeBigEndian(const uint8_t* bytes) {
    return (uint32_t(bytes[0]) << 24) | (uint32_t(bytes[1]) << 16) |
           (uint32_t(bytes[2]) << 8) | uint32_t(bytes[3]);
  }
};
