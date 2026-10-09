#pragma once
#include <stdint.h>
#include "DisplaySleep.h"

// The UI owns language selection. This single worker owns preference writes;
// it never accesses Wire, SPI, HID, UI, or configuration/calibration records.
namespace desklanguage {
enum class Kind:uint8_t {Language,Display};
struct Request {Kind kind=Kind::Language;uint8_t language=0;deskdisplay::Settings display;};
struct Result { Kind kind=Kind::Language;uint8_t language=0;deskdisplay::Settings display;bool okay=false; uint32_t elapsedUs=0; const char* error="none"; };
bool begin();
bool start(uint8_t language);
bool startDisplay(const deskdisplay::Settings& settings);
bool busy();
bool take(Result& result);
}
