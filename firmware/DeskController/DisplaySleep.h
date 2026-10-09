#pragma once
#include <stdint.h>

// Independent of Arduino, HID, rendering and storage. No method arms output.
namespace deskdisplay {
#if __cplusplus >= 201402L
#define DESK_DISPLAY_CX constexpr
#else
#define DESK_DISPLAY_CX inline
#endif
constexpr uint16_t MaxSeconds=43200; // 12 hours; still fits the existing u16 record.
constexpr bool validSeconds(uint16_t value) { return value==0||value==30||(value>=60&&value<=MaxSeconds&&value%60==0); }
struct Settings {
  uint16_t usbSeconds, batterySeconds;
  constexpr Settings(uint16_t usb=300,uint16_t battery=60):usbSeconds(usb),batterySeconds(battery){}
  constexpr bool valid() const {return validSeconds(usbSeconds)&&validSeconds(batterySeconds);}
  constexpr bool operator==(const Settings& other) const {return usbSeconds==other.usbSeconds&&batterySeconds==other.batterySeconds;}
};
class State {
 public:
  static constexpr uint32_t QuietMs=200;
  DESK_DISPLAY_CX void begin(uint32_t now,bool usb) { asleep_=blocked_=entryHeld_=false;lastInput_=quietAt_=now;usb_=usb;wakes_=0; }
  DESK_DISPLAY_CX void power(uint32_t now,bool usb) {if(usb!=usb_){usb_=usb;lastInput_=now;}}
  DESK_DISPLAY_CX void activity(uint32_t now) {lastInput_=now;}
  DESK_DISPLAY_CX bool sleep(uint32_t now,bool held=false) {if(asleep_)return false;asleep_=blocked_=true;entryHeld_=held;quietAt_=now;return true;}
  DESK_DISPLAY_CX bool wake(uint32_t now) {lastInput_=now;if(!asleep_)return false;asleep_=false;blocked_=true;quietAt_=now;++wakes_;return true;}
  // Called once for a complete snapshot, before any mapping in that snapshot.
  // A wake consumes the whole batch; held contacts and continuous rotation keep
  // the quarantine active until every contact is released and 200 ms is quiet.
  DESK_DISPLAY_CX bool batch(uint32_t now,bool event,bool held) {
    if(event||held)lastInput_=now;
    if(asleep_){if(entryHeld_){if(!held)entryHeld_=false;return false;}if(event)wake(now);return false;}
    if(blocked_){if(event||held)quietAt_=now;else if(uint32_t(now-quietAt_)>=QuietMs)blocked_=false;return false;}
    return true;
  }
  // Cooperative touch sampling does not know the other held contacts; it may
  // wake/extend quarantine but must never release quarantine on its own.
  DESK_DISPLAY_CX void contact(uint32_t now) {lastInput_=now;if(asleep_&&!entryHeld_)wake(now);if(blocked_)quietAt_=now;}
  constexpr bool asleep() const {return asleep_;}
  constexpr bool suppressed() const {return asleep_||blocked_;}
  constexpr uint32_t wakeCount() const {return wakes_;}
  constexpr uint32_t idle(uint32_t now) const {return uint32_t(now-lastInput_);}
  constexpr bool due(uint32_t now,const Settings& settings,bool eligible) const {
    return eligible&&!suppressed()&&(usb_?settings.usbSeconds:settings.batterySeconds)!=0&&
      uint32_t(now-lastInput_)>=uint32_t(usb_?settings.usbSeconds:settings.batterySeconds)*1000u;
  }
 private:
  bool asleep_=false,blocked_=false,entryHeld_=false,usb_=true;
  uint32_t lastInput_=0,quietAt_=0,wakes_=0;
};
#undef DESK_DISPLAY_CX
}
