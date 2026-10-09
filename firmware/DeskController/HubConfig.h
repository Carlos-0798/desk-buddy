#pragma once
#include <stdint.h>
#include <stddef.h>
#include <string.h>

// Protocol 1 is an explicitly packed byte array, never a native C++ struct.
namespace deskconfig {
constexpr size_t BankSize=224, ProfileSize=72, ProfileCount=3, BindingCount=12;
constexpr uint8_t ThemeCount=8, FaceCount=12, HomeModeCount=3;
struct Binding { uint8_t kind; uint16_t value; uint8_t modifiers; };
struct Bank {
  uint8_t bytes[BankSize] = {};
  uint8_t active() const { return bytes[1]; }
  const uint8_t* profile() const { return bytes+8+ProfileSize*active(); }
  uint8_t* profile() { return bytes+8+ProfileSize*active(); }
  Binding binding(uint8_t index) const {
    if(index>=BindingCount)return {0,0,0};
    const uint8_t* p=profile()+24+4*index;
    return {p[0],static_cast<uint16_t>(p[1]|(uint16_t(p[2])<<8)),p[3]};
  }
};
inline bool same(const Bank& a,const Bank& b){return memcmp(a.bytes,b.bytes,BankSize)==0;}
// Only the active bindings and encoder direction affect future HID events.
// Appearance, names and edits to inactive profiles must not interrupt output.
inline bool sameInputMapping(const Bank& a,const Bank& b){
  return a.profile()[3]==b.profile()[3]&&
    memcmp(a.profile()+24,b.profile()+24,4*BindingCount)==0;
}
inline bool consumer(uint16_t v){return v==0xCD||v==0xB6||v==0xB5||v==0xE2||v==0xE9||v==0xEA;}
inline bool validate(const Bank& b){
  if(b.bytes[0]!=1||b.bytes[1]>=ProfileCount||b.bytes[2]!=ProfileCount)return false;
  for(uint8_t i=3;i<8;++i)if(b.bytes[i])return false;
  for(uint8_t n=0;n<ProfileCount;++n){
    const uint8_t* p=b.bytes+8+n*ProfileSize;
    if(p[0]>=ThemeCount||p[1]>=FaceCount||p[2]>1||p[3]>1||p[6]>=HomeModeCount||p[7])return false;
    bool ended=false;
    if(!p[8]||p[23])return false;
    for(uint8_t i=8;i<24;++i){
      if(!p[i])ended=true;
      else if(ended||p[i]<0x20||p[i]>0x7E)return false;
    }
    for(uint8_t i=0;i<BindingCount;++i){
      const uint8_t* q=p+24+4*i;const uint16_t v=q[1]|(uint16_t(q[2])<<8);
      switch(q[0]){
        case 0:if(v||q[3])return false;break;
        case 1:if(!consumer(v)||q[3])return false;break;
        case 2:if(v<4||v>0x73)return false;break;
        case 3:if(v<1||v>5||q[3])return false;break;
        default:return false;
      }
    }
  }
  return true;
}
inline Bank defaults(){
  Bank b;b.bytes[0]=1;b.bytes[2]=ProfileCount;
  const char* names[]={"MEDIA","STUDY","WORK"};
  const uint8_t kinds[]={1,1,1,1,3,3,1,1,1,1,1,1};
  const uint16_t values[]={0xCD,0xB6,0xB5,0xE2,1,2,0xE9,0xEA,0xE2,0xB6,0xCD,0xB5};
  for(uint8_t n=0;n<ProfileCount;++n){
    uint8_t* p=b.bytes+8+n*ProfileSize;p[2]=1;strcpy(reinterpret_cast<char*>(p+8),names[n]);
    for(uint8_t i=0;i<BindingCount;++i){uint8_t* q=p+24+4*i;q[0]=kinds[i];q[1]=values[i];q[2]=values[i]>>8;}
  }
  return b;
}
inline int hexDigit(char c){return c>='0'&&c<='9'?c-'0':(c>='A'&&c<='F'?c-'A'+10:(c>='a'&&c<='f'?c-'a'+10:-1));}
inline bool fromHex(const char* text,Bank& result){
  if(!text||strlen(text)!=BankSize*2)return false;
  Bank candidate;
  for(size_t i=0;i<BankSize;++i){int a=hexDigit(text[2*i]),b=hexDigit(text[2*i+1]);if(a<0||b<0)return false;candidate.bytes[i]=(a<<4)|b;}
  if(!validate(candidate))return false;result=candidate;return true;
}
inline void toHex(const Bank& b,char* result){
  constexpr char digits[]="0123456789ABCDEF";
  for(size_t i=0;i<BankSize;++i){result[i*2]=digits[b.bytes[i]>>4];result[i*2+1]=digits[b.bytes[i]&15];}result[BankSize*2]=0;
}
class Runtime {
 public:
  Runtime():current_(defaults()),persisted_(current_){}
  const Bank& bank()const{return current_;}
  const Bank& persistedBank()const{return persisted_;}
  uint32_t revision()const{return revision_;}
  bool preview()const{return preview_;}
  void initialize(const Bank& bank){current_=persisted_=bank;revision_=1;preview_=false;}
  bool apply(const Bank& bank,bool persisted){
    if(!validate(bank))return false;
    current_=bank;if(persisted)persisted_=bank;preview_=!persisted;advance();return true;
  }
  void revert(){current_=persisted_;preview_=false;advance();}
  bool select(uint8_t index){if(index>=ProfileCount)return false;current_.bytes[1]=index;preview_=true;advance();return true;}
 private:
  void advance(){if(++revision_==0)++revision_;}
  Bank current_,persisted_;uint32_t revision_=1;bool preview_=false;
};
}
