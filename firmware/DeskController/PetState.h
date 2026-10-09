#pragma once
#include <stdint.h>
namespace deskpet {
enum class Event:uint8_t { Press, Turn, TurnCounterClockwise };
enum class Motion:uint8_t { Idle=0, Happy=1, Walk=2 };
// Session-only visual companionship. Sleep is a pose, never MCU power saving
// or an output gate. Call begin after startup; only accepted physical input wakes.
class State {
 public:
  void begin(uint32_t now){count_=taps_=last_=effectAt_=motionAt_=0;inputAt_=now;seen_=effect_=sleeping_=false;event_=Event::Press;}
  void record(uint32_t now,uint32_t count=1,Event event=Event::Press){
    if(!count)return;
    count_=count>UINT32_MAX-count_?UINT32_MAX:count_+count;
    const Event next=(seen_&&last_==now&&event_==Event::Press&&event!=Event::Press)?event_:event;
    // Repeated detents sustain the walk cycle instead of resetting frame A on
    // every tick. A new gesture/direction starts a fresh, bounded timeline.
    if(!seen_||static_cast<uint32_t>(now-last_)>=1800u||next!=event_)motionAt_=now;
    event_=next;
    last_=inputAt_=now;seen_=true;sleeping_=false;
  }
  // The mapped physical touch was already counted once by pollInputs. This
  // local action adds affection feedback/tap statistics, never another input.
  void affection(uint32_t now){
    if(taps_!=UINT32_MAX)++taps_;
    last_=inputAt_=effectAt_=motionAt_=now;seen_=effect_=true;sleeping_=false;event_=Event::Press;
  }
  // Resetting diagnostic counters is not a physical wake or activity event.
  void reset(){count_=taps_=0;seen_=effect_=false;event_=Event::Press;}
  uint32_t interactions() const{return count_;}
  uint32_t taps() const{return taps_;}
  Motion motion(uint32_t now){update(now);return sleeping_||!seen_?Motion::Idle:(event_!=Event::Press?Motion::Walk:Motion::Happy);}
  int8_t direction(uint32_t now){return motion(now)==Motion::Walk?(event_==Event::TurnCounterClockwise?-1:1):0;}
  uint16_t elapsed(uint32_t now){update(now);return seen_?static_cast<uint16_t>((now-motionAt_)%1800u):0;}
  bool active(uint32_t now){return motion(now)!=Motion::Idle;}
  bool sleeping(uint32_t now){update(now);return sleeping_;}
  uint8_t effect(uint32_t now){update(now);return !sleeping_&&effect_?1:0;}
 private:
  void update(uint32_t now){
    if(seen_&&static_cast<uint32_t>(now-last_)>=1800u)seen_=false;
    if(effect_&&static_cast<uint32_t>(now-effectAt_)>=1200u)effect_=false;
    // Latch until a real input; no old pose can revive at the next millis cycle.
    if(!sleeping_&&static_cast<uint32_t>(now-inputAt_)>=30000u)sleeping_=true;
  }
  uint32_t count_=0,taps_=0,last_=0,inputAt_=0,effectAt_=0,motionAt_=0;
  bool seen_=false,effect_=false,sleeping_=false;Event event_=Event::Press;
};
}
