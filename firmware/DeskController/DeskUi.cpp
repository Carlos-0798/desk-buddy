#include "DeskUi.h"
#include "SpriteArt.h"
#include "PortraitArt.h"
#include "AnimationPreview.h"
#include "PetVisual.h"
#include <Adafruit_GFX.h>
#include <stdio.h>

namespace deskview {
namespace {
using deskui::TextId;
// Palette is selected on every draw; renderers can be used independently by
// the host raster tests without leaking a preceding renderer's theme.
uint16_t BG=0x0863,PANEL=0x1106,EDGE=0x224A,INK=0xEF9E;
uint16_t MINT=0x5ED7,MUTED=0x8CD3,CORAL=0xFBAE,AMBER=0xFDCB;
void palette(const Model& m){
  static const uint16_t colors[8][8]={
    {0x0863,0x1106,0x224A,0xEF9E,0x5ED7,0x8CD3,0xFBAE,0xFDCB},
    {0xF7BE,0xFFFF,0xBDF7,0x18E3,0x0471,0x632C,0xB986,0x8B20},
    {0x0020,0x00A1,0x1245,0xCFF9,0x07E0,0x65AD,0xFC80,0xFFE0},
    {0x18A6,0x292A,0x526F,0xF7BF,0xB55F,0xAD56,0xFBB6,0xFE35},
    // Ocean, Sunset, Garden and Blueprint: contract background/accent RGB888
    // are quantized once to RGB565; UI text colors retain separate contrast.
    {0x1168,0x19EF,0x32D4,0xEF9E,0x7EFE,0xAE39,0xFD93,0xFE49},
    {0xFF9B,0xFFFE,0xDD2F,0x4984,0xAA05,0x8B6B,0xC968,0xAC05},
    {0xEF9C,0xF7DE,0xAD53,0x21E5,0x3328,0x638C,0xC32A,0x9B84},
    {0x116B,0x19F0,0x3B37,0xF7DF,0xBF1F,0x9DDB,0xFD93,0xFE69}};
  const uint16_t* c=colors[m.theme<8?m.theme:0];
  BG=c[0];PANEL=c[1];EDGE=c[2];INK=c[3];MINT=m.accent?m.accent:c[4];MUTED=c[5];CORAL=c[6];AMBER=c[7];
}
uint16_t contrast(uint16_t color){
  const unsigned r=(color>>11)*255/31,g=((color>>5)&63)*255/63,b=(color&31)*255/31;
  return 299*r+587*g+114*b>145000?0x0000:0xFFFF;
}
const char* mediaLabel(const Model& m,uint8_t i){
  if(m.mediaKinds[i]==0)return "--";
  if(m.mediaKinds[i]==2)return "KEY";
  if(m.mediaKinds[i]==3){switch(m.mediaValues[i]){case 1:return "MENU";case 2:return "MARK";case 3:return "OFF";case 4:return "PROFILE";}return "--";}
  switch(m.mediaValues[i]){case 0xB6:return deskui::text(TextId::Previous,m.language);case 0xB5:return deskui::text(TextId::Next,m.language);
    case 0xCD:return deskui::text(TextId::Play,m.language);case 0xE2:return deskui::text(TextId::Mute,m.language);case 0xE9:return "VOL +";case 0xEA:return "VOL -";}
  return "--";
}
const char* tr(TextId id,const Model& m){return deskui::text(id,m.language);}
void text(Adafruit_GFX& g,int x,int y,const char* s,uint16_t color=INK){
  deskui::drawText(g,x,y,s,color);
}
void centered(Adafruit_GFX& g,int x,int y,int w,const char* s,uint16_t color=INK){
  text(g,x+(w-deskui::textWidth(s))/2,y,s,color);
}
// A compact output card uses two bounded lines instead of clipping a long
// English readiness message. Break on spaces when possible; never split UTF-8.
void wrappedCentered(Adafruit_GFX& g,int x,int y,int w,const char* value,uint16_t color){
  const char* cursor=value;
  for(uint8_t row=0;row<2&&*cursor;++row){
    while(*cursor==' ')++cursor;
    char line[96]={};uint8_t length=0,lastSpace=0;int width=0;
    const char* nextWord=nullptr;
    while(*cursor){
      const uint8_t c=static_cast<uint8_t>(*cursor);
      const uint8_t bytes=c<0x80?1:(c<0xE0?2:(c<0xF0?3:4));
      const int advance=c<0x80?8:16;
      if(width+advance>w||length+bytes>=sizeof(line))break;
      if(*cursor==' '){lastSpace=length;nextWord=cursor+1;}
      for(uint8_t i=0;i<bytes;++i)line[length++]=*cursor++;
      width+=advance;
    }
    if(*cursor&&nextWord){length=lastSpace;cursor=nextWord;}
    while(length&&line[length-1]==' ')--length;
    line[length]=0;centered(g,x,y+row*18,w,line,color);
  }
}
void tile(Adafruit_GFX& g,const Rect& r,const char* s,bool selected=false){
  g.fillRoundRect(r.x,r.y,r.w,r.h,9,selected?MINT:PANEL);
  if(!selected)g.drawRoundRect(r.x,r.y,r.w,r.h,9,EDGE);
  centered(g,r.x,r.y+(r.h-16)/2,r.w,s,selected?contrast(MINT):INK);
}
TextId outputHint(const Model& m){
  if(!m.encoderOk||!m.touchOk||!m.calibrated)return TextId::CheckDevices;
  if(!m.connected)return TextId::ConnectHost;
  if(m.armed)return TextId::Ready;
  return m.ble?TextId::BluetoothConnected:TextId::USBConnected;
}
void outputTile(Adafruit_GFX& g,const Model& m){
  const Rect& r=layout::Output;
  const bool blocked=!m.encoderOk||!m.touchOk||!m.calibrated||!m.connected;
  g.fillRoundRect(r.x,r.y,r.w,r.h,9,m.armed?MINT:PANEL);
  if(!m.armed)g.drawRoundRect(r.x,r.y,r.w,r.h,9,blocked?AMBER:EDGE);
  // A refused ON tap still acknowledges the touch, without showing a false ON.
  if(!m.armed&&m.actionActive&&(m.message==TextId::ConnectHost||m.message==TextId::CheckDevices))
    g.drawRoundRect(r.x+2,r.y+2,r.w-4,r.h-4,7,AMBER);
  const uint16_t color=m.armed?contrast(MINT):INK;
  centered(g,r.x+8,r.y+21,r.w-16,tr(m.armed?TextId::OutputOn:TextId::OutputOff,m),color);
  wrappedCentered(g,r.x+8,r.y+54,r.w-16,tr(outputHint(m),m),m.armed?color:(blocked?AMBER:MUTED));
}
void header(Adafruit_GFX& g,const Model& m,TextId title){
  text(g,12,14,tr(title,m),INK);
  tile(g,m.page==Page::Home?layout::HeaderButton:layout::BackButton,tr(m.page==Page::Home?TextId::Menu:TextId::Back,m));
}
void powerIcon(Adafruit_GFX& g,int x,int y,int percent){
  g.drawRoundRect(x,y,26,14,3,MUTED);g.fillRect(x+26,y+4,3,6,MUTED);
  if(percent>=0){
    const int fill=percent*20/100;
    if(fill)g.fillRect(x+3,y+3,fill,8,percent<=15?CORAL:MINT);
  }else{g.fillRect(x+12,y+4,2,6,MUTED);}
}
void linkIcon(Adafruit_GFX& g,int x,int y,bool ble,uint16_t color){
  if(ble){g.drawLine(x+7,y,x+7,y+19,color);g.drawLine(x+7,y,x+14,y+6,color);
    g.drawLine(x+14,y+6,x,y+14,color);g.drawLine(x,y+5,x+14,y+13,color);g.drawLine(x+14,y+13,x+7,y+19,color);
  }else{g.drawRoundRect(x+2,y,12,15,3,color);g.fillRect(x+5,y+3,2,4,color);
    g.fillRect(x+9,y+3,2,4,color);g.drawFastVLine(x+8,y+15,5,color);}
}
void homeTop(Adafruit_GFX& g,const Model& m){
  g.fillRect(0,0,256,40,BG);
  text(g,12,14,m.profileName,INK);
  const char* output=tr(m.armed?TextId::On:TextId::Off,m);
  g.fillRoundRect(180,12,65,22,7,PANEL);
  g.fillCircle(190,23,3,m.armed?MINT:AMBER);
  text(g,200,15,output,m.armed?MINT:MUTED);
  g.fillRect(8,46,304,36,BG);
  g.fillRoundRect(8,46,176,36,8,PANEL);
  linkIcon(g,18,54,m.ble,m.connected?MINT:MUTED);
  // Output route is independent of the cable supplying power and the ON/OFF pill.
  text(g,41,48,tr(m.ble?TextId::OutputBle:TextId::OutputUsb,m),INK);
  text(g,41,65,tr(m.connected?TextId::Connected:TextId::Waiting,m),m.connected?MINT:MUTED);
  g.fillRoundRect(192,46,120,36,8,PANEL);
  centered(g,192,48,120,tr(m.usbPower?TextId::UsbPower:TextId::BatteryPower,m),MUTED);
  if(m.usbPower){
    // BAT is shared with the charger: neither a percentage nor a presence claim.
    centered(g,192,65,120,tr(TextId::BatteryPresenceUnknown,m),MUTED);
  }else{
    powerIcon(g,200,66,m.batteryPercent);
    char b[12];if(m.batteryPercent<0)snprintf(b,sizeof(b),"--%%");
    else snprintf(b,sizeof(b),"~%d%%",m.batteryPercent);
    text(g,235,65,b,m.batteryPercent>=0&&m.batteryPercent<=15?CORAL:INK);
  }
}
// Small integer ellipses use scan lines: no bitmap heap/framebuffer or floats.
// All artwork is clipped by construction to x=82..237, y=87..148.
void oval(Adafruit_GFX& g,int cx,int cy,int rx,int ry,uint16_t color){
  const int32_t rx2=rx*rx,ry2=ry*ry,limit=rx2*ry2;
  for(int y=-ry;y<=ry;++y){int x=rx;while(x>0&&x*x*ry2+y*y*rx2>limit)--x;g.drawFastHLine(cx-x,cy+y,2*x+1,color);}
}
void themeDetail(Adafruit_GFX& g,uint8_t theme){
  if(theme==4){ // Two small waves at each side, away from the character.
    for(int y=125;y<=137;y+=12)for(int x=87;x<108;x+=10){g.drawLine(x,y,x+5,y-3,EDGE);g.drawLine(x+5,y-3,x+10,y,EDGE);}
    g.drawCircle(226,96,4,EDGE);g.drawCircle(220,106,2,EDGE);
  }else if(theme==5){
    g.drawCircle(103,106,10,EDGE);g.drawFastHLine(89,111,28,PANEL);g.fillRect(91,112,25,6,PANEL);
    g.drawFastHLine(88,120,29,EDGE);g.drawFastHLine(92,125,21,EDGE);
    g.drawLine(217,100,229,92,EDGE);g.drawLine(219,110,234,110,EDGE);
  }else if(theme==6){
    for(int side=0;side<2;++side){int x=side?221:97;g.drawLine(x,140,x+4,105,EDGE);
      oval(g,x-2,119,5,3,EDGE);oval(g,x+7,111,5,3,EDGE);oval(g,x+3,132,5,3,EDGE);}
  }else if(theme==7){
    for(int x=87;x<=231;x+=12)g.drawFastVLine(x,91,54,EDGE);
    for(int y=91;y<=145;y+=12)g.drawFastHLine(87,y,145,EDGE);
  }
}
deskart::Frame spriteFrame(const Model& m,bool blink){
  if(m.demoAction)return static_cast<deskart::Frame>(deskdemo::visual(m.demoAction,m.demoElapsed).frame);
  if(m.petSleeping)return deskart::Frame::Blink;
  if(m.petMotion==1)return deskart::Frame::Happy;
  if(m.petMotion==2){
    if(m.homeMode)return (m.petElapsed/180u)&1u?deskart::Frame::WalkB:deskart::Frame::WalkA;
    return deskart::Frame::Happy;
  }
  return blink?deskart::Frame::Blink:deskart::Frame::Idle;
}
template<class Art> void indexed(Adafruit_GFX& g,const Art& art,int width,int height,int scale,int offsetX=0,int offsetY=0,bool mirror=false){
  // Immutable assets are validated by the compiler and host pixel oracle.
  // A bad metadata view fails closed instead of indexing outside a sprite.
  if(!art.indices||!art.palette||art.width!=width||art.height!=height||
     !art.paletteCount||art.paletteCount>32||art.transparentIndex>=art.paletteCount)return;
  const int dx=160-width*scale/2+offsetX,dy=87+(62-height*scale)/2+offsetY;
  g.startWrite();
  for(int y=0;y<height;++y){
    const uint8_t* row=art.indices+y*width;
    for(int x=0;x<width;){
      const uint8_t index=row[x];int end=x+1;while(end<width&&row[end]==index)++end;
      if(index!=art.transparentIndex&&index<art.paletteCount)
        g.writeFillRect(dx+(mirror?width-end:x)*scale,dy+y*scale,(end-x)*scale,scale,art.palette[index]);
      x=end;
    }
  }
  g.endWrite();
}
void heart(Adafruit_GFX& g,int x,int y){
  static const uint8_t rows[]={0x36,0x7F,0x7F,0x3E,0x1C,0x08};
  for(int yy=0;yy<6;++yy)for(int xx=0;xx<7;++xx)
    if(rows[yy]&(1u<<(6-xx)))g.fillRect(x+xx*2,y+yy*2,2,2,CORAL);
}
void face(Adafruit_GFX& g,const Model& m,bool blink){
  // Full portraits and bodies share one bounded dirty rectangle, restored
  // before each pose/effect transition so transparent pixels leave no residue.
  g.fillRect(82,87,156,62,PANEL);themeDetail(g,m.theme);
  const auto demo=deskdemo::visual(m.demoAction,m.demoElapsed);
  const auto physical=deskpet::physicalVisual(m.petMotion,m.petDirection,m.petElapsed,m.homeMode!=0,m.animation,m.petSleeping);
  const int dx=m.demoAction?demo.dx:physical.dx,dy=m.demoAction?demo.dy:physical.dy;
  const bool mirror=!m.demoAction&&physical.mirror;
  if(m.homeMode)indexed(g,deskart::get(m.face,spriteFrame(m,blink)),64,60,1,dx,dy,mirror);
  else indexed(g,deskportrait::get(m.face,spriteFrame(m,blink)),48,30,2,dx,dy);
  const uint8_t sleepLetters=m.demoAction?demo.sleepLetters:(m.petSleeping?3:0);
  const bool showHeart=m.demoAction?demo.heart:(!m.petSleeping&&m.petEffect);
  if(sleepLetters){text(g,210,122,sleepLetters==1?"Z":sleepLetters==2?"Zz":"Zzz",MUTED);}
  else if(showHeart){
    const int lift=(m.demoAction?demo.heartPhase:((m.now/180u)&1u))?-3:0;
    heart(g,92,106+lift);heart(g,214,132+lift);
    g.drawFastHLine(105,133+lift,5,MINT);g.drawFastVLine(107,131+lift,5,MINT);
  }
  if(!m.encoderOk||!m.touchOk){g.fillCircle(227,96,6,CORAL);g.drawFastVLine(227,92,5,PANEL);g.drawPixel(227,100,PANEL);}
}
// One 16-pixel text field is the largest regular detail-page erase. Keeping
// static labels outside these patches avoids a flash/long SPI transfer on every
// 150 ms UI service. Full-page backgrounds are drawn only on page entry.
void field(Adafruit_GFX& g,int x,int y,int w,const char* value,uint16_t color,bool full){
  if(!full)g.fillRect(x,y,w,16,BG);
  text(g,x,y,value,color);
}
void diagnostic(Adafruit_GFX& g,const Model& m,const Model& old,bool full,void(*service)()){
  if(full||m.armed!=old.armed||m.hidSuppressed!=old.hidSuppressed)
    field(g,12,45,296,tr(m.armed?(m.hidSuppressed?TextId::TestingOnPaused:TextId::OutputOn):TextId::OutputOff,m),m.armed?AMBER:MUTED,full);
  if(full){text(g,12,65,tr(TextId::Inputs,m),MINT);
    text(g,132,65,tr(TextId::Presses,m),MUTED);text(g,224,65,tr(TextId::Releases,m),MUTED);}
  char b[32];
  for(uint8_t i=0;i<6;++i){int y=85+i*17;
    if(full){snprintf(b,sizeof(b),"K%u",i+1);text(g,18,y,b);}
    if(full||m.keyDown[i]!=old.keyDown[i])
      g.fillRoundRect(52,y+3,9,9,2,m.keyDown[i]?MINT:EDGE);
    // Fixed columns fit every uint32 count (80 px at the normal font size).
    if(full||m.keyPresses[i]!=old.keyPresses[i]||m.keyDown[i]!=old.keyDown[i]){
      snprintf(b,sizeof(b),"%lu",static_cast<unsigned long>(m.keyPresses[i]));
      field(g,128,y,80,b,m.keyDown[i]?MINT:INK,full);if(service)service();}
    if(full||m.keyReleases[i]!=old.keyReleases[i]){
      snprintf(b,sizeof(b),"%lu",static_cast<unsigned long>(m.keyReleases[i]));
      field(g,222,y,80,b,INK,full);if(service)service();}
  }
}
void information(Adafruit_GFX& g,const Model& m,const Model& old,bool full,void(*service)()){
  char b[60];
  // The information page keeps raw values out of the default little-face page.
  if(full){
    text(g,12,45,tr(TextId::Firmware,m),MUTED);text(g,112,45,"R3H 2026-10-05");
    text(g,12,65,tr(TextId::Voltage,m),MUTED);text(g,12,85,tr(TextId::Uptime,m),MUTED);
    // Entry snapshot only: displaying this page's latest render duration would
    // feed its own changing duration back into the next render indefinitely.
    snprintf(b,sizeof(b),"SPI8M I2C100k UI%lums",static_cast<unsigned long>(m.uiMillis));text(g,12,105,b,MUTED);
    text(g,12,125,tr(TextId::Calibration,m),MUTED);text(g,12,145,tr(TextId::BatteryNote,m),MUTED);
  }
  if(full||m.batteryMv!=old.batteryMv){
    snprintf(b,sizeof(b),"%u.%03u V",m.batteryMv/1000,m.batteryMv%1000);
    field(g,112,65,88,b,INK,full);if(service)service();}
  if(full||m.usbPower!=old.usbPower){
    field(g,212,65,64,m.usbPower?"USB +":"USB -",m.usbPower?MINT:MUTED,full);
    field(g,12,165,296,tr(m.usbPower?TextId::BatteryUsbNote:TextId::NoHostFeedback,m),MUTED,full);
    if(service)service();}
  if(full||m.uptimeSeconds!=old.uptimeSeconds){
    snprintf(b,sizeof(b),"%lus",static_cast<unsigned long>(m.uptimeSeconds));
    field(g,112,85,88,b,INK,full);if(service)service();}
  if(full||m.calSaved!=old.calSaved){
    field(g,112,125,80,tr(m.calSaved?TextId::Stored:TextId::NotStored,m),m.calSaved?MINT:CORAL,full);
    if(service)service();}
}
void activity(Adafruit_GFX& g,const Model& m,const Model& old,bool full,void(*service)()){
  char b[60];
  if(full||m.armed!=old.armed||m.hidSuppressed!=old.hidSuppressed)
    field(g,12,45,296,tr(m.armed?(m.hidSuppressed?TextId::TestingOnPaused:TextId::OutputOn):TextId::OutputOff,m),m.armed?AMBER:MUTED,full);
  if(full){text(g,12,68,tr(TextId::Knob,m),MUTED);text(g,12,109,tr(TextId::Touch,m),MUTED);
    text(g,12,150,tr(TextId::LocalMarkers,m),MUTED);}
  if(full||m.knobPresses!=old.knobPresses){
    snprintf(b,sizeof(b),"%lu",static_cast<unsigned long>(m.knobPresses));
    field(g,124,68,80,b,INK,full);if(service)service();}
  if(full||m.knobDown!=old.knobDown)
    g.fillRoundRect(284,72,9,9,2,m.knobDown?MINT:EDGE);
  if(full||m.cw!=old.cw){snprintf(b,sizeof(b),"CW  %lu",static_cast<unsigned long>(m.cw));
    field(g,12,88,144,b,INK,full);if(service)service();}
  if(full||m.ccw!=old.ccw){snprintf(b,sizeof(b),"CCW %lu",static_cast<unsigned long>(m.ccw));
    field(g,164,88,144,b,INK,full);if(service)service();}
  if(full||m.touchX!=old.touchX||m.touchY!=old.touchY){
    snprintf(b,sizeof(b),"%d, %d",m.touchX,m.touchY);field(g,124,109,168,b,INK,full);if(service)service();}
  if(full||m.rawX!=old.rawX||m.rawY!=old.rawY){
    snprintf(b,sizeof(b),"RAW %u, %u",m.rawX,m.rawY);field(g,12,129,296,b,MUTED,full);if(service)service();}
  if(full||m.markers!=old.markers){
    snprintf(b,sizeof(b),"%lu",static_cast<unsigned long>(m.markers));field(g,210,150,80,b,INK,full);if(service)service();}
  if(full||m.sent!=old.sent||m.dropped!=old.dropped){
    snprintf(b,sizeof(b),"TX %lu  DROP %lu",static_cast<unsigned long>(m.sent),static_cast<unsigned long>(m.dropped));
    field(g,12,170,296,b,MUTED,full);if(service)service();}
}
void calibration(Adafruit_GFX& g,const Model& m){
  const bool center=m.calStage==4;
  centered(g,0,center?42:68,320,tr(TextId::TouchCalibration,m),MINT);
  char b[32];snprintf(b,sizeof(b),"%s %u / 5",tr(TextId::Target,m),m.calStage+1);
  centered(g,0,center?66:94,320,b);
  centered(g,0,center?156:126,320,tr(m.calNeedRelease?TextId::ReleaseThenPress:TextId::HoldCross,m),MUTED);
  if(m.calError)centered(g,0,177,320,tr(TextId::CalibrationRetry,m),CORAL);
  if(!m.touchOk)centered(g,0,196,320,tr(TextId::TouchMissing,m),CORAL);
  constexpr int16_t tx[]={20,299,299,20,160},ty[]={20,20,219,219,120};
  const uint8_t i=m.calStage<5?m.calStage:0;
  g.drawCircle(tx[i],ty[i],12,AMBER);g.drawFastHLine(tx[i]-16,ty[i],33,INK);g.drawFastVLine(tx[i],ty[i]-16,33,INK);
}
} // namespace

int8_t mediaHit(int16_t x,int16_t y){for(int8_t i=0;i<3;++i)if(layout::Media[i].contains(x,y))return i;return -1;}
Command hitTest(Page page,int16_t x,int16_t y){
  if(page==Page::Calibration)return Command::None;
  if(page==Page::Home&&layout::HeaderButton.contains(x,y))return Command::Settings;
  if(page!=Page::Home&&layout::BackButton.contains(x,y))return Command::Back;
  if(page==Page::Management){if(layout::ManageToggle.contains(x,y))return Command::ToggleManagement;return Command::None;}
  if(page==Page::Home&&(layout::HomeOutputStatus.contains(x,y)||layout::HomeConnectionStatus.contains(x,y)||layout::HomeMessage.contains(x,y)))return Command::Settings;
  if(page==Page::Home&&layout::CharacterZone.contains(x,y))return Command::PetInteract;
  if(page==Page::Settings){
    if(layout::Routes[0].contains(x,y))return Command::Usb;
    if(layout::Routes[1].contains(x,y))return Command::Ble;
    if(layout::Output.contains(x,y))return Command::ToggleOutput;
    if(layout::LanguageEntry.contains(x,y))return Command::Language;
    if(layout::ToolsEntry.contains(x,y))return Command::Tools;
  }else if(page==Page::Language){
    if(layout::Languages[0].contains(x,y))return Command::Chinese;
    if(layout::Languages[1].contains(x,y))return Command::English;
  }else if(page==Page::Tools){
    if(layout::ToolGrid[0].contains(x,y))return Command::Diagnostics;
    if(layout::ToolGrid[1].contains(x,y))return Command::Activity;
    if(layout::ToolGrid[2].contains(x,y))return Command::Information;
    if(layout::ToolGrid[3].contains(x,y))return Command::Management;
  }else if(page==Page::Diagnostics){
    if(layout::Tools[0].contains(x,y))return Command::ResetCounters;
    if(layout::Tools[1].contains(x,y))return Command::Activity;
  }else if(page==Page::Activity){
    if(layout::Tools[0].contains(x,y))return Command::ResetCounters;
    if(layout::Tools[1].contains(x,y))return Command::Diagnostics;
  }else if(page==Page::Information){
    if(layout::Tools[0].contains(x,y))return Command::Calibrate;
    if(layout::Tools[1].contains(x,y))return Command::Management;
  }
  return Command::None;
}
void Renderer::draw(Adafruit_GFX& g,const Model& m,bool full,void(*service)()){
  palette(m);
  const uint32_t appearance=(static_cast<uint32_t>(m.accent)<<10)|(uint32_t(m.theme)<<7)|(uint32_t(m.face)<<3)|(uint32_t(m.homeMode)<<1)|(m.animation?1u:0u);
  full=full||m.page!=previous_||m.language!=language_||appearance!=appearanceCache_||m.configRevision!=configRevision_;
  appearanceCache_=appearance;configRevision_=m.configRevision;
  previous_=m.page;language_=m.language;
  auto tick=[&](){if(service)service();};
  if(full){g.fillScreen(BG);tick();}
  if(m.page==Page::Home){
    if(full){tile(g,layout::HeaderButton,tr(TextId::Menu,m));
      g.fillRoundRect(8,83,304,99,14,PANEL);
      tile(g,layout::Media[0],mediaLabel(m,0));tick();
      tile(g,layout::Media[1],mediaLabel(m,1),true);
      tile(g,layout::Media[2],mediaLabel(m,2));tick();}
    const uint32_t h=static_cast<uint32_t>(m.batteryPercent+1)*32u+(m.ble?1:0)+(m.connected?2:0)+(m.armed?4:0)+(m.usbPower?8:0);
    if(full||h!=headerCache_){homeTop(g,m);headerCache_=h;tick();}
    const bool blink=m.animation&&m.now%4400>4250;
    const auto demo=deskdemo::visual(m.demoAction,m.demoElapsed);
    const auto physical=deskpet::physicalVisual(m.petMotion,m.petDirection,m.petElapsed,m.homeMode!=0,m.animation,m.petSleeping);
    const int dx=m.demoAction?demo.dx:physical.dx,dy=m.demoAction?demo.dy:physical.dy;
    const uint32_t f=static_cast<uint8_t>(spriteFrame(m,blink))+(m.connected?8:0)+(m.encoderOk?16:0)+(m.touchOk?32:0)+(m.petSleeping?64:0)
      +(m.petEffect?128:0)+(m.petEffect&&((m.now/180u)&1u)?256:0)
      +(uint32_t(dx+32)<<9)+(uint32_t(dy+1)<<15)+(uint32_t(demo.sleepLetters)<<17)
      +(uint32_t(m.demoAction)<<19)+(demo.heart?1u<<22:0)+(uint32_t(demo.heartPhase)<<23)
      +(!m.demoAction&&physical.mirror?1u<<24:0);
    if(full||f!=faceCache_){face(g,m,blink);faceCache_=f;tick();}
    const TextId id=m.actionActive?m.message:(!m.connected?TextId::ConnectHost:(m.armed?TextId::Ready:TextId::OutputOff));
    const bool session=m.homeMode==2&&!m.actionActive;
    const uint32_t msg=session?(0x80000000u+(m.petActive?1u:0u)+(m.petSleeping?2u:0u)):static_cast<uint32_t>(id)*2u+(m.actionBlocked?1:0);
    if(full||msg!=messageCache_||(session&&m.petInteractions!=petCountCache_)){
      g.fillRect(16,156,288,17,PANEL);
      if(session){char b[64];snprintf(b,sizeof(b),"%s  %s %lu",tr(m.petSleeping?TextId::PetSleeping:(m.petActive?TextId::PetActive:TextId::PetIdle),m),tr(TextId::PetSession,m),static_cast<unsigned long>(m.petInteractions));centered(g,16,156,288,b,m.petActive?MINT:MUTED);}
      else centered(g,16,156,288,tr(id,m),m.actionActive&&m.actionBlocked?AMBER:MUTED);
      messageCache_=msg;petCountCache_=m.petInteractions;
    }
  }else if(m.page==Page::Settings){
    const bool refused=!m.armed&&m.actionActive&&(m.message==TextId::ConnectHost||m.message==TextId::CheckDevices);
    const uint32_t sc=(m.ble?1:0)+(m.armed?2:0)+(m.languageSaved?4:0)
      +(m.connected?8:0)+(m.encoderOk?16:0)+(m.touchOk?32:0)+(m.calibrated?64:0)+(refused?128:0);
    if(!full&&sc==settingsCache_)return;
    const uint32_t changed=sc^settingsCache_;
    settingsCache_=sc;
    if(full){header(g,m,TextId::Settings);}tick();
    // Update the pressed control first. Do not stall input sampling by repainting
    // the six unrelated buttons whenever output/readiness changes.
    if(full||(changed&0xFBu)){outputTile(g,m);tick();}
    if(full||(changed&1u)){
      tile(g,layout::Routes[0],"USB",!m.ble);tile(g,layout::Routes[1],"BLE",m.ble);tick();}
    if(full){
      tile(g,layout::LanguageEntry,tr(TextId::Language,m));tick();
      tile(g,layout::ToolsEntry,tr(TextId::Tools,m));tick();}
  }else if(m.page==Page::Language){
    if(full){
      header(g,m,TextId::SelectLanguage);tick();
      tile(g,layout::Languages[0],deskui::text(TextId::Chinese,deskui::Language::Chinese),m.language==deskui::Language::Chinese);tick();
      tile(g,layout::Languages[1],"English",m.language==deskui::Language::English);tick();}
    if(full||m.languageSaved!=detailCache_.languageSaved||m.languageSaving!=detailCache_.languageSaving)
      field(g,12,181,296,tr(m.languageSaving?TextId::LanguageSaving:(m.languageSaved?TextId::LanguageSaved:TextId::LanguageRam),m),m.languageSaving?MUTED:(m.languageSaved?MINT:AMBER),full);
    detailCache_=m;
  }else if(m.page==Page::Tools){
    if(full){
      header(g,m,TextId::Tools);tick();
      tile(g,layout::ToolGrid[0],tr(TextId::Diagnostics,m));tick();
      tile(g,layout::ToolGrid[1],tr(TextId::Knob,m));tick();
      tile(g,layout::ToolGrid[2],tr(TextId::DeviceInfo,m));tick();
      tile(g,layout::ToolGrid[3],tr(TextId::BleManagement,m));tick();}
  }else if(m.page==Page::Diagnostics){
    if(full){header(g,m,TextId::Diagnostics);tile(g,layout::Tools[0],tr(TextId::ResetCounters,m));tile(g,layout::Tools[1],tr(TextId::Knob,m));tick();}
    diagnostic(g,m,detailCache_,full,service);detailCache_=m;tick();
  }else if(m.page==Page::Activity){
    if(full){header(g,m,TextId::Knob);tile(g,layout::Tools[0],tr(TextId::ResetCounters,m));tile(g,layout::Tools[1],tr(TextId::Inputs,m));tick();}
    activity(g,m,detailCache_,full,service);detailCache_=m;tick();
  }else if(m.page==Page::Information){
    if(full){header(g,m,TextId::DeviceInfo);tile(g,layout::Tools[0],tr(TextId::Calibrate,m));tile(g,layout::Tools[1],tr(TextId::BleManagement,m));tick();}
    information(g,m,detailCache_,full,service);detailCache_=m;tick();
  }else if(m.page==Page::Management){
    if(full){header(g,m,TextId::BleManagement);text(g,12,154,tr(TextId::ManageHint,m),MUTED);text(g,12,176,tr(TextId::ManageSessionHint,m),MUTED);}
    if(full||m.manageState!=detailCache_.manageState){
      field(g,12,52,296,tr(m.manageState==2?TextId::ManageAuthorized:m.manageState==1?TextId::ManageWindow:TextId::ManageClosed,m),m.manageState==2?MINT:INK,full);
      tile(g,layout::ManageToggle,tr(m.manageState?TextId::ManageRevoke:TextId::ManageAllow,m),m.manageState!=0);tick();}
    if(full||m.manageState!=detailCache_.manageState||m.manageRemaining!=detailCache_.manageRemaining){char b[20];if(m.manageState==1)snprintf(b,sizeof(b),"%us / 60s",m.manageRemaining);else b[0]=0;field(g,12,72,200,b,MUTED,full);}
    detailCache_=m;
  }else {if(!full)g.fillScreen(BG);calibration(g,m);tick();}
}
} // namespace deskview
