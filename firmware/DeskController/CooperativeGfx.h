#pragma once
#include <Arduino.h>
#include <Adafruit_GFX.h>

namespace deskview {

// Rendering facade for the existing UI/font/sprite code. Each forwarded call
// completes its own target transaction before input sampling is allowed. The
// callback may capture/queue inputs and service HID/WDT, but MUST NOT draw, change
// page/layout, dispatch actions, access this facade or invoke renderUi recursively.
// Rendering and dispatch remain single-owner operations in the application loop.
class CooperativeGfx final : public Adafruit_GFX {
 public:
  static constexpr int16_t MaxChunkRows = 8;
  static constexpr uint32_t ServiceIntervalUs = 4000;
  using Service = void (*)();

  CooperativeGfx(Adafruit_GFX& target, Service service)
      : Adafruit_GFX(target.width(), target.height()), target_(target),
        service_(service), lastServiceAt_(micros()) {}

  // GFX algorithms wrap write* calls in these methods. Do not forward the outer
  // transaction: each target draw/fill below is self-contained, with no nesting.
  void startWrite() override {}
  void endWrite() override {}

  void drawPixel(int16_t x, int16_t y, uint16_t color) override {
    if (x >= 0 && y >= 0 && x < width() && y < height()) target_.drawPixel(x,y,color);
    serviceDue();
  }
  void writePixel(int16_t x, int16_t y, uint16_t color) override { drawPixel(x,y,color); }
  void fillRect(int16_t x, int16_t y, int16_t w, int16_t h, uint16_t color) override {
    // Normalize in 32 bits to safely handle negative int16 lengths and clipping.
    int32_t left=x, top=y, wide=w, high=h;
    if (wide < 0) { left += wide + 1; wide = -wide; }
    if (high < 0) { top += high + 1; high = -high; }
    int32_t right=left+wide, bottom=top+high;
    if (left < 0) left=0;
    if (top < 0) top=0;
    if (right > width()) right=width();
    if (bottom > height()) bottom=height();
    if (right <= left || bottom <= top) { serviceDue(); return; }
    while (top < bottom) {
      const int16_t rows=static_cast<int16_t>(bottom-top > MaxChunkRows ? MaxChunkRows : bottom-top);
      target_.fillRect(static_cast<int16_t>(left),static_cast<int16_t>(top),
                      static_cast<int16_t>(right-left),rows,color);
      top += rows;
      serviceDue();
    }
  }
  void writeFillRect(int16_t x,int16_t y,int16_t w,int16_t h,uint16_t color) override {
    fillRect(x,y,w,h,color);
  }
  void drawFastHLine(int16_t x,int16_t y,int16_t w,uint16_t color) override { fillRect(x,y,w,1,color); }
  void drawFastVLine(int16_t x,int16_t y,int16_t h,uint16_t color) override { fillRect(x,y,1,h,color); }
  void writeFastHLine(int16_t x,int16_t y,int16_t w,uint16_t color) override { fillRect(x,y,w,1,color); }
  void writeFastVLine(int16_t x,int16_t y,int16_t h,uint16_t color) override { fillRect(x,y,1,h,color); }
  void fillScreen(uint16_t color) override { fillRect(0,0,width(),height(),color); }

  void setRotation(uint8_t next) override {
    target_.setRotation(next);
    rotation=target_.getRotation();
    _width=target_.width(); _height=target_.height();
  }
  void invertDisplay(bool inverted) override { target_.invertDisplay(inverted); }

  // Optional service at the end of a render shorter than the normal interval.
  void serviceNow() {
    if (!service_ || inService_) return;
    inService_=true;
    lastServiceAt_=micros();
    service_();
    lastServiceAt_=micros();
    inService_=false;
  }

 private:
  void serviceDue() {
    if (static_cast<uint32_t>(micros()-lastServiceAt_) >= ServiceIntervalUs) serviceNow();
  }
  Adafruit_GFX& target_;
  Service service_;
  uint32_t lastServiceAt_;
  bool inService_=false;
};
}  // namespace deskview
