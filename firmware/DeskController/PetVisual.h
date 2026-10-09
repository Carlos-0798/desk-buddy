#pragma once
#include <stdint.h>

namespace deskpet {
// Pure visual projection of physical events. No HID command, stored preference,
// health score or power behavior is created by this animation.
struct Visual {int8_t dx=0,dy=0;bool mirror=false;};
inline Visual physicalVisual(uint8_t motion,int8_t direction,uint16_t elapsed,
                             bool body,bool /*idleAnimation*/,bool sleeping){
  Visual v;
  if(sleeping||!motion)return v;
  if(motion==2)v.mirror=body&&direction<0;
  // profile.animation governs automatic idle blinking only. Deliberate input
  // always receives its short response, even when idle motion is disabled.
  if(motion==2){
    if(!direction)return v; // Legacy snapshots have no directional event timeline.
    // Move outwards over one input's feedback window, bounded to +/-18 px.
    // A portrait acknowledges a turn with a small tilt rather than leg frames.
    const uint16_t phase=elapsed/180u;
    const int8_t distance=body?(phase>9?18:phase*2):2;
    v.dx=direction<0?-distance:distance;v.dy=phase&1u?1:-1;
  }else v.dy=(elapsed/180u)&1u?1:-1;
  return v;
}
}
