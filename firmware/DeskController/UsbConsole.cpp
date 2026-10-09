#include "UsbConsole.h"
#include <Adafruit_TinyUSB.h>
#include <device/dcd.h>
#include <string.h>

namespace deskusb {
namespace {
constexpr uint16_t Capacity=4097, RxCapacity=1024;
constexpr uint32_t ProgressTimeoutMs=5000;
volatile uint32_t eventEpoch=0,rxEpoch=UINT32_MAX,rxFaults=0;
volatile uint16_t rxHead=0,rxTail=0;
volatile bool rxPoison=false;
uint8_t rxData[RxCapacity];
uint32_t seenEpoch=UINT32_MAX,session=0,assemblyEpoch=0,queuedEpoch=0;
uint32_t drops=0,timeouts=0,lastProgress=0;
uint16_t assembled=0,queued=0,offset=0;
uint8_t assembly[Capacity],pending[Capacity];
bool connected=false,blocked=false,replyMode=false,discard=false;
bool physicalReady(){return (NRF_POWER->USBREGSTATUS&POWER_USBREGSTATUS_VBUSDETECT_Msk)&&tud_cdc_n_connected(0);}
void clearLoop(){assembled=queued=offset=0;replyMode=discard=false;}
void commit(){
  if(!assembled)return;
  if(discard||!ready()||assemblyEpoch!=eventEpoch||queued){++drops;}
  else {memcpy(pending,assembly,assembled);queued=assembled;offset=0;queuedEpoch=assemblyEpoch;lastProgress=millis();}
  assembled=0;discard=false;
}
class Writer:public Print {
 public:
  size_t write(uint8_t c)override{
    // A generation change invalidates a response being assembled as well.
    if(!ready()||(assembled&&assemblyEpoch!=eventEpoch)){discard=true;}
    if(!assembled)assemblyEpoch=eventEpoch;
    if(!discard){if(assembled<Capacity)assembly[assembled++]=c;else discard=true;}
    if(c=='\n'&&!replyMode){if(discard){++drops;assembled=0;discard=false;}else commit();}
    return 1; // Best-effort log writes never retry or block their caller.
  }
  using Print::write;
}writer;
}
Print& out(){return writer;}
bool ready(){return connected&&!blocked&&!rxPoison&&seenEpoch==eventEpoch&&physicalReady();}
uint32_t generation(){return session;}
uint32_t droppedLines(){return drops;}
uint32_t txTimeouts(){return timeouts;}
uint32_t rxOverflows(){return rxFaults;}
uint16_t queuedBytes(){return queued-offset;}
bool stalled(){return blocked||rxPoison;}
void poll(uint32_t now){
  const bool live=physicalReady();
  // Also invalidate immediately on a polled power/DTR loss if its hardware
  // notification has not reached the hook yet.
  taskENTER_CRITICAL();
  if(connected&&!live&&seenEpoch==eventEpoch){++eventEpoch;rxPoison=false;}
  const uint32_t epoch=eventEpoch;
  taskEXIT_CRITICAL();
  if(epoch!=seenEpoch||live!=connected){
    // Do not clear rxData here: a callback may already have copied the first
    // packet of this new generation. Only the callback owns ring resets.
    seenEpoch=epoch;connected=live;blocked=false;clearLoop();++session;
    // Drop bytes from the old CDC TX FIFO. No scheduler/IRQ lock is held.
    tud_cdc_n_write_clear(0);
  }
  if(rxPoison&&!blocked){blocked=true;clearLoop();++session;tud_cdc_n_write_clear(0);}
  if(queued&&uint32_t(now-lastProgress)>=ProgressTimeoutMs){
    ++timeouts;++drops;blocked=true;clearLoop();++session;tud_cdc_n_write_clear(0);
  }
}
bool canReply(){return ready()&&!queued&&!replyMode;}
bool beginReply(){
  if(!canReply())return false;
  if(assembled||discard){++drops;assembled=0;discard=false;}
  replyMode=true;assemblyEpoch=eventEpoch;return true;
}
void finishReply(){
  if(!replyMode)return;
  replyMode=false;
  if(discard){++drops;assembled=0;discard=false;return;}
  commit();
}
int read(){
  if(!ready())return -1;
  taskENTER_CRITICAL();
  if(seenEpoch!=eventEpoch||rxEpoch!=eventEpoch||rxTail==rxHead||rxPoison){taskEXIT_CRITICAL();return -1;}
  const uint8_t value=rxData[rxTail];rxTail=(rxTail+1)%RxCapacity;
  taskEXIT_CRITICAL();return value;
}
void pump(uint32_t now){
  poll(now);
  if(!ready()||!queued||queuedEpoch!=eventEpoch)return;
  const uint32_t epoch=eventEpoch;
  const uint32_t available=tud_cdc_n_write_available(0);
  uint16_t count=queued-offset;if(count>64)count=64;if(count>available)count=static_cast<uint16_t>(available);
  if(!count)return;
  // The sole loop writer never retries. TinyUSB may briefly acquire its own
  // FIFO mutex; calling it under task suspension/critical masking is forbidden.
  const uint32_t written=tud_cdc_n_write(0,pending+offset,count);
  if(epoch!=eventEpoch||!physicalReady()){poll(now);return;}
  // A final fragment shorter than the endpoint packet must be submitted too;
  // otherwise its LF can sit in FIFO with no later write to trigger transfer.
  // This single TinyUSB attempt never waits for host completion (not Serial.flush).
  if(written)tud_cdc_n_write_flush(0);
  if(written){offset+=static_cast<uint16_t>(written);lastProgress=now;if(offset==queued)queued=offset=0;}
}

// Called from TinyUSB's event hook: bounded scalar stores only, no FIFO APIs.
void boundary(){++eventEpoch;rxPoison=false;}
void receive(){
  uint8_t buffer[64];
  // TinyUSB's RX FIFO is bounded; cap work even under a continuously sending host.
  for(uint8_t pass=0;pass<8;++pass){
    const uint32_t epoch=eventEpoch;
    const uint32_t n=tud_cdc_n_read(0,buffer,sizeof(buffer));
    if(!n)break;
    if(!physicalReady()||epoch!=eventEpoch)continue;
    taskENTER_CRITICAL();
    if(epoch==eventEpoch){
      if(rxEpoch!=epoch){rxEpoch=epoch;rxHead=rxTail=0;}
      if(!rxPoison)for(uint32_t i=0;i<n;++i){
        const uint16_t next=(rxHead+1)%RxCapacity;
        if(next==rxTail){rxPoison=true;++rxFaults;break;}
        rxData[rxHead]=buffer[i];rxHead=next;
      }
    }
    taskEXIT_CRITICAL();
  }
}
}
extern "C" void tud_cdc_rx_cb(uint8_t instance){if(instance==0)deskusb::receive();}
extern "C" void tud_event_hook_cb(uint8_t rhport,uint32_t event,bool inIsr){
  (void)rhport;(void)inIsr;
  bool boundary=event==DCD_EVENT_BUS_RESET||event==DCD_EVENT_UNPLUGGED||event==DCD_EVENT_SUSPEND;
  // nRF52840's SETUP registers are latched before dcd_event_setup_received.
  // The board's primary CDC control interface is 0 (CDC precedes HID).
  // Preserve the library's own line-state callback and its 1200-baud DFU path.
  if(event==DCD_EVENT_SETUP_RECEIVED&&NRF_USBD->BMREQUESTTYPE==0x21u&&
     NRF_USBD->BREQUEST==0x22u&&NRF_USBD->WINDEXL==0&&NRF_USBD->WINDEXH==0)boundary=true;
  if(boundary)deskusb::boundary();
}
