#pragma once
#include <stdint.h>
#include "SpriteArt.h"
namespace deskportrait {
struct Sprite { const uint8_t* indices; const uint16_t* palette;
  uint8_t width,height,paletteCount,transparentIndex; };
// WalkA/WalkB and invalid frames return Idle; invalid characters return character 0.
const Sprite& get(uint8_t character,deskart::Frame frame);
}
