#pragma once

#include <stdint.h>

enum class DeskRoute : uint8_t { Usb, Ble };

// Explicit read-only diagnostic. A failed/unattempted read must not be treated
// as a disabled CCCD: raw is meaningful only when valid is true.
struct DeskBleReportStatus {
  uint8_t reportId = 0;
  bool present = false, readAttempted = false, valid = false;
  bool notify = false, ready = false;
  uint16_t valueHandle = 0, cccdHandle = 0;
  uint16_t raw = 0xFFFF, length = 0;
  uint32_t result = UINT32_MAX;
};

struct DeskBleStatus {
  bool attempted = false, started = false, link = false;
  bool secured = false, bonded = false, bootMode = false, consistent = false;
  uint8_t securityMode = 0, securityLevel = 0;
  uint16_t connectionHandle = 0xFFFF;
  uint32_t epochBefore = 0, epochAfter = 0;
  DeskBleReportStatus keyboard, consumer;
};

// One instance, called only from setup()/loop() (not from an ISR/callback).
// begin() registers USB HID; tick(millis()) must continue even while disarmed.
// USB keyboard/consumer report IDs are 1/2, matching BLEHidAdafruit.
class DeskHid {
 public:
  void begin();
  // false services only an existing zero-release obligation: no USB attach,
  // advertising restart, or new press report. Used while waiting for reboot.
  void tick(uint32_t now, bool maintainTransport = true);
  void setRoute(DeskRoute route);
  DeskRoute route() const;
  void setArmed(bool armed);
  bool armed() const;
  // Connected means usable for media: USB mounted/awake, or BLE encrypted
  // with Consumer notification enabled. It does not imply endpoint idle.
  bool connected() const;
  bool usbConnected() const;
  bool bleConnected() const;
  // Does not start BLE, restore system attributes, send reports, or change
  // permissions. Keyboard follows the current boot/report protocol path.
  DeskBleStatus bleStatus() const;
  bool setManagementRadio(bool enable);
  bool busy() const;
  bool queueConsumer(uint16_t usage);
  bool queueKey(uint8_t keycode, uint8_t modifiers = 0);
  // Disarm, discard unsent actions, and request release on the original route.
  void clear();
  // A mapping update cancels old actions but preserves the user's output state.
  // It never arms an OFF session. A pending release blocks new presses; link
  // loss or a release failure still disarms through the normal transport path.
  void releaseForConfiguration();
  // Accepted press reports, not host/application acknowledgements.
  uint32_t sentCount() const;
  // Rejected, expired or discarded unsent actions; release reports not counted.
  uint32_t droppedCount() const;

 private:
  enum class Kind : uint8_t { Keyboard = 1, Consumer = 2 };
  struct Action {
    Kind kind;
    uint16_t value;
    uint8_t modifiers;
    uint32_t queuedAt;
  };
  static constexpr uint8_t QueueCapacity = 6;
  static constexpr uint32_t MaxQueueAgeMs = 250;
  static constexpr uint32_t PressMs = 20;
  static constexpr uint32_t ReleaseFaultMs = 250;

  bool enqueue(Kind kind, uint16_t value, uint8_t modifiers);
  void refreshLink();
  void discardQueue();
  bool activeSessionExists() const;
  bool tryReport(const Action& action, bool release, DeskRoute destination,
                 uint16_t handle, uint32_t epoch);

  DeskRoute _route = DeskRoute::Usb;
  bool _begun = false;
  bool _managementRadio = false;
  bool _armed = false;
  bool _linkReady = false;
  uint32_t _observedEpoch = 0;
  Action _queue[QueueCapacity] = {};
  uint8_t _head = 0;
  uint8_t _count = 0;
  bool _active = false;
  bool _releaseRequested = false;
  bool _releaseFault = false;
  Action _pressed = {};
  DeskRoute _pressedRoute = DeskRoute::Usb;
  uint16_t _pressedHandle = 0xFFFF;
  uint32_t _pressedEpoch = 0;
  uint32_t _pressedAt = 0;
  uint32_t _sent = 0;
  uint32_t _dropped = 0;
};
