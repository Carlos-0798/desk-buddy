#pragma once
#include <stdint.h>
#include <stddef.h>
namespace deskmanage {
enum class Source:uint8_t { None=0,Usb=1,Ble=2 };
// Loop-owned write lease. Failed requests never commit/extend a lease.
class Lease {
 public:
  Source owner(uint32_t now){expire(now);return owner_;}
  uint16_t remaining(uint32_t now){expire(now);return owner_==Source::None?0:static_cast<uint16_t>(10000u-(now-at_));}
  bool available(Source s,uint32_t now){expire(now);return owner_==Source::None||owner_==s;}
  void commit(Source s,uint32_t now){owner_=s;at_=now;}
  void clear(Source s=Source::None){if(s==Source::None||s==owner_)owner_=Source::None;}
 private:
  void expire(uint32_t now){if(owner_!=Source::None&&uint32_t(now-at_)>=10000u)owner_=Source::None;}
  Source owner_=Source::None;uint32_t at_=0;
};
// Physical/USB permission is separate from pairing, encryption and HID output.
class Permission {
 public:
  void open(uint32_t now){state_=1;at_=now;active_=false;}
  void close(){state_=0;}
  void update(uint32_t now,bool trusted){
    if(state_==1){if(uint32_t(now-at_)>=60000u)state_=0;else if(trusted)state_=2;}
    else if(state_==2&&(!trusted||uint32_t(now-at_)>=(active_?15000u:60000u)))state_=0;
  }
  void touch(uint32_t now){if(state_==2){at_=now;active_=true;}} // Successful authorized requests only.
  uint8_t state()const{return state_;}
  uint32_t remaining(uint32_t now){if(state_==1&&uint32_t(now-at_)>=60000u)state_=0;return state_==1?60000u-(now-at_):0;}
 private:uint8_t state_=0;uint32_t at_=0;bool active_=false;
};
// Bounded LF framing. Invalid/overflow/timed-out lines are discarded in full,
// through their next LF. No suffix is ever interpreted as a new command.
class Lines {
 public:
  enum class Result:uint8_t {None,Ready,Invalid};
  void clear(){length_=0;discard_=false;started_=false;}
  void abort(){length_=0;discard_=true;started_=false;}
  void tick(uint32_t now){if(started_&&uint32_t(now-last_)>=5000u)abort();}
  Result push(uint8_t ch,uint32_t now){
    tick(now);
    if(ch=='\n'){data_[length_]=0;const auto result=discard_?Result::Invalid:(length_?Result::Ready:Result::None);length_=0;discard_=false;started_=false;return result;}
    if(discard_)return Result::None;
    started_=true;last_=now;
    if(ch<0x20||ch>0x7e||length_>=511){abort();return Result::None;}
    data_[length_++]=static_cast<char>(ch);return Result::None;
  }
  char* data(){return data_;}
 private:char data_[512]={};uint16_t length_=0;uint32_t last_=0;bool discard_=false,started_=false;
};
}
