// Desk Buddy: generic media controller, not a host playback-state monitor.
// USB is the default route. Output requires an explicit ON after each boot,
// route change or disconnect. Calibration and UI language use separate records.
// DESK_UI_R2_INTEGRATED
#include <Arduino.h>
#include <Adafruit_TinyUSB.h>
#include <Wire.h>
#include <SPI.h>
#include <Adafruit_GFX.h>
#include <Adafruit_ILI9341.h>
#include <Adafruit_TSC2007.h>
#include <Adafruit_seesaw.h>
#include "AppConfig.h"
#include "ControllerCore.h"
#include "DeskHid.h"
#include "CalibrationStore.h"
#include "BatteryGauge.h"
#include "UiPreferencesStore.h"
#include "DeskUi.h"
#include "HubConfig.h"
#include "HubConfigStore.h"
#include "HubProtocol.h"
#include "PetState.h"
#include "AnimationPreview.h"
#include "BleManagement.h"
#include "RebootGuard.h"
#include "UsbConsole.h"
#include "TouchGesture.h"
#include "CooperativeGfx.h"
#include "LanguageSaveTask.h"
#include "I2cBusClear.h"
#include "DeskEncoder.h"
#include "DisplaySleep.h"
#include "DisplayPreferencesStore.h"

#if !defined(DESK_WIRE_BOUNDED_MASTER)
#error "Use --library firmware/libraries/DeskWire for bounded I2C transactions"
#endif

#if !defined(ARDUINO_NRF52840_FEATHER) || !NRFX_SPIM2_ENABLED
#error "Build for Adafruit Feather nRF52840 Express with SPIM2 support"
#endif

uint8_t deskBusBefore=0,deskBusPulses=0;
bool i2cBusReady=true;
uint32_t i2cTimeoutSeen=0,i2cRecoveries=0;
using deskcore::Action;
using deskui::TextId;
using deskview::Page;
SPIClass deskSPI(NRF_SPIM2,PIN_SPI_MISO,PIN_SPI_SCK,PIN_SPI_MOSI);
Adafruit_ILI9341 display(&deskSPI,appcfg::TftDc,appcfg::TftCs,-1);
Adafruit_TSC2007 touch;
DeskEncoder encoder(&Wire);
DeskHid hid;
deskdisplay::State displaySleep;
deskdisplay::Settings displaySettings;
bool displaySaved=false,displaySavePending=false,displaySaveOkay=false;
bool displayClearPending=false,displayFullRedraw=false;
const char* displaySaveError="none";
deskconfig::Runtime hubConfig;
deskpet::State petState;
deskdemo::State animationPreview;
deskmanage::Lease managementLease;
deskmanage::Source hubSource=deskmanage::Source::Usb;
bool hubMutation=false;
bool serialHubMode=false; // USB Hub polls STATUS; suppress unsolicited 1KB/s stream.
Print& hubOutput(){return hubSource==deskmanage::Source::Ble?deskble::response():deskusb::out();}
deskcore::TrackedButton keys[6];
deskcore::DebouncedButton knobButton;
deskcore::TouchGesture touchGesture;
deskcore::TouchGesture::Event pendingTouch[4];
uint8_t touchQueueHead=0,touchQueueCount=0;
uint32_t touchQueueDropped=0;
uint16_t touchContext=0;
uint32_t lastTouchPoll=0,touchPollMaxGap=0,touchAccepted=0,touchInvalid=0;
bool rendering=false,paintingLayout=false;
deskcore::EncoderDelta encoderDelta;
deskcore::TouchCalibration calibration={0,0,0,0,false};
bool encoderOk=false,touchOk=false,calibrated=false,calSaved=false;
int8_t encoderSign=1;
int32_t encoderPosition=0, rotationTotal=0;
uint32_t knobPresses=0,markers=0,lastPoll=0,lastProbe=0,lastUI=0,lastStatus=0;
uint32_t clockwiseCounts=0,counterClockwiseCounts=0,calReleaseAt=0;
uint32_t lastActionAt=0,lastTouchAt=0,swHoldAt=0;
uint32_t rawSampleCount=0;
uint32_t lastBatterySample=0;
uint32_t uiLastMicros=0,uiMaxMicros=0;
uint16_t vbatRaw=0,vbatMillivolts=0;
bool vbatSampled=false;
uint16_t rawX=0,rawY=0,rawZ=0;
int16_t touchX=0,touchY=0;
bool rawDown=false,uiDirty=true,serialBanner=false,userSwHeld=false,userSwHandled=false;
deskview::Renderer uiRenderer;
deskbattery::Gauge batteryGauge;
deskui::Language language=deskui::Language::Chinese;
bool languageSaved=false,languagePending=false,languageWorkerReady=false;
uint32_t languageRequestedAt=0,languageSaveLastUs=0;
const char* languageSaveError="none";
Page page=Page::Home;
TextId lastNote=TextId::OutputOff;
bool lastActionBlocked=false;
char lastCommand[512]; uint16_t commandLength=0; bool overflow=false;
// Five on-screen targets, inset from the glass edge. ADC axes are discovered
// from these observations, not guessed from an earlier unlabelled touch test.
constexpr int16_t calScreenX[5]={20,299,299,20,160};
constexpr int16_t calScreenY[5]={20,20,219,219,120};
int32_t calRawX[5],calRawY[5];
uint8_t calStage=0,calSamples=0;
int32_t calSumX=0,calSumY=0;
uint16_t calMinX=4095,calMaxX=0,calMinY=4095,calMaxY=0;
bool calNeedRelease=true,calError=false;
uint32_t calTargetAt=0;

bool addressResponds(uint8_t address) {
  if(!i2cBusReady)return false;
  Wire.beginTransmission(address); return Wire.endTransmission()==0;
}
void note(TextId id) {
  lastNote=id;lastActionBlocked=false;lastActionAt=millis();uiDirty=true;
}
bool checkI2cTimeout() {
  const uint32_t count=Wire.timeoutCount();
  if(count==i2cTimeoutSeen)return false;
  i2cTimeoutSeen=count;i2cBusReady=false;encoderOk=touchOk=false;
  hid.setArmed(false);rawDown=false;touchQueueHead=touchQueueCount=0;
  touchGesture.blockUntilRelease(millis(),touchContext);note(TextId::CheckDevices);
  return true;
}
bool readEncoderSnapshot(int32_t& position,bool& down) {
  const bool okay=encoder.checkedSnapshot(position,down,appcfg::EncoderSwitch);
  if(checkI2cTimeout())return false;
  if(!okay){encoderOk=false;hid.setArmed(false);note(TextId::EncoderMissing);}
  return okay;
}
bool recoverI2cBus() {
  Wire.end();const auto bus=deskbus::clear();
  i2cBusReady=bus.ready;deskBusBefore=(bus.initialSda?2:0)|(bus.initialScl?1:0);deskBusPulses=bus.pulses;
  Wire.begin();Wire.setClock(appcfg::I2cHz);Wire.setTransactionTimeoutUs(20000);
  if(bus.ready)++i2cRecoveries;
  return bus.ready;
}
void cancelAnimation(){if(animationPreview.stop())uiDirty=true;}
void recordPetInput(uint32_t now,uint32_t count=1,deskpet::Event event=deskpet::Event::Press) {
  if(count){displaySleep.activity(now);managementLease.clear();cancelAnimation();if(page!=Page::Calibration){petState.record(now,count,event);uiDirty=true;}}
}
bool inputTestPage(){return page==Page::Diagnostics||page==Page::Activity;}
bool hidSuppressed(){return page==Page::Calibration||inputTestPage()||displaySleep.suppressed();}
void backlight(bool on) {
  if(!appcfg::BacklightControl)return;
  if(on)pinMode(appcfg::BacklightPin,INPUT);
  else {digitalWrite(appcfg::BacklightPin,LOW);pinMode(appcfg::BacklightPin,OUTPUT);}
}
void displayWoke() {
  displayClearPending=false;displayFullRedraw=true;uiDirty=true;backlight(true);
  ++touchContext;touchQueueHead=touchQueueCount=0;
  touchGesture.blockUntilRelease(millis(),touchContext);
}
void wakeDisplay() {if(displaySleep.wake(millis()))displayWoke();}
bool physicalContactHeld();
void sleepDisplay() {
  if(!displaySleep.sleep(millis(),physicalContactHeld()))return;
  cancelAnimation();hid.releaseForConfiguration(); // preserves armed; clears every unsent action
  ++touchContext;touchQueueHead=touchQueueCount=0;
  touchGesture.blockUntilRelease(millis(),touchContext);
  displayClearPending=true;backlight(false);
}
void serviceDisplay();
void serviceTouch(uint32_t now);
void rebasePhysicalInputs();
void navigate(Page next) {
  wakeDisplay();
  if(next==page)return;
  if(inputTestPage()&&next!=Page::Diagnostics&&next!=Page::Activity)rebasePhysicalInputs();
  if(next!=Page::Home)cancelAnimation();
  if(next==Page::Diagnostics||next==Page::Activity||next==Page::Calibration)
    hid.releaseForConfiguration(); // discard pending host actions, keep user's ON/OFF
  page=next;++touchContext;touchQueueHead=touchQueueCount=0;
  touchGesture.blockUntilRelease(millis(),touchContext);uiDirty=true;
}
bool languageSaving(){return languagePending||desklanguage::busy();}
void languageSaveComplete() {
  desklanguage::Result result;
  if(!desklanguage::take(result))return;
  if(result.kind==desklanguage::Kind::Display){
    displaySavePending=false;displaySaveOkay=result.okay;displaySaveError=result.error;
    if(result.okay){displaySettings=result.display;displaySaved=true;displaySleep.activity(millis());}
    uiDirty=true;return;
  }
  languageSaveLastUs=result.elapsedUs;
  languageSaveError=result.error;
  // A newer finger selection may already be waiting; never label it as saved.
  if(result.language==static_cast<uint8_t>(language)) {
    languageSaved=result.okay;languagePending=false;
    note(result.okay?TextId::LanguageSaved:TextId::LanguageRam);
  }
  uiDirty=true;
}
void requestLanguage(deskui::Language next) {
  wakeDisplay();
  if(page==Page::Calibration){note(TextId::TestOutputOff);return;}
  if(next==language&&languageSaved&&!languageSaving())return;
  language=next;languageSaved=false;languagePending=true;
  languageRequestedAt=millis();uiDirty=true;
}
void serviceLanguage() {
  languageSaveComplete();
  // Draw the selection first. Coalesce quick choices and start after release.
  if(!languagePending||desklanguage::busy()||touchGesture.held()||
      static_cast<uint32_t>(millis()-languageRequestedAt)<150)return;
  if(!languageWorkerReady||!desklanguage::start(static_cast<uint8_t>(language))) {
    languagePending=false;note(TextId::LanguageRam);
  }else languagePending=false;
}
bool setLanguage(deskui::Language next) {
  wakeDisplay();
  // Hub LANGUAGE retains its saved=1 acknowledgement contract. Touch/legacy
  // choices use requestLanguage and never wait for flash on the UI loop.
  languageSaveComplete();
  if(languageSaving())return false;
  if(next!=language||!languageSaved){
    if(!languageWorkerReady||!desklanguage::start(static_cast<uint8_t>(next)))return false;
    language=next;languageSaved=false;uiDirty=true;
    const uint32_t began=millis();
    while(desklanguage::busy()&&static_cast<uint32_t>(millis()-began)<2500){
      serviceDisplay();languageSaveComplete();delay(1);
    }
    if(desklanguage::busy())return false;
  }
  note(languageSaved?TextId::LanguageSaved:TextId::LanguageRam);
  return true;
}
const char* setDisplaySettings(const deskdisplay::Settings& settings) {
  languageSaveComplete();
  if(languageSaving()||page==Page::Calibration)return "BUSY";
  if(displaySaved&&settings==displaySettings)return nullptr;
  hid.releaseForConfiguration();hid.tick(millis(),false);
  if(hid.busy())return "BUSY";
  if(!languageWorkerReady||!desklanguage::startDisplay(settings))return "BUSY";
  displaySavePending=true;displaySaveOkay=false;
  const uint32_t began=millis();
  while(desklanguage::busy()&&uint32_t(millis()-began)<2500){
    serviceDisplay();languageSaveComplete();delay(1);
  }
  if(desklanguage::busy())return "BUSY";
  return displaySaveOkay?nullptr:"SAVE_FAILED";
}
bool usbVbusPresent() {
  // Supply voltage detection only: not USB enumeration, charge state or a
  // battery-presence test. This register is readable with the SoftDevice on.
  return (NRF_POWER->USBREGSTATUS & POWER_USBREGSTATUS_VBUSDETECT_Msk) != 0;
}
void sampleBattery(uint32_t now) {
  static bool sampledUsbPower=false;
  const bool usbPower=usbVbusPresent();
  displaySleep.power(now,usbPower);
  const bool powerChanged=usbPower!=sampledUsbPower;
  batteryGauge.setUsbVbus(usbPower);
  sampledUsbPower=usbPower;
  if(powerChanged)uiDirty=true;
  if(!powerChanged && vbatSampled && static_cast<uint32_t>(now-lastBatterySample)<appcfg::BatterySampleMs)return;
  // BSP analogRead is a short synchronous conversion, not an asynchronous
  // ADC. Only scheduling is non-waiting; no 1 s delay is added to the loop.
  // BSP 1.7.0 has no timeout inside analogRead; the existing runtime WDT is
  // the last resort if its peripheral-event wait stalls.
  vbatRaw=static_cast<uint16_t>(analogRead(PIN_VBAT));
  vbatMillivolts=deskcore::vbatEstimateMillivolts(vbatRaw);
  lastBatterySample=millis();vbatSampled=true;
  batteryGauge.observe(vbatMillivolts,lastBatterySample);
}
void printDisplayFields() {
  hubOutput().print("display_asleep=");hubOutput().print(displaySleep.asleep());
  hubOutput().print(" display_usb_s=");hubOutput().print(displaySettings.usbSeconds);
  hubOutput().print(" display_battery_s=");hubOutput().print(displaySettings.batterySeconds);
  hubOutput().print(" display_saved=");hubOutput().print(displaySaved);
  hubOutput().print(" display_backlight=");hubOutput().print(appcfg::BacklightControl);
  hubOutput().print(" display_wake_count=");hubOutput().print(displaySleep.wakeCount());
  hubOutput().print(" display_idle_ms=");hubOutput().print(displaySleep.idle(millis()));
}
void printStatusFields() {
  hubOutput().print("build=");hubOutput().print(appcfg::BuildId);
  hubOutput().print(" boot_reset_reason=");hubOutput().print(readResetReason());
  hubOutput().print(" usb_tx_dropped=");hubOutput().print(deskusb::droppedLines());
  hubOutput().print(" usb_tx_timeouts=");hubOutput().print(deskusb::txTimeouts());
  hubOutput().print(" usb_rx_overflows=");hubOutput().print(deskusb::rxOverflows());
  hubOutput().print(" usb_session=");hubOutput().print(deskusb::generation());
  hubOutput().print(" usb_tx_queued=");hubOutput().print(deskusb::queuedBytes());
  hubOutput().print(" usb_tx_stalled=");hubOutput().print(deskusb::stalled());
  hubOutput().print(" uptime_ms=");hubOutput().print(millis());
  hubOutput().print(" route=");hubOutput().print(hid.route()==DeskRoute::Usb?"USB":"BLE");
  hubOutput().print(" connected=");hubOutput().print(hid.connected());
  hubOutput().print(" armed=");hubOutput().print(hid.armed());
  hubOutput().print(" hid_suppressed=");hubOutput().print(hidSuppressed());
  hubOutput().print(' ');printDisplayFields();
  hubOutput().print(" encoder=");hubOutput().print(encoderOk);
  hubOutput().print(" touch=");hubOutput().print(touchOk);
  hubOutput().print(" calibrated=");hubOutput().print(calibrated);
  hubOutput().print(" stored=");hubOutput().print(calSaved);
  hubOutput().print(" enc=");hubOutput().print(encoderPosition);
  hubOutput().print(" rotation=");hubOutput().print(rotationTotal);
  hubOutput().print(" presses=");hubOutput().print(knobPresses);
  hubOutput().print(" cw=");hubOutput().print(clockwiseCounts);
  hubOutput().print(" ccw=");hubOutput().print(counterClockwiseCounts);
  hubOutput().print(" switch_down=");hubOutput().print(knobButton.pressed());
  hubOutput().print(" sent=");hubOutput().print(hid.sentCount());
  hubOutput().print(" dropped=");hubOutput().print(hid.droppedCount());
  hubOutput().print(" page=");hubOutput().print(static_cast<uint8_t>(page));
  hubOutput().print(" keys_down=");for(uint8_t i=0;i<6;++i)hubOutput().print(keys[i].pressed()?1:0);
  hubOutput().print(" key_presses=");for(uint8_t i=0;i<6;++i){if(i)hubOutput().print(',');hubOutput().print(keys[i].presses());}
  hubOutput().print(" key_releases=");for(uint8_t i=0;i<6;++i){if(i)hubOutput().print(',');hubOutput().print(keys[i].releases());}
  hubOutput().print(" markers=");hubOutput().print(markers);
  hubOutput().print(" vbat_est_mV=");hubOutput().print(vbatMillivolts);
  hubOutput().print(" vbat_raw=");hubOutput().print(vbatRaw);
  hubOutput().print(" vbat_sampled=");hubOutput().print(vbatSampled);
  hubOutput().print(" vbat_age_ms=");hubOutput().print(vbatSampled?static_cast<uint32_t>(millis()-lastBatterySample):UINT32_MAX);
  hubOutput().print(" usb_vbus=");hubOutput().print(usbVbusPresent());
  hubOutput().print(" language=");hubOutput().print(language==deskui::Language::Chinese?"ZH":"EN");
  hubOutput().print(" language_saved=");hubOutput().print(languageSaved);
  hubOutput().print(" language_saving=");hubOutput().print(languagePending||(desklanguage::busy()&&!displaySavePending));
  hubOutput().print(" language_save_us=");hubOutput().print(languageSaveLastUs);
  hubOutput().print(" i2c_timeouts=");hubOutput().print(Wire.timeoutCount());
  hubOutput().print(" i2c_recoveries=");hubOutput().print(i2cRecoveries);
  hubOutput().print(" i2c_bus_ready=");hubOutput().print(i2cBusReady);
  hubOutput().print(" i2c_clear_before=");hubOutput().print(deskBusBefore);
  hubOutput().print(" i2c_clear_pulses=");hubOutput().print(deskBusPulses);
  hubOutput().print(" touch_poll_max_gap_ms=");hubOutput().print(touchPollMaxGap);
  hubOutput().print(" touch_invalid=");hubOutput().print(touchInvalid);
  hubOutput().print(" touch_accepted=");hubOutput().print(touchAccepted);
  hubOutput().print(" touch_queue_dropped=");hubOutput().print(touchQueueDropped);
  hubOutput().print(" language_save_error=");hubOutput().print(languageSaveError);
  hubOutput().print(" battery_est_percent=");hubOutput().print(batteryGauge.state(millis(),usbVbusPresent()).percent);
  hubOutput().print(" ui_last_us=");hubOutput().print(uiLastMicros);
  hubOutput().print(" ui_max_us=");hubOutput().print(uiMaxMicros);
  hubOutput().print(" usb_connected=");hubOutput().print(hid.usbConnected());
  hubOutput().print(" ble_connected=");hubOutput().print(hid.bleConnected());
  hubOutput().print(" config_revision=");hubOutput().print(hubConfig.revision());
  hubOutput().print(" config_preview=");hubOutput().print(hubConfig.preview());
  hubOutput().print(" profile_index=");hubOutput().print(hubConfig.bank().active());
  hubOutput().print(" theme=");hubOutput().print(hubConfig.bank().profile()[0]);
  hubOutput().print(" face=");hubOutput().print(hubConfig.bank().profile()[1]);
  hubOutput().print(" home_mode=");hubOutput().print(hubConfig.bank().profile()[6]);
  const uint32_t petNow=millis();
  hubOutput().print(" pet_interactions=");hubOutput().print(petState.interactions());
  hubOutput().print(" pet_active=");hubOutput().print(petState.active(petNow));
  hubOutput().print(" pet_motion=");hubOutput().print(static_cast<uint8_t>(petState.motion(petNow)));
  hubOutput().print(" pet_direction=");hubOutput().print(petState.direction(petNow));
  hubOutput().print(" pet_elapsed_ms=");hubOutput().print(petState.elapsed(petNow));
  hubOutput().print(" pet_sleeping=");hubOutput().print(petState.sleeping(petNow));
  hubOutput().print(" pet_effect=");hubOutput().print(petState.effect(petNow));
  hubOutput().print(" pet_taps=");hubOutput().print(petState.taps());
  const auto demo=animationPreview.snapshot(petNow);
  hubOutput().print(" demo_action=");hubOutput().print(static_cast<uint8_t>(demo.action));
  hubOutput().print(" demo_remaining_ms=");hubOutput().print(demo.remaining);
  hubOutput().print(" ble_manage=");hubOutput().print(deskble::state());
  hubOutput().print(" ble_manage_remaining_ms=");hubOutput().print(deskble::remaining(petNow));
  hubOutput().print(" ble_link=");hubOutput().print(deskble::linked());
  hubOutput().print(" ble_secured=");hubOutput().print(deskble::secured());
  hubOutput().print(" ble_bonded=");hubOutput().print(deskble::bonded());
  hubOutput().print(" ble_rx_handle=");hubOutput().print(deskble::rxHandle());
  hubOutput().print(" ble_tx_handle=");hubOutput().print(deskble::txHandle());
  hubOutput().print(" ble_tx_cccd=");hubOutput().print(deskble::txCccdHandle());
  hubOutput().print(" ble_sc_start=");hubOutput().print(deskble::serviceChangedStart());
  hubOutput().print(" ble_sc_end=");hubOutput().print(deskble::serviceChangedEnd());
  hubOutput().print(" ble_sc_result=");hubOutput().print(deskble::serviceChangedResult());
  hubOutput().print(" ble_sc_attempts=");hubOutput().print(deskble::serviceChangedAttempts());
  hubOutput().print(" ble_sc_confirmed=");hubOutput().print(deskble::serviceChangedConfirmed());
  hubOutput().print(" manage_owner=");hubOutput().print(static_cast<uint8_t>(managementLease.owner(petNow)));
  hubOutput().print(" manage_lease_ms=");hubOutput().println(managementLease.remaining(petNow));
}
void printStatus() {if(deskusb::ready()){deskusb::out().print("STATUS ");printStatusFields();}}

void printHelp() {
  deskusb::out().println(appcfg::BuildId);
  deskusb::out().println("HELP STATUS HID ON HID OFF USB BLE CALIBRATE DIAG HOME INFO ACTIVITY LANG ZH LANG EN REBOOT");
  deskusb::out().println("DISPLAY STATUS: USB-only five TFT register reads; no display reset or recovery.");
  deskusb::out().println("BLE STATUS: USB-only link/security and raw HID CCCD snapshot; no attribute writes.");
  deskusb::out().println("ENC NORMAL / ENC REVERSE / COUNTERS RESET");
  deskusb::out().println("HID defaults OFF. Generic PREV/NEXT are track/chapter actions, not time seeks.");
  deskusb::out().println("Default K1..K6: play, previous, next, mute, settings, local marker. Hub profiles may remap these.");
  deskusb::out().println("HUB <id> HELLO/GET/STATUS/PREVIEW/SAVE/REVERT/SELECT/CONTROL; protocol 1, 224-byte bank.");
  deskusb::out().println("HUB <id> DISPLAY GET/SET <usb_s> <battery_s>/SLEEP/WAKE; seconds 0/30 or 60..43200 in whole minutes. First input wakes only; no system power-off.");
  deskusb::out().println("HUB <id> ANIMATE IDLE/BLINK/HAPPY/WALK/TAP/SLEEP/WAKE/STOP: visual preview 6s; preserve output; no save or pet counts.");
  deskusb::out().println("keys_down: K1..K6 left to right, 1=pressed. P/R count stable press/release edges.");
  deskusb::out().println("COUNTERS RESET clears key/knob/rotation/marker/pet counts, not held-key states.");
  deskusb::out().println("Battery ~%: coarse estimate in 5% steps, hidden on USB power. BAT voltage cannot detect battery presence or charging under USB.");
  deskusb::out().println("Pet SESSION count is RAM-only physical interaction count; ACTIVE lasts 1.8s; pet_motion 0=idle 1=happy 2=walk, not health or battery.");
  deskusb::out().println("Tap the Home character to say hello (local only, no HID). 30s inactivity shows a sleeping pose, not power saving.");
  deskusb::out().println("usb_vbus: USB supply present, independent of selected HID route/host connection.");
  deskusb::out().println("Hold board USER SW 2s to stop output. Hold USER SW at reset for calibration.");
}
void startCalibration() {
  cancelAnimation();
  if(languageSaving()){note(TextId::ReleaseThenPress);return;}
  hid.setArmed(false); navigate(Page::Calibration);
  calStage=0;calSamples=0;calSumX=calSumY=0;
  calMinX=calMinY=4095;calMaxX=calMaxY=0;
  calNeedRelease=true;calReleaseAt=0;calError=false;calTargetAt=millis();uiDirty=true;
  if(deskusb::ready())deskusb::out().println("CAL BEGIN: press each displayed cross, then release. Output OFF.");
}
void route(DeskRoute r) {
  wakeDisplay();
  cancelAnimation();
  // Reselecting the current transport is navigation, not a route change.
  if(r!=hid.route()){hid.setRoute(r);hid.setArmed(false);}
  note(r==DeskRoute::Usb?TextId::UsbSelected:TextId::BleSelected);
}
void arm() {
  wakeDisplay();
  cancelAnimation();
  if(hidSuppressed()){note(TextId::TestOutputOff);return;}
  if(!encoderOk || !touchOk || !calibrated) {note(TextId::CheckDevices);return;}
  hid.setArmed(true);note(hid.armed()?TextId::OutputOn:TextId::ConnectHost);
}
void emitAction(Action action) {
  if(action==Action::Mode) {navigate(page==Page::Settings?Page::Home:Page::Settings);note(hid.armed()?TextId::OutputOn:TextId::OutputOff);return;}
  if(action==Action::Marker) {++markers;note(TextId::LocalMarker);if(deskusb::ready()){deskusb::out().print("LOCAL_MARKER ");deskusb::out().println(markers);}return;}
  uint16_t usage=0;
  switch(action) {
    case Action::PlayPause:usage=appcfg::PlayPause;break;
    case Action::Previous:usage=appcfg::Previous;break;
    case Action::Next:usage=appcfg::Next;break;
    case Action::Mute:usage=appcfg::Mute;break;
    case Action::VolumeUp:usage=appcfg::VolumeUp;break;
    case Action::VolumeDown:usage=appcfg::VolumeDown;break;
    default:return;
  }
  const bool queued=hid.queueConsumer(usage);
  switch(action){
    case Action::PlayPause:lastNote=TextId::PlayPause;break;
    case Action::Previous:lastNote=TextId::Previous;break;
    case Action::Next:lastNote=TextId::Next;break;
    case Action::Mute:lastNote=TextId::Mute;break;
    case Action::VolumeUp:lastNote=TextId::VolumeUp;break;
    case Action::VolumeDown:lastNote=TextId::VolumeDown;break;
    default:break;
  }
  lastActionBlocked=!queued;
  if(!queued)lastNote=TextId::ActionBlocked;
  lastActionAt=millis();uiDirty=true;
  if(deskusb::ready()){deskusb::out().print("ACTION ");deskusb::out().print(deskcore::actionLabel(action));deskusb::out().print(" queued=");deskusb::out().println(queued);}
}
void configChanged() {
  wakeDisplay();
  cancelAnimation();
  encoderSign=hubConfig.bank().profile()[3]?-1:1;
  note(hid.armed()?TextId::OutputOn:TextId::OutputOff);
}
void prepareConfigChange(const deskconfig::Bank& next) {
  if(!deskconfig::sameInputMapping(hubConfig.bank(),next)){
    ++touchContext;touchQueueHead=touchQueueCount=0;
    touchGesture.blockUntilRelease(millis(),touchContext);
    hid.releaseForConfiguration();
    hid.tick(millis(),false); // Only release an accepted old action; never send queued input.
  }
}
void selectProfile(uint8_t index) {
  auto next=hubConfig.bank();next.bytes[1]=index;
  prepareConfigChange(next);hubConfig.select(index);configChanged();
}
void executeBinding(uint8_t index) {
  if(hidSuppressed())return;
  const deskconfig::Binding b=hubConfig.bank().binding(index);
  if(b.kind==0)return;
  if(b.kind==3){
    if(b.value==1)emitAction(Action::Mode);
    else if(b.value==2)emitAction(Action::Marker);
    else if(b.value==3){hid.setArmed(false);note(TextId::OutputOff);}
    else if(b.value==4)selectProfile((hubConfig.bank().active()+1)%3);
    else if(b.value==5)sleepDisplay();
    return;
  }
  if(b.kind==1){
    Action action=Action::None;
    switch(b.value){case 0xCD:action=Action::PlayPause;break;case 0xB6:action=Action::Previous;break;
      case 0xB5:action=Action::Next;break;case 0xE2:action=Action::Mute;break;
      case 0xE9:action=Action::VolumeUp;break;case 0xEA:action=Action::VolumeDown;break;}
    emitAction(action);return;
  }
  const bool queued=hid.queueKey(static_cast<uint8_t>(b.value),b.modifiers);
  note(queued?TextId::Queued:TextId::ActionBlocked);lastActionBlocked=!queued;
  if(deskusb::ready()){deskusb::out().print("BINDING index=");deskusb::out().print(index);deskusb::out().print(" kind=KEYBOARD queued=");deskusb::out().println(queued);}
}
void hubPrefix(uint32_t id,bool okay){if(okay&&hubSource==deskmanage::Source::Ble)deskble::touch(millis());if(okay&&hubMutation)managementLease.commit(hubSource,millis());hubOutput().print("HUB ");hubOutput().print(id);hubOutput().print(okay?" OK ":" ERR code=");}
void hubError(uint32_t id,const char* code){hubPrefix(id,false);hubOutput().println(code);}
void hubRevision(){hubOutput().print("revision=");hubOutput().print(hubConfig.revision());hubOutput().print(" preview=");hubOutput().print(hubConfig.preview());}
void resetCounters();
bool manageCommand(bool enable){
  managementLease.clear();cancelAnimation();
  if(enable&&!hid.setManagementRadio(true))return false;
  deskble::tick(millis(),hid.busy());deskble::authorize(enable,millis());
  hid.setManagementRadio(deskble::radioWanted());uiDirty=true;return true;
}
void hubCommand(char* line) {
  hubMutation=false;
  deskhub::Request r;const char* error=deskhub::parse(line,r);
  if(error){hubError(r.id,error);return;}
  using V=deskhub::Verb;
  if(hubSource==deskmanage::Source::Ble){
    if(r.verb==V::Manage&&r.value){hubError(r.id,"FORBIDDEN");return;}
    if(!deskble::allowed()){hubError(r.id,"LOCKED");return;}
  }
  const bool mutation=r.verb==V::Preview||r.verb==V::Save||r.verb==V::Select;
  if(mutation&&r.revision!=hubConfig.revision()){hubError(r.id,"STALE");return;}
  if((mutation||r.verb==V::Revert)&&page==Page::Calibration){hubError(r.id,"BUSY");return;}
  const bool emergency=r.verb==V::Output&&!r.value;
  hubMutation=r.verb!=V::Hello&&r.verb!=V::Get&&r.verb!=V::Status&&r.verb!=V::DisplayGet&&r.verb!=V::Manage&&!emergency;
  if(hubMutation&&!managementLease.available(hubSource,millis())){hubMutation=false;hubError(r.id,"BUSY");return;}
  if(emergency)managementLease.clear();
  switch(r.verb){
    case V::Manage:
      if(hubSource==deskmanage::Source::Ble){
        // Closing the companion management channel must not stop media HID.
        managementLease.clear(deskmanage::Source::Ble);deskble::authorize(false,millis());uiDirty=true;
      }else if(!manageCommand(r.value)){hubError(r.id,"NOT_READY");return;}
      hubPrefix(r.id,true);hubOutput().print("ble_manage=");hubOutput().print(deskble::state());
      hubOutput().print(" ble_manage_remaining_ms=");hubOutput().print(deskble::remaining(millis()));hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::Hello:
      hubPrefix(r.id,true);hubOutput().print("proto=1 build=");hubOutput().print(appcfg::BuildId);
      hubOutput().print(" display_sleep=1 display_custom=1 sleep_binding=1 backlight_control=");hubOutput().print(appcfg::BacklightControl);
      hubOutput().print(" config_bytes=224 profiles=3 bindings=12 theme_count=8 face_count=12 home_mode_count=3 animation_preview=1 preserve_output=1 pet_input_feedback=1 preserve_page_output=1 ble_management=1 ble_manage_idle_ms=15000 ");hubRevision();hubOutput().println();return;
    case V::Get:{
      char hex[deskconfig::BankSize*2+1];deskconfig::toHex(hubConfig.bank(),hex);
      hubPrefix(r.id,true);hubRevision();hubOutput().print(" hex=");hubOutput().println(hex);return;}
    case V::Status:hubPrefix(r.id,true);printStatusFields();return;
    case V::DisplayGet:
      hubPrefix(r.id,true);printDisplayFields();hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::DisplaySet:{
      const char* failed=setDisplaySettings(r.display);if(failed){hubError(r.id,failed);return;}
      wakeDisplay();hubPrefix(r.id,true);printDisplayFields();hubOutput().print(" armed=");hubOutput().println(hid.armed());return;}
    case V::DisplaySleep:
      if(page==Page::Calibration){hubError(r.id,"BUSY");return;}
      sleepDisplay();hubPrefix(r.id,true);printDisplayFields();hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::DisplayWake:
      wakeDisplay();hubPrefix(r.id,true);printDisplayFields();hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::Animate:{
      if(r.value&&page==Page::Calibration){hubError(r.id,"BUSY");return;}
      if(r.value==static_cast<uint8_t>(deskdemo::Action::Walk)&&hubConfig.bank().profile()[6]==0){hubError(r.id,"INCOMPATIBLE");return;}
      wakeDisplay();
      if(r.value){
        navigate(Page::Home);
        animationPreview.start(static_cast<deskdemo::Action>(r.value),millis());
      }else cancelAnimation();
      uiDirty=true;hubPrefix(r.id,true);hubOutput().print("demo_action=");hubOutput().print(r.value);
      hubOutput().print(" demo_remaining_ms=");hubOutput().print(r.value?deskdemo::DurationMs:0u);
      hubOutput().print(" armed=");hubOutput().println(hid.armed());return;}
    case V::Preview:case V::Save:
      if(r.verb==V::Save){
        if(languageSaving()){hubError(r.id,"BUSY");return;}
        // Never hold an accepted host key while synchronous flash I/O runs.
        // Preserve the user's output state and retry releases on the normal
        // loop; a busy endpoint yields BUSY with no configuration mutation.
        hid.releaseForConfiguration();hid.tick(millis(),false);
        if(hid.busy()){hubError(r.id,"BUSY");return;}
      }
      if(r.verb==V::Save&&!deskconfigstore::save(r.bank)){hubError(r.id,"SAVE_FAILED");return;}
      prepareConfigChange(r.bank);
      hubConfig.apply(r.bank,r.verb==V::Save);configChanged();
      hubPrefix(r.id,true);hubRevision();if(r.verb==V::Save)hubOutput().print(" saved=1");hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::Revert:prepareConfigChange(hubConfig.persistedBank());hubConfig.revert();configChanged();hubPrefix(r.id,true);hubRevision();hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::Select:selectProfile(r.value);hubPrefix(r.id,true);hubRevision();hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::Output:
      wakeDisplay();
      cancelAnimation();
      if(r.value&&(page==Page::Calibration||inputTestPage())){hubError(r.id,"NOT_READY");return;}
      if(r.value&&displaySleep.suppressed()){hubError(r.id,"BUSY");return;}
      if(r.value)arm();else {hid.setArmed(false);note(TextId::OutputOff);}
      if(r.value&&!hid.armed()){hubError(r.id,"NOT_READY");return;}
      hubPrefix(r.id,true);hubOutput().print("armed=");hubOutput().println(hid.armed());return;
    case V::Route:
      route(r.value?DeskRoute::Ble:DeskRoute::Usb);hubPrefix(r.id,true);
      hubOutput().print("route=");hubOutput().print(r.value?"BLE":"USB");hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::Language:
      if(page==Page::Calibration){hubError(r.id,"BUSY");return;}
      if(!setLanguage(static_cast<deskui::Language>(r.value))){hubError(r.id,"BUSY");return;}
      if(!languageSaved){hubError(r.id,"SAVE_FAILED");return;}
      hubPrefix(r.id,true);hubOutput().print("language=");hubOutput().print(r.value?"EN":"ZH");hubOutput().print(" saved=1 armed=");hubOutput().println(hid.armed());return;
    case V::Page:
      if(r.value!=0)cancelAnimation();
      navigate(static_cast<Page>(r.value));
      hubPrefix(r.id,true);hubOutput().print("page=");hubOutput().print(r.value);hubOutput().print(" armed=");hubOutput().println(hid.armed());return;
    case V::Counters:wakeDisplay();resetCounters();hubPrefix(r.id,true);hubOutput().println("reset=1");return;
    default:hubError(r.id,"UNSUPPORTED");return;
  }
}
void serviceDisplay(){hid.tick(millis());NRF_WDT->RR[0]=0x6E524635;serviceTouch(millis());delay(1);}
void renderUi(bool full) {
  // ADC and input handling can finish after the loop's timestamp was captured.
  // Snapshot time here so a fresh sample cannot look stale after unsigned wrap.
  const uint32_t now=millis();
  if(page!=Page::Home)cancelAnimation();
  deskview::Model m;
  m.page=page;m.language=language;m.languageSaved=languageSaved;
  m.languageSaving=languagePending||(desklanguage::busy()&&!displaySavePending);m.hidSuppressed=hidSuppressed();
  m.manageState=deskble::state();m.manageRemaining=static_cast<uint16_t>((deskble::remaining(now)+999)/1000);
  const uint8_t* profile=hubConfig.bank().profile();
  m.theme=profile[0];m.face=profile[1];m.animation=profile[2];m.accent=profile[4]|(uint16_t(profile[5])<<8);
  m.homeMode=profile[6];m.petInteractions=petState.interactions();m.petActive=petState.active(now);m.petMotion=static_cast<uint8_t>(petState.motion(now));
  m.petSleeping=petState.sleeping(now);m.petEffect=petState.effect(now);
  m.petDirection=petState.direction(now);m.petElapsed=petState.elapsed(now);
  const auto demo=animationPreview.snapshot(now);m.demoAction=static_cast<uint8_t>(demo.action);m.demoElapsed=demo.elapsed;
  m.configRevision=hubConfig.revision();memcpy(m.profileName,profile+8,16);
  for(uint8_t i=0;i<3;++i){const auto b=hubConfig.bank().binding(9+i);m.mediaKinds[i]=b.kind;m.mediaValues[i]=b.value;}

  m.ble=hid.route()==DeskRoute::Ble;m.connected=hid.connected();m.armed=hid.armed();
  m.usbPower=usbVbusPresent();m.batteryPercent=batteryGauge.state(now,m.usbPower).percent;
  m.batteryMv=vbatMillivolts;m.encoderOk=encoderOk;m.touchOk=touchOk;
  m.calibrated=calibrated;m.calSaved=calSaved;m.now=now;m.uptimeSeconds=now/1000;m.uiMillis=uiLastMicros/1000;
  m.message=lastNote;m.actionActive=now-lastActionAt<1800;m.actionBlocked=lastActionBlocked;
  m.cw=clockwiseCounts;m.ccw=counterClockwiseCounts;m.knobPresses=knobPresses;m.knobDown=knobButton.pressed();
  m.sent=hid.sentCount();m.dropped=hid.droppedCount();m.markers=markers;
  m.touchX=touchX;m.touchY=touchY;m.rawX=rawX;m.rawY=rawY;
  for(uint8_t i=0;i<6;++i){m.keyDown[i]=keys[i].pressed();m.keyPresses[i]=keys[i].presses();m.keyReleases[i]=keys[i].releases();}
  m.calStage=calStage;m.calNeedRelease=calNeedRelease;m.calError=calError;
  const uint32_t began=micros();
  deskview::CooperativeGfx responsiveDisplay(display,serviceDisplay);
  responsiveDisplay.setTextWrap(false);rendering=true;paintingLayout=full;
  uiRenderer.draw(responsiveDisplay,m,full,serviceDisplay);
  rendering=false;paintingLayout=false;
  uiLastMicros=micros()-began;if(uiLastMicros>uiMaxMicros)uiMaxMicros=uiLastMicros;
}
bool finishCalibration() {
  const int32_t dxX=abs(calRawX[1]-calRawX[0])+abs(calRawX[2]-calRawX[3]);
  const int32_t dxY=abs(calRawY[1]-calRawY[0])+abs(calRawY[2]-calRawY[3]);
  const bool swap=dxY>dxX;
  int32_t x[5],y[5];for(uint8_t i=0;i<5;++i){x[i]=swap?calRawY[i]:calRawX[i];y[i]=swap?calRawX[i]:calRawY[i];}
  int32_t left=(x[0]+x[3])/2,right=(x[1]+x[2])/2,top=(y[0]+y[1])/2,bottom=(y[2]+y[3])/2;
  if(abs(right-left)<700 || abs(bottom-top)<700)return false;
  // Extrapolate from the inset targets to display coordinates 0..319 / 0..239.
  deskcore::TouchCalibration candidate={left-(right-left)*20/279,right+(right-left)*20/279,top-(bottom-top)*20/199,bottom+(bottom-top)*20/199,swap};
  for(uint8_t i=0;i<5;++i){int16_t px,py;if(!deskcore::mapTouch(calRawX[i],calRawY[i],candidate,px,py)||abs(px-calScreenX[i])>22||abs(py-calScreenY[i])>22)return false;}
  hid.setArmed(false);calibration=candidate;calibrated=true;calSaved=deskstore::save(calibration,encoderSign);
  if(deskusb::ready()){deskusb::out().print("CAL PASS swap=");deskusb::out().print(swap);deskusb::out().print(" saved=");deskusb::out().println(calSaved);}
  navigate(Page::Home);note(calSaved?TextId::CalibrationSaved:TextId::CalibrationNotSaved);return true;
}
void calibrationSample(bool down,uint32_t now) {
  if(calNeedRelease){
    // Invalid coordinate samples are not evidence that the finger was lifted.
    if(digitalRead(appcfg::TouchIrq)==HIGH){if(!calReleaseAt)calReleaseAt=now;else if(now-calReleaseAt>=50&&now-calTargetAt>300){calNeedRelease=false;uiDirty=true;}}
    else calReleaseAt=0;
    return;
  }
  if(!down){calSamples=0;calSumX=calSumY=0;calMinX=calMinY=4095;calMaxX=calMaxY=0;return;}
  calMinX=min(calMinX,rawX);calMaxX=max(calMaxX,rawX);calMinY=min(calMinY,rawY);calMaxY=max(calMaxY,rawY);
  calSumX+=rawX;calSumY+=rawY;++calSamples;
  if(calSamples<10)return;
  if(calMaxX-calMinX>170||calMaxY-calMinY>170){calSamples=0;calSumX=calSumY=0;calMinX=calMinY=4095;calMaxX=calMaxY=0;return;}
  calRawX[calStage]=calSumX/calSamples;calRawY[calStage]=calSumY/calSamples;
  if(deskusb::ready()){deskusb::out().print("CAL POINT ");deskusb::out().print(calStage);deskusb::out().print(" raw=");deskusb::out().print(calRawX[calStage]);deskusb::out().print(',');deskusb::out().println(calRawY[calStage]);}
  ++calStage;
  if(calStage==5){if(finishCalibration())return;calStage=0;calError=true;}
  calSamples=0;calSumX=calSumY=0;calMinX=calMinY=4095;calMaxX=calMaxY=0;calNeedRelease=true;calReleaseAt=0;calTargetAt=now;uiDirty=true;
}
void resetCounters(){petState.reset();rotationTotal=0;knobPresses=0;clockwiseCounts=0;counterClockwiseCounts=0;markers=0;for(auto& key:keys)key.resetCounters();uiDirty=true;}
void touchAction(uint16_t target) {
  managementLease.clear();
  cancelAnimation();
  if(target>=100&&target<=102){executeBinding(9+target-100);return;}
  switch(static_cast<deskview::Command>(target)){
    case deskview::Command::PetInteract:petState.affection(millis());note(TextId::PetHello);break;
    case deskview::Command::Settings:navigate(Page::Settings);break;
    case deskview::Command::Home:navigate(Page::Home);break;
    case deskview::Command::Back:navigate(page==Page::Settings?Page::Home:(page==Page::Language||page==Page::Tools?Page::Settings:Page::Tools));break;
    case deskview::Command::Language:navigate(Page::Language);break;
    case deskview::Command::Tools:navigate(Page::Tools);break;
    case deskview::Command::Usb:route(DeskRoute::Usb);break;
    case deskview::Command::Ble:route(DeskRoute::Ble);break;
    case deskview::Command::ToggleOutput:if(hid.armed()){hid.setArmed(false);note(TextId::OutputOff);}else arm();break;
    case deskview::Command::Chinese:requestLanguage(deskui::Language::Chinese);break;
    case deskview::Command::English:requestLanguage(deskui::Language::English);break;
    case deskview::Command::Diagnostics:navigate(Page::Diagnostics);break;
    case deskview::Command::Information:navigate(Page::Information);break;
    case deskview::Command::Activity:navigate(Page::Activity);break;
    case deskview::Command::Management:navigate(Page::Management);break;
    case deskview::Command::ToggleManagement:manageCommand(deskble::state()==0);break;
    case deskview::Command::Calibrate:startCalibration();break;
    case deskview::Command::ResetCounters:resetCounters();note(TextId::CountersReset);break;
    default:break;
  }
}
void rebasePhysicalInputs() {
  const uint32_t now=millis();
  for(uint8_t i=0;i<6;++i)keys[i].rebase(digitalRead(appcfg::ButtonPins[i])==LOW,now);
  if(encoderOk&&addressResponds(appcfg::EncoderAddress)){
    bool down=false;int32_t position=0;
    if(readEncoderSnapshot(position,down)){encoderPosition=position;encoderDelta.reset(position);knobButton.reset(down,millis());}
  }
}
void pollInputs(uint32_t now) {
  // Read/debounce the complete input batch before dispatch. If one input wakes
  // the screen, every other input in this batch is consumed too.
  deskcore::ButtonEdge edges[6];bool event=false,held=rawDown||userSwHeld;
  for(uint8_t i=0;i<6;++i){
    const bool down=digitalRead(appcfg::ButtonPins[i])==LOW;
    edges[i]=keys[i].update(down,now);held=held||down||keys[i].pressed();
    event=event||edges[i]==deskcore::ButtonEdge::Pressed;
  }
  int32_t delta=0;bool knobPress=false,knobReleased=false,knobDown=false;
  if(encoderOk){
    if(!addressResponds(appcfg::EncoderAddress)){encoderOk=false;hid.setArmed(false);note(TextId::EncoderMissing);}
    else {
      int32_t position=0;
      if(readEncoderSnapshot(position,knobDown)){
        encoderPosition=position;delta=encoderDelta.update(position)*encoderSign;
        const bool previous=knobButton.pressed();knobPress=knobButton.update(knobDown,now);
        knobReleased=previous&&!knobButton.pressed();
      }
    }
  }
  held=held||knobDown||knobButton.pressed()||(touchOk&&digitalRead(appcfg::TouchIrq)==LOW);
  event=event||delta!=0||knobPress;
  const bool wasAsleep=displaySleep.asleep();
  const bool allow=displaySleep.batch(now,event,held);
  if(wasAsleep&&!displaySleep.asleep())displayWoke();
  for(uint8_t i=0;i<6;++i){
    if(edges[i]==deskcore::ButtonEdge::None)continue;
    managementLease.clear();
    cancelAnimation();
    uiDirty=true;
    if(edges[i]==deskcore::ButtonEdge::Pressed){
      if(allow&&!displaySleep.suppressed())recordPetInput(now);
      if(deskusb::ready()){deskusb::out().print("KEY ");deskusb::out().println(i+1);}
      if(allow)executeBinding(i);
    }else if(deskusb::ready()){deskusb::out().print("KEY_RELEASE ");deskusb::out().println(i+1);}
  }
  if(delta){
    if(allow&&!displaySleep.suppressed())recordPetInput(now,static_cast<uint32_t>(delta<0?-delta:delta),delta<0?deskpet::Event::TurnCounterClockwise:deskpet::Event::Turn);
    if(delta>0)clockwiseCounts+=uint32_t(delta);else counterClockwiseCounts+=uint32_t(-delta);
    const int64_t sum=int64_t(rotationTotal)+delta;rotationTotal=int32_t(sum>INT32_MAX?INT32_MAX:(sum<INT32_MIN?INT32_MIN:sum));uiDirty=true;
    if(deskusb::ready()){deskusb::out().print("ENC delta=");deskusb::out().print(delta);deskusb::out().print(" total=");deskusb::out().println(rotationTotal);}
    if(allow){uint8_t n=min(int32_t(6),abs(delta));while(n--)executeBinding(delta>0?6:7);}
  }
  if(knobPress){if(allow&&!displaySleep.suppressed())recordPetInput(now);++knobPresses;uiDirty=true;if(deskusb::ready()){deskusb::out().print("KNOB_PRESS ");deskusb::out().println(knobPresses);}if(allow)executeBinding(8);}
  if(knobReleased){managementLease.clear();cancelAnimation();}
}
void clearSleepingDisplay() {
  if(!displayClearPending)return;
  displayClearPending=false;
  // Keep the display controller awake: only blank the framebuffer once. The
  // unmodified harness still has an illuminated black LCD, not a power-off.
  rendering=true;paintingLayout=true;
  deskview::CooperativeGfx cooperative(display,serviceDisplay);
  cooperative.fillScreen(0x0000);
  paintingLayout=false;rendering=false;
}
bool physicalContactHeld() {
  if(userSwHeld||knobButton.pressed()||rawDown||(touchOk&&digitalRead(appcfg::TouchIrq)==LOW))return true;
  for(uint8_t i=0;i<6;++i)if(keys[i].pressed()||digitalRead(appcfg::ButtonPins[i])==LOW)return true;
  return false;
}
void serviceDisplaySleep(uint32_t now) {
  const bool eligible=page==Page::Home&&!languageSaving()&&!physicalContactHeld()&&
    animationPreview.snapshot(now).remaining==0&&!hid.busy();
  if(displaySleep.due(now,displaySettings,eligible))sleepDisplay();
  clearSleepingDisplay();
}
void serviceTouch(uint32_t now) {
  if(static_cast<uint32_t>(now-lastTouchPoll)<10)return;
  const uint32_t gap=now-lastTouchPoll;
  if(lastTouchPoll&&gap>touchPollMaxGap)touchPollMaxGap=gap;
  lastTouchPoll=now;
  rawDown=false;
  const bool irqDown=touchOk&&digitalRead(appcfg::TouchIrq)==LOW;
  if(irqDown){
    uint16_t z2=0;const bool valid=touch.read_touch(&rawX,&rawY,&rawZ,&z2);
    if(checkI2cTimeout())return;
    rawDown=valid&&digitalRead(appcfg::TouchIrq)==LOW&&rawZ>20&&rawZ<4095&&z2>rawZ&&rawX>0&&rawY>0&&rawX<4095&&rawY<4095;
    if(rawDown){++rawSampleCount;lastTouchAt=now;}else ++touchInvalid;
  }
  if(rawDown){const bool wasAsleep=displaySleep.asleep();displaySleep.contact(now);if(wasAsleep&&!displaySleep.asleep())displayWoke();}
  if(displaySleep.suppressed()){
    touchGesture.blockUntilRelease(now,touchContext);touchQueueHead=touchQueueCount=0;return;
  }
  // Calibration changes pages/storage and therefore runs only in loop, never
  // from a cooperative renderer callback.
  if(page==Page::Calibration){if(!rendering)calibrationSample(rawDown,now);return;}
  // Ignore contacts begun against a partly painted new layout; still sample
  // release, so the original tap cannot carry into a new page.
  if(paintingLayout&&irqDown)touchGesture.blockUntilRelease(now,touchContext);
  const bool mapped=rawDown&&calibrated&&deskcore::mapTouch(rawX,rawY,calibration,touchX,touchY);
  uint16_t target=static_cast<uint16_t>(deskview::hitTest(page,touchX,touchY));
  if(page==Page::Home){const int8_t media=deskview::mediaHit(touchX,touchY);if(media>=0)target=100+media;}
  const auto event=touchGesture.update(now,irqDown,mapped,touchX,touchY,target,touchContext);
  if(event.accepted){
    ++touchAccepted;
    if(touchQueueCount<4){pendingTouch[(touchQueueHead+touchQueueCount)%4]=event;++touchQueueCount;}
    else ++touchQueueDropped;
  }
}
void dispatchTouch() {
  if(displaySleep.suppressed()){touchQueueHead=touchQueueCount=0;return;}
  if(!touchQueueCount)return;
  const auto event=pendingTouch[touchQueueHead];touchQueueHead=(touchQueueHead+1)%4;--touchQueueCount;
  if(event.context!=touchContext||page==Page::Calibration)return;
  recordPetInput(millis());
  if(deskusb::ready()){deskusb::out().print("TOUCH x=");deskusb::out().print(event.x);deskusb::out().print(" y=");deskusb::out().print(event.y);deskusb::out().print(" target=");deskusb::out().println(event.target);}
  touchAction(event.target);
}
bool rebootNotice(const char* message) {
  if(!deskusb::ready())return false;
  deskusb::out().print(message);return true;
}

void waitForReboot() {
  // A failed/deferred reset must not strand a previously accepted key press.
  // Queue was cleared on REBOOT; retry only zeros on that original session.
  hid.tick(millis(),false);
  deskreboot::poll(millis());
  static bool failureReported=false;
  if(deskreboot::failure()!=deskreboot::Failure::None&&!failureReported){
    failureReported=rebootNotice(deskreboot::failure()==deskreboot::Failure::Enqueue?
      "REBOOT_FAILED output=OFF reason=enqueue reset=0\r\n":
      "REBOOT_FAILED output=OFF reason=timeout reset=0\r\n");
  }
  deskusb::pump(millis());
  delay(1);
}
void command(const char* s) {
  if(deskreboot::pending())return;
  if(strcmp(s,"HELP")&&strcmp(s,"STATUS")&&strcmp(s,"DISPLAY STATUS")&&strcmp(s,"BLE STATUS"))managementLease.clear(); // Local wired console takes explicit control.
  if(!strcmp(s,"HELP"))printHelp();
  else if(!strcmp(s,"STATUS"))printStatus();
  else if(!strcmp(s,"BLE STATUS")){
    const DeskBleStatus snapshot=hid.bleStatus();
    deskusb::out().print("BLE uptime_ms=");deskusb::out().print(millis());
    deskusb::out().print(" attempted=");deskusb::out().print(snapshot.attempted);
    deskusb::out().print(" started=");deskusb::out().print(snapshot.started);
    deskusb::out().print(" conn_handle=");deskusb::out().print(snapshot.connectionHandle);
    deskusb::out().print(" link=");deskusb::out().print(snapshot.link);
    deskusb::out().print(" secured=");deskusb::out().print(snapshot.secured);
    deskusb::out().print(" bonded=");deskusb::out().print(snapshot.bonded);
    deskusb::out().print(" security_sm=");deskusb::out().print(snapshot.securityMode);
    deskusb::out().print(" security_lv=");deskusb::out().print(snapshot.securityLevel);
    deskusb::out().print(" boot_mode=");deskusb::out().print(snapshot.bootMode);
    deskusb::out().print(" epoch_before=");deskusb::out().print(snapshot.epochBefore);
    deskusb::out().print(" epoch_after=");deskusb::out().print(snapshot.epochAfter);
    deskusb::out().print(" consistent=");deskusb::out().print(snapshot.consistent);
    const DeskBleReportStatus* reports[]={&snapshot.keyboard,&snapshot.consumer};
    const char* prefixes[]={" keyboard_"," consumer_"};
    for(uint8_t i=0;i<2;++i){const auto& r=*reports[i];
      deskusb::out().print(prefixes[i]);deskusb::out().print("id=");deskusb::out().print(r.reportId);
      deskusb::out().print(prefixes[i]);deskusb::out().print("present=");deskusb::out().print(r.present);
      deskusb::out().print(prefixes[i]);deskusb::out().print("value_handle=");deskusb::out().print(r.valueHandle);
      deskusb::out().print(prefixes[i]);deskusb::out().print("cccd_handle=");deskusb::out().print(r.cccdHandle);
      deskusb::out().print(prefixes[i]);deskusb::out().print("read=");deskusb::out().print(r.readAttempted);
      deskusb::out().print(prefixes[i]);deskusb::out().print("result=");deskusb::out().print(r.result);
      deskusb::out().print(prefixes[i]);deskusb::out().print("length=");deskusb::out().print(r.length);
      deskusb::out().print(prefixes[i]);deskusb::out().print("raw=");deskusb::out().print(r.raw);
      deskusb::out().print(prefixes[i]);deskusb::out().print("valid=");deskusb::out().print(r.valid);
      deskusb::out().print(prefixes[i]);deskusb::out().print("notify=");deskusb::out().print(r.notify);
      deskusb::out().print(prefixes[i]);deskusb::out().print("ready=");deskusb::out().print(r.ready);
    }
    deskusb::out().println();
  }
  else if(!strcmp(s,"DISPLAY STATUS")){
    // Fixed five-read snapshot; no automatic poll, SWRESET, begin or redraw.
    // The BSP SPI transfer itself has no timeout; the runtime WDT remains active.
    const uint8_t commands[]={ILI9341_RDMODE,ILI9341_RDMADCTL,ILI9341_RDPIXFMT,ILI9341_RDIMGFMT,ILI9341_RDSELFDIAG};
    const char* names[]={" rdmode=0x"," rdmadctl=0x"," rdpixfmt=0x"," rdimgfmt=0x"," rdselfdiag=0x"};
    deskusb::out().print("DISPLAY uptime_ms=");deskusb::out().print(millis());
    for(uint8_t i=0;i<5;++i){const uint8_t value=display.readcommand8(commands[i]);deskusb::out().print(names[i]);if(value<16)deskusb::out().print('0');deskusb::out().print(value,HEX);}
    deskusb::out().println();
  }
  else if(!strcmp(s,"REBOOT")){
    cancelAnimation();hid.setArmed(false);hid.tick(millis(),false);
    if(deskreboot::request(millis())){
      rebootNotice("REBOOT output=OFF queued=1\r\n");
      deskusb::finishReply();deskusb::pump(millis());
      deskreboot::releaseAfterNotice();
    }
  }
  else if(!strcmp(s,"HID ON"))arm();
  else if(!strcmp(s,"HID OFF")){cancelAnimation();hid.setArmed(false);note(TextId::OutputOff);}
  else if(!strcmp(s,"USB"))route(DeskRoute::Usb);
  else if(!strcmp(s,"BLE"))route(DeskRoute::Ble);
  else if(!strcmp(s,"CALIBRATE"))startCalibration();
  else if(!strcmp(s,"DIAG"))navigate(Page::Diagnostics);
  else if(!strcmp(s,"HOME"))navigate(Page::Home);
  else if(!strcmp(s,"LANG ZH"))requestLanguage(deskui::Language::Chinese);
  else if(!strcmp(s,"LANG EN"))requestLanguage(deskui::Language::English);
  else if(!strcmp(s,"INFO"))navigate(Page::Information);
  else if(!strcmp(s,"ACTIVITY"))navigate(Page::Activity);
  else if(!strcmp(s,"ENC NORMAL")){auto b=hubConfig.bank();b.profile()[3]=0;prepareConfigChange(b);hubConfig.apply(b,false);configChanged();note(TextId::DirectionNormal);}
  else if(!strcmp(s,"ENC REVERSE")){auto b=hubConfig.bank();b.profile()[3]=1;prepareConfigChange(b);hubConfig.apply(b,false);configChanged();note(TextId::DirectionReverse);}
  else if(!strcmp(s,"COUNTERS RESET"))resetCounters();
  else if(deskusb::ready())deskusb::out().println("Unknown command. HELP");
}
void serialTask() {
  if(deskreboot::pending())return;
  deskusb::poll(millis());
  static uint32_t session=UINT32_MAX;
  if(session!=deskusb::generation()||!deskusb::ready()){
    session=deskusb::generation();managementLease.clear(deskmanage::Source::Usb);
    serialHubMode=false;serialBanner=false;commandLength=0;overflow=false;
    if(!deskusb::ready())return;
  }
  if(!deskusb::canReply())return;
  if(!serialBanner){
    if(!deskusb::beginReply())return;
    printHelp();deskusb::finishReply();serialBanner=true;return;
  }
  for(uint8_t n=0;n<64;++n){
    const int byte=deskusb::read();if(byte<0)break;const char ch=static_cast<char>(byte);
    if(ch=='\n'||ch=='\r'){
      if(!deskusb::ready()||session!=deskusb::generation()){commandLength=0;overflow=false;return;}
      lastCommand[commandLength]=0;
      if(!commandLength&&!overflow)continue;
      // Reserve the entire reply slot before executing any command/mutation.
      if(!deskusb::beginReply())return;
      if(overflow){
        if(!strncmp(lastCommand,"HUB ",4)){deskhub::Request r;deskhub::parse(lastCommand,r);hubError(r.id,"INVALID");}
        else deskusb::out().println("Command too long or non-ASCII; ignored");
      }else {
        if(!strncmp(lastCommand,"HUB ",4)){serialHubMode=true;hubCommand(lastCommand);}
        else {for(uint16_t i=0;i<commandLength;++i)lastCommand[i]=toupper(static_cast<unsigned char>(lastCommand[i]));command(lastCommand);}
      }
      deskusb::finishReply();commandLength=0;overflow=false;
      return; // One complete command per loop; never overwrite a pending reply.
    }else if(!overflow){
      if(static_cast<uint8_t>(ch)<0x20||static_cast<uint8_t>(ch)>0x7E)overflow=true;
      else if(commandLength<sizeof(lastCommand)-1)lastCommand[commandLength++]=ch;
      else overflow=true;
    }
  }
}

void bleTask(){
  if(deskreboot::pending())return;
  const uint32_t now=millis();deskble::tick(now,hid.busy());
  static uint32_t generation=0;if(generation!=deskble::generation()){generation=deskble::generation();managementLease.clear(deskmanage::Source::Ble);}
  hid.setManagementRadio(deskble::radioWanted());
  char* line=nullptr;bool invalid=false;
  if(deskble::takeLine(line,invalid)){
    hubSource=deskmanage::Source::Ble;hubMutation=false;deskble::startResponse();
    if(invalid)hubError(0,"INVALID");
    else if(strncmp(line,"HUB ",4))hubError(0,"FORBIDDEN");
    else hubCommand(line);
    deskble::finishResponse();hubSource=deskmanage::Source::Usb;hubMutation=false;
  }
}
void setup() {
  Serial.begin(115200);hid.begin();
  backlight(true); // no GPIO operation in the default, unconnected build
  pinMode(PIN_BUTTON1,INPUT_PULLUP);const bool forceCalibration=digitalRead(PIN_BUTTON1)==LOW;
  for(uint8_t i=0;i<6;++i){pinMode(appcfg::ButtonPins[i],INPUT_PULLUP);keys[i].reset(digitalRead(appcfg::ButtonPins[i])==LOW,millis());}
  // A6 is the onboard VBAT divider, separate from external A0..A5 keys.
  // Explicit settings match vbatEstimateMillivolts; 40 us accommodates the
  // 50 kohm Thevenin source. No ADC configuration from another sketch assumed.
  analogReference(AR_INTERNAL_3_0);analogReadResolution(12);
  analogSampleTime(40);analogOversampling(1);
  pinMode(appcfg::TouchIrq,INPUT_PULLUP);
  digitalWrite(appcfg::SdCs,HIGH);pinMode(appcfg::SdCs,OUTPUT);
  digitalWrite(appcfg::TftCs,HIGH);pinMode(appcfg::TftCs,OUTPUT);
  deskSPI.begin();
  // TFT FeatherWing power supervisor holds the display in reset after cold
  // power-up. Wait BEFORE begin/SWRESET; its later delays cannot recover an
  // initialization command sent while hardware reset was still asserted.
  // https://learn.adafruit.com/adafruit-2-4-tft-touch-screen-featherwing/troubleshooting
  delay(appcfg::TftPowerSettleMs);
  display.begin(appcfg::SpiHz);display.setRotation(1);display.setTextWrap(false);
  const auto bus=deskbus::clear();i2cBusReady=bus.ready;deskBusBefore=(bus.initialSda?2:0)|(bus.initialScl?1:0);deskBusPulses=bus.pulses;
  Wire.begin();Wire.setClock(appcfg::I2cHz);Wire.setTransactionTimeoutUs(20000);
  encoderOk=addressResponds(appcfg::EncoderAddress);
  if(encoderOk)encoderOk=encoder.begin(appcfg::EncoderAddress);checkI2cTimeout();
  touchOk=addressResponds(appcfg::TouchAddress)&&touch.begin(appcfg::TouchAddress,&Wire);checkI2cTimeout();
  Wire.setClock(appcfg::I2cHz);
  if(encoderOk){encoder.pinMode(appcfg::EncoderSwitch,INPUT_PULLUP);rebasePhysicalInputs();checkI2cTimeout();}
  const bool fsReady=deskstore::prepare();
  calSaved=fsReady&&deskstore::load(calibration,encoderSign);calibrated=calSaved;
  uint8_t savedLanguage=0;languageSaved=fsReady&&deskprefs::load(savedLanguage);
  if(languageSaved)language=static_cast<deskui::Language>(savedLanguage);
  displaySaved=fsReady&&deskdisplaystore::load(displaySettings);
  deskconfig::Bank bank=deskconfig::defaults();
  if(!(fsReady&&deskconfigstore::load(bank))){
    // Preserve the pre-Hub encoder direction until a bank is explicitly saved.
    for(uint8_t i=0;i<3;++i)bank.bytes[8+i*deskconfig::ProfileSize+3]=encoderSign<0;
  }
  hubConfig.initialize(bank);encoderSign=bank.profile()[3]?-1:1;
  languageWorkerReady=desklanguage::begin();
  displaySleep.begin(millis(),usbVbusPresent());
  petState.begin(millis()); // Rebase inactivity after startup work, not at MCU timestamp zero.
  if(forceCalibration||!calibrated)startCalibration();
  // Runtime bus stalls reset into output-OFF; never an input replay mechanism.
  NRF_WDT->CONFIG=(WDT_CONFIG_SLEEP_Run<<WDT_CONFIG_SLEEP_Pos);
  NRF_WDT->CRV=4*32768;NRF_WDT->RREN=1;NRF_WDT->TASKS_START=1;
}
void loop() {
  const uint32_t now=millis();NRF_WDT->RR[0]=0x6E524635;
  if(deskreboot::pending()){waitForReboot();return;}
  deskusb::poll(now);hid.tick(now);serialTask();
  if(deskreboot::pending()){waitForReboot();return;}
  bleTask();
  sampleBattery(now);
  if(digitalRead(PIN_BUTTON1)==LOW){if(!userSwHeld){wakeDisplay();managementLease.clear();cancelAnimation();userSwHeld=true;swHoldAt=now;}else if(!userSwHandled&&now-swHoldAt>=2000){userSwHandled=true;hid.setArmed(false);manageCommand(false);note(TextId::UserStopped);}}
  else {userSwHeld=false;userSwHandled=false;}
  if(static_cast<uint32_t>(millis()-lastPoll)>=20){lastPoll=millis();pollInputs(lastPoll);}
  serviceTouch(millis());dispatchTouch();serviceLanguage();
  hid.tick(millis());
  checkI2cTimeout();
  if(now-lastProbe>=1000){lastProbe=now;
    if(!i2cBusReady)recoverI2cBus();
    if(!encoderOk&&addressResponds(appcfg::EncoderAddress)){encoderOk=encoder.begin(appcfg::EncoderAddress);if(encoderOk){encoder.pinMode(appcfg::EncoderSwitch,INPUT_PULLUP);rebasePhysicalInputs();uiDirty=true;}}
    if(!touchOk&&addressResponds(appcfg::TouchAddress)){touchOk=touch.begin(appcfg::TouchAddress,&Wire);uiDirty=true;}
    if(touchOk&&!addressResponds(appcfg::TouchAddress)){touchOk=false;hid.setArmed(false);note(TextId::TouchMissing);}
    checkI2cTimeout();Wire.setClock(appcfg::I2cHz);
  }
  static Page previousPage=Page::Calibration;static bool first=true,oldConnected=false,oldArmed=false;
  if(hid.connected()!=oldConnected||hid.armed()!=oldArmed){oldConnected=hid.connected();oldArmed=hid.armed();uiDirty=true;}
  serviceDisplaySleep(millis());
  if(!displaySleep.asleep()&&((uiDirty&&now-lastUI>=(page==Page::Settings?20u:80u))||((page==Page::Home||page==Page::Settings||inputTestPage()||page==Page::Information||page==Page::Management||page==Page::Language||page==Page::Tools)&&now-lastUI>=150))){
    bool full=first||page!=previousPage||displayFullRedraw;displayFullRedraw=false;first=false;previousPage=page;lastUI=now;uiDirty=false;
    renderUi(full);
  }
  if(deskusb::ready()&&!serialHubMode&&now-lastStatus>=1000){lastStatus=now;printStatus();}
  deskusb::pump(millis());
  delay(1);
}
