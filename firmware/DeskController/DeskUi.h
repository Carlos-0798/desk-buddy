#pragma once
#include <stdint.h>
#include "UiText.h"

class Adafruit_GFX;
namespace deskview {
enum class Page : uint8_t { Home, Settings, Diagnostics, Calibration, Information, Activity, Management, Language, Tools };
enum class Command : uint8_t { None, Settings, Home, Usb, Ble, ToggleOutput, Chinese,
  English, Diagnostics, Information, Activity, Calibrate, ResetCounters, PetInteract, Management, ToggleManagement, Language, Tools, Back };
struct Rect { int16_t x,y,w,h; bool contains(int16_t px,int16_t py) const {
  return px>=x && py>=y && px<x+w && py<y+h;
}};
namespace layout {
constexpr Rect CharacterZone={82,87,156,62};
// Home status shortcuts only open Settings; they never enable HID directly.
constexpr Rect HomeOutputStatus={176,4,76,36};
constexpr Rect HomeConnectionStatus={8,46,176,36};
constexpr Rect HomeMessage={16,152,288,28};
constexpr Rect ManageToggle={8,88,304,46};
constexpr Rect HeaderButton={260,4,52,36};
constexpr Rect BackButton={236,4,76,38};
constexpr Rect Media[3]={{8,190,96,42},{112,190,96,42},{216,190,96,42}};
constexpr Rect Routes[2]={{168,50,144,48},{168,108,144,48}};
constexpr Rect Output={8,50,144,106};
constexpr Rect LanguageEntry={8,168,144,64};
constexpr Rect ToolsEntry={168,168,144,64};
constexpr Rect Languages[2]={{8,72,148,80},{164,72,148,80}};
constexpr Rect ToolGrid[4]={{8,56,148,72},{164,56,148,72},{8,144,148,72},{164,144,148,72}};
constexpr Rect Tools[2]={{8,190,148,42},{164,190,148,42}};
}
// All data are a snapshot. Rendering never sends HID or writes preferences.
struct Model {
  Page page=Page::Home;
  uint8_t theme=0,face=0,homeMode=0;bool animation=true;uint16_t accent=0;
  uint32_t petInteractions=0;bool petActive=false;uint8_t petMotion=0,petEffect=0;bool petSleeping=false;
  int8_t petDirection=0;uint16_t petElapsed=0;
  uint8_t demoAction=0;uint16_t demoElapsed=0;
  uint8_t manageState=0;uint16_t manageRemaining=0;
  uint32_t configRevision=1;char profileName[16]="MEDIA";
  uint8_t mediaKinds[3]={1,1,1};uint16_t mediaValues[3]={0xB6,0xCD,0xB5};
  deskui::Language language=deskui::Language::Chinese;
  bool ble=false,connected=false,armed=false,usbPower=false,hidSuppressed=false;
  bool encoderOk=false,touchOk=false,calibrated=false,calSaved=false,languageSaved=false,languageSaving=false;
  int16_t batteryPercent=-1;
  uint16_t batteryMv=0;
  deskui::TextId message=deskui::TextId::Ready;
  bool actionActive=false,actionBlocked=false;
  uint32_t now=0,uptimeSeconds=0,sent=0,dropped=0,markers=0;
  uint32_t uiMillis=0;
  uint32_t cw=0,ccw=0,knobPresses=0;
  bool knobDown=false;
  bool keyDown[6]={};
  uint32_t keyPresses[6]={},keyReleases[6]={};
  int16_t touchX=0,touchY=0;
  uint16_t rawX=0,rawY=0;
  uint8_t calStage=0;
  bool calNeedRelease=true,calError=false;
};
Command hitTest(Page page,int16_t x,int16_t y);
int8_t mediaHit(int16_t x,int16_t y);
class Renderer {
 public:
  void draw(Adafruit_GFX& gfx,const Model& model,bool full,void (*service)()=nullptr);
 private:
  uint32_t headerCache_=UINT32_MAX, faceCache_=UINT32_MAX, messageCache_=UINT32_MAX;
  uint32_t settingsCache_=UINT32_MAX, appearanceCache_=UINT32_MAX;
  uint32_t configRevision_=0, petCountCache_=UINT32_MAX;
  // Snapshot of values last considered on the active detail page. A page or
  // language transition is always full, so values from another page cannot leak.
  Model detailCache_;
  Page previous_=Page::Calibration;
  deskui::Language language_=deskui::Language::English;
};
}
