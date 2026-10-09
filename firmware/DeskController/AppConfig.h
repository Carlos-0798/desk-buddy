#pragma once
#include <Arduino.h>
namespace appcfg {
constexpr char BuildId[] = "DESK_BUDDY_R3H_20261005";
// LITE is not connected in the existing harness. Enable only after physical
// confirmation; LOW sinks LITE, INPUT releases its onboard 3.3 V pull-up.
#ifndef DESK_BACKLIGHT_PIN
#define DESK_BACKLIGHT_PIN -1
#endif
constexpr int8_t BacklightPin=DESK_BACKLIGHT_PIN;
static_assert(BacklightPin==-1||BacklightPin==11,"Only verified optional D11 LITE wiring is supported");
constexpr bool BacklightControl=BacklightPin>=0;
constexpr uint8_t TftCs=9, TftDc=10, SdCs=5, TouchIrq=6;
constexpr uint8_t TouchAddress=0x48, EncoderAddress=0x36, EncoderSwitch=24;
constexpr uint8_t ButtonPins[]={A0,A1,A2,A3,A4,A5};
constexpr uint32_t SpiHz=8000000, I2cHz=100000;
constexpr uint16_t TftPowerSettleMs=500;
constexpr uint32_t BatterySampleMs=1000;
constexpr uint16_t PlayPause=0xCD, Previous=0xB6, Next=0xB5;
constexpr uint16_t Mute=0xE2, VolumeUp=0xE9, VolumeDown=0xEA;
}
