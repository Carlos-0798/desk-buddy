#include <cstdio>
#include <cstdlib>
#include <cstring>
#include "../firmware/DeskController/HubConfig.h"
#include "../firmware/DeskController/HubProtocol.h"
#include "hub-store-fakes/InternalFileSystem.h"
InternalFileSystem InternalFS;
#include "../firmware/DeskController/HubConfigStore.cpp"
static unsigned checks=0;
#define CHECK(x) do{++checks;if(!(x)){fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x);exit(1);}}while(false)
using deskconfig::Bank;
void reset(){InternalFS=InternalFileSystem{};}
auto entry(const char* p)->Adafruit_LittleFS::Entry& {int i=InternalFS.find(p);CHECK(i>=0);return InternalFS.files[i];}
void loaded(const Bank& b){Bank got;CHECK(deskconfigstore::load(got));CHECK(deskconfig::same(b,got));}
void parser(const char* line,const char* error,deskhub::Verb verb=deskhub::Verb::Invalid,uint32_t id=1){
  char buf[512];CHECK(strlen(line)<sizeof(buf));strcpy(buf,line);deskhub::Request r;
  const char* got=deskhub::parse(buf,r);CHECK((!error&&!got)||(error&&got&&!strcmp(error,got)));
  CHECK(r.id==id);if(!error)CHECK(r.verb==verb);
}
int main(){
  Bank d=deskconfig::defaults();CHECK(sizeof(d)==224);CHECK(deskconfig::validate(d));
  for(unsigned profile=0;profile<3;++profile)for(unsigned theme=0;theme<256;++theme)for(unsigned face=0;face<256;++face){
    Bank b=d;b.bytes[1]=profile;b.profile()[0]=theme;b.profile()[1]=face;
    CHECK(deskconfig::validate(b)==(theme<8&&face<12));
  }
  char hex[449];deskconfig::toHex(d,hex);CHECK(strlen(hex)==448);Bank got;CHECK(deskconfig::fromHex(hex,got));CHECK(deskconfig::same(d,got));
  for(unsigned i=0;i<448;++i){char original=hex[i];hex[i]='X';CHECK(!deskconfig::fromHex(hex,got));hex[i]=original;}
  CHECK(!deskconfig::fromHex("",got));CHECK(!deskconfig::fromHex(nullptr,got));
  for(unsigned n=0;n<3;++n){
    Bank b=d;b.bytes[1]=n;CHECK(deskconfig::validate(b));
    for(unsigned i=0;i<12;++i){
      auto* q=b.profile()+24+i*4;
      for(unsigned key=0;key<256;++key){q[0]=2;q[1]=key;q[2]=0;q[3]=255;CHECK(deskconfig::validate(b)==(key>=4&&key<=0x73));}
      q[1]=4;for(unsigned mod=0;mod<256;++mod){q[3]=mod;CHECK(deskconfig::validate(b));}
      q[0]=0;q[1]=q[2]=q[3]=0;CHECK(deskconfig::validate(b));q[3]=1;CHECK(!deskconfig::validate(b));
      q[3]=0;q[0]=1;for(unsigned v=0;v<256;++v){q[1]=v;CHECK(deskconfig::validate(b)==deskconfig::consumer(v));}
      q[0]=3;for(unsigned v=0;v<8;++v){q[1]=v;CHECK(deskconfig::validate(b)==(v>=1&&v<=4));}
      // Reset preceding binding before validating the next one.
      b=d;b.bytes[1]=n;
    }
    for(unsigned pos=0;pos<24;++pos){
      if(pos==4||pos==5)continue;
      Bank b=d;b.bytes[1]=n;b.profile()[pos]=255;CHECK(!deskconfig::validate(b));
    }
    b=d;b.bytes[1]=n;memset(b.profile()+8,0,16);CHECK(!deskconfig::validate(b));
    memcpy(b.profile()+8,"15CHARACTERS_OK!",15);CHECK(deskconfig::validate(b));
    b.profile()[23]='X';CHECK(!deskconfig::validate(b));
  }
  for(unsigned i=3;i<8;++i){Bank b=d;b.bytes[i]=1;CHECK(!deskconfig::validate(b));}
  Bank wrong=d;wrong.bytes[0]=2;CHECK(!deskconfig::validate(wrong));wrong=d;wrong.bytes[2]=2;CHECK(!deskconfig::validate(wrong));
  using V=deskhub::Verb;
  parser("HUB 1 HELLO",nullptr,V::Hello);parser("HUB 1 GET",nullptr,V::Get);
  parser("HUB 1 GET EXTRA","INVALID");parser("HUB 1 HELLO EXTRA","INVALID");
  parser("HUB 1 REVERT EXTRA","INVALID");parser("HUB 1 STATUS EXTRA","INVALID");
  parser("HUB 2147483647 STATUS",nullptr,V::Status,2147483647u);
  parser("HUB 2147483648 GET","INVALID",V::Invalid,0);
  parser("HUB 4294967296 GET","INVALID",V::Invalid,0);
  parser("HUB 0 GET","INVALID",V::Invalid,0);parser("HUB -1 GET","INVALID",V::Invalid,0);
  parser("HUB 1 NOPE","UNSUPPORTED");parser("HUB 1 CONTROL OUTPUT 1",nullptr,V::Output);
  parser("HUB 1 CONTROL OUTPUT 1 EXTRA","INVALID");parser("HUB 1 CONTROL OUTPUT 2","INVALID");
  parser("HUB 1 CONTROL ROUTE BLE",nullptr,V::Route);parser("HUB 1 CONTROL ROUTE USB",nullptr,V::Route);
  parser("HUB 1 CONTROL LANGUAGE ZH",nullptr,V::Language);parser("HUB 1 CONTROL LANGUAGE EN",nullptr,V::Language);
  parser("HUB 1 CONTROL PAGE CALIBRATE","INVALID");parser("HUB 1 CONTROL COUNTERS RESET",nullptr,V::Counters);
  parser("HUB 1 SELECT 4294967295 2",nullptr,V::Select);parser("HUB 1 SELECT 4294967296 2","INVALID");
  parser("HUB 1 SELECT 1 3","INVALID");parser("HUB 1 SELECT 1 0 EXTRA","INVALID");
  char request[512];snprintf(request,sizeof(request),"HUB 1 PREVIEW 1 %s",hex);parser(request,nullptr,V::Preview);
  strcat(request," EXTRA");parser(request,"INVALID");
  deskconfig::Runtime runtime;CHECK(runtime.revision()==1&&!runtime.preview());
  Bank b=d;b.profile()[0]=2;CHECK(runtime.apply(b,false));CHECK(runtime.preview()&&runtime.revision()==2);
  runtime.revert();CHECK(deskconfig::same(runtime.bank(),d)&&!runtime.preview()&&runtime.revision()==3);
  CHECK(runtime.apply(b,true));CHECK(runtime.select(2));runtime.revert();CHECK(deskconfig::same(runtime.bank(),b));
  CHECK(!runtime.apply(wrong,true));CHECK(!runtime.select(3));
  for(unsigned profile=0;profile<3;++profile)for(unsigned mode=0;mode<256;++mode){auto x=d;x.bytes[8+profile*72+6]=mode;CHECK(validate(x)==(mode<3));}
  reset();Bank empty;CHECK(!deskconfigstore::load(empty));CHECK(InternalFS.writes==0);
  CHECK(!deskconfigstore::save(wrong));CHECK(InternalFS.baseBeginCalls==1); // prior load only
  InternalFS.mountOkay=false;CHECK(!deskconfigstore::save(d));CHECK(InternalFS.writes==0);reset();
  const char* unrelated[]={"/deskcal0.bin","/deskui0.bin","/bond/example"};
  for(auto p:unrelated){int i=InternalFS.create(p);CHECK(i>=0);InternalFS.files[i].bytes[0]=0xa5;InternalFS.files[i].length=1;}
  CHECK(deskconfigstore::save(d));loaded(d);CHECK(entry("/deskcfg0.bin").length==240);
  unsigned writes=InternalFS.writes,removes=InternalFS.removes;CHECK(deskconfigstore::save(d));
  CHECK(InternalFS.writes==writes&&InternalFS.removes==removes);
  CHECK(deskconfigstore::save(b));loaded(b);CHECK(deskconfigstore::save(d));loaded(d);
  for(auto p:unrelated)CHECK(entry(p).bytes[0]==0xa5&&entry(p).length==1);
  for(unsigned cut=0;cut<240;++cut){
    reset();CHECK(deskconfigstore::save(d));CHECK(deskconfigstore::save(b));
    uint8_t newest[240];memcpy(newest,entry("/deskcfg1.bin").bytes,240);
    InternalFS.writeLimit=cut;CHECK(!deskconfigstore::save(d));loaded(b);
    CHECK(!memcmp(newest,entry("/deskcfg1.bin").bytes,240));
    InternalFS.writeLimit=512;CHECK(deskconfigstore::save(d));loaded(d);
  }
  for(unsigned byte=0;byte<240;++byte){
    reset();CHECK(deskconfigstore::save(d));CHECK(deskconfigstore::save(b));
    entry("/deskcfg1.bin").bytes[byte]^=0x80;loaded(d);
  }
  reset();CHECK(deskconfigstore::save(d));InternalFS.corruptWrite=true;CHECK(!deskconfigstore::save(b));loaded(d);
  reset();CHECK(deskconfigstore::save(d));CHECK(deskconfigstore::save(b));InternalFS.removeOkay=false;CHECK(!deskconfigstore::save(d));loaded(b);
  reset();CHECK(deskconfigstore::save(d));for(unsigned i=0;i<7;++i){char p[20];snprintf(p,sizeof(p),"/f%u",i);CHECK(InternalFS.create(p)>=0);}
  CHECK(!deskconfigstore::save(b));loaded(d);CHECK(InternalFS.renames==0&&InternalFS.derivedBeginCalls==0);
  printf("Hub bank/parser/store: %u assertions, 0 failures. FakeFS fault injection; no hardware.\n",checks);
}
