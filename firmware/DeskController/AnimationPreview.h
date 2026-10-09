#pragma once
#include <stdint.h>

namespace deskdemo {
enum class Action:uint8_t { None=0, Idle=1, Blink=2, Happy=3, Walk=4, Tap=5, Sleep=6, Wake=7 };
constexpr uint32_t DurationMs=6000;
struct Snapshot { Action action=Action::None;uint16_t elapsed=0,remaining=0; };
// RAM-only visual override. Expiry is latched; old demos cannot revive at wrap.
class State {
 public:
  void start(Action action,uint32_t now){action_=action;started_=now;}
  bool stop(){const bool wasActive=action_!=Action::None;action_=Action::None;return wasActive;}
  Snapshot snapshot(uint32_t now){
    Snapshot out;
    if(action_==Action::None)return out;
    const uint32_t elapsed=now-started_;
    if(elapsed>=DurationMs){stop();return out;}
    out.action=action_;out.elapsed=static_cast<uint16_t>(elapsed);
    out.remaining=static_cast<uint16_t>(DurationMs-elapsed);return out;
  }
 private: Action action_=Action::None;uint32_t started_=0;
};
// Frame numbers match the immutable art API: Idle/Blink/Happy/WalkA/WalkB.
// A 60px-tall sprite is centered at y88, so +/-1 stays inside y87..148.
struct Visual {uint8_t frame=0,heartPhase=0,sleepLetters=0;int8_t dx=0,dy=0;bool heart=false;};
inline Visual visual(uint8_t action,uint16_t elapsed){
  Visual v;const uint32_t t=elapsed;
  switch(static_cast<Action>(action)){
    case Action::Idle:v.frame=t%4400>=4220?1:0;v.dy=(t/600)&1u?1:-1;break;
    case Action::Blink:v.frame=t%900>=600?1:0;break;
    case Action::Happy:v.frame=2;v.dy=(t/300)&1u?1:-1;break;
    case Action::Walk:{static const int8_t step[]={-3,-1,1,3,1,-1};v.frame=3+((t/180)&1u);v.dx=step[(t/180)%6];v.dy=(t/180)&1u?1:-1;break;}
    case Action::Tap:{const uint32_t phase=t%2400;v.frame=phase<1800?2:0;v.heart=phase<1200;v.heartPhase=(t/180)&1u;v.dy=(t/300)&1u?1:-1;break;}
    case Action::Sleep:v.frame=1;v.sleepLetters=1+(t/600)%3;break;
    case Action::Wake:
      if(t<600){v.frame=1;v.sleepLetters=1+(t/200)%3;}
      else if(t<2400){v.frame=2;v.dy=((t-600)/300)&1u?1:-1;}
      else {v.frame=(t-2400)%2000>=1800?1:0;v.dy=((t-2400)/600)&1u?1:-1;}
      break;
    default:break;
  }
  return v;
}
}
