#include "DeskHid.h"
#include "BleManagement.h"
#include "UsbConsole.h"

#include <Arduino.h>
#include <Adafruit_TinyUSB.h>
#include <InternalFileSystem.h>
#include <bluefruit.h>

namespace {
constexpr uint8_t KeyboardId = 1;
constexpr uint8_t ConsumerId = 2;
const uint8_t UsbReportDescriptor[] = {
    TUD_HID_REPORT_DESC_KEYBOARD(HID_REPORT_ID(KeyboardId)),
    TUD_HID_REPORT_DESC_CONSUMER(HID_REPORT_ID(ConsumerId))};

Adafruit_USBD_HID usbHid;
DeskHid* owner = nullptr;
bool usbStarted = false;
bool usbAttachPending = false;
uint32_t usbDetachAt = 0;
// The callbacks only mark a new connection generation. All queue state is
// owned by loop(); reconnecting never revives a previous generation's press.
volatile uint32_t usbEpoch = 0;
volatile uint32_t bleEpoch = 0;
bool bleAttempted = false;
bool bleStarted = false;

class DeskBleHid : public BLEHidAdafruit {
 public:
  bool reportReady(uint16_t handle, uint8_t id) {
    BLEConnection* conn = Bluefruit.Connection(handle);
    if (!conn || !conn->connected() || !conn->secured()) return false;
    const ble_gap_conn_sec_mode_t sec = conn->getSecureMode();
    if (sec.sm != 1 || sec.lv < 2) return false;
    BLECharacteristic* chr = reportCharacteristic(id);
    return chr && chr->notifyEnabled(handle);
  }

  DeskBleReportStatus reportStatus(uint16_t handle, uint8_t id, bool link) {
    DeskBleReportStatus status;
    status.reportId = id;
    BLECharacteristic* chr = reportCharacteristic(id);
    if (!chr) return status;
    status.present = true;
    status.valueHandle = chr->handles().value_handle;
    status.cccdHandle = chr->handles().cccd_handle;
    if (!link || status.cccdHandle == BLE_GATT_HANDLE_INVALID) return status;
    uint8_t raw[2] = {};
    ble_gatts_value_t value = {};
    value.len = sizeof(raw);
    value.p_value = raw;
    status.readAttempted = true;
    // Bypass getCccd's error-to-zero conversion without changing any attribute.
    // On error, preserve the status and sentinel rather than interpreting bytes.
    status.result = sd_ble_gatts_value_get(handle, status.cccdHandle, &value);
    status.length = value.len;
    status.valid = status.result == NRF_SUCCESS && value.len == sizeof(raw);
    if (status.valid) {
      status.raw = static_cast<uint16_t>(raw[0]) |
                   (static_cast<uint16_t>(raw[1]) << 8);
      status.notify = (status.raw & BLE_GATT_HVX_NOTIFICATION) != 0;
    }
    // Keep the original readiness predicate untouched and report it separately.
    // This second read may race a peer CCCD change; the snapshot is observational.
    status.ready = reportReady(handle, id);
    return status;
  }

  bool tryReport(uint16_t handle, uint8_t id, const void* data, uint16_t length) {
    if (!reportReady(handle, id)) return false;
    BLECharacteristic* chr = reportCharacteristic(id);
    // BLECharacteristic::notify() in BSP 1.7.0 waits up to 100 ms on its
    // packet semaphore. A single SoftDevice attempt is nonblocking and returns
    // NRF_ERROR_RESOURCES when busy. This module owns all HID notifications;
    // do not mix its sends with Bluefruit notify()/keyPress() on this service.
    // No private library fields, global BSP changes or manual semaphore use.
    ble_gatts_hvx_params_t params = {};
    params.handle = chr->handles().value_handle;
    params.type = BLE_GATT_HVX_NOTIFICATION;
    params.p_len = &length;
    params.p_data = static_cast<const uint8_t*>(data);
    const uint16_t reportLength = length;
    if (sd_ble_gatts_hvx(handle, &params) == NRF_SUCCESS) return true;
    // S140 may update the readable attribute even when HVX returns BUSY or
    // RESOURCES. Restore neutral locally so an expired unsent press cannot be
    // recovered later by a host read. This is a value write, not a notification.
    uint8_t neutral[sizeof(hid_keyboard_report_t)] = {};
    ble_gatts_value_t reset = {};
    reset.len = reportLength;
    reset.p_value = neutral;
    sd_ble_gatts_value_set(handle, params.handle, &reset);
    return false;
  }

 private:
  BLECharacteristic* reportCharacteristic(uint8_t id) {
    if (id == KeyboardId && isBootMode()) return _chr_boot_keyboard_input;
    if (id < KeyboardId || id > ConsumerId || !_chr_inputs) return nullptr;
    return &_chr_inputs[id - 1];
  }
};

DeskBleHid bleHid;

void bleEventCallback(ble_evt_t* event) {
  deskble::event(event);
  // Unlike Periph callbacks (deferred through ada_callback), this hook runs
  // in the BLE event task before it handles the next connection generation.
  if (event->header.evt_id == BLE_GAP_EVT_CONNECTED ||
      event->header.evt_id == BLE_GAP_EVT_DISCONNECTED) {
    ++bleEpoch;
  }
}

uint32_t epochFor(DeskRoute route) {
  return route == DeskRoute::Usb ? usbEpoch : bleEpoch;
}

uint16_t bleHandle() {
  return bleStarted ? Bluefruit.connHandle() : BLE_CONN_HANDLE_INVALID;
}

void startBle() {
  if (bleAttempted) return;
  bleAttempted = true;
  // Bluefruit.begin()->bond_init()->InternalFS.begin() otherwise formats the
  // entire filesystem after a mount failure. Refuse that implicit recovery;
  // existing calibration and bonds must not be erased by selecting BLE.
  if (!InternalFS.Adafruit_LittleFS::begin()) {
    if (deskusb::ready()) deskusb::out().println("BLE unavailable: InternalFS mount failed; no format attempted.");
    return;
  }
  // A single peripheral connection; pairing is initiated by the user's host.
  // No advertising or BLE initialization occurs until BLE is selected.
  if (!Bluefruit.begin(1, 0)) return;
  Bluefruit.setName("Desk Buddy");
  Bluefruit.setTxPower(4);
  Bluefruit.setAppearance(BLE_APPEARANCE_HID_KEYBOARD);
  Bluefruit.setEventCallback(bleEventCallback);
  if (bleHid.begin() != ERROR_NONE) return;
  if (!deskble::begin()) return;
  Bluefruit.Advertising.restartOnDisconnect(false);
  Bluefruit.Advertising.clearData();
  Bluefruit.ScanResponse.clearData();
  Bluefruit.Advertising.addFlags(BLE_GAP_ADV_FLAGS_LE_ONLY_GENERAL_DISC_MODE);
  Bluefruit.Advertising.addTxPower();
  Bluefruit.Advertising.addAppearance(BLE_APPEARANCE_HID_KEYBOARD);
  Bluefruit.Advertising.addService(bleHid);
  deskble::advertise();Bluefruit.ScanResponse.addName();
  Bluefruit.Advertising.setInterval(32, 244);
  Bluefruit.Advertising.setFastTimeout(30);
  bleStarted = true;
}
}  // namespace

// This module owns these two TinyUSB lifecycle hooks. A USB bus reset/replug
// invalidates queued input even if detach and mount happen between two ticks.
extern "C" void tud_mount_cb(void) { ++usbEpoch; }
extern "C" void tud_umount_cb(void) { ++usbEpoch; }

void DeskHid::begin() {
  if (_begun || (owner && owner != this)) return;
  owner = this;
  _begun = true;
  _route = DeskRoute::Usb;
  _armed = false;
  if (!TinyUSBDevice.isInitialized()) TinyUSBDevice.begin(0);
  usbHid.setPollInterval(2);
  usbHid.setBootProtocol(HID_ITF_PROTOCOL_NONE);
  usbHid.setReportDescriptor(UsbReportDescriptor, sizeof(UsbReportDescriptor));
  usbHid.setStringDescriptor("Desk Buddy keyboard and media");
  usbStarted = usbHid.begin();
  if (usbStarted && TinyUSBDevice.mounted()) {
    // Registering HID after enumeration requires a new descriptor exchange.
    // Finish this detach in tick(), without a delay or serial-monitor wait.
    TinyUSBDevice.detach();
    usbAttachPending = true;
    usbDetachAt = millis();
  }
  _observedEpoch = epochFor(_route);
  _linkReady = connected();
}

bool DeskHid::usbConnected() const {
  return _begun && usbStarted && !usbAttachPending &&
         TinyUSBDevice.mounted() && !TinyUSBDevice.suspended();
}

bool DeskHid::bleConnected() const {
  return _begun && bleStarted && bleHid.reportReady(bleHandle(), ConsumerId);
}

DeskBleStatus DeskHid::bleStatus() const {
  DeskBleStatus status;
  status.epochBefore = bleEpoch;
  status.attempted = bleAttempted;
  status.started = bleStarted;
  status.keyboard.reportId = KeyboardId;
  status.consumer.reportId = ConsumerId;
  if (bleStarted) {
    status.connectionHandle = bleHandle();
    status.bootMode = bleHid.isBootMode();
    BLEConnection* conn = Bluefruit.Connection(status.connectionHandle);
    if (conn) {
      status.link = conn->connected();
      status.secured = conn->secured();
      status.bonded = conn->bonded();
      const ble_gap_conn_sec_mode_t sec = conn->getSecureMode();
      status.securityMode = sec.sm;
      status.securityLevel = sec.lv;
    }
    status.keyboard = bleHid.reportStatus(status.connectionHandle, KeyboardId, status.link);
    status.consumer = bleHid.reportStatus(status.connectionHandle, ConsumerId, status.link);
  }
  status.epochAfter = bleEpoch;
  // This detects GAP generation changes, not a guarantee that the peer cannot
  // change security/CCCD values between the individual read-only calls.
  status.consistent = status.epochBefore == status.epochAfter;
  return status;
}

bool DeskHid::connected() const {
  return _route == DeskRoute::Usb ? usbConnected() : bleConnected();
}

DeskRoute DeskHid::route() const { return _route; }
bool DeskHid::armed() const { return _armed; }
uint32_t DeskHid::sentCount() const { return _sent; }
uint32_t DeskHid::droppedCount() const { return _dropped; }

void DeskHid::discardQueue() {
  _dropped += _count;
  _count = 0;
  _head = 0;
}

bool DeskHid::activeSessionExists() const {
  if (!_active || _pressedEpoch != epochFor(_pressedRoute)) return false;
  if (_pressedRoute == DeskRoute::Usb) {
    // Preserve only the zero-release obligation across suspend. No press is
    // retained; refreshLink() disarms and discards the queue on loss of ready.
    return usbStarted && TinyUSBDevice.mounted();
  }
  if (!bleStarted || _pressedHandle != bleHandle()) return false;
  BLEConnection* conn = Bluefruit.Connection(_pressedHandle);
  return conn && conn->connected();
}

void DeskHid::clear() {
  _armed = false;
  discardQueue();
  if (_active) _releaseRequested = true;
}

void DeskHid::releaseForConfiguration() {
  refreshLink();
  discardQueue();
  if (_active) _releaseRequested = true;
}

void DeskHid::refreshLink() {
  const uint32_t epoch = epochFor(_route);
  const bool ready = connected();
  if (epoch != _observedEpoch || (_linkReady && !ready)) clear();
  _observedEpoch = epoch;
  _linkReady = ready;
  if (_active && !activeSessionExists()) {
    // A disconnected host owns the teardown. Never replay its old press or
    // send its pending release to a newly connected host/handle generation.
    _active = false;
    _releaseRequested = false;
    _releaseFault = false;
    clear();
  }
}

void DeskHid::setRoute(DeskRoute next) {
  if (next != DeskRoute::Usb && next != DeskRoute::Ble) return;
  if (next == _route) return;
  clear();
  _route = next;
  if (_begun && next == DeskRoute::Ble) startBle();
  if (bleStarted && next == DeskRoute::Usb && !_managementRadio) Bluefruit.Advertising.stop();
  _observedEpoch = epochFor(_route);
  _linkReady = connected();
}

bool DeskHid::setManagementRadio(bool enable){
  _managementRadio=enable;
  if(enable&&_begun)startBle();
  if(!enable&&bleStarted&&_route==DeskRoute::Usb)Bluefruit.Advertising.stop();
  return bleStarted;
}
bool DeskHid::busy()const{return _active||_count||_releaseRequested;}

void DeskHid::setArmed(bool enable) {
  refreshLink();
  if (!enable) {
    clear();
    return;
  }
  // Changing route/disarming cannot be undone until any old press is released.
  _armed = _begun && connected() && !_releaseRequested && !_releaseFault &&
           (!_active || _pressedRoute == _route);
}

bool DeskHid::enqueue(Kind kind, uint16_t value, uint8_t modifiers) {
  refreshLink();
  const uint8_t id = static_cast<uint8_t>(kind);
  const bool reportUsable = _route != DeskRoute::Ble ||
      (bleStarted && bleHid.reportReady(bleHandle(), id));
  if (!_begun || !_armed || !connected() || !reportUsable ||
      _releaseRequested || _releaseFault || _count == QueueCapacity) {
    ++_dropped;
    return false;
  }
  _queue[(_head + _count) % QueueCapacity] = {kind, value, modifiers, millis()};
  ++_count;
  return true;
}

bool DeskHid::queueConsumer(uint16_t usage) {
  // TinyUSB's consumer descriptor defines the logical range 0..0x03FF.
  // Zero is reserved for release; all configured media actions are nonzero.
  if (usage == 0 || usage > 0x03FF) { ++_dropped; return false; }
  return enqueue(Kind::Consumer, usage, 0);
}

bool DeskHid::queueKey(uint8_t keycode, uint8_t modifiers) {
  // 0..3 are no-key/error reports; modifiers belong in the separate bitmap.
  if (keycode < 4 || keycode >= 0xE0) { ++_dropped; return false; }
  return enqueue(Kind::Keyboard, keycode, modifiers);
}

bool DeskHid::tryReport(const Action& action, bool release,
                        DeskRoute destination, uint16_t handle, uint32_t epoch) {
  if (epoch != epochFor(destination)) return false;
  const uint8_t id = static_cast<uint8_t>(action.kind);
  hid_keyboard_report_t keyboard = {};
  uint16_t consumer = release ? 0 : action.value;
  if (!release && action.kind == Kind::Keyboard) {
    keyboard.modifier = action.modifiers;
    keyboard.keycode[0] = static_cast<uint8_t>(action.value);
  }
  const void* data = action.kind == Kind::Consumer
      ? static_cast<const void*>(&consumer) : static_cast<const void*>(&keyboard);
  const uint8_t length = action.kind == Kind::Consumer ? sizeof(consumer) : sizeof(keyboard);
  if (destination == DeskRoute::Usb) {
    return usbConnected() && usbHid.ready() && usbHid.sendReport(id, data, length);
  }
  return bleStarted && handle == bleHandle() &&
         bleHid.tryReport(handle, id, data, length);
}

void DeskHid::tick(uint32_t now, bool maintainTransport) {
  if (!_begun) return;
  if (maintainTransport && usbAttachPending && static_cast<uint32_t>(now - usbDetachAt) >= 20) {
    TinyUSBDevice.attach();
    usbAttachPending = false;
  }
  if (maintainTransport && bleStarted && (_route == DeskRoute::Ble || _managementRadio) &&
      !Bluefruit.Periph.connected() && !Bluefruit.Advertising.isRunning()) {
    Bluefruit.Advertising.start(0);
  }
  refreshLink();

  if (_active) {
    const uint32_t age = now - _pressedAt;
    if (!_releaseRequested && age < PressMs) return;
    if (tryReport(_pressed, true, _pressedRoute, _pressedHandle, _pressedEpoch)) {
      _active = false;
      _releaseRequested = false;
      _releaseFault = false;
    } else if (age >= ReleaseFaultMs && !_releaseFault) {
      // Keep retrying only the zero report on this same connection. Abandoning
      // a release merely because its deadline passed could leave a stuck key.
      _releaseFault = true;
      clear();
    }
    return;  // At most one transport attempt per tick, including releases.
  }

  // Enforce release-only in this API, even if a caller forgot to disarm.
  if (!maintainTransport || !_armed || !connected()) return;
  while (_count && static_cast<uint32_t>(now - _queue[_head].queuedAt) >= MaxQueueAgeMs) {
    _head = (_head + 1) % QueueCapacity;
    --_count;
    ++_dropped;
  }
  if (!_count) return;
  const Action& next = _queue[_head];
  const uint32_t epoch = epochFor(_route);
  const uint16_t handle = _route == DeskRoute::Ble ? bleHandle() : BLE_CONN_HANDLE_INVALID;
  if (!tryReport(next, false, _route, handle, epoch)) return;
  _pressed = next;
  _pressedRoute = _route;
  _pressedHandle = handle;
  _pressedEpoch = epoch;
  _pressedAt = now;
  _active = true;
  _releaseRequested = false;
  _releaseFault = false;
  _head = (_head + 1) % QueueCapacity;
  --_count;
  ++_sent;
}
