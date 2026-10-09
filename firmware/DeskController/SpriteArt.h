#pragma once
#include <stdint.h>
namespace deskart {
enum class Frame:uint8_t { Idle=0, Blink=1, Happy=2, WalkA=3, WalkB=4 };
struct Sprite { const uint8_t* indices; const uint16_t* palette;
  uint8_t width,height,paletteCount,transparentIndex;
  uint8_t portraitX,portraitY,portraitW,portraitH; };
const Sprite& get(uint8_t character,Frame frame);
}
