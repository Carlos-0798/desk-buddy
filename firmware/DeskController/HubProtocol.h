#pragma once
#include "HubConfig.h"
#include "DisplaySleep.h"
#include <stdlib.h>

namespace deskhub {
enum class Verb:uint8_t{Invalid,Hello,Get,Preview,Save,Revert,Select,Status,Output,Route,Language,Page,Counters,Animate,Manage,DisplayGet,DisplaySet,DisplaySleep,DisplayWake};
struct Request {uint32_t id=0,revision=0;Verb verb=Verb::Invalid;uint8_t value=0;deskconfig::Bank bank;deskdisplay::Settings display;};
inline bool number(const char* s,uint32_t& out){
  if(!s||!*s)return false;uint32_t n=0;
  for(;*s;++s){if(*s<'0'||*s>'9')return false;uint8_t d=*s-'0';if(n>(UINT32_MAX-d)/10u)return false;n=n*10u+d;}out=n;return true;
}
// Mutates only the caller's private line copy. No heap or flash activity.
inline const char* parse(char* line,Request& r){
  r=Request{};char* token[6]={};uint8_t count=0;bool extra=false;char* p=line;
  while(*p){while(*p==' ')++p;if(!*p)break;if(count==6){extra=true;break;}token[count++]=p;while(*p&&*p!=' ')++p;if(*p)*p++=0;}
  if(count<2||strcmp(token[0],"HUB")||!number(token[1],r.id)||r.id==0||r.id>2147483647u){r.id=0;return "INVALID";}
  if(extra||count<3)return "INVALID";
  const char* v=token[2];
  if(!strcmp(v,"HELLO"))r.verb=Verb::Hello;
  else if(!strcmp(v,"GET"))r.verb=Verb::Get;
  else if(!strcmp(v,"REVERT"))r.verb=Verb::Revert;
  else if(!strcmp(v,"STATUS"))r.verb=Verb::Status;
  else if(!strcmp(v,"PREVIEW")||!strcmp(v,"SAVE")||!strcmp(v,"SELECT")){
    if(count!=5||!number(token[3],r.revision))return "INVALID";
    if(!strcmp(v,"SELECT")){uint32_t n;if(!number(token[4],n)||n>=3)return "INVALID";r.value=n;r.verb=Verb::Select;}
    else {if(!deskconfig::fromHex(token[4],r.bank))return "INVALID";r.verb=!strcmp(v,"SAVE")?Verb::Save:Verb::Preview;}
    return nullptr;
  }else if(!strcmp(v,"DISPLAY")){
    if(count==4){if(!strcmp(token[3],"GET"))r.verb=Verb::DisplayGet;else if(!strcmp(token[3],"SLEEP"))r.verb=Verb::DisplaySleep;else if(!strcmp(token[3],"WAKE"))r.verb=Verb::DisplayWake;else return "INVALID";return nullptr;}
    uint32_t usb,battery;
    if(count!=6||strcmp(token[3],"SET")||!number(token[4],usb)||!number(token[5],battery)||usb>deskdisplay::MaxSeconds||battery>deskdisplay::MaxSeconds)return "INVALID";
    r.display=deskdisplay::Settings(uint16_t(usb),uint16_t(battery));if(!r.display.valid())return "INVALID";
    r.verb=Verb::DisplaySet;return nullptr;
  }else if(!strcmp(v,"ANIMATE")){
    if(count!=4)return "INVALID";
    static const char* const names[]={"STOP","IDLE","BLINK","HAPPY","WALK","TAP","SLEEP","WAKE"};
    for(uint8_t i=0;i<8;++i)if(!strcmp(token[3],names[i])){r.verb=Verb::Animate;r.value=i;return nullptr;}
    return "INVALID";
  }else if(!strcmp(v,"CONTROL")){
    if(count!=5)return "INVALID";const char* a=token[3];const char* b=token[4];
    if(!strcmp(a,"OUTPUT")||!strcmp(a,"MANAGE")){if(strcmp(b,"0")&&strcmp(b,"1"))return "INVALID";r.verb=!strcmp(a,"MANAGE")?Verb::Manage:Verb::Output;r.value=b[0]-'0';}
    else if(!strcmp(a,"ROUTE")){if(strcmp(b,"USB")&&strcmp(b,"BLE"))return "INVALID";r.verb=Verb::Route;r.value=!strcmp(b,"BLE");}
    else if(!strcmp(a,"LANGUAGE")){if(strcmp(b,"ZH")&&strcmp(b,"EN"))return "INVALID";r.verb=Verb::Language;r.value=!strcmp(b,"EN");}
    else if(!strcmp(a,"PAGE")){r.verb=Verb::Page;if(!strcmp(b,"HOME"))r.value=0;else if(!strcmp(b,"SETTINGS"))r.value=1;else if(!strcmp(b,"DIAG"))r.value=2;else if(!strcmp(b,"INFO"))r.value=4;else if(!strcmp(b,"ACTIVITY"))r.value=5;else if(!strcmp(b,"MANAGEMENT"))r.value=6;else if(!strcmp(b,"LANGUAGE"))r.value=7;else if(!strcmp(b,"TOOLS"))r.value=8;else return "INVALID";}
    else if(!strcmp(a,"COUNTERS")&&!strcmp(b,"RESET")){r.verb=Verb::Counters;}
    else return "UNSUPPORTED";
    return nullptr;
  }else return "UNSUPPORTED";
  return count==3?nullptr:"INVALID";
}
}
