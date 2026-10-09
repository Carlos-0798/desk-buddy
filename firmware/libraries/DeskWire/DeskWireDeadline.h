#pragma once
#include <stdint.h>
namespace deskwire {
constexpr uint32_t boundedTimeout(uint32_t value) {
  return value == 0 ? 1u : (value > 1000000u ? 1000000u : value);
}
constexpr bool expired(uint32_t now, uint32_t began, uint32_t timeout) {
  return static_cast<uint32_t>(now - began) >= timeout;
}
}
