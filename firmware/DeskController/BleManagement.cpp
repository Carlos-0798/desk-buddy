#include "BleManagement.h"
#include <string.h>
namespace deskble {
namespace {
BLEService service("8e7b0001-6f9a-4c4d-9f32-2f3c1d0b5a60");
BLECharacteristic rx("8e7b0002-6f9a-4c4d-9f32-2f3c1d0b5a60");
BLECharacteristic tx("8e7b0003-6f9a-4c4d-9f32-2f3c1d0b5a60");
bool started=false,link=false,secure=false,bond=false,subscribed=false;
volatile uint32_t epoch=0,subscriptionEpoch=0;
volatile bool disconnected=false,notifyValue=false,notifyRevoked=false,overflow=false;
volatile uint16_t head=0,tail=0;
constexpr uint16_t RingSize=1536;
uint8_t ring[RingSize];
uint32_t seenEpoch=0,seenSubscription=0,session=0;
uint16_t handle=BLE_CONN_HANDLE_INVALID;
// Standard GATT database migration indication, separate from Hub permission.
// Do not clear sysattrs: the bonded peer's existing HID/SC CCCDs must survive.
volatile uint32_t scConfirmEpoch=0;
volatile bool scConfirmSeen=false;
uint32_t scResult=UINT32_MAX,scStartedAt=0,scAttemptAt=0,scRangeResult=NRF_SUCCESS;
uint16_t scRangeStart=0,scRangeEnd=0;
uint8_t scAttempts=0;
bool scTracking=false,scQueued=false,scConfirmed=false,scStopped=false;
void resetServiceChange(){
  scResult=scRangeResult==NRF_SUCCESS?UINT32_MAX:scRangeResult;scAttempts=0;scTracking=scQueued=scConfirmed=false;scStopped=scRangeResult!=NRF_SUCCESS;
  taskENTER_CRITICAL();scConfirmSeen=false;taskEXIT_CRITICAL();
}
void serviceChangeTick(uint32_t now){
  if(!link||!secure||!bond)return;
  if(scQueued){if(scConfirmSeen&&scConfirmEpoch==seenEpoch)scConfirmed=true;return;}
  if(scStopped)return;
  if(!scTracking){scTracking=true;scStartedAt=scAttemptAt=now;return;}
  // Allow encryption/CCCD restoration and MTU exchange to settle. One attempt
  // per 500ms, at most six attempts/5s, never a blocking confirmation wait.
  if(scAttempts>=6||uint32_t(now-scStartedAt)>=5000u){scStopped=true;return;}
  if(uint32_t(now-scAttemptAt)<500u)return;
  scAttemptAt=now;
  vTaskSuspendAll();
  if(seenEpoch==epoch){
    scResult=sd_ble_gatts_service_changed(handle,scRangeStart,scRangeEnd);++scAttempts;
    if(scResult==NRF_SUCCESS)scQueued=true;
    else if(scResult!=NRF_ERROR_BUSY&&scResult!=NRF_ERROR_INVALID_STATE&&scResult!=BLE_ERROR_GATTS_SYS_ATTR_MISSING)scStopped=true;
  }
  xTaskResumeAll();
}
deskmanage::Permission permission;
deskmanage::Lines lines;
bool lineReady=false,lineInvalid=false;
class Reply:public Print {
 public:
  size_t write(uint8_t c)override{if(c=='\r')return 1;if(size>=4097){failed=true;return 0;}data[size++]=c;return 1;}
  void clear(){size=offset=0;failed=false;pending=false;}
  uint8_t data[4097];uint16_t size=0,offset=0;bool failed=false,pending=false;
  uint32_t queued=0,lastSent=0;
  uint32_t connectionEpoch=0,cccdEpoch=0;
}reply;
void resetRing(){taskENTER_CRITICAL();head=tail=0;overflow=false;taskEXIT_CRITICAL();}
void clearLoopQueues(){lines.clear();lineReady=lineInvalid=false;reply.clear();}
void clearQueues(){resetRing();clearLoopQueues();}
void clearObservedQueues(){
  // A callback may have advanced the generation after tick took its snapshot.
  // Never erase bytes already delivered by that newer connection/subscription.
  vTaskSuspendAll();
  if(seenEpoch==epoch&&seenSubscription==subscriptionEpoch)clearQueues();else clearLoopQueues();
  xTaskResumeAll();
}
void invalidate(){permission.close();clearObservedQueues();++session;}
BLEConnection* connection(){return started?Bluefruit.Connection(Bluefruit.connHandle()):nullptr;}
void receive(uint16_t conn,BLECharacteristic*,uint8_t* data,uint16_t len){
  BLEConnection* c=Bluefruit.Connection(conn);
  if(!c||!c->connected()||!c->secured()||!c->bonded()||!tx.notifyEnabled(conn))return;
  // Called synchronously on the BLE event task; only bounded byte copying.
  for(uint16_t i=0;i<len;++i){const uint16_t next=(head+1)%RingSize;if(next==tail){overflow=true;break;}ring[head]=data[i];__DMB();head=next;}
}
void cccd(uint16_t,BLECharacteristic*,uint16_t value){
  // Clear at the callback boundary, before the peer's next Write event. A later
  // loop tick must not discard fragments already received in this new session.
  taskENTER_CRITICAL();notifyValue=(value&1u)!=0;if(!notifyValue)notifyRevoked=true;++subscriptionEpoch;resetRing();taskEXIT_CRITICAL();
}
}
bool begin(){
  if(started)return true;
  service.setPermission(SECMODE_ENC_NO_MITM,SECMODE_ENC_NO_MITM);
  if(service.begin()!=ERROR_NONE)return false;
  rx.setProperties(CHR_PROPS_WRITE);rx.setPermission(SECMODE_NO_ACCESS,SECMODE_ENC_NO_MITM);rx.setMaxLen(20);rx.setWriteCallback(receive,false);
  if(rx.begin()!=ERROR_NONE)return false;
  tx.setProperties(CHR_PROPS_NOTIFY);tx.setPermission(SECMODE_ENC_NO_MITM,SECMODE_NO_ACCESS);tx.setMaxLen(20);tx.setCccdWriteCallback(cccd,false);
  if(tx.begin()!=ERROR_NONE)return false;
  // S140 6.1.1 rejects handles outside the application-populated table.
  // Query its first user handle; our TX CCCD is the last registered attribute.
  scRangeResult=sd_ble_gatts_initial_user_handle_get(&scRangeStart);
  scRangeEnd=tx.handles().cccd_handle;
  if(scRangeResult==NRF_SUCCESS&&(scRangeStart==0||scRangeStart>scRangeEnd||scRangeEnd==0xFFFF))scRangeResult=BLE_ERROR_INVALID_ATTR_HANDLE;
  resetServiceChange();
  started=true;return true;
}
void event(ble_evt_t* e){
  if(e->header.evt_id==BLE_GATTS_EVT_SC_CONFIRM){scConfirmEpoch=epoch;scConfirmSeen=true;}
  if(e->header.evt_id==BLE_GAP_EVT_CONNECTED||e->header.evt_id==BLE_GAP_EVT_DISCONNECTED){
    taskENTER_CRITICAL();
    if(e->header.evt_id==BLE_GAP_EVT_DISCONNECTED)disconnected=true;
    ++epoch;resetRing();
    taskEXIT_CRITICAL();
  }
}
void advertise(){if(started)Bluefruit.ScanResponse.addService(service);}
void authorize(bool enable,uint32_t now){
  // One atomic task-level authorization boundary: old callback fragments cannot
  // slip between queue clearing and the new permission window.
  vTaskSuspendAll();
  clearQueues();++session;
  if(enable)permission.open(now);else permission.close();
  permission.update(now,link&&secure&&bond);
  xTaskResumeAll();
}
void tick(uint32_t now,bool hidBusy){
  if(!started)return;
  if(seenEpoch!=epoch){taskENTER_CRITICAL();seenEpoch=epoch;const bool lost=disconnected;disconnected=false;taskEXIT_CRITICAL();clearLoopQueues();++session;subscribed=false;resetServiceChange();
    if(lost)permission.close();
  }
  BLEConnection* c=connection();link=c&&c->connected();secure=link&&c->secured();bond=link&&c->bonded();handle=link?Bluefruit.connHandle():BLE_CONN_HANDLE_INVALID;
  if(secure){const auto mode=c->getSecureMode();secure=mode.sm==1&&mode.lv>=2;}
  serviceChangeTick(now);
  if(seenSubscription!=subscriptionEpoch){taskENTER_CRITICAL();seenSubscription=subscriptionEpoch;const bool revoked=notifyRevoked;notifyRevoked=false;subscribed=notifyValue;taskEXIT_CRITICAL();clearLoopQueues();++session;if(revoked||!subscribed)permission.close();}
  // Bonded CCCDs may restore without a write callback. Read current state too.
  const bool notify=link&&tx.notifyEnabled(handle);
  if(subscribed&&!notify)invalidate();subscribed=notify;
  const uint8_t before=permission.state();permission.update(now,link&&secure&&bond);
  if(before==2&&permission.state()!=2){clearObservedQueues();++session;}
  if(overflow){clearObservedQueues();lines.abort();}
  lines.tick(now);
  if(reply.pending){
    if(reply.connectionEpoch!=epoch||reply.cccdEpoch!=subscriptionEpoch){clearLoopQueues();return;}
    if(uint32_t(now-reply.queued)>=5000u){invalidate();return;}
    if(!link||!secure||!bond||!subscribed){reply.clear();return;}
    if(hidBusy||uint32_t(now-reply.lastSent)<2u)return;
    uint16_t len=min<uint16_t>(20,reply.size-reply.offset);
    ble_gatts_hvx_params_t params={};params.handle=tx.handles().value_handle;params.type=BLE_GATT_HVX_NOTIFICATION;params.p_len=&len;params.p_data=reply.data+reply.offset;
    // Freeze task switching (including BLE event callbacks), not interrupts.
    // SoftDevice SVC must not run under FreeRTOS BASEPRI critical masking.
    // This single HVX attempt cannot wait, allocate or access files.
    vTaskSuspendAll();
    const uint32_t result=(reply.connectionEpoch==epoch&&reply.cccdEpoch==subscriptionEpoch)?sd_ble_gatts_hvx(handle,&params):NRF_ERROR_INVALID_STATE;
    xTaskResumeAll();
    if(result==NRF_SUCCESS){reply.offset+=len;reply.lastSent=now;if(reply.offset==reply.size)reply.clear();}
    return;
  }
  if(lineReady||!subscribed)return;
  for(uint8_t n=0;n<64;++n){
    taskENTER_CRITICAL();
    if(seenEpoch!=epoch||seenSubscription!=subscriptionEpoch||tail==head){taskEXIT_CRITICAL();break;}
    __DMB();const uint8_t ch=ring[tail];tail=(tail+1)%RingSize;
    taskEXIT_CRITICAL();
    const auto result=lines.push(ch,now);
    if(result!=deskmanage::Lines::Result::None){lineReady=true;lineInvalid=result==deskmanage::Lines::Result::Invalid;break;}
  }
}
bool takeLine(char*& line,bool& invalid){if(!lineReady||reply.pending||seenEpoch!=epoch||seenSubscription!=subscriptionEpoch)return false;line=lines.data();invalid=lineInvalid;lineReady=false;return true;}
bool allowed(){return permission.state()==2&&link&&secure&&bond&&subscribed&&seenEpoch==epoch&&seenSubscription==subscriptionEpoch;}
void touch(uint32_t now){if(allowed())permission.touch(now);}
bool radioWanted(){return permission.state()!=0;}
uint8_t state(){return permission.state();}
uint32_t remaining(uint32_t now){return permission.remaining(now);}
bool linked(){return link;}bool secured(){return secure;}bool bonded(){return bond;}
uint32_t generation(){return session;}
bool ready(){return started;}
uint16_t rxHandle(){return started?rx.handles().value_handle:0;}
uint16_t txHandle(){return started?tx.handles().value_handle:0;}
uint16_t txCccdHandle(){return started?tx.handles().cccd_handle:0;}
uint16_t serviceChangedStart(){return scRangeStart;}
uint16_t serviceChangedEnd(){return scRangeEnd;}
uint32_t serviceChangedResult(){return scResult;}
uint8_t serviceChangedAttempts(){return scAttempts;}
bool serviceChangedConfirmed(){return scConfirmed;}
Print& response(){return reply;}
void startResponse(){reply.clear();reply.connectionEpoch=epoch;reply.cccdEpoch=subscriptionEpoch;}
void finishResponse(){
  if(reply.failed||reply.size>4097){reply.clear();const char* error="HUB 0 ERR code=RESPONSE_TOO_LONG\n";while(*error)reply.write(*error++);}
  if(reply.size){reply.pending=true;reply.queued=millis();reply.lastSent=reply.queued-2u;}
}
}
