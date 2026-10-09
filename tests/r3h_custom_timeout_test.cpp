#include "DisplaySleep.h"
#include "DisplayPreferencesCodec.h"
#include "HubConfig.h"

using deskdisplay::Settings;
using deskdisplay::State;
static_assert(deskdisplay::MaxSeconds==43200,"12-hour upper bound");
static_assert(uint32_t(deskdisplay::MaxSeconds)*1000u==43200000u,"milliseconds do not narrow through 16 bits");
static_assert(uint32_t(deskdisplay::MaxSeconds)*1000u<0x80000000u,"deadline is below half of millis range");
static_assert(sizeof(Settings)==4,"two existing u16 settings fields");
static_assert(deskdisplaystore::RecordSize==20,"existing display record size");
static_assert(deskconfig::BankSize==224,"unrelated profile bank size preserved");

#if __cplusplus >= 201402L
constexpr bool allU16Values(){
  unsigned accepted=0;
  for(uint32_t seconds=0;seconds<=UINT16_MAX;++seconds){
    const bool expected=seconds==0||seconds==30||(seconds/60>=1&&seconds/60<=720&&seconds%60==0);
    const bool actual=deskdisplay::validSeconds(uint16_t(seconds));
    if(actual!=expected)return false;
    accepted+=actual?1:0;
  }
  return accepted==722;
}
constexpr bool legacyDefaults(){
  Settings s;if(s.usbSeconds!=300||s.batterySeconds!=60)return false;
  const uint16_t legacy[]={0,30,60,300};
  for(auto a:legacy)for(auto b:legacy)if(!Settings(a,b).valid())return false;
  return true;
}
constexpr bool deadlineForEveryMinute(){
  for(uint16_t minute=1;minute<=720;++minute){
    Settings c(uint16_t(minute*60),uint16_t(minute*60));State s;s.begin(37,true);
    const uint32_t deadline=37+uint32_t(minute)*60000u;
    if(s.due(deadline-1,c,true)||!s.due(deadline,c,true)||s.due(deadline,c,false))return false;
  }
  return true;
}
constexpr bool customBoundaryWrap(){
  const uint32_t start=UINT32_MAX-5000u;State s;s.begin(start,true);Settings c(43200,1200);
  const uint32_t deadline=start+43200000u;
  return !s.due(deadline-1,c,true)&&s.due(deadline,c,true)&&s.idle(deadline)==43200000u;
}
constexpr bool powerChangeToCustom(){
  State s;s.begin(0,true);Settings c(1200,43200);
  if(s.due(1199999,c,true)||!s.due(1200000,c,true))return false;
  s.power(1200001,false);
  if(s.idle(1200001)||s.due(1200001+43200000u-1,c,true)||!s.due(1200001+43200000u,c,true))return false;
  s.power(45000002,true);
  return s.idle(45000002)==0&&!s.due(46200001,c,true)&&s.due(46200002,c,true);
}
constexpr bool noPollExtension(){
  State s;s.begin(100,true);Settings c(1200,1200);
  for(uint32_t time=1100;time<1200100;time+=1000){if(s.idle(time)!=time-100||s.due(time,c,true))return false;}
  return s.due(1200100,c,true);
}
constexpr bool disabledAndNavigation(){
  State s;s.begin(0,true);Settings off(0,0),longWait(43200,43200);
  if(s.due(43200000,off,true)||s.due(UINT32_MAX,off,true)||s.due(43200000,longWait,false))return false;
  s.activity(43200000);return !s.due(43200001,longWait,true);
}
constexpr bool wakeGuardUnchanged(){
  State s;s.begin(UINT32_MAX-500,true);s.sleep(UINT32_MAX-100,true);
  s.contact(20);if(!s.asleep())return false;
  if(s.batch(30,false,false)||!s.asleep())return false;
  if(s.batch(40,true,true)||s.asleep())return false;
  if(s.batch(400,false,true)||!s.suppressed())return false;
  if(s.batch(599,false,false)||s.batch(600,false,false)||s.suppressed())return false;
  return s.batch(620,true,false)&&s.wakeCount()==1;
}
static_assert(allU16Values(),"all 65536 raw u16 values enforce exactly 722 supported values");
static_assert(legacyDefaults(),"old timeout combinations and defaults remain accepted");
static_assert(deadlineForEveryMinute(),"every custom minute fires only at its exact deadline");
static_assert(customBoundaryWrap(),"12-hour deadline crosses millis rollover correctly");
static_assert(powerChangeToCustom(),"power change resets idle for selected 20-minute/12-hour timeout");
static_assert(noPollExtension(),"20-minute simulated polling observes time without extending idle");
static_assert(disabledAndNavigation(),"never and navigation eligibility remain unchanged");
static_assert(wakeGuardUnchanged(),"held entry, first input and quiet guard survive rollover");
#endif
