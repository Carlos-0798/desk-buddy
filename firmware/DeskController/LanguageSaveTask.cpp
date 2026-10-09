#include <Arduino.h>
#include "LanguageSaveTask.h"
#include "UiPreferencesStore.h"
#include "DisplayPreferencesStore.h"

namespace desklanguage {
namespace {
QueueHandle_t requests=nullptr, results=nullptr;
TaskHandle_t worker=nullptr;
bool outstanding=false; // accessed only by setup/loop
void work(void*) {
  for (;;) {
    Request selected;
    if (xQueueReceive(requests,&selected,portMAX_DELAY)!=pdTRUE) continue;
    Result result; result.kind=selected.kind;result.language=selected.language;result.display=selected.display;
    const uint32_t began=micros();
    if(selected.kind==Kind::Language){result.okay=deskprefs::save(selected.language);result.error=deskprefs::lastErrorName();}
    else {result.okay=deskdisplaystore::save(selected.display);result.error=deskdisplaystore::lastErrorName();}
    result.elapsedUs=micros()-began;
    // One outstanding request; the result queue cannot already contain one.
    xQueueSend(results,&result,portMAX_DELAY);
  }
}
}
bool begin() {
  if(worker) return true;
  // BSP 1.7.0 disables time slicing. A same-priority storage worker can occupy
  // the CPU for hundreds of ms; let the UI preempt it on the next RTOS tick.
  // USB/BLE/SOC remain at HIGH; do not place a worker on the reserved idle level.
  vTaskPrioritySet(nullptr,TASK_PRIO_NORMAL);
  requests=xQueueCreate(1,sizeof(Request));
  results=xQueueCreate(1,sizeof(Result));
  if(!requests||!results) return false;
  return xTaskCreate(work,"ui-save",1536,nullptr,TASK_PRIO_LOW,&worker)==pdPASS;
}
bool start(uint8_t language) {
  if(!worker||outstanding||language>1) return false;
  Request request;request.language=language;
  if(xQueueSend(requests,&request,0)!=pdTRUE) return false;
  outstanding=true; return true;
}
bool startDisplay(const deskdisplay::Settings& settings) {
  if(!worker||outstanding||!settings.valid())return false;
  Request request;request.kind=Kind::Display;request.display=settings;
  if(xQueueSend(requests,&request,0)!=pdTRUE)return false;
  outstanding=true;return true;
}
bool busy(){return outstanding;}
bool take(Result& result) {
  if(!outstanding||xQueueReceive(results,&result,0)!=pdTRUE) return false;
  outstanding=false; return true;
}
}
