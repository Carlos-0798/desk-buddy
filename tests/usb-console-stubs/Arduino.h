#pragma once
#include <stdint.h>
#include <stddef.h>
#include <string.h>
struct PowerStub {uint32_t USBREGSTATUS;};
struct UsbdStub {uint32_t BMREQUESTTYPE,BREQUEST,WINDEXL,WINDEXH;};
extern PowerStub powerStub;extern UsbdStub usbdStub;
#define NRF_POWER (&powerStub)
#define NRF_USBD (&usbdStub)
#define POWER_USBREGSTATUS_VBUSDETECT_Msk 1u
extern unsigned criticalDepth;
#define taskENTER_CRITICAL() (++criticalDepth)
#define taskEXIT_CRITICAL() (--criticalDepth)
uint32_t millis();
class Print {public:virtual ~Print(){};virtual size_t write(uint8_t)=0;
  size_t write(const uint8_t* p,size_t n){for(size_t i=0;i<n;++i)write(p[i]);return n;}
  size_t write(const char* s){return write(reinterpret_cast<const uint8_t*>(s),strlen(s));}};
