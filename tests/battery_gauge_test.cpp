#include <cstdio>
#include <cstdlib>
#include "../firmware/DeskController/BatteryGauge.h"
static unsigned checks=0;
#define CHECK(x) do{++checks;if(!(x)){fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x);exit(1);}}while(false)
int main(){
  using namespace deskbattery;
  CHECK(approximatePercent(0)==0);CHECK(approximatePercent(3300)==0);
  CHECK(approximatePercent(3450)==5);CHECK(approximatePercent(3600)==10);
  CHECK(approximatePercent(3700)==25);CHECK(approximatePercent(3800)==40);
  CHECK(approximatePercent(4000)==70);CHECK(approximatePercent(4200)==100);
  CHECK(approximatePercent(65535)==100);
  int16_t previous=0;
  for(unsigned mv=2800;mv<=4350;++mv){
    const auto p=approximatePercent(static_cast<uint16_t>(mv));
    CHECK(p>=previous&&p<=100&&p%5==0);previous=p;
  }
  Gauge g;auto s=g.state(0,false);
  CHECK(!s.valid&&s.percent==-1&&s.ageMs==UINT32_MAX&&!s.usbBiased);
  CHECK(g.state(10,true).usbBiased);  // USB flag never invents a valid sample.
  g.observe(3700,0);s=g.state(0,false);
  CHECK(s.valid&&s.percent==25&&s.millivolts==3700&&s.ageMs==0);
  CHECK(g.state(4999,false).valid);
  s=g.state(5000,false);CHECK(!s.valid&&s.percent==-1&&s.millivolts==3700);
  g.observe(4200,6000);s=g.state(6000,true);
  CHECK(!s.valid&&s.usbBiased&&s.percent==-1&&s.millivolts==4200&&s.ageMs==0); // USB hides percentage, retains diagnostic sample.
  g.observe(3800,7000);s=g.state(7000,false);
  CHECK(s.valid&&!s.usbBiased&&s.millivolts==4100); // Quarter EMA, not old sample replay.
  g.observe(2800,7000);CHECK(g.state(7000,false).millivolts==4100); // Duplicate timestamp ignored.
  g.observe(2800,6999);CHECK(g.state(7000,false).millivolts==4100); // Out of order ignored.
  for(unsigned i=0;i<32;++i)g.observe(3800,8000+i*1000);
  s=g.state(39000,false);CHECK(s.valid&&s.millivolts==3800&&s.percent==40);
  g.observe(0,40000);s=g.state(40000,true);CHECK(!s.valid&&s.percent==-1&&s.millivolts==0&&s.usbBiased);
  g.observe(3700,41000);CHECK(g.state(41000,false).millivolts==3700); // Reseed after invalid.
  g.observe(4351,42000);CHECK(!g.state(42000,false).valid);
  g.observe(4350,43000);CHECK(g.state(43000,false).valid&&g.state(43000,false).percent==100);
  g.observe(2799,44000);CHECK(!g.state(44000,false).valid);
  g.observe(2800,45000);CHECK(g.state(45000,false).valid&&g.state(45000,false).percent==0);
  g.reset();CHECK(!g.state(45000,false).valid);
  g.observe(4000,UINT32_MAX-500u);g.observe(3600,499u);
  s=g.state(499u,false);CHECK(s.valid&&s.millivolts==3900&&s.ageMs==0);
  CHECK(g.state(5498u,false).valid);CHECK(!g.state(5499u,false).valid);
  // A live USB argument hides immediately, even before the loop records it.
  g.reset();g.observe(3700,100);const Gauge& readOnly=g;
  s=readOnly.state(105,true);CHECK(!s.valid&&s.percent==-1&&s.usbBiased&&s.millivolts==3700&&s.ageMs==5);
  g.setUsbVbus(true);
  // A query after removal but before source tracking cannot expose old data.
  s=readOnly.state(110,false);CHECK(!s.valid&&s.percent==-1&&s.millivolts==3700&&s.ageMs==10);
  g.observe(4200,200);s=readOnly.state(200,true);
  CHECK(!s.valid&&s.percent==-1&&s.millivolts==4200&&s.usbBiased);
  g.setUsbVbus(false);s=readOnly.state(250,false);
  CHECK(!s.valid&&s.percent==-1&&s.millivolts==4200&&s.ageMs==50&&!s.usbBiased);
  // Repeated const reads/source updates never unlock the cached charger value.
  for(unsigned i=0;i<8;++i){g.setUsbVbus(false);CHECK(!readOnly.state(250+i,false).valid);}
  g.observe(3300,200);CHECK(!readOnly.state(250,false).valid); // Cached timestamp rejected.
  g.observe(3300,199);CHECK(!readOnly.state(250,false).valid); // Earlier sample rejected.
  g.observe(3600,300);s=readOnly.state(300,false);
  CHECK(s.valid&&s.percent==10&&s.millivolts==3600); // Seed, NOT 4050mV USB/battery EMA.
  g.setUsbVbus(false);CHECK(readOnly.state(300,false).valid);
  g.observe(3800,400);CHECK(readOnly.state(400,false).millivolts==3650); // Normal filtering resumes.
  g.setUsbVbus(true);g.observe(4250,500);
  CHECK(!readOnly.state(500,true).valid&&readOnly.state(500,true).percent==-1);
  g.setUsbVbus(false);g.observe(0,600);CHECK(!readOnly.state(600,false).valid);
  g.observe(3700,700);CHECK(readOnly.state(700,false).valid&&readOnly.state(700,false).millivolts==3700);
  // Startup powered by USB and transitions spanning millis wrap.
  g.reset();g.setUsbVbus(true);g.observe(4200,UINT32_MAX-2u);
  CHECK(!readOnly.state(UINT32_MAX,true).valid);
  g.setUsbVbus(false);CHECK(!readOnly.state(0,false).valid);
  g.observe(3800,1);s=readOnly.state(1,false);
  CHECK(s.valid&&s.percent==40&&s.millivolts==3800&&s.ageMs==0);
  CHECK(!readOnly.state(5001,false).valid);
  g.reset();CHECK(!readOnly.state(10,false).valid&&readOnly.state(10,false).ageMs==UINT32_MAX);
  printf("BatteryGauge: %u assertions, 0 failures.\n",checks);
  puts("Display heuristic only: not a physical SOC, presence or charge validation.");
}
