#pragma once
#include "DisplaySleep.h"
#include <string.h>
namespace deskdisplaystore {
constexpr uint16_t RecordSize=20,CrcOffset=16;
struct Record {uint32_t sequence;deskdisplay::Settings settings;};
inline uint16_t read16(const uint8_t* p){return uint16_t(p[0])|(uint16_t(p[1])<<8);}
inline uint32_t read32(const uint8_t* p){return uint32_t(p[0])|(uint32_t(p[1])<<8)|(uint32_t(p[2])<<16)|(uint32_t(p[3])<<24);}
inline void write16(uint8_t* p,uint16_t n){p[0]=uint8_t(n);p[1]=uint8_t(n>>8);}
inline void write32(uint8_t* p,uint32_t n){for(uint8_t i=0;i<4;++i)p[i]=uint8_t(n>>(i*8));}
inline uint32_t crc32(const uint8_t* p,uint16_t n){uint32_t crc=0xffffffffu;for(uint16_t i=0;i<n;++i){crc^=p[i];for(uint8_t b=0;b<8;++b)crc=(crc>>1)^((crc&1)?0xedb88320u:0);}return crc^0xffffffffu;}
// DDSP[4], version:u16, size:u16, sequence:u32, USB:u16, battery:u16, CRC32.
inline void encode(const Record& r,uint8_t* p){memcpy(p,"DDSP",4);write16(p+4,1);write16(p+6,RecordSize);write32(p+8,r.sequence);write16(p+12,r.settings.usbSeconds);write16(p+14,r.settings.batterySeconds);write32(p+CrcOffset,crc32(p,CrcOffset));}
inline bool decode(const uint8_t* p,Record& r){
  const deskdisplay::Settings settings(read16(p+12),read16(p+14));
  if(memcmp(p,"DDSP",4)||read16(p+4)!=1||read16(p+6)!=RecordSize||!settings.valid()||read32(p+CrcOffset)!=crc32(p,CrcOffset))return false;
  r.sequence=read32(p+8);r.settings=settings;return true;
}
inline int latestSlot(const Record* r,const bool* valid){if(!valid[0])return valid[1]?1:-1;if(!valid[1])return 0;const uint32_t d=r[1].sequence-r[0].sequence;return d&&d<0x80000000u?1:0;}
inline bool sameRecord(const Record& a,const Record& b){return a.sequence==b.sequence&&a.settings==b.settings;}
}
