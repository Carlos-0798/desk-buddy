#include "DisplayPreferencesStore.h"
#include "DisplayPreferencesCodec.h"
#include <Adafruit_LittleFS.h>
#include <InternalFileSystem.h>
namespace deskdisplaystore {
namespace {
using Adafruit_LittleFS_Namespace::File;
using Adafruit_LittleFS_Namespace::FILE_O_READ;
using Adafruit_LittleFS_Namespace::FILE_O_WRITE;
const char* const slots[]={"/deskdsp0.bin","/deskdsp1.bin"};
const char* error="none";
bool fail(const char* message){error=message;return false;}
bool mount(){return InternalFS.Adafruit_LittleFS::begin();} // never format
bool readRecord(const char* path,Record& r){File f(InternalFS);if(!f.open(path,FILE_O_READ))return false;uint8_t data[RecordSize];const bool complete=!f.isDirectory()&&f.size()==RecordSize&&f.read(data,RecordSize)==RecordSize;f.close();return complete&&decode(data,r);}
}
const char* lastErrorName(){return error;}
bool load(deskdisplay::Settings& settings){if(!mount())return false;Record r[2]={};const bool valid[]={readRecord(slots[0],r[0]),readRecord(slots[1],r[1])};const int latest=latestSlot(r,valid);if(latest<0)return false;settings=r[latest].settings;return true;}
bool save(const deskdisplay::Settings& settings){
  error="none";if(!settings.valid())return fail("invalid_settings");if(!mount())return fail("mount");
  Record r[2]={};const bool valid[]={readRecord(slots[0],r[0]),readRecord(slots[1],r[1])};const int latest=latestSlot(r,valid);
  if(latest>=0&&r[latest].settings==settings)return true; // verified existing record; no flash write
  const int target=latest==0?1:0;const Record candidate={latest<0?1u:r[latest].sequence+1u,settings};
  uint8_t data[RecordSize];encode(candidate,data);
  // FILE_O_WRITE appends in this BSP. Preserve the newest valid slot until the
  // alternate is fully written/read/CRC verified; do not use LittleFS rename.
  if(InternalFS.exists(slots[target])&&!InternalFS.remove(slots[target]))return fail("slot_remove");
  File f(InternalFS);if(!f.open(slots[target],FILE_O_WRITE))return fail("slot_open");
  const bool complete=f.write(data,RecordSize)==RecordSize;f.flush();f.close();
  if(!complete)return fail("slot_write");Record verified={};
  if(!readRecord(slots[target],verified)||!sameRecord(candidate,verified))return fail("slot_verify");
  return true;
}
}
