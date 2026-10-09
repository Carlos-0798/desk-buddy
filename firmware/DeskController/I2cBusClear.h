#pragma once
#include <Arduino.h>

namespace deskbus {
struct ClearResult { bool ready; bool initialSda; bool initialScl; uint8_t pulses; };
// Called only while TWIM is disabled. Never drive a shared I2C line HIGH.
// NXP UM10204 bus clear: at most nine SCL pulses, then release/STOP.
inline ClearResult clear() {
  pinMode(PIN_WIRE_SDA,INPUT_PULLUP);pinMode(PIN_WIRE_SCL,INPUT_PULLUP);
  delayMicroseconds(10);
  ClearResult r={false,digitalRead(PIN_WIRE_SDA)!=LOW,digitalRead(PIN_WIRE_SCL)!=LOW,0};
  auto clockReleased=[](){
    const uint32_t start=micros();
    while(digitalRead(PIN_WIRE_SCL)==LOW){if(uint32_t(micros()-start)>=2000)return false;}
    return true;
  };
  if(!clockReleased())return r;
  while(digitalRead(PIN_WIRE_SDA)==LOW&&r.pulses<9){
    digitalWrite(PIN_WIRE_SCL,LOW);pinMode(PIN_WIRE_SCL,OUTPUT);delayMicroseconds(5);
    pinMode(PIN_WIRE_SCL,INPUT_PULLUP);++r.pulses;
    if(!clockReleased())return r;
    delayMicroseconds(5);
  }
  if(r.pulses){
    digitalWrite(PIN_WIRE_SCL,LOW);pinMode(PIN_WIRE_SCL,OUTPUT);
    digitalWrite(PIN_WIRE_SDA,LOW);pinMode(PIN_WIRE_SDA,OUTPUT);delayMicroseconds(5);
    pinMode(PIN_WIRE_SCL,INPUT_PULLUP);
    if(!clockReleased()){pinMode(PIN_WIRE_SDA,INPUT_PULLUP);return r;}
    delayMicroseconds(5);pinMode(PIN_WIRE_SDA,INPUT_PULLUP);delayMicroseconds(5);
  }
  r.ready=digitalRead(PIN_WIRE_SDA)!=LOW&&digitalRead(PIN_WIRE_SCL)!=LOW;
  return r;
}
}
