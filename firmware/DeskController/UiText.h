#pragma once
#include <stdint.h>

class Adafruit_GFX;

// Generated bitmap text uses top-left coordinates, a 16 px line height,
// 8 px ASCII cells and 16 px non-ASCII cells. scale=1 is the normal UI size.
// drawText paints foreground only; the caller owns background clearing.
namespace deskui {
enum class Language : uint8_t { Chinese = 0, English = 1 };
enum class TextId : uint8_t {
  Ready, OutputOn, OutputOff, Settings, Menu, Back, Connection, Connected,
  Waiting, Previous, Play, Next, Language, Chinese, English, Diagnostics,
  DeviceInfo, Calibrate, ResetCounters, Inputs, Down, Presses, Releases, Knob,
  Clockwise, CounterClockwise, LocalMarkers, Touch, Encoder, Available,
  Missing, Voltage, Estimated, BatteryNote, BatteryUsbNote, UsbPower, Present,
  Absent, Uptime, Sent, Dropped, Firmware, Calibration, Stored, NotStored,
  Storage, Target, ReleaseThenPress, HoldCross, CalibrationRetry, TouchMissing,
  CalibrationSaved, CalibrationNotSaved, CheckDevices, ConnectHost,
  UsbSelected, BleSelected, CountersReset, DirectionNormal, DirectionReverse,
  UserStopped, PlayPause, Mute, VolumeUp, VolumeDown, LocalMarker, SaveFailed,
  LanguageSaved,
  On, Off, Output, USBConnected, USBWaiting, BluetoothConnected,
  BluetoothWaiting, BatteryEstimate, LanguageChinese, LanguageEnglish,
  TouchCalibration, Recalibrate, VBAT, NoConnection, Queued, ActionBlocked,
  EncoderMissing, TestOutputOff, SettingsOutputOff, MarkerAdded, Position,
  Keys, Raw, MarkersRam, InputTest, Unknown, Calibrated, NotCalibrated,
  StartupOutputOff, SingleRoute, TrackWarning, TurnVolumePressMute,
  NoHostFeedback, NotBatteryPercent, NoChargeStatus, SelectLanguage,
  LanguageRam, PetActive, PetIdle, PetSession, PetSleeping, PetHello, BleManagement, ManageAllow, ManageRevoke, ManageClosed, ManageWindow, ManageAuthorized, ManageHint, ManageSessionHint,
  OutputUsb, OutputBle, BatteryPower, BatteryPresenceUnknown, Tools, TestingOnPaused, LanguageSaving, Count
};

const char* text(TextId id, Language language);
int16_t textWidth(const char* utf8, uint8_t scale = 1);
void drawText(Adafruit_GFX& gfx, int16_t x, int16_t y, const char* utf8,
              uint16_t color, uint8_t scale = 1);
}  // namespace deskui
