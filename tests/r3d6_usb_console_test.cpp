// Actual UsbConsole.cpp; fake only the CDC peripheral and event/register inputs.
// Built, not executed under this host's current application-control policy.
#include "UsbConsole.h"
#include <Adafruit_TinyUSB.h>
#include <device/dcd.h>
#include <stdio.h>
#include <string.h>
PowerStub powerStub={1};UsbdStub usbdStub={};unsigned criticalDepth=0;
static uint32_t now=0,space=4096,writeCalls=0,maxWrite=0,locksWhileCritical=0;
static bool dtr=true;
static uint8_t input[4096],sent[20000];static unsigned inputAt=0,inputLen=0,sentLen=0;
static uint8_t fifo[8192];static unsigned fifoLen=0,flushCalls=0;
static unsigned checks=0,failures=0;
#define CHECK(x) do{++checks;if(!(x)){++failures;fprintf(stderr,"FAIL %d %s\n",__LINE__,#x);}}while(0)
uint32_t millis(){return now;}
bool tud_cdc_n_connected(uint8_t){return dtr;}
uint32_t tud_cdc_n_write_available(uint8_t){if(criticalDepth)++locksWhileCritical;return space;}
uint32_t tud_cdc_n_write(uint8_t,const void* p,uint32_t n){
  if(criticalDepth)++locksWhileCritical;++writeCalls;if(n>maxWrite)maxWrite=n;
  CHECK(n<=space);CHECK(fifoLen+n<=sizeof(fifo));memcpy(fifo+fifoLen,p,n);fifoLen+=n;space-=n;
  // Match the real CDC trigger: a short first/trailing packet is not automatic.
  if(fifoLen>=64)tud_cdc_n_write_flush(0);return n;
}
uint32_t tud_cdc_n_write_flush(uint8_t){if(criticalDepth)++locksWhileCritical;++flushCalls;
  const auto n=fifoLen;CHECK(sentLen+n<=sizeof(sent));memcpy(sent+sentLen,fifo,n);sentLen+=n;fifoLen=0;return n;}
bool tud_cdc_n_write_clear(uint8_t){if(criticalDepth)++locksWhileCritical;fifoLen=0;return true;}
uint32_t tud_cdc_n_read(uint8_t,void* p,uint32_t n){if(criticalDepth)++locksWhileCritical;
  if(n>inputLen-inputAt)n=inputLen-inputAt;memcpy(p,input+inputAt,n);inputAt+=n;return n;}
void packet(const char* s){inputAt=0;inputLen=strlen(s);memcpy(input,s,inputLen);tud_cdc_rx_cb(0);}
void control(bool value){usbdStub={0x21,0x22,0,0};tud_event_hook_cb(0,DCD_EVENT_SETUP_RECEIVED,true);dtr=value;}
void fresh(){control(false);deskusb::poll(++now);control(true);deskusb::poll(++now);space=4096;sentLen=0;writeCalls=0;CHECK(deskusb::ready());}
int main(){
  fresh();CHECK(deskusb::beginReply());deskusb::out().write("HUB 1 OK test=1\n");deskusb::finishReply();
  CHECK(!deskusb::canReply());CHECK(writeCalls==0);deskusb::pump(++now);CHECK(writeCalls==1);CHECK(deskusb::canReply());
  CHECK(sentLen==16);CHECK(sent[sentLen-1]=='\n');CHECK(fifoLen==0);
  fresh();deskusb::out().write("\n");deskusb::pump(++now);CHECK(sentLen==1);CHECK(sent[0]=='\n');CHECK(fifoLen==0);
  fresh();CHECK(deskusb::beginReply());for(unsigned i=0;i<64;++i)deskusb::out().write(uint8_t('a'));deskusb::out().write(uint8_t('\n'));deskusb::finishReply();
  deskusb::pump(++now);CHECK(sentLen==64);deskusb::pump(++now);CHECK(sentLen==65);CHECK(sent[64]=='\n');CHECK(fifoLen==0);
  // FIFO starvation is one no-op attempt per pump, not a busy wait.
  CHECK(deskusb::beginReply());for(unsigned i=0;i<4096;++i)deskusb::out().write(uint8_t('x'));deskusb::out().write(uint8_t('\n'));deskusb::finishReply();
  space=0;unsigned calls=writeCalls;deskusb::pump(++now);CHECK(writeCalls==calls);
  space=17;deskusb::pump(++now);CHECK(writeCalls==calls+1);CHECK(maxWrite<=64);CHECK(deskusb::queuedBytes()==4080);
  const auto drop=deskusb::droppedLines();deskusb::out().write("KEY 1\n");CHECK(deskusb::droppedLines()==drop+1);
  now+=5000;deskusb::poll(now);CHECK(deskusb::stalled());CHECK(!deskusb::ready());CHECK(deskusb::txTimeouts()==1);
  // Reopen discards expired response, never completes its old tail.
  fresh();CHECK(deskusb::queuedBytes()==0);deskusb::pump(++now);CHECK(writeCalls==0);
  // Mid-assembly DTR boundary discards the whole reply.
  CHECK(deskusb::beginReply());deskusb::out().write("HUB 2 OK ");control(false);control(true);deskusb::out().write("bad=1\n");deskusb::finishReply();deskusb::poll(++now);CHECK(deskusb::queuedBytes()==0);
  // A rapid DTR off/on between loop polls cannot append a new command to old RX.
  packet("HUB 3 PRE");CHECK(deskusb::read()=='H');control(false);control(true);packet("HUB 4 HELLO\n");deskusb::poll(++now);
  const char* want="HUB 4 HELLO\n";for(;*want;++want)CHECK(deskusb::read()==*want);CHECK(deskusb::read()==-1);
  // Unrelated EP0/HID requests must not tear down the serial session.
  auto generation=deskusb::generation();usbdStub={0xA1,1,2,0};tud_event_hook_cb(0,DCD_EVENT_SETUP_RECEIVED,true);deskusb::poll(++now);CHECK(deskusb::generation()==generation);
  // Oversized response is discarded, with no partial bytes ever submitted.
  fresh();CHECK(deskusb::beginReply());for(unsigned i=0;i<4098;++i)deskusb::out().write(uint8_t('y'));deskusb::finishReply();CHECK(deskusb::queuedBytes()==0);CHECK(writeCalls==0);
  // RX ring overload poisons this session through subsequent packets.
  char large[601];memset(large,'q',600);large[600]=0;packet(large);packet(large);deskusb::poll(++now);CHECK(deskusb::stalled());CHECK(deskusb::rxOverflows()==1);packet("HUB 5 HELLO\n");CHECK(deskusb::read()==-1);fresh();
  // Physical VBUS removal rejects stale DTR even before the event task catches up.
  packet("HUB 6 HELLO\n");powerStub.USBREGSTATUS=0;deskusb::poll(++now);CHECK(!deskusb::ready());powerStub.USBREGSTATUS=1;deskusb::poll(++now);CHECK(deskusb::read()==-1);
  // Expiry works over millis rollover.
  fresh();now=0xFFFFFF00;CHECK(deskusb::beginReply());deskusb::out().write("x\n");deskusb::finishReply();space=0;now+=4999;deskusb::poll(now);CHECK(!deskusb::stalled());++now;deskusb::poll(now);CHECK(deskusb::stalled());
  CHECK(locksWhileCritical==0);CHECK(criticalDepth==0);printf("checks=%u failures=%u\n",checks,failures);return failures?1:0;
}
